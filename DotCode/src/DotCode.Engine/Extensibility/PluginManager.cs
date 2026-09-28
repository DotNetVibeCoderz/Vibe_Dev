using System.Text.Json;
using System.Text.Json.Nodes;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Util;

namespace DotCode.Engine.Extensibility;

/// <summary>Installs and manages plugins (compatible with Claude Code's plugin layout: <c>.claude-plugin/plugin.json</c>
/// plus commands/, agents/, skills/, hooks/hooks.json, .mcp.json) and plugin marketplaces.</summary>
public static class PluginManager
{
    public static string InstalledFile => Path.Combine(DotCodePaths.PluginsDir, "installed.json");
    public static string MarketplacesFile => Path.Combine(DotCodePaths.PluginsDir, "known_marketplaces.json");
    public static string CacheDir => Path.Combine(DotCodePaths.PluginsDir, "cache");
    public static string MarketplacesDir => Path.Combine(DotCodePaths.PluginsDir, "marketplaces");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string? FindManifestPath(string root)
    {
        foreach (var dir in new[] { ".dotcode-plugin", ".claude-plugin" })
        {
            var p = Path.Combine(root, dir, "plugin.json");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static PluginManifest? ReadManifest(string root)
    {
        var path = FindManifestPath(root);
        if (path is null) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.PluginManifest); }
        catch (JsonException) { return null; }
    }

    public static string ExpandRoot(string text, string root)
    {
        var escaped = JsonEncodedText.Encode(root.Replace('\\', '/')).ToString();
        return text.Replace("${CLAUDE_PLUGIN_ROOT}", escaped).Replace("${DOTCODE_PLUGIN_ROOT}", escaped);
    }

    private static JsonObject ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: DotCodeJson.DocumentOptions) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch (JsonException) { return new JsonObject(); }
    }

    private static void WriteJson(string path, JsonObject obj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, obj.ToJsonString(Indented));
    }

    public static List<PluginInfo> ListInstalled(Settings? settings = null)
    {
        var result = new List<PluginInfo>();
        var root = ReadJson(InstalledFile);
        if (root["plugins"] is not JsonObject plugins) return result;
        foreach (var (name, node) in plugins)
        {
            if (node is not JsonObject p) continue;
            var path = p["path"]?.GetValue<string>();
            if (path is null || !Directory.Exists(path)) continue;
            var marketplace = p["marketplace"]?.GetValue<string>();
            var enabled = p["enabled"]?.GetValue<bool>() ?? true;
            var id = marketplace is null ? name : $"{name}@{marketplace}";
            if (settings?.EnabledPlugins is { } ep && (ep.TryGetValue(id, out var e) || ep.TryGetValue(name, out e))) enabled = e;
            var manifest = ReadManifest(path);
            result.Add(new PluginInfo(name, manifest?.Version ?? p["version"]?.GetValue<string>() ?? "0.0.0", manifest?.Description ?? "", path, marketplace, enabled));
        }
        return result;
    }

    /// <summary>Installs from a local directory, a git URL, or <c>name@marketplace</c>.</summary>
    public static PluginInfo Install(string source, string cwd)
    {
        string? marketplace = null;
        string sourceDir;
        var at = source.LastIndexOf('@');
        if (at > 0 && !source.Contains("://", StringComparison.Ordinal) && !source.StartsWith("git@", StringComparison.Ordinal) && !Directory.Exists(DotCodePaths.Resolve(source, cwd)))
        {
            var pluginName = source[..at];
            marketplace = source[(at + 1)..];
            sourceDir = ResolveFromMarketplace(pluginName, marketplace);
        }
        else if (IsGitUrl(source))
        {
            sourceDir = CloneTemp(source);
        }
        else
        {
            sourceDir = DotCodePaths.Resolve(source, cwd);
            if (!Directory.Exists(sourceDir)) throw new InvalidOperationException($"Plugin source not found: {source}");
        }

        var manifest = ReadManifest(sourceDir) ?? throw new InvalidOperationException($"No .dotcode-plugin/plugin.json or .claude-plugin/plugin.json in {sourceDir}");
        var name = manifest.Name ?? Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(CacheDir, name);
        if (!PathsEqual(sourceDir, target))
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            CopyDirectory(sourceDir, target);
        }

        var root = ReadJson(InstalledFile);
        if (root["plugins"] is not JsonObject plugins) root["plugins"] = plugins = new JsonObject();
        plugins[name] = new JsonObject
        {
            ["path"] = target,
            ["source"] = source,
            ["marketplace"] = marketplace,
            ["version"] = manifest.Version ?? "0.0.0",
            ["enabled"] = true,
            ["installedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        WriteJson(InstalledFile, root);
        return new PluginInfo(name, manifest.Version ?? "0.0.0", manifest.Description ?? "", target, marketplace, true);
    }

    public static bool Uninstall(string name)
    {
        var root = ReadJson(InstalledFile);
        if (root["plugins"] is not JsonObject plugins || plugins[name] is not JsonObject p) return false;
        var path = p["path"]?.GetValue<string>();
        plugins.Remove(name);
        WriteJson(InstalledFile, root);
        if (path is not null && DotCodePaths.IsUnder(path, CacheDir) && Directory.Exists(path)) Directory.Delete(path, true);
        return true;
    }

    public static bool SetEnabled(string name, bool enabled)
    {
        var root = ReadJson(InstalledFile);
        if (root["plugins"] is not JsonObject plugins || plugins[name] is not JsonObject p) return false;
        p["enabled"] = enabled;
        WriteJson(InstalledFile, root);
        return true;
    }

    // ---------- marketplaces ----------

    public sealed record MarketplaceEntry(string Name, string Description, string? Version, string Source);
    public sealed record Marketplace(string Name, string Dir, string Source, List<MarketplaceEntry> Plugins);

    public static Marketplace AddMarketplace(string source, string cwd)
    {
        string dir;
        if (IsGitUrl(source))
        {
            var tmp = CloneTemp(source);
            var name0 = ReadMarketplaceName(tmp) ?? Path.GetFileNameWithoutExtension(source.TrimEnd('/'));
            dir = Path.Combine(MarketplacesDir, name0);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            CopyDirectory(tmp, dir);
        }
        else
        {
            dir = DotCodePaths.Resolve(source, cwd);
        }
        var name = ReadMarketplaceName(dir) ?? throw new InvalidOperationException($"No marketplace.json found in {dir} (.dotcode-plugin/ or .claude-plugin/)");
        var root = ReadJson(MarketplacesFile);
        root[name] = new JsonObject { ["source"] = source, ["path"] = dir, ["addedAt"] = DateTimeOffset.UtcNow.ToString("O") };
        WriteJson(MarketplacesFile, root);
        return LoadMarketplace(name, dir, source);
    }

    public static List<Marketplace> ListMarketplaces()
    {
        var result = new List<Marketplace>();
        foreach (var (name, node) in ReadJson(MarketplacesFile))
        {
            if (node is not JsonObject o || o["path"]?.GetValue<string>() is not { } dir || !Directory.Exists(dir)) continue;
            result.Add(LoadMarketplace(name, dir, o["source"]?.GetValue<string>() ?? dir));
        }
        return result;
    }

    public static bool RemoveMarketplace(string name)
    {
        var root = ReadJson(MarketplacesFile);
        if (!root.Remove(name)) return false;
        WriteJson(MarketplacesFile, root);
        return true;
    }

    private static string? MarketplaceManifest(string dir)
    {
        foreach (var d in new[] { ".dotcode-plugin", ".claude-plugin" })
        {
            var p = Path.Combine(dir, d, "marketplace.json");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? ReadMarketplaceName(string dir) =>
        MarketplaceManifest(dir) is { } p ? ReadJson(p)["name"]?.GetValue<string>() : null;

    private static Marketplace LoadMarketplace(string name, string dir, string source)
    {
        var entries = new List<MarketplaceEntry>();
        if (MarketplaceManifest(dir) is { } p && ReadJson(p)["plugins"] is JsonArray arr)
        {
            foreach (var item in arr.OfType<JsonObject>())
            {
                var src = item["source"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject so => so["url"]?.GetValue<string>() ?? (so["repo"]?.GetValue<string>() is { } repo ? $"https://github.com/{repo}.git" : null),
                    _ => null,
                } ?? "./" + item["name"]?.GetValue<string>();
                entries.Add(new MarketplaceEntry(item["name"]?.GetValue<string>() ?? "?", item["description"]?.GetValue<string>() ?? "", item["version"]?.GetValue<string>(), src));
            }
        }
        return new Marketplace(name, dir, source, entries);
    }

    private static string ResolveFromMarketplace(string plugin, string marketplace)
    {
        var mp = ListMarketplaces().FirstOrDefault(m => string.Equals(m.Name, marketplace, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException($"Unknown marketplace '{marketplace}'. Add it with: dotcode plugin marketplace add <path|git-url>");
        var entry = mp.Plugins.FirstOrDefault(e => string.Equals(e.Name, plugin, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Plugin '{plugin}' not found in marketplace '{marketplace}'");
        if (IsGitUrl(entry.Source)) return CloneTemp(entry.Source);
        return Path.GetFullPath(Path.Combine(mp.Dir, entry.Source));
    }

    private static bool IsGitUrl(string s) =>
        s.StartsWith("https://", StringComparison.Ordinal) || s.StartsWith("git@", StringComparison.Ordinal) || s.EndsWith(".git", StringComparison.Ordinal);

    private static string CloneTemp(string url)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dotcode-plugin-" + Guid.NewGuid().ToString("n")[..8]);
        var result = ProcessRunner.TryRun("git", $"clone --depth 1 \"{url}\" \"{tmp}\"", Path.GetTempPath(), 120_000);
        if (result is null || !Directory.Exists(tmp)) throw new InvalidOperationException($"git clone failed for {url}");
        return tmp;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (rel.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel.Contains("node_modules", StringComparison.Ordinal)) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
