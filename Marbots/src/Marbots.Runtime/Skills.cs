using System.Diagnostics;
using System.Text;
using Marbots.Abstractions;
using Marbots.Kernel;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>Minimal YAML front-matter reader: scalars, inline lists, block lists and one level of nesting (dotted keys).</summary>
public static class FrontMatter
{
    public static (Dictionary<string, string> Fields, string Body) Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (fields, text);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (fields, text);
        var yaml = text[4..end];
        var bodyStart = text.IndexOf('\n', end + 4);
        var body = bodyStart < 0 ? "" : text[(bodyStart + 1)..];

        string? parent = null;
        string? listKey = null;
        foreach (var raw in yaml.Split('\n'))
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) && listKey is not null)
            {
                fields[listKey] = fields.TryGetValue(listKey, out var existing) && existing.Length > 0 ? existing + "," + Unquote(line[2..]) : Unquote(line[2..]);
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (indent == 0) parent = null;
            var fullKey = indent > 0 && parent is not null ? $"{parent}.{key}" : key;
            if (value.Length == 0)
            {
                if (indent == 0) parent = key;
                listKey = fullKey;
                fields.TryAdd(fullKey, "");
                continue;
            }
            listKey = null;
            if (value.StartsWith('[') && value.EndsWith(']'))
                value = string.Join(',', value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote));
            fields[fullKey] = Unquote(value);
        }
        return (fields, body);
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;
}

/// <summary>
/// Discovers SKILL.md packages (Claude/Agent Skills and OpenClaw compatible). Only name + description are
/// placed in context; full instructions load on demand (progressive disclosure).
/// </summary>
public sealed class SkillRegistry
{
    private readonly MarbotsOptions _options;
    private readonly ILogger<SkillRegistry> _log;
    private volatile IReadOnlyList<SkillInfo> _skills = [];

    public SkillRegistry(MarbotsOptions options, ILogger<SkillRegistry> log)
    {
        _options = options;
        _log = log;
        Refresh();
    }

    public string InstalledDirectory => _options.DataPath("skills");
    public string PendingDirectory => _options.DataPath("skills-pending");

    public IReadOnlyList<SkillInfo> All => _skills;

    public SkillInfo? Find(string name) => _skills.FirstOrDefault(s => !s.Pending && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Refresh()
    {
        var list = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        var builtIn = Path.Combine(AppContext.BaseDirectory, "skills");
        foreach (var dir in _options.SkillDirectories.Append(builtIn))
            Scan(dir, "Marbots Verified", "built-in", false, list);
        Scan(InstalledDirectory, "Local", "installed", false, list);
        Scan(PendingDirectory, "Unverified", "auto-learn", true, list);
        _skills = [.. list.Values.OrderBy(s => s.Pending).ThenBy(s => s.Name)];
    }

    private void Scan(string root, string trust, string source, bool pending, Dictionary<string, SkillInfo> into)
    {
        if (!Directory.Exists(root)) return;
        foreach (var skillFile in Directory.EnumerateFiles(root, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
        {
            try
            {
                var info = Read(skillFile, trust, source, pending);
                var key = pending ? "pending:" + info.Name : info.Name;
                into[key] = info;
            }
            catch (IOException ex) { _log.LogWarning(ex, "Failed to read skill {File}", skillFile); }
        }
    }

    public static SkillInfo Read(string skillFile, string trust, string source, bool pending)
    {
        var (fields, _) = FrontMatter.Parse(File.ReadAllText(skillFile));
        var dir = Path.GetDirectoryName(skillFile)!;
        return new SkillInfo
        {
            Name = fields.GetValueOrDefault("name") is { Length: > 0 } n ? n : Path.GetFileName(dir),
            Description = fields.GetValueOrDefault("description") ?? "",
            Version = fields.GetValueOrDefault("version") is { Length: > 0 } v ? v : "1.0.0",
            Path = dir,
            Trust = trust,
            Source = source,
            Pending = pending,
            RequiresTools = (fields.GetValueOrDefault("requires.tools") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            NeedsNetwork = fields.GetValueOrDefault("permissions.network") is "true",
            NeedsShell = fields.GetValueOrDefault("permissions.shell") is "true",
            HasScripts = Directory.Exists(Path.Combine(dir, "scripts")),
        };
    }

    public IReadOnlyList<SkillInfo> ForBot(BotDefinition bot) =>
        bot.Skills.Contains("*") ? _skills.Where(s => !s.Pending).ToList()
            : _skills.Where(s => !s.Pending && bot.Skills.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    public async Task<string> LoadBodyAsync(SkillInfo skill, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(skill.Path, "SKILL.md"), ct);
        var (_, body) = FrontMatter.Parse(text);
        var sb = new StringBuilder();
        sb.Append("# Skill: ").AppendLine(skill.Name).AppendLine(body.Trim());
        var resources = Directory.EnumerateFiles(skill.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skill.Path, f).Replace('\\', '/'))
            .Where(f => f != "SKILL.md").Take(50).ToList();
        if (resources.Count > 0)
        {
            sb.AppendLine().AppendLine("## Bundled resources (read with read_skill_file)");
            foreach (var r in resources) sb.Append("- ").AppendLine(r);
        }
        return sb.ToString();
    }

    /// <summary>Install from a local folder or a git URL. Returns the installed skills.</summary>
    public async Task<IReadOnlyList<SkillInfo>> InstallAsync(string source, CancellationToken ct)
    {
        string root;
        string? temp = null;
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            temp = Path.Combine(Path.GetTempPath(), "marbots-skill-" + Guid.NewGuid().ToString("N")[..8]);
            var psi = new ProcessStartInfo("git") { ArgumentList = { "clone", "--depth", "1", source, temp }, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("git clone failed: " + await p.StandardError.ReadToEndAsync(ct));
            root = temp;
        }
        else root = Path.GetFullPath(source);

        try
        {
            var installed = new List<SkillInfo>();
            foreach (var skillFile in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                var info = Read(skillFile, "Local", "installed", false);
                var name = Ids.Slug(info.Name);
                if (name.Length == 0) continue;
                var dest = Path.Combine(InstalledDirectory, name);
                CopyDirectory(Path.GetDirectoryName(skillFile)!, dest);
                installed.Add(info);
            }
            Refresh();
            return installed;
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    public bool Uninstall(string name)
    {
        var dir = Path.Combine(InstalledDirectory, Ids.Slug(name));
        if (!Directory.Exists(dir)) return false;
        TryDelete(dir);
        Refresh();
        return true;
    }

    public async Task<SkillInfo> CreateAsync(string name, string description, string body, bool pending, CancellationToken ct)
    {
        var slug = Ids.Slug(name);
        if (slug.Length == 0) throw new ArgumentException("Skill name is required.");
        var dir = Path.Combine(pending ? PendingDirectory : InstalledDirectory, slug);
        Directory.CreateDirectory(dir);
        var content = $"---\nname: {slug}\ndescription: {description.Replace('\n', ' ')}\nversion: 1.0.0\n---\n\n{body.Trim()}\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), content, ct);
        Refresh();
        return Read(Path.Combine(dir, "SKILL.md"), pending ? "Unverified" : "Local", pending ? "auto-learn" : "installed", pending);
    }

    public bool ApprovePending(string name)
    {
        var src = Path.Combine(PendingDirectory, Ids.Slug(name));
        if (!Directory.Exists(src)) return false;
        CopyDirectory(src, Path.Combine(InstalledDirectory, Ids.Slug(name)));
        TryDelete(src);
        Refresh();
        return true;
    }

    public bool RejectPending(string name)
    {
        var src = Path.Combine(PendingDirectory, Ids.Slug(name));
        if (!Directory.Exists(src)) return false;
        TryDelete(src);
        Refresh();
        return true;
    }

    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (rel.StartsWith(".git", StringComparison.Ordinal)) continue;
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class LoadSkillFunction(SkillRegistry registry) : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "load_skill", "Load the full instructions of one of your skills. Always load a skill before applying it.",
        Schema(("name", "string", "Skill name", true)), "skills", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var name = call.Require("name");
        var skill = registry.ForBot(ctx.Bot).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return FunctionResult.Fail($"Skill '{name}' is not enabled for this bot.");
        if (ctx.Services.GetService(typeof(IEventBus)) is IEventBus bus)
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.SkillLoaded, BotId = ctx.Bot.Id, TaskId = ctx.TaskId, ThreadId = ctx.ThreadId, Message = skill.Name }, ct);
        return FunctionResult.Ok(await registry.LoadBodyAsync(skill, ct));
    }
}

public sealed class ReadSkillFileFunction(SkillRegistry registry) : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "read_skill_file", "Read a bundled resource (template, reference, script) from one of your skills.",
        Schema(("name", "string", "Skill name", true), ("path", "string", "Resource path inside the skill", true)),
        "skills", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var skill = registry.ForBot(ctx.Bot).FirstOrDefault(s => s.Name.Equals(call.Require("name"), StringComparison.OrdinalIgnoreCase));
        if (skill is null) return FunctionResult.Fail("Skill is not enabled for this bot.");
        var path = WorkspacePaths.Resolve(skill.Path, call.Require("path"));
        if (!File.Exists(path)) return FunctionResult.Fail("Resource not found.");
        var text = await File.ReadAllTextAsync(path, ct);
        return FunctionResult.Ok(text.Length > 60_000 ? text[..60_000] + "…" : text);
    }
}
