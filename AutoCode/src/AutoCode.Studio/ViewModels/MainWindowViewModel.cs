// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Collections.ObjectModel;
using AutoCode.Core.Configuration;
using AutoCode.Studio.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoCode.Studio.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private CancellationTokenSource? _testCancellation;

    public MainWindowViewModel()
    {
        Targets = new ObservableCollection<SettingsTarget>(
            SettingsStore.DiscoverTargets(DiscoverWorkspace()));

        // Open on the file the user is most likely to be editing: the first one that actually
        // declares providers. Defaulting to user settings would show an empty panel to someone
        // who has a perfectly good project configuration a click away.
        _selectedTarget = Targets.FirstOrDefault(t => _store.Load(t.Path).Providers.Count > 0)
                          ?? Targets[0];

        Load();
    }

    public ObservableCollection<SettingsTarget> Targets { get; }

    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    public IReadOnlyList<ProviderKind> Kinds { get; } = Enum.GetValues<ProviderKind>();

    /// <summary>Vendor names offered when adding a channel; anything else can be typed.</summary>
    public IReadOnlyList<string> KnownVendors { get; } = [.. ProviderPresets.All.Keys];

    [ObservableProperty] private SettingsTarget _selectedTarget;
    [ObservableProperty] private ProviderViewModel? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _revealKey;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _addVendor;

    /// <summary>Null hides the key behind bullets; the null character shows it in the clear.</summary>
    public char KeyMaskChar => RevealKey ? '\0' : '•';

    public string RevealLabel => RevealKey ? "hide" : "show";

    partial void OnRevealKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyMaskChar));
        OnPropertyChanged(nameof(RevealLabel));
    }

    /// <summary>Picking a vendor in the rail's combo box adds that channel, then clears the box.</summary>
    partial void OnAddVendorChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        AddProvider(value);
        AddVendor = null;
    }

    [RelayCommand]
    private void ToggleReveal() => RevealKey = !RevealKey;

    /// <summary>
    /// Vendors whose conventional API-key variable is already set in this environment.
    ///
    /// EN: an empty panel is a dead end, and the user almost always already has a key exported —
    /// that is how they were running the CLI before opening this. Offering exactly those vendors
    /// turns the empty state into one click instead of a form to fill in from memory.
    /// ID: panel kosong adalah jalan buntu, dan pengguna hampir selalu sudah punya key di
    /// environment. Menawarkan vendor tersebut mengubah keadaan kosong menjadi satu klik.
    /// </summary>
    public IReadOnlyList<string> DetectedVendors { get; } =
    [
        .. ProviderPresets.All
            .Where(p => p.Value.ApiKey is not null &&
                        p.Value.ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase) &&
                        p.Value.ResolveApiKey() is not null)
            .Select(p => p.Key)
    ];

    public bool HasDetectedVendors => DetectedVendors.Count > 0;

    public string DetectedSummary => DetectedVendors.Count switch
    {
        0 => "",
        1 => $"Found a key for {DetectedVendors[0]} in your environment.",
        _ => $"Found keys for {string.Join(", ", DetectedVendors)} in your environment.",
    };

    public bool IsEmpty => Providers.Count == 0;

    /// <summary>Adds a channel for every vendor whose key is already exported.</summary>
    [RelayCommand]
    private void AddDetected()
    {
        foreach (var vendor in DetectedVendors)
            AddProvider(vendor);

        Status = $"Added {DetectedVendors.Count} channel(s) from your environment. Test them, then save.";
    }

    /// <summary>True when the chosen file is meant to be committed and a literal key would leak.</summary>
    public bool SecretWarning =>
        Selected?.HoldsLiteralSecret == true &&
        SelectedTarget.Path.EndsWith(ConfigurationLoader.SettingsFileName, StringComparison.OrdinalIgnoreCase) &&
        !SelectedTarget.Path.Contains(".local.", StringComparison.OrdinalIgnoreCase) &&
        SelectedTarget.Label.StartsWith("Project", StringComparison.Ordinal);

    partial void OnSelectedTargetChanged(SettingsTarget value)
    {
        Load();
        OnPropertyChanged(nameof(SecretWarning));
    }

    partial void OnSelectedChanged(ProviderViewModel? value) => OnPropertyChanged(nameof(SecretWarning));

    private void Load()
    {
        Providers.Clear();

        var (active, profiles) = _store.Load(SelectedTarget.Path);

        foreach (var profile in profiles.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Providers.Add(new ProviderViewModel(profile));

        Selected = Providers.FirstOrDefault(p => string.Equals(p.Name, active, StringComparison.OrdinalIgnoreCase))
                   ?? Providers.FirstOrDefault();

        Status = Providers.Count == 0
            ? "No providers in this file yet."
            : $"{Providers.Count} provider{(Providers.Count == 1 ? "" : "s")} loaded.";

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void AddProvider(string? vendor)
    {
        // Naming it after a known vendor pulls in that vendor's endpoint, models and pricing;
        // anything else starts as a blank OpenAI-compatible channel.
        var name = string.IsNullOrWhiteSpace(vendor) ? UniqueName("custom") : UniqueName(vendor);

        var profile = ProviderPresets.TryCreate(vendor ?? "") ?? new ProviderProfile
        {
            Kind = ProviderKind.OpenAICompatible,
            Endpoint = "http://localhost:11434/v1",
            Model = "",
        };

        profile.Name = name;

        var channel = new ProviderViewModel(profile) { IsDirty = true };
        Providers.Add(channel);
        Selected = channel;

        OnPropertyChanged(nameof(IsEmpty));
        Status = $"Added '{name}'. Test it before saving.";
    }

    [RelayCommand]
    private void RemoveProvider()
    {
        if (Selected is null)
            return;

        var removed = Selected.Name;
        var index = Providers.IndexOf(Selected);

        Providers.Remove(Selected);
        Selected = Providers.Count == 0 ? null : Providers[Math.Max(0, index - 1)];

        OnPropertyChanged(nameof(IsEmpty));
        Status = $"Removed '{removed}'. Save to apply.";
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Selected is null || IsBusy)
            return;

        IsBusy = true;
        _testCancellation?.Cancel();
        _testCancellation = new CancellationTokenSource();

        try
        {
            await Selected.TestAsync(_testCancellation.Token).ConfigureAwait(true);
            Status = Selected.State == SignalState.Verified
                ? $"'{Selected.Name}' answered."
                : $"'{Selected.Name}' did not answer.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        _testCancellation?.Cancel();
        _testCancellation = new CancellationTokenSource();

        try
        {
            // Sequential rather than parallel: several of these hit the same host, and a burst of
            // simultaneous requests is the fastest way to get rate limited while diagnosing.
            foreach (var provider in Providers)
                await provider.TestAsync(_testCancellation.Token).ConfigureAwait(true);

            var verified = Providers.Count(p => p.State == SignalState.Verified);
            Status = $"{verified} of {Providers.Count} verified.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _store.Save(SelectedTarget.Path, Selected?.Name, Providers.Select(p => p.ToProfile()));

            foreach (var provider in Providers)
                provider.IsDirty = false;

            Status = $"Saved to {SelectedTarget.Path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not write the file: {ex.Message}";
        }
    }

    /// <summary>Shell exports, for keeping keys out of files entirely.</summary>
    public string BuildEnvironmentScript(bool powershell) =>
        SettingsStore.ToEnvironmentScript(Providers.Select(p => p.ToProfile()), powershell);

    private string UniqueName(string basis)
    {
        var name = basis;
        var suffix = 2;

        while (Providers.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{basis}-{suffix++}";

        return name;
    }

    /// <summary>
    /// Finds a repository near the working directory, so the project-scoped targets are offered
    /// when Studio is launched from inside one.
    /// </summary>
    private static string? DiscoverWorkspace()
    {
        try
        {
            var root = ConfigurationLoader.DiscoverWorkspaceRoot(Environment.CurrentDirectory);

            // DiscoverWorkspaceRoot falls back to its input, which for a GUI launched from a menu
            // is wherever the shell happened to be. Only offer project targets on a real marker.
            var isRepository =
                Directory.Exists(Path.Combine(root, ".git")) ||
                Directory.Exists(Path.Combine(root, ".autocode"));

            return isRepository ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
