using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;

namespace Marbots.AgentHost;

/// <summary>
/// marbots-host: enrolls this computer with a Marbots control plane and then executes the bots' environment tools
/// (files, search, shell, install_package, desktop) in local workspaces. The connection is outbound, so the host works
/// behind NAT; it reconnects on its own and answers repeated requests from a result cache (idempotent).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.FirstOrDefault() ?? "help";
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        try
        {
            switch (command)
            {
                case "enroll":
                    return await EnrollAsync(Opt("--server") ?? throw new ArgumentException("--server is required"), Opt("--token") ?? throw new ArgumentException("--token is required"), Opt("--name"));
                case "run":
                    using (var cts = new CancellationTokenSource())
                    {
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                        var config = HostConfig.Load() ?? throw new InvalidOperationException("Not enrolled. Run: marbots-host enroll --server <url> --token <token>");
                        var ui = new HostConsole(Environment.MachineName, config.HostId, config.Server);
                        var screen = ui.RunAsync(cts.Token);
                        await new HostAgent(config, ui).RunAsync(cts.Token);
                        await screen;
                    }
                    return 0;
                case "status":
                    HostConsole.Status(HostConfig.Load(), Capabilities.Detect());
                    return 0;
                case "capabilities":
                    Console.WriteLine(string.Join(", ", Capabilities.Detect()));
                    foreach (var g in Capabilities.Gpus) Console.WriteLine($"GPU: {g.Name} ({g.Vendor}, {g.Api}, {g.MemoryMb / 1024.0:0.#} GB)");
                    return 0;
                default:
                    HostConsole.Help();
                    return command == "help" ? 0 : 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
        {
            HostConsole.Error(ex.Message);
            return 1;
        }
    }

    private static async Task<int> EnrollAsync(string server, string token, string? name)
    {
        var baseUri = new Uri(server.TrimEnd('/') + "/");
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        var hello = Capabilities.Hello(name ?? Environment.MachineName);
        HostConsole.Banner($"joining {baseUri.Authority}");
        HostConsole.Step(true, $"This computer can do: {string.Join(", ", hello.Capabilities)}");
        using var resp = await http.PostAsJsonAsync("api/v1/hosts/enroll", new HostEnrollmentRequest(token, hello), MarbotsJsonContext.Default.HostEnrollmentRequest);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Enrollment failed: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        var result = (await resp.Content.ReadFromJsonAsync(MarbotsJsonContext.Default.HostEnrollmentResult))!;
        new HostConfig { Server = baseUri.ToString(), HostId = result.HostId, Secret = result.Secret }.Save();
        HostConsole.Step(true, $"Enrolled as {result.HostId} with {baseUri} (server {result.ServerVersion})");
        HostConsole.Step(true, "The secret is stored for this user only. Start with: marbots-host run");
        return 0;
    }
}

/// <summary>host.json in %LOCALAPPDATA%\Marbots\Host (or ~/.local/share/marbots/host). The secret is DPAPI-protected on Windows.</summary>
internal sealed class HostConfig
{
    public string Server { get; set; } = "";
    public string HostId { get; set; } = "";
    public string Secret { get; set; } = "";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OperatingSystem.IsWindows() ? "Marbots" : "marbots", OperatingSystem.IsWindows() ? "Host" : "host");
    public static string WorkspacesDir => Path.Combine(Dir, "workspaces");
    private static string FilePath => Path.Combine(Dir, "host.json");

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var stored = new StoredConfig(Server, HostId, Protect(Secret));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, HostJson.Default.StoredConfig));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static HostConfig? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), HostJson.Default.StoredConfig);
        return s is null ? null : new HostConfig { Server = s.Server, HostId = s.HostId, Secret = Unprotect(s.Secret) };
    }

    private static string Protect(string secret) => OperatingSystem.IsWindows()
        ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser))
        : secret;

    private static string Unprotect(string stored) => OperatingSystem.IsWindows() && stored.StartsWith("dpapi:", StringComparison.Ordinal)
        ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser))
        : stored;
}

internal sealed record StoredConfig(string Server, string HostId, string Secret);

[System.Text.Json.Serialization.JsonSerializable(typeof(StoredConfig))]
internal sealed partial class HostJson : System.Text.Json.Serialization.JsonSerializerContext;

internal static class Capabilities
{
    public static string Version => typeof(Capabilities).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static List<string> Detect()
    {
        var caps = new List<string> { "shell", "files" };
        // Desktop needs an interactive session (session 0 is services/SSH without a desktop).
        if (OperatingSystem.IsWindows() && Process.GetCurrentProcess().SessionId != 0) caps.Add("desktop");
        foreach (var (cap, exe) in new[] { ("docker", "docker"), ("dotnet", "dotnet"), ("node", "node"), ("python", OperatingSystem.IsWindows() ? "python" : "python3"), ("git", "git"), ("java", "java") })
            if (Prerequisites.OnPath(exe)) caps.Add(cap);
        if (caps.Contains("docker") && !DockerRunning()) caps.Remove("docker");
        caps.AddRange(Prerequisites.Managers().Select(m => "pkg:" + m));
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright"))
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright")))
            caps.Add("playwright");
        caps.AddRange(GpuDetector.Capabilities(Gpus));
        return caps;
    }

    private static readonly Lazy<List<GpuInfo>> _gpus = new(GpuDetector.Detect);
    public static List<GpuInfo> Gpus => _gpus.Value;

    private static bool DockerRunning()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Environment = { ["PATH"] = Prerequisites.CurrentPath() } })!;
            return p.WaitForExit(15000) && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static HostHello Hello(string name)
    {
        var gc = GC.GetGCMemoryInfo();
        return new HostHello
        {
            Name = name, AgentVersion = Version, Os = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount, TotalMemoryMb = gc.TotalAvailableMemoryBytes / 1024 / 1024,
            Capabilities = Detect(),
            Gpus = Gpus,
            Functions = KernelCatalog.CreateDefault().Where(f => HostProtocol.RemotePacks.Contains(f.Descriptor.Pack)).Select(f => f.Descriptor.Name).ToList(),
        };
    }
}

internal sealed class HostAgent(HostConfig config, HostConsole ui)
{
    private readonly Dictionary<string, IKernelFunction> _functions = KernelCatalog.CreateDefault()
        .Where(f => HostProtocol.RemotePacks.Contains(f.Descriptor.Pack)).ToDictionary(f => f.Descriptor.Name);
    private readonly ConcurrentDictionary<string, Task<FunctionResult>> _inFlight = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancels = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private readonly ConcurrentDictionary<string, FunctionResult> _cache = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly IServiceProvider _services = new EmptyServices();
    private ClientWebSocket? _socket;
    private int _running;

    private static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        try
        {
            Directory.CreateDirectory(HostConfig.Dir);
            File.AppendAllText(Path.Combine(HostConfig.Dir, "host.log"), line + Environment.NewLine);
        }
        catch (IOException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        Log($"marbots-host {Capabilities.Version} → {config.Server} as {config.HostId}");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ui.SetLink(LinkState.Connecting);
                await ServeOnceAsync(ct);
                delay = TimeSpan.FromSeconds(1);
                ui.SetLink(LinkState.Offline, "the server closed the connection");
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                Log("connection lost: " + ex.Message);
                ui.SetLink(LinkState.Offline, ex.Message.Length > 60 ? ex.Message[..60] + "..." : ex.Message);
            }
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }
    }

    private async Task ServeOnceAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(HostProtocol.HostIdHeader, config.HostId);
        socket.Options.SetRequestHeader(HostProtocol.HostSecretHeader, config.Secret);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = new UriBuilder(new Uri(new Uri(config.Server), "api/v1/hosts/connect")) { Scheme = config.Server.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws" }.Uri;
        await socket.ConnectAsync(uri, ct);
        _socket = socket;
        Log("connected");
        var hello = Capabilities.Hello(Environment.MachineName);
        ui.SetCapabilities(hello.Capabilities);
        ui.SetLink(LinkState.Connected);
        ui.Note($"Connected to {uri.Authority}");
        await SendAsync(new HostFrame { Type = HostProtocol.Frames.Hello, Hello = hello }, ct);
        using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(beat.Token);
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, ct);
                if (frame is null) break;
                _ = Task.Run(() => HandleAsync(frame, ct), ct);
            }
        }
        finally
        {
            await beat.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
            _socket = null;
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var lastAt = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            var gc = GC.GetGCMemoryInfo();
            using var proc = Process.GetCurrentProcess();
            var cpu = proc.TotalProcessorTime;
            var now = DateTime.UtcNow;
            var pct = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (now - lastAt).TotalMilliseconds * Environment.ProcessorCount) * 100;
            (lastCpu, lastAt) = (cpu, now);
            var drive = new DriveInfo(Path.GetPathRoot(HostConfig.Dir)!);
            var (gpuPct, gpuFree) = Capabilities.Gpus.Any(g => g.Vendor == "NVIDIA") ? GpuDetector.Sample() : (null, null);
            var metrics = new HostMetrics
            {
                CpuPercent = Math.Round(pct, 1), RunningCalls = _running,
                FreeMemoryMb = (gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes) / 1024 / 1024,
                FreeDiskMb = drive.IsReady ? drive.AvailableFreeSpace / 1024 / 1024 : 0,
                GpuPercent = gpuPct, FreeGpuMemoryMb = gpuFree,
            };
            ui.SetMetrics(metrics);
            await SendAsync(new HostFrame { Type = HostProtocol.Frames.Heartbeat, Metrics = metrics }, ct);
        }
    }

    private async Task HandleAsync(HostFrame frame, CancellationToken ct)
    {
        try
        {
            switch (frame.Type)
            {
                case HostProtocol.Frames.Invoke when frame.Invoke is { } invoke && frame.RequestId is { } id:
                    var result = await InvokeOnceAsync(id, invoke, ct);
                    await SendAsync(new HostFrame { Type = HostProtocol.Frames.Result, RequestId = id, Result = result }, ct);
                    break;
                case HostProtocol.Frames.Cancel when frame.RequestId is { } cancelId:
                    if (_cancels.TryGetValue(cancelId, out var c)) await c.CancelAsync();
                    break;
                case HostProtocol.Frames.ListFiles:
                    await SendAsync(new HostFrame { Type = HostProtocol.Frames.ListFiles, RequestId = frame.RequestId, Files = ListFiles(Workspace(frame.Workspace)) }, ct);
                    break;
                case HostProtocol.Frames.PutFiles:
                    var root = Workspace(frame.Workspace);
                    foreach (var f in frame.Upload ?? [])
                    {
                        var dest = WorkspacePaths.Resolve(root, f.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await File.WriteAllBytesAsync(dest, Convert.FromBase64String(f.Data), ct);
                    }
                    await SendAsync(new HostFrame { Type = HostProtocol.Frames.PutFiles, RequestId = frame.RequestId }, ct);
                    break;
                case HostProtocol.Frames.ReadFile:
                    var full = WorkspacePaths.Resolve(Workspace(frame.Workspace), frame.Path);
                    await SendAsync(File.Exists(full)
                        ? new HostFrame { Type = HostProtocol.Frames.ReadFile, RequestId = frame.RequestId, Data = Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct)) }
                        : new HostFrame { Type = HostProtocol.Frames.ReadFile, RequestId = frame.RequestId, Error = "File not found." }, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or WebSocketException)
        {
            try { await SendAsync(new HostFrame { Type = HostProtocol.Frames.Error, RequestId = frame.RequestId, Error = ex.Message }, ct); }
            catch (WebSocketException) { }
        }
    }

    /// <summary>Runs a request once; a repeated request id (after a reconnect) gets the same task or cached result.</summary>
    private Task<FunctionResult> InvokeOnceAsync(string requestId, HostInvoke invoke, CancellationToken ct)
    {
        if (_cache.TryGetValue(requestId, out var cached)) return Task.FromResult(cached);
        return _inFlight.GetOrAdd(requestId, key => Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cancels[requestId] = cts;
            Interlocked.Increment(ref _running);
            ui.Started(key, invoke);
            var result = FunctionResult.Fail("Cancelled.");
            try
            {
                result = await ExecuteAsync(invoke, cts.Token);
                _cache[requestId] = result;
                _cacheOrder.Enqueue(requestId);
                while (_cacheOrder.Count > 256 && _cacheOrder.TryDequeue(out var old)) _cache.TryRemove(old, out _);
                return result;
            }
            catch (OperationCanceledException)
            {
                return result;
            }
            finally
            {
                ui.Finished(key, result);
                Interlocked.Decrement(ref _running);
                _cancels.TryRemove(requestId, out _);
                _inFlight.TryRemove(requestId, out _);
            }
        }, CancellationToken.None));
    }

    private async Task<FunctionResult> ExecuteAsync(HostInvoke invoke, CancellationToken ct)
    {
        if (!_functions.TryGetValue(invoke.Function, out var fn)) return FunctionResult.Fail($"This host has no tool '{invoke.Function}'.");
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoke.Arguments) ? "{}" : invoke.Arguments);
        var call = new FunctionCall(invoke.CallId, invoke.Function, args.RootElement.Clone());
        var ctx = new FunctionExecutionContext
        {
            Bot = new BotDefinition { Id = invoke.BotId, Name = invoke.BotName, Container = invoke.Container },
            TaskId = invoke.TaskId, ThreadId = invoke.ThreadId, WorkspacePath = Workspace(invoke.Workspace), Services = _services,
        };
        Log($"{invoke.BotName}: {invoke.Function} in {invoke.Workspace}");
        return await fn.InvokeAsync(call, ctx, ct);
    }

    private static string Workspace(string? slug)
    {
        var safe = Ids.Slug(slug ?? "") is { Length: > 0 } s ? s : "default";
        var dir = Path.Combine(HostConfig.WorkspacesDir, safe);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", ".marbots", ".venv", "__pycache__" };

    private static List<HostFileEntry> ListFiles(string root)
    {
        var list = new List<HostFileEntry>();
        void Walk(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(f);
                list.Add(new HostFileEntry(Path.GetRelativePath(root, f).Replace('\\', '/'), info.Length, info.LastWriteTimeUtc));
                if (list.Count >= 2000) return;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!Noise.Contains(Path.GetFileName(d))) Walk(d);
        }
        Walk(root);
        return list;
    }

    private async Task SendAsync(HostFrame frame, CancellationToken ct)
    {
        var socket = _socket ?? throw new WebSocketException("not connected");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MarbotsJsonContext.Default.HostFrame);
        await _sendLock.WaitAsync(ct);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private static async Task<HostFrame?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize(ms.GetBuffer().AsSpan(0, (int)ms.Length), MarbotsJsonContext.Default.HostFrame);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
