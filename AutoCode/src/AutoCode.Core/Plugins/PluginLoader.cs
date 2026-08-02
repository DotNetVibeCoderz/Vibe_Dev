// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using AutoCode.Core.Agents;
using AutoCode.Core.Configuration;
using AutoCode.Core.Skills;
using AutoCode.Core.Utilities;

namespace AutoCode.Core.Plugins;

/// <summary>Everything a plugin directory contributed.</summary>
public sealed record LoadedPlugin(
    string Name,
    string Version,
    string Directory,
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<AgentDefinition> Agents,
    IReadOnlyDictionary<string, List<HookOptions>> Hooks,
    IReadOnlyDictionary<string, McpServerOptions> McpServers);

/// <summary>
/// Loads plugins: self-contained bundles of skills, subagents, hooks and MCP servers.
///
/// EN: a plugin is just a directory with an <c>autocode-plugin.json</c> manifest and the same
/// <c>skills/</c> and <c>agents/</c> layout the workspace itself uses. Nothing is compiled or
/// executed at load time — a plugin contributes declarations, and its hooks only run when their
/// event fires and the user's settings have already been consulted.
/// ID: plugin hanyalah direktori berisi manifest <c>autocode-plugin.json</c> dengan struktur
/// <c>skills/</c> dan <c>agents/</c> yang sama seperti workspace. Tidak ada kode yang dieksekusi saat
/// pemuatan; plugin hanya menyumbang deklarasi.
/// </summary>
public static class PluginLoader
{
    public const string ManifestName = "autocode-plugin.json";

    public static IReadOnlyList<LoadedPlugin> Load(string workspaceRoot, AutoCodeOptions options)
    {
        var plugins = new List<LoadedPlugin>();

        foreach (var directory in CandidateDirectories(workspaceRoot, options))
        {
            if (!Directory.Exists(directory))
                continue;

            // A directory may itself be a plugin, or be a folder of plugins.
            if (File.Exists(Path.Combine(directory, ManifestName)))
            {
                if (TryLoad(directory) is { } single)
                    plugins.Add(single);

                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (TryLoad(child) is { } loaded)
                    plugins.Add(loaded);
            }
        }

        return plugins;
    }

    /// <summary>Merges a plugin's contributions into the session's settings.</summary>
    public static void Apply(LoadedPlugin plugin, AutoCodeOptions options)
    {
        foreach (var (eventName, hooks) in plugin.Hooks)
        {
            if (!options.Hooks.TryGetValue(eventName, out var existing))
                options.Hooks[eventName] = existing = [];

            existing.AddRange(hooks);
        }

        foreach (var (name, server) in plugin.McpServers)
            options.McpServers.TryAdd(name, server);
    }

    private static IEnumerable<string> CandidateDirectories(string workspaceRoot, AutoCodeOptions options)
    {
        yield return Path.Combine(ConfigurationLoader.UserHome, "plugins");
        yield return Path.Combine(ConfigurationLoader.WorkspaceHome(workspaceRoot), "plugins");

        foreach (var configured in options.Plugins)
            yield return WorkspacePath.Resolve(workspaceRoot, configured);
    }

    private static LoadedPlugin? TryLoad(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestName);

        if (!File.Exists(manifestPath))
            return null;

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (manifest is null)
            return null;

        var name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileName(directory) : manifest.Name;

        return new LoadedPlugin(
            Name: name,
            Version: manifest.Version ?? "0.0.0",
            Directory: directory,
            Skills: [.. SkillLoader.LoadFrom(Path.Combine(directory, "skills"))],
            Agents: [.. AgentDefinitionLoader.LoadFrom(Path.Combine(directory, "agents"))],
            Hooks: manifest.Hooks ?? new Dictionary<string, List<HookOptions>>(),
            McpServers: manifest.McpServers ?? new Dictionary<string, McpServerOptions>());
    }

    private sealed class PluginManifest
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, List<HookOptions>>? Hooks { get; set; }
        public Dictionary<string, McpServerOptions>? McpServers { get; set; }
    }
}
