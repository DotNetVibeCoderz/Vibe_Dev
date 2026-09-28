using System.Text.Json;
using System.Text.Json.Nodes;
using DotCode.Abstractions;

namespace DotCode.Engine.Configuration;

public enum SettingsScope { User, Project, Local, Cli, Managed }

public sealed record SettingsSource(SettingsScope Scope, string Path, bool Exists);

/// <summary>Loads and deep-merges settings files. Objects merge recursively, permission/allow-style arrays are
/// concatenated (deduplicated), scalars are overridden by the higher-precedence scope.</summary>
public sealed class SettingsLoader
{
    public string Cwd { get; }
    public string ProjectRoot { get; }
    public List<SettingsSource> Sources { get; } = [];
    public List<string> Errors { get; } = [];

    public SettingsLoader(string cwd)
    {
        Cwd = Path.GetFullPath(cwd);
        ProjectRoot = DotCodePaths.FindProjectRoot(Cwd);
    }

    public Settings Load(string? cliSettingsPath = null, string? cliSettingsJson = null)
    {
        var merged = new JsonObject();
        void Apply(SettingsScope scope, string path, bool compat = false)
        {
            var exists = File.Exists(path);
            Sources.Add(new SettingsSource(scope, path, exists));
            if (!exists) return;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: DotCodeJson.DocumentOptions);
                if (node is not JsonObject obj) return;
                if (compat)
                {
                    // Claude-specific keys that don't translate (model aliases, credential helpers).
                    obj.Remove("model");
                    obj.Remove("apiKeyHelper");
                    obj.Remove("statusLine");
                    obj.Remove("outputStyle");
                }
                Merge(merged, obj);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Errors.Add($"{path}: {ex.Message}");
            }
        }

        Apply(SettingsScope.User, DotCodePaths.UserSettings);
        Apply(SettingsScope.Project, Path.Combine(ProjectRoot, DotCodePaths.CompatDirName, "settings.json"), compat: true);
        Apply(SettingsScope.Project, DotCodePaths.ProjectSettings(ProjectRoot));
        Apply(SettingsScope.Local, Path.Combine(ProjectRoot, DotCodePaths.CompatDirName, "settings.local.json"), compat: true);
        Apply(SettingsScope.Local, DotCodePaths.ProjectLocalSettings(ProjectRoot));
        if (cliSettingsPath is not null) Apply(SettingsScope.Cli, DotCodePaths.Resolve(cliSettingsPath, Cwd));
        if (cliSettingsJson is not null)
        {
            try
            {
                if (JsonNode.Parse(cliSettingsJson, documentOptions: DotCodeJson.DocumentOptions) is JsonObject o) Merge(merged, o);
            }
            catch (JsonException ex) { Errors.Add($"--settings: {ex.Message}"); }
        }
        Apply(SettingsScope.Managed, DotCodePaths.ManagedSettings);

        try
        {
            return merged.Deserialize(SettingsJsonContext.Default.Settings) ?? new Settings();
        }
        catch (JsonException ex)
        {
            Errors.Add($"settings: {ex.Message}");
            return new Settings();
        }
    }

    private static readonly HashSet<string> ConcatArrays = ["allow", "deny", "ask", "additionalDirectories", "allowedProviders"];

    public static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject so && target[key] is JsonObject to)
            {
                Merge(to, so);
            }
            else if (value is JsonArray sa && target[key] is JsonArray ta && ConcatArrays.Contains(key))
            {
                var seen = ta.Select(x => x?.ToJsonString()).ToHashSet();
                foreach (var item in sa)
                    if (seen.Add(item?.ToJsonString())) ta.Add(item?.DeepClone());
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>Appends a permission rule to a settings file (used by "Yes, don't ask again").</summary>
    public static void AddPermissionRule(string settingsPath, string list, string rule)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var root = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: DotCodeJson.DocumentOptions) as JsonObject ?? new JsonObject()
            : new JsonObject();
        if (root["permissions"] is not JsonObject perms) root["permissions"] = perms = new JsonObject();
        if (perms[list] is not JsonArray arr) perms[list] = arr = new JsonArray();
        if (!arr.Any(x => x?.GetValue<string>() == rule)) arr.Add((JsonNode?)JsonValue.Create(rule));
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Sets a top-level (dotted path) value in a settings file, e.g. "theme".</summary>
    public static void SetValue(string settingsPath, string dottedKey, JsonNode? value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var root = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: DotCodeJson.DocumentOptions) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var parts = dottedKey.Split('.');
        var cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) cur[parts[i]] = next = new JsonObject();
            cur = next;
        }
        cur[parts[^1]] = value;
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
