using System.Diagnostics;
using Marbots.Abstractions;
using Marbots.Sdk;

namespace Marbots.Desktop.Services;

public enum ConnectionState { Disconnected, Connecting, Connected }

/// <summary>
/// The desktop app's link to a Marbots server: an SDK client plus one live event stream that reconnects on its own.
/// Can also start a local server when none is running.
/// </summary>
public sealed class MarbotsConnection : IAsyncDisposable
{
    private CancellationTokenSource? _pump;
    private Process? _localServer;
    private long _lastEventId;

    public MarbotsClient? Client { get; private set; }
    public Uri BaseAddress { get; private set; } = new("http://localhost:5170/");
    public ConnectionState State { get; private set; }
    public string? LastError { get; private set; }
    public SystemInfo? System { get; private set; }

    /// <summary>Raised on a background thread for every live event (including streaming text).</summary>
    public event Action<AgentEvent>? EventReceived;

    /// <summary>Raised on a background thread when <see cref="State"/> changes.</summary>
    public event Action<ConnectionState>? StateChanged;

    public static (Uri Url, string? ApiKey) FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("MARBOTS_URL");
        return (new Uri(string.IsNullOrWhiteSpace(url) ? "http://localhost:5170/" : url.TrimEnd('/') + "/"),
            Environment.GetEnvironmentVariable("MARBOTS_API_KEY"));
    }

    /// <summary>Connects (or reconnects) to <paramref name="url"/> and starts following the event stream.</summary>
    public async Task<bool> ConnectAsync(Uri url, string? apiKey, CancellationToken ct = default)
    {
        await StopPumpAsync();
        Client?.Dispose();
        BaseAddress = url;
        Client = new MarbotsClient(url, apiKey);
        SetState(ConnectionState.Connecting);
        try
        {
            System = await Client.SystemAsync(ct);
            LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or MarbotsApiException or TaskCanceledException)
        {
            LastError = ex.Message;
            SetState(ConnectionState.Disconnected);
            return false;
        }
        _pump = new CancellationTokenSource();
        _ = PumpAsync(Client, _pump.Token);
        SetState(ConnectionState.Connected);
        return true;
    }

    private async Task PumpAsync(MarbotsClient client, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Resume after the last stored event so nothing is missed across reconnects.
                await foreach (var e in client.Events.StreamAsync(null, _lastEventId > 0 ? _lastEventId : null, ct))
                {
                    if (e.Id > 0) _lastEventId = e.Id;
                    if (State != ConnectionState.Connected) SetState(ConnectionState.Connected);
                    delay = TimeSpan.FromSeconds(1);
                    EventReceived?.Invoke(e);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or MarbotsApiException or OperationCanceledException)
            {
                LastError = ex.Message;
            }
            SetState(ConnectionState.Connecting);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Finds a Marbots server next to the app, in the repository build output, or on PATH (<c>marbots-server</c>).
    /// </summary>
    public static string? FindLocalServer()
    {
        string[] names = OperatingSystem.IsWindows() ? ["Marbots.Server.exe", "marbots-server.exe"] : ["Marbots.Server", "marbots-server"];
        var dirs = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "server") };
        // Running from the repository: src/Marbots.Desktop/bin/<cfg>/net10.0 → src/Marbots.Server/bin/<cfg>/net10.0
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Marbots.Server", "bin")))
            {
                foreach (var cfg in new[] { "Release", "Debug" })
                    dirs.Add(Path.Combine(dir.FullName, "Marbots.Server", "bin", cfg, "net10.0"));
                break;
            }
        }
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.SelectMany(d => names.Select(n => Path.Combine(d, n))).FirstOrDefault(File.Exists);
    }

    /// <summary>Starts a local server on <paramref name="url"/> and waits until it answers.</summary>
    public async Task<bool> StartLocalServerAsync(Uri url, CancellationToken ct = default)
    {
        var exe = FindLocalServer();
        if (exe is null)
        {
            LastError = "No Marbots server found next to the app or on PATH. Install it or start it yourself (dotnet run --project src/Marbots.Server).";
            return false;
        }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(url.GetLeftPart(UriPartial.Authority));
        _localServer = Process.Start(psi);
        for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
        {
            if (_localServer is null || _localServer.HasExited)
            {
                LastError = "The local server stopped while starting.";
                return false;
            }
            try
            {
                using var probe = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(2) };
                using var resp = await probe.GetAsync("api/v1/system", ct);
                if ((int)resp.StatusCode is 200 or 401) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct);
        }
        LastError = "The local server did not answer within 30 seconds.";
        return false;
    }

    public bool OwnsLocalServer => _localServer is { HasExited: false };

    private async Task StopPumpAsync()
    {
        if (_pump is null) return;
        await _pump.CancelAsync();
        _pump.Dispose();
        _pump = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopPumpAsync();
        Client?.Dispose();
        if (_localServer is { HasExited: false })
        {
            try { _localServer.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        _localServer?.Dispose();
    }
}
