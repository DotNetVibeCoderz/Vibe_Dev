// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Configuration;

namespace AutoCode.Studio.Services;

/// <summary>A settings file Studio can read from and write to.</summary>
public sealed record SettingsTarget(string Label, string Path, string Note)
{
    public override string ToString() => Label;
}

/// <summary>
/// Reads and writes the provider section of an Auto Code settings file.
///
/// EN: edits are surgical. The file is loaded as a JSON tree and only <c>activeProvider</c> and
/// <c>providers</c> are replaced, because a settings file is usually far more than provider
/// credentials — permissions, hooks, MCP servers, verify commands. A GUI that round-trips a typed
/// object would silently delete every one of those the moment it saved.
/// ID: penyuntingan dilakukan secara bedah. Berkas dimuat sebagai pohon JSON dan hanya
/// <c>activeProvider</c> serta <c>providers</c> yang diganti, karena berkas settings biasanya juga
/// berisi permissions, hooks, dan server MCP. GUI yang menulis ulang seluruh objek akan menghapus
/// semuanya secara diam-diam.
/// </summary>
public sealed class SettingsStore
{
    // The CLI reads settings through Microsoft.Extensions.Configuration, which maps "OpenAICompatible"
    // onto the enum without being asked. System.Text.Json does not, so a settings file written for
    // the CLI would fail to parse here — and a config editor that cannot read its own format is
    // worse than none at all.
    private static readonly System.Text.Json.Serialization.JsonStringEnumConverter EnumConverter = new();

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { EnumConverter },
    };

    /// <summary>The files Studio offers to write to, in the order the docs recommend.</summary>
    public static IReadOnlyList<SettingsTarget> DiscoverTargets(string? workspaceRoot)
    {
        var targets = new List<SettingsTarget>
        {
            new("User settings",
                System.IO.Path.Combine(ConfigurationLoader.UserHome, ConfigurationLoader.SettingsFileName),
                "Applies to every project on this machine."),
        };

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var home = ConfigurationLoader.WorkspaceHome(workspaceRoot);

            targets.Add(new SettingsTarget("Project settings",
                System.IO.Path.Combine(home, ConfigurationLoader.SettingsFileName),
                "Committed with the repository. Keep secrets out of this one."));

            targets.Add(new SettingsTarget("Project settings (local)",
                System.IO.Path.Combine(home, ConfigurationLoader.LocalSettingsFileName),
                "Gitignored. The right place for a literal API key."));
        }

        return targets;
    }

    /// <summary>
    /// Profiles that could not be parsed, kept verbatim so saving cannot delete them.
    ///
    /// EN: an editor that silently drops what it does not understand is a data-loss bug waiting to
    /// happen — load, fail quietly, save, and the profile is gone from the user's file. Anything
    /// unrecognised is carried through untouched instead.
    /// ID: editor yang diam-diam membuang apa yang tidak dipahaminya adalah bug kehilangan data.
    /// Profil yang tidak dikenali dibawa serta apa adanya, bukan dihapus.
    /// </summary>
    private readonly Dictionary<string, JsonNode> _passthrough = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of profiles that were preserved without being understood.</summary>
    public IReadOnlyCollection<string> UnparsedProfiles => _passthrough.Keys;

    /// <summary>Loads the providers declared in a file, expanded over their vendor presets.</summary>
    public (string? ActiveProvider, Dictionary<string, ProviderProfile> Providers) Load(string path)
    {
        var providers = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);
        _passthrough.Clear();

        if (!File.Exists(path))
            return (null, providers);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return (null, providers);
        }

        // Settings may sit at the document root or under an "AutoCode" section; both are valid.
        var section = root?["AutoCode"] as JsonObject ?? root as JsonObject;

        if (section is null)
            return (null, providers);

        var active = section["activeProvider"]?.GetValue<string>();

        if (section["providers"] is JsonObject declared)
        {
            foreach (var (name, node) in declared)
            {
                if (node is null)
                    continue;

                try
                {
                    var profile = node.Deserialize<ProviderProfile>(ReadOptions) ?? new ProviderProfile();
                    providers[name] = ProviderPresets.ApplyPreset(name, profile);
                }
                catch (JsonException)
                {
                    // Keep it verbatim. It will be written back exactly as it came in, so a profile
                    // Studio cannot model is preserved rather than quietly deleted on the next save.
                    _passthrough[name] = node.DeepClone();
                }
            }
        }

        return (active, providers);
    }

    /// <summary>
    /// Writes the providers back, leaving every other setting in the file exactly as it was.
    /// </summary>
    public void Save(string path, string? activeProvider, IEnumerable<ProviderProfile> providers)
    {
        JsonObject root;

        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                // Refusing here would strand the user with a file they cannot fix from the GUI.
                // Starting fresh is recoverable; the old file is kept as .bak below.
                File.Copy(path, path + ".bak", overwrite: true);
                root = [];
            }
        }
        else
        {
            root = [];
        }

        var nested = root["AutoCode"] as JsonObject;
        var section = nested ?? root;

        if (!string.IsNullOrWhiteSpace(activeProvider))
            section["activeProvider"] = activeProvider;

        var bag = new JsonObject();

        foreach (var profile in providers)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                continue;

            bag[profile.Name] = JsonSerializer.SerializeToNode(Trim(profile), WriteOptions);
        }

        // Anything Studio could not parse goes back untouched, unless the user has since defined a
        // profile with the same name — in which case theirs wins.
        foreach (var (name, node) in _passthrough)
        {
            if (!bag.ContainsKey(name))
                bag[name] = node.DeepClone();
        }

        section["providers"] = bag;

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Write-then-move, so an interrupted save cannot truncate a working settings file.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Renders the profiles as shell exports, for users who would rather keep keys out of files.
    /// </summary>
    public static string ToEnvironmentScript(IEnumerable<ProviderProfile> providers, bool powershell)
    {
        var builder = new System.Text.StringBuilder();

        builder.Append(powershell ? "# Auto Code — PowerShell\n" : "# Auto Code — bash/zsh\n");

        foreach (var profile in providers)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                continue;

            var prefix = $"AUTOCODE_PROVIDERS__{profile.Name.ToUpperInvariant().Replace('-', '_')}__";

            void Emit(string key, string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                builder.Append(powershell
                    ? $"$env:{prefix}{key} = \"{value}\"\n"
                    : $"export {prefix}{key}=\"{value}\"\n");
            }

            builder.Append('\n');
            Emit("KIND", profile.Kind.ToString());
            Emit("ENDPOINT", profile.Endpoint);
            Emit("MODEL", profile.Model);
            Emit("SMALLMODEL", profile.SmallModel);

            // The key is emitted resolved, because an "env:NAME" indirection inside an environment
            // variable would just be a string the loader never expands twice.
            Emit("APIKEY", profile.ResolveApiKey());
        }

        return builder.ToString();
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { EnumConverter },
    };

    /// <summary>
    /// Drops string values that match the vendor preset, so a saved file stays readable instead of
    /// restating every default.
    ///
    /// EN: only nullable strings are trimmed. Numeric fields are left alone deliberately — writing
    /// a sentinel zero for "same as the preset" would produce <c>contextWindow: 0</c>, which reads
    /// back as a real setting and disables the compaction threshold.
    /// ID: hanya string yang dipangkas. Field numerik sengaja dibiarkan, karena menulis nol sebagai
    /// penanda "sama dengan preset" akan terbaca sebagai nilai sungguhan saat dimuat kembali.
    /// </summary>
    private static ProviderProfile Trim(ProviderProfile profile)
    {
        var preset = ProviderPresets.TryCreate(profile.Name);

        if (preset is null)
            return profile;

        var trimmed = profile.Clone();

        if (trimmed.Endpoint == preset.Endpoint) trimmed.Endpoint = null;
        if (trimmed.SmallModel == preset.SmallModel) trimmed.SmallModel = null;
        if (trimmed.EmbeddingModel == preset.EmbeddingModel) trimmed.EmbeddingModel = null;

        return trimmed;
    }
}
