using System.Collections.Concurrent;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Providers;

namespace DotCode.Engine.Agent;

public sealed record ResolvedModel(IModelProvider Provider, string ProviderName, string Model, ModelCapabilities Capabilities)
{
    public string Qualified => $"{ProviderName}:{Model}";
    public override string ToString() => Qualified;
}

/// <summary>Resolves model references (<c>provider:model</c>, bare ids, aliases and roles such as main/fast/planner/
/// subagent) to provider instances. Providers come from settings plus environment-variable auto-configuration.</summary>
public sealed class ModelRouter
{
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string, IModelProvider> _instances = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProviderConfig> Providers { get; }
    public string DefaultProvider { get; }

    public static readonly string[] Roles = ["main", "fast", "planner", "subagent", "advisor"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = "anthropic:claude-opus-4-5",
        ["sonnet"] = "anthropic:claude-sonnet-4-5",
        ["haiku"] = "anthropic:claude-haiku-4-5",
        ["gpt"] = "openai:gpt-5",
        ["gpt-mini"] = "openai:gpt-5-mini",
        ["gemini"] = "gemini:gemini-2.5-pro",
        ["gemini-flash"] = "gemini:gemini-2.5-flash",
        ["deepseek"] = "deepseek:deepseek-chat",
    };

    public ModelRouter(Settings settings)
    {
        _settings = settings;
        Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ProviderFactory.FromEnvironment()) Providers[k] = v;
        if (settings.Providers is not null)
            foreach (var (k, v) in settings.Providers) Providers[k] = v;
        Providers.TryAdd("mock", new ProviderConfig { Type = "mock" });
        if (settings.AllowedProviders is { Count: > 0 } allowed)
            foreach (var name in Providers.Keys.ToList())
                if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase) && name != "mock") Providers.Remove(name);

        DefaultProvider = Providers.Keys.FirstOrDefault(k => k != "mock") ?? "mock";
    }

    /// <summary>Main model reference from CLI/settings, or a sensible default for the first configured provider.</summary>
    public string DefaultMainModel()
    {
        if (_settings.Model is { Length: > 0 } m) return m;
        if (_settings.Models?.GetValueOrDefault("main") is { Length: > 0 } main) return main;
        if (Providers.TryGetValue(DefaultProvider, out var cfg))
        {
            if (cfg.Models is { Count: > 0 } declared) return $"{DefaultProvider}:{declared[0]}";
            return (cfg.Type ?? DefaultProvider) switch
            {
                "anthropic" => $"{DefaultProvider}:claude-sonnet-4-5",
                "openai" => $"{DefaultProvider}:gpt-5",
                "azure" => $"{DefaultProvider}:gpt-5-mini",
                "gemini" => $"{DefaultProvider}:gemini-2.5-pro",
                "deepseek" => $"{DefaultProvider}:deepseek-chat",
                "ollama" => $"{DefaultProvider}:qwen3-coder",
                _ => $"{DefaultProvider}:default",
            };
        }
        return "mock:echo";
    }

    public string RoleModel(string role, string mainModel) =>
        _settings.Models?.GetValueOrDefault(role) is { Length: > 0 } r ? r : mainModel;

    public ResolvedModel Resolve(string reference, string? mainModel = null)
    {
        reference = reference.Trim();
        if (Roles.Contains(reference, StringComparer.OrdinalIgnoreCase) || reference.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            var main = mainModel ?? DefaultMainModel();
            reference = reference.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? main : RoleModel(reference.ToLowerInvariant(), main);
        }
        if (Aliases.TryGetValue(reference, out var alias))
        {
            // Prefer the alias target's model on whichever configured provider speaks that protocol.
            var aref = ModelRef.Parse(alias, DefaultProvider);
            var provider = Providers.ContainsKey(aref.Provider) ? aref.Provider : Providers.FirstOrDefault(p => p.Value.Type == aref.Provider).Key ?? aref.Provider;
            reference = $"{provider}:{aref.Model}";
        }

        string providerName, model;
        var colon = reference.IndexOf(':');
        if (colon > 0 && Providers.ContainsKey(reference[..colon]))
        {
            providerName = reference[..colon];
            model = reference[(colon + 1)..];
        }
        else if (colon > 0 && ProviderFactory.KnownTypes.Contains(reference[..colon], StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Provider '{reference[..colon]}' is not configured. Add it under \"providers\" in settings or set its API key environment variable.");
        }
        else
        {
            providerName = DefaultProvider;
            model = reference;
        }

        var instance = GetProvider(providerName);
        var caps = instance.GetCapabilities(model);
        if (!caps.Tools) instance = new TextToolProtocolProvider(instance);
        return new ResolvedModel(instance, providerName, model, caps);
    }

    public IModelProvider GetProvider(string name) =>
        _instances.GetOrAdd(name, n => Providers.TryGetValue(n, out var cfg)
            ? ProviderFactory.Create(n, cfg)
            : throw new InvalidOperationException($"Unknown provider '{n}'. Configured: {string.Join(", ", Providers.Keys)}"));

    /// <summary>All models from all providers (declared lists or live API listing; failures are skipped).</summary>
    public async Task<List<ModelInfo>> ListAllModelsAsync(CancellationToken ct)
    {
        var tasks = Providers.Keys.Where(k => k != "mock").Select(async name =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                return (await GetProvider(name).ListModelsAsync(cts.Token).ConfigureAwait(false)).ToList();
            }
            catch (Exception) { return new List<ModelInfo>(); }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.SelectMany(r => r)];
    }

    public IReadOnlyList<string> FallbackChain(string errorCode)
    {
        if (_settings.Fallback is null) return [];
        foreach (var rule in _settings.Fallback)
            if (rule.On.Any(o => o == errorCode || o == "5xx" && errorCode is "server_error" or "overloaded" || o == "*"))
                return rule.Chain;
        return [];
    }
}
