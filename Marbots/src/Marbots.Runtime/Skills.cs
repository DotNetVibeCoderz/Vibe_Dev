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

    /// <summary>Earlier versions of installed skills, newest last: skills-history/&lt;slug&gt;/&lt;utc-stamp&gt;_&lt;version&gt;.</summary>
    public string HistoryDirectory => _options.DataPath("skills-history");

    public IReadOnlyList<SkillInfo> All => _skills;

    /// <summary>Folder of trusted publisher public keys: &lt;publisher&gt;.pem.</summary>
    public string TrustedPublishersDirectory => _options.DataPath("trusted-publishers");

    public IReadOnlyList<TrustedSkillPublisher> TrustedPublishers()
    {
        var list = new List<TrustedSkillPublisher>(_options.TrustedSkillPublishers);
        if (Directory.Exists(TrustedPublishersDirectory))
            foreach (var f in Directory.EnumerateFiles(TrustedPublishersDirectory, "*.pem"))
                list.Add(new TrustedSkillPublisher { Name = Path.GetFileNameWithoutExtension(f), PublicKeyPem = File.ReadAllText(f) });
        return list;
    }

    /// <summary>Adds a trusted publisher key (stored as data/trusted-publishers/&lt;name&gt;.pem).</summary>
    public void TrustPublisher(string name, string publicKeyPem)
    {
        using (var key = System.Security.Cryptography.ECDsa.Create()) key.ImportFromPem(publicKeyPem);
        var slug = Ids.Slug(name);
        if (slug.Length == 0) throw new ArgumentException("Publisher name is required.");
        Directory.CreateDirectory(TrustedPublishersDirectory);
        File.WriteAllText(Path.Combine(TrustedPublishersDirectory, slug + ".pem"), publicKeyPem.Trim() + "\n");
        Refresh();
    }

    public SkillInfo? Find(string name) => _skills.FirstOrDefault(s => !s.Pending && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<TrustedSkillPublisher>? _trusted;

    public void Refresh()
    {
        _trusted = TrustedPublishers();
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
                var v = SkillSigning.Verify(info.Path, _trusted ?? []);
                if (v.Status == SkillSignatureStatus.Invalid)
                {
                    // A signed skill whose files changed is never loaded.
                    _log.LogWarning("Skill {Skill} not loaded: {Detail}", info.Name, v.Detail);
                    continue;
                }
                if (_options.RequireSignedSkills && source == "installed" && v.Status != SkillSignatureStatus.Verified)
                {
                    _log.LogWarning("Skill {Skill} not loaded: unsigned or untrusted ({Detail})", info.Name, v.Detail);
                    continue;
                }
                info.Signature = v.Status.ToString();
                info.Publisher = v.Publisher;
                if (v.Status == SkillSignatureStatus.Verified && source == "installed") info.Trust = "Verified Publisher";
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
            Author = fields.GetValueOrDefault("author") is { Length: > 0 } a ? a : null,
        };
    }

    /// <summary>
    /// Skills a bot may load: its enabled published skills, plus drafts it wrote itself (a trial that gives the
    /// learning evaluation evidence before a human publishes them).
    /// </summary>
    public IReadOnlyList<SkillInfo> ForBot(BotDefinition bot)
    {
        var published = bot.Skills.Contains("*") ? _skills.Where(s => !s.Pending)
            : _skills.Where(s => !s.Pending && bot.Skills.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
        var trials = _skills.Where(s => s.Pending && s.Author == bot.Id && !published.Any(p => p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)));
        return published.Concat(trials).ToList();
    }

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
    public async Task<IReadOnlyList<SkillInfo>> InstallAsync(string source, CancellationToken ct, IReadOnlyCollection<string>? only = null)
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
                if (only is not null && !only.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
                var v = SkillSigning.Verify(Path.GetDirectoryName(skillFile)!, TrustedPublishers());
                if (v.Status == SkillSignatureStatus.Invalid) throw new InvalidOperationException($"Skill {info.Name} was modified after signing: {v.Detail}.");
                if (_options.RequireSignedSkills && v.Status != SkillSignatureStatus.Verified)
                    throw new InvalidOperationException($"Skill {info.Name} is {(v.Status == SkillSignatureStatus.Unsigned ? "not signed" : v.Detail)}; this server only installs skills signed by a trusted publisher.");
                var dest = Path.Combine(InstalledDirectory, name);
                if (Directory.Exists(dest)) { Archive(name); TryDelete(dest); }
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

    public async Task<SkillInfo> CreateAsync(string name, string description, string body, bool pending, CancellationToken ct, string? author = null)
    {
        var slug = Ids.Slug(name);
        if (slug.Length == 0) throw new ArgumentException("Skill name is required.");
        var dir = Path.Combine(pending ? PendingDirectory : InstalledDirectory, slug);
        Directory.CreateDirectory(dir);
        var authorLine = author is null ? "" : $"author: {author}\n";
        var content = $"---\nname: {slug}\ndescription: {description.Replace('\n', ' ')}\nversion: 1.0.0\n{authorLine}---\n\n{body.Trim()}\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), content, ct);
        Refresh();
        return Read(Path.Combine(dir, "SKILL.md"), pending ? "Unverified" : "Local", pending ? "auto-learn" : "installed", pending);
    }

    public bool ApprovePending(string name)
    {
        var slug = Ids.Slug(name);
        var src = Path.Combine(PendingDirectory, slug);
        if (!Directory.Exists(src)) return false;
        var dest = Path.Combine(InstalledDirectory, slug);
        string? replaced = null;
        if (File.Exists(Path.Combine(dest, "SKILL.md")))
        {
            replaced = Read(Path.Combine(dest, "SKILL.md"), "Local", "installed", false).Version;
            Archive(slug);
            TryDelete(dest);
        }
        CopyDirectory(src, dest);
        // A new version of an existing skill gets a higher version number, so its outcomes are tracked separately.
        if (replaced is not null) SetVersion(Path.Combine(dest, "SKILL.md"), NextVersion(replaced));
        TryDelete(src);
        Refresh();
        return true;
    }

    /// <summary>Versions kept for rollback, oldest first.</summary>
    public IReadOnlyList<(string Version, string Path)> History(string name)
    {
        var dir = Path.Combine(HistoryDirectory, Ids.Slug(name));
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir).Order(StringComparer.Ordinal)
            .Select(d => (Read(Path.Combine(d, "SKILL.md"), "Local", "history", false).Version, d)).ToList();
    }

    /// <summary>Restores the previous version of an installed skill; the current one is archived. Returns the restored version.</summary>
    public string? Rollback(string name)
    {
        var slug = Ids.Slug(name);
        var history = History(slug);
        var dest = Path.Combine(InstalledDirectory, slug);
        if (history.Count == 0 || !Directory.Exists(dest)) return null;
        var (version, path) = history[^1];
        Archive(slug);
        TryDelete(dest);
        CopyDirectory(path, dest);
        TryDelete(path);
        Refresh();
        return version;
    }

    private void Archive(string slug)
    {
        var current = Path.Combine(InstalledDirectory, slug);
        if (!File.Exists(Path.Combine(current, "SKILL.md"))) return;
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        CopyDirectory(current, Path.Combine(HistoryDirectory, slug, stamp));
    }

    internal static string NextVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? $"{major}.{minor + 1}.0" : version + ".1";
    }

    private static void SetVersion(string skillFile, string version)
    {
        var lines = File.ReadAllLines(skillFile).ToList();
        var end = lines.Count > 0 && lines[0].Trim() == "---" ? lines.FindIndex(1, l => l.Trim() == "---") : -1;
        if (end < 0) return;
        var at = lines.FindIndex(1, end - 1, l => l.StartsWith("version:", StringComparison.Ordinal));
        if (at >= 0) lines[at] = "version: " + version;
        else lines.Insert(end, "version: " + version);
        File.WriteAllLines(skillFile, lines);
    }

    public bool RejectPending(string name)
    {
        var src = Path.Combine(PendingDirectory, Ids.Slug(name));
        if (!Directory.Exists(src)) return false;
        TryDelete(src);
        Refresh();
        return true;
    }

    /// <summary>
    /// The skill's files as workspace-relative paths under .skills/&lt;name&gt;/ (max 20 MB, no dependency folders).
    /// With <paramref name="workspace"/> they are also copied there.
    /// </summary>
    public static List<(string Relative, string Full)> Materialize(SkillInfo skill, string? workspace)
    {
        var list = new List<(string, string)>();
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(skill.Path, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(skill.Path, f).Replace('\\', '/');
            if (rel.Split('/').Any(p => p is "node_modules" or ".git" or "__pycache__" or ".venv")) continue;
            total += new FileInfo(f).Length;
            if (total > 20 * 1024 * 1024) break;
            var target = $".skills/{Ids.Slug(skill.Name)}/{rel}";
            list.Add((target, f));
            if (workspace is not null)
            {
                var dest = Path.Combine(workspace, target);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
        }
        return list;
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
        var body = await registry.LoadBodyAsync(skill, ct);
        if (SkillRegistry.Materialize(skill, ctx.WorkspacePath).Count > 1)
            body += $"\n\nThe skill's files are copied to .skills/{Ids.Slug(skill.Name)}/ in your workspace; run its scripts from there.";
        return FunctionResult.Ok(body);
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

/// <summary>
/// Curated skills that bots (through Boss Man) may install from chat. Anything else is installed by a person on the
/// Skills page. Built-in and already installed skills are always offered too.
/// </summary>
public static class SkillCatalog
{
    public const string AnthropicRepo = "https://github.com/anthropics/skills.git";

    public sealed record Entry(string Name, string Description, string Source, string Publisher);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("pptx", "Create, edit and analyse PowerPoint decks (layouts, speaker notes, thumbnails).", AnthropicRepo, "Anthropic"),
        new("docx", "Create and edit Word documents with tracked changes, comments and formatting.", AnthropicRepo, "Anthropic"),
        new("xlsx", "Spreadsheets with formulas, formatting, charts and recalculation.", AnthropicRepo, "Anthropic"),
        new("pdf", "Read, fill, merge, split and create PDF files.", AnthropicRepo, "Anthropic"),
        new("frontend-design", "Distinctive, intentional visual design for web and app UIs.", AnthropicRepo, "Anthropic"),
        new("webapp-testing", "Test local web apps with Playwright: screenshots, logs, UI checks.", AnthropicRepo, "Anthropic"),
        new("canvas-design", "Visual art and posters as PNG/PDF using design philosophy.", AnthropicRepo, "Anthropic"),
        new("algorithmic-art", "Generative art with p5.js (seeded randomness, flow fields).", AnthropicRepo, "Anthropic"),
        new("theme-factory", "Apply consistent themes (colours, fonts) to slides, docs and pages.", AnthropicRepo, "Anthropic"),
        new("brand-guidelines", "Apply brand colours and typography to artifacts.", AnthropicRepo, "Anthropic"),
        new("doc-coauthoring", "A structured workflow for co-writing documents and proposals.", AnthropicRepo, "Anthropic"),
        new("internal-comms", "Status reports, updates, newsletters and FAQs in house style.", AnthropicRepo, "Anthropic"),
        new("mcp-builder", "Build high-quality MCP servers (Python or TypeScript).", AnthropicRepo, "Anthropic"),
        new("skill-creator", "Create and improve skills, with evals.", AnthropicRepo, "Anthropic"),
        new("web-artifacts-builder", "Multi-component HTML artifacts with React, Tailwind and shadcn/ui.", AnthropicRepo, "Anthropic"),
        new("slack-gif-creator", "Animated GIFs optimised for Slack.", AnthropicRepo, "Anthropic"),
    ];

    public static Entry? Find(string name) => Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
