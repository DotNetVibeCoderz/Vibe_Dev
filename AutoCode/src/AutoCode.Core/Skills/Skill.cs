// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;
using AutoCode.Core.Utilities;
using Microsoft.SemanticKernel;

namespace AutoCode.Core.Skills;

/// <summary>A packaged, reusable workflow the agent can invoke.</summary>
public sealed record Skill
{
    /// <summary>Invocation name, also the slash command (<c>/deploy</c>).</summary>
    public required string Name { get; init; }

    /// <summary>One line telling the model when this skill applies. This is what gets it selected.</summary>
    public required string Description { get; init; }

    /// <summary>The instructions themselves, injected into the conversation when invoked.</summary>
    public required string Instructions { get; init; }

    /// <summary>Directory the skill was loaded from; referenced files resolve against it.</summary>
    public required string Directory { get; init; }

    /// <summary>Tools the skill restricts itself to. Empty means all.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Model override, e.g. run this workflow on the small model.</summary>
    public string? Model { get; init; }

    /// <summary>True when the instructions contain Semantic Kernel template syntax.</summary>
    public bool IsTemplated { get; init; }

    /// <summary>
    /// Renders the skill body, expanding <c>{{$argument}}</c> style placeholders through Semantic
    /// Kernel's prompt template engine when the skill declares itself templated.
    /// </summary>
    public async Task<string> RenderAsync(string? arguments, CancellationToken cancellationToken)
    {
        if (!IsTemplated)
        {
            return string.IsNullOrWhiteSpace(arguments)
                ? Instructions
                : $"{Instructions}\n\n## Arguments\n\n{arguments}";
        }

        var factory = new KernelPromptTemplateFactory();
        var template = factory.Create(new PromptTemplateConfig(Instructions)
        {
            Name = Name,
            TemplateFormat = PromptTemplateConfig.SemanticKernelTemplateFormat,
            AllowDangerouslySetContent = true,
        });

        var kernel = new Kernel();
        var kernelArguments = new KernelArguments
        {
            ["arguments"] = arguments ?? "",
            ["input"] = arguments ?? "",
        };

        return await template.RenderAsync(kernel, kernelArguments, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Discovers skills on disk.
///
/// EN: a skill is a directory containing SKILL.md — front matter for the metadata, markdown body for
/// the instructions. Project skills win over user skills of the same name, so a repository can
/// override a personal default without the user having to remember it did.
/// ID: sebuah skill adalah direktori berisi SKILL.md. Skill milik proyek menimpa skill pengguna
/// dengan nama sama, sehingga repositori bisa menyesuaikan alur kerja tanpa mengubah setelan pribadi.
/// </summary>
public static class SkillLoader
{
    public const string ManifestName = "SKILL.md";

    public static IReadOnlyList<Skill> Discover(string workspaceRoot, AutoCodeOptions options)
    {
        var skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

        // Lowest precedence first; later loads overwrite earlier ones.
        foreach (var directory in CandidateDirectories(workspaceRoot, options))
        {
            foreach (var skill in LoadFrom(directory))
                skills[skill.Name] = skill;
        }

        return [.. skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> CandidateDirectories(string workspaceRoot, AutoCodeOptions options)
    {
        yield return Path.Combine(ConfigurationLoader.UserHome, "skills");
        yield return Path.Combine(ConfigurationLoader.WorkspaceHome(workspaceRoot), "skills");

        // Claude Code layout, so an existing project's skills work unchanged.
        yield return Path.Combine(workspaceRoot, ".claude", "skills");

        foreach (var configured in options.SkillDirectories)
            yield return WorkspacePath.Resolve(workspaceRoot, configured);
    }

    /// <summary>Loads every skill directly beneath <paramref name="root"/>.</summary>
    public static IEnumerable<Skill> LoadFrom(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var manifest = Path.Combine(directory, ManifestName);

            if (!File.Exists(manifest))
                continue;

            Skill? skill = null;

            try
            {
                skill = Parse(File.ReadAllText(manifest), directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip an unreadable skill rather than failing the whole session.
            }

            if (skill is not null)
                yield return skill;
        }
    }

    internal static Skill? Parse(string document, string directory)
    {
        var (fields, body) = FrontMatter.Parse(document);

        var name = fields.GetValueOrDefault("name", Path.GetFileName(directory));
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
            return null;

        return new Skill
        {
            Name = name,
            Description = fields.GetValueOrDefault("description", ""),
            Instructions = body,
            Directory = directory,
            AllowedTools = FrontMatter.AsList(fields, "allowed-tools") is { Count: > 0 } tools
                ? tools
                : FrontMatter.AsList(fields, "tools"),
            Model = fields.GetValueOrDefault("model"),
            IsTemplated = bool.TryParse(fields.GetValueOrDefault("templated"), out var templated) && templated,
        };
    }
}
