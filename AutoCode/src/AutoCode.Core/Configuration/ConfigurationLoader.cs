// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Configuration;
using Microsoft.Extensions.Configuration;
using SysConfigurationManager = System.Configuration.ConfigurationManager;

namespace AutoCode.Core.Configuration;

/// <summary>
/// Builds <see cref="AutoCodeOptions"/> from every supported source.
///
/// EN — precedence, lowest to highest:
///   1. built-in vendor presets
///   2. app.config &lt;appSettings&gt; (keys use <c>AutoCode:</c> paths)
///   3. ~/.autocode/settings.json          (user, machine-wide)
///   4. &lt;workspace&gt;/.autocode/settings.json        (project, committed)
///   5. &lt;workspace&gt;/.autocode/settings.local.json  (personal, gitignored)
///   6. environment variables prefixed <c>AUTOCODE_</c>
///   7. command-line overrides
///
/// ID — urutan prioritas dari terendah ke tertinggi sama seperti di atas; lapisan yang lebih
/// tinggi menimpa yang lebih rendah, sehingga rahasia bisa disimpan di settings.local.json
/// atau environment variable tanpa ikut ter-commit.
/// </summary>
public static class ConfigurationLoader
{
    public const string UserDirectoryName = ".autocode";
    public const string SettingsFileName = "settings.json";
    public const string LocalSettingsFileName = "settings.local.json";

    /// <summary>Absolute path to the per-user Auto Code directory (<c>~/.autocode</c>).</summary>
    public static string UserHome =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
            UserDirectoryName);

    /// <summary>Absolute path to the workspace-local Auto Code directory.</summary>
    public static string WorkspaceHome(string workspaceRoot) =>
        Path.Combine(workspaceRoot, UserDirectoryName);

    /// <summary>
    /// Loads and materialises settings for a workspace.
    /// </summary>
    /// <param name="workspaceRoot">Directory the session is rooted at.</param>
    /// <param name="overrides">Flat key/value pairs from the command line, highest precedence.</param>
    public static AutoCodeOptions Load(string workspaceRoot, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var builder = new ConfigurationBuilder();

        var appConfig = ReadAppConfigAppSettings();
        if (appConfig.Count > 0)
            builder.AddInMemoryCollection(appConfig);

        builder.AddJsonFile(Path.Combine(UserHome, SettingsFileName), optional: true, reloadOnChange: false);
        builder.AddJsonFile(Path.Combine(WorkspaceHome(workspaceRoot), SettingsFileName), optional: true, reloadOnChange: false);
        builder.AddJsonFile(Path.Combine(WorkspaceHome(workspaceRoot), LocalSettingsFileName), optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables("AUTOCODE_");

        if (overrides is { Count: > 0 })
            builder.AddInMemoryCollection(overrides);

        var configuration = builder.Build();
        var options = new AutoCodeOptions();

        // "AutoCode" section is optional — settings may also sit at the document root.
        var section = configuration.GetSection("AutoCode");
        (section.Exists() ? section : (IConfiguration)configuration).Bind(options);

        NormalizeProviders(options);
        ApplyWellKnownEnvironmentFallbacks(options);

        return options;
    }

    /// <summary>
    /// Reads <c>&lt;appSettings&gt;</c> from app.config / autocode.dll.config and reshapes the keys into
    /// configuration paths. Both <c>AutoCode:Providers:openai:Model</c> and the flatter
    /// <c>AutoCode.Providers.openai.Model</c> spellings are accepted.
    /// </summary>
    private static Dictionary<string, string?> ReadAppConfigAppSettings()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        System.Collections.Specialized.NameValueCollection settings;
        try
        {
            settings = SysConfigurationManager.AppSettings;
        }
        catch (ConfigurationErrorsException)
        {
            // A malformed or absent .config must never stop the CLI from starting.
            return result;
        }

        foreach (var rawKey in settings.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
                continue;

            if (!rawKey.StartsWith("AutoCode", StringComparison.OrdinalIgnoreCase))
                continue;

            var key = rawKey.Replace('.', ':');
            result[key] = settings[rawKey];
        }

        return result;
    }

    /// <summary>
    /// Expands each configured provider over its vendor preset and guarantees the profile carries its own name.
    /// When nothing is configured at all, every preset whose API key resolves is offered.
    /// </summary>
    private static void NormalizeProviders(AutoCodeOptions options)
    {
        if (options.Providers.Count > 0)
        {
            var normalized = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, profile) in options.Providers)
                normalized[name] = ProviderPresets.ApplyPreset(name, profile);

            options.Providers = normalized;
            return;
        }

        foreach (var (name, preset) in ProviderPresets.All)
        {
            var candidate = preset.Clone();
            if (candidate.ResolveApiKey() is not null)
                options.Providers[name] = candidate;
        }
    }

    /// <summary>
    /// Honours the conventional vendor environment variables so <c>OPENAI_API_KEY=… autocode</c>
    /// works with zero configuration, and picks a sensible active provider when none was named.
    /// </summary>
    private static void ApplyWellKnownEnvironmentFallbacks(AutoCodeOptions options)
    {
        var explicitProvider = Environment.GetEnvironmentVariable("AUTOCODE_PROVIDER");
        if (!string.IsNullOrWhiteSpace(explicitProvider))
            options.ActiveProvider = explicitProvider;

        var explicitModel = Environment.GetEnvironmentVariable("AUTOCODE_MODEL");
        if (!string.IsNullOrWhiteSpace(explicitModel))
        {
            var target = options.ResolveActiveProfile();
            if (target is not null)
                target.Model = explicitModel;
        }

        if (string.IsNullOrWhiteSpace(options.ActiveProvider) && options.Providers.Count > 0)
        {
            // Prefer a profile that can actually authenticate.
            var usable = options.Providers.FirstOrDefault(p => p.Value.ResolveApiKey() is not null);
            options.ActiveProvider = usable.Key ?? options.Providers.First().Key;
        }
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a repository or workspace marker.
    /// Falls back to the starting directory when nothing is found.
    /// </summary>
    public static string DiscoverWorkspaceRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                Directory.Exists(Path.Combine(current.FullName, UserDirectoryName)) ||
                File.Exists(Path.Combine(current.FullName, "AUTOCODE.md")) ||
                File.Exists(Path.Combine(current.FullName, "CLAUDE.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(startDirectory);
    }
}
