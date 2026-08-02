// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using AutoCode.Core.Configuration;
using AutoCode.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.AI;

namespace AutoCode.Studio.ViewModels;

/// <summary>Whether this channel has been proven to work.</summary>
public enum SignalState
{
    /// <summary>Never tested. Not a failure — just unknown.</summary>
    Unknown = 0,

    /// <summary>A request is in flight.</summary>
    Testing = 1,

    /// <summary>The endpoint answered.</summary>
    Verified = 2,

    /// <summary>The endpoint refused, or could not be reached.</summary>
    Faulted = 3,
}

/// <summary>One provider profile, as an editable channel on the panel.</summary>
public sealed partial class ProviderViewModel : ObservableObject
{
    private readonly ProviderProfile _profile;

    public ProviderViewModel(ProviderProfile profile)
    {
        _profile = profile;

        _name = profile.Name;
        _kind = profile.Kind;
        _endpoint = profile.ResolveEndpoint() ?? profile.Endpoint ?? "";
        _apiKey = profile.ApiKey ?? "";
        _model = profile.Model;
        _smallModel = profile.SmallModel ?? "";
        _contextWindow = profile.ContextWindow;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private ProviderKind _kind;
    [ObservableProperty] private string _endpoint;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private string _model;
    [ObservableProperty] private string _smallModel;
    [ObservableProperty] private int _contextWindow;

    [ObservableProperty] private SignalState _state = SignalState.Unknown;
    [ObservableProperty] private string _reading = "not tested";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isDirty;

    /// <summary>True when the key is an <c>env:</c> reference rather than a literal.</summary>
    public bool UsesEnvironmentVariable =>
        ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the key is a literal rather than an <c>env:</c> reference. The view uses this to
    /// warn before writing to a file that is meant to be committed.
    /// </summary>
    public bool HoldsLiteralSecret => ApiKey.Length > 0 && !UsesEnvironmentVariable;

    partial void OnNameChanged(string value) => Touch();
    partial void OnKindChanged(ProviderKind value) => Touch();
    partial void OnEndpointChanged(string value) => Touch();
    partial void OnModelChanged(string value) => Touch();
    partial void OnSmallModelChanged(string value) => Touch();
    partial void OnContextWindowChanged(int value) => Touch();

    partial void OnApiKeyChanged(string value)
    {
        Touch();
        OnPropertyChanged(nameof(UsesEnvironmentVariable));
        OnPropertyChanged(nameof(HoldsLiteralSecret));
    }

    /// <summary>Any edit invalidates a previous verification — the reading described a different config.</summary>
    private void Touch()
    {
        IsDirty = true;

        if (State == SignalState.Verified)
        {
            State = SignalState.Unknown;
            Reading = "changed since last test";
            Detail = "";
        }
    }

    /// <summary>Materialises the edits back into a profile the CLI can consume.</summary>
    public ProviderProfile ToProfile()
    {
        var profile = _profile.Clone();

        profile.Name = Name.Trim();
        profile.Kind = Kind;
        profile.Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim();
        profile.ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        profile.Model = Model.Trim();
        profile.SmallModel = string.IsNullOrWhiteSpace(SmallModel) ? null : SmallModel.Trim();

        if (ContextWindow > 0)
            profile.ContextWindow = ContextWindow;

        return profile;
    }

    /// <summary>
    /// Sends a real request and reports what came back.
    ///
    /// EN: this is the whole point of the app. Validating that a form is filled in proves nothing;
    /// only a round trip distinguishes a working configuration from a plausible-looking one, and
    /// most misconfigurations — wrong endpoint suffix, revoked key, model that does not exist —
    /// are indistinguishable until you actually ask.
    /// ID: inilah inti aplikasinya. Memvalidasi bahwa formulir terisi tidak membuktikan apa pun;
    /// hanya permintaan sungguhan yang membedakan konfigurasi yang bekerja dari yang sekadar
    /// tampak benar.
    /// </summary>
    public async Task TestAsync(CancellationToken cancellationToken)
    {
        State = SignalState.Testing;
        Reading = "connecting…";
        Detail = "";

        var profile = ToProfile();

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            State = SignalState.Faulted;
            Reading = "no model set";
            Detail = "Enter a model id before testing.";
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = ProviderFactory.CreateChatClient(profile);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 16 },
                timeout.Token).ConfigureAwait(false);

            stopwatch.Stop();

            var reply = response.Text.Trim().ReplaceLineEndings(" ");
            var tokens = response.Usage?.TotalTokenCount;

            State = SignalState.Verified;
            Reading = tokens is > 0
                ? $"verified · {stopwatch.ElapsedMilliseconds} ms · {tokens} tok"
                : $"verified · {stopwatch.ElapsedMilliseconds} ms";

            Detail = reply.Length > 90 ? reply[..90] + "…" : reply;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            State = SignalState.Faulted;
            Reading = "timed out after 45 s";
            Detail = "The endpoint accepted the connection but never answered.";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            State = SignalState.Faulted;
            Reading = $"failed · {stopwatch.ElapsedMilliseconds} ms";
            Detail = Explain(ex);
        }
    }

    /// <summary>
    /// Turns a provider exception into something the user can act on. The raw messages are long
    /// and bury the one fact that matters.
    /// </summary>
    private static string Explain(Exception ex)
    {
        var message = ex.Message;

        if (message.Contains("401") || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return "The API key was rejected. Check it has not been revoked or mistyped.";

        if (message.Contains("404"))
            return "Not found — usually a wrong endpoint path or a model id that does not exist on this provider.";

        if (message.Contains("429"))
            return "Rate limited or out of quota. The key works; the account cannot serve the request right now.";

        if (message.Contains("No connection could be made") ||
            message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Nothing is listening at that endpoint. If this is a local model, start the server first.";
        }

        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return "The hostname does not resolve. Check the endpoint for a typo.";

        if (message.Contains("is not configured"))
            return message;

        return message.Length > 220 ? message[..220] + "…" : message;
    }
}
