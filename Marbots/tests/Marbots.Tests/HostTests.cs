using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;
using Marbots.Providers;
using Marbots.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Marbots.Tests;

public class HostUnitTests
{
    private static PlacementService.HostCandidate C(string id, string[] caps, double cpu = 10, long freeMb = 8000, int running = 0, bool local = false) =>
        new(id, caps, new HostMetrics { CpuPercent = cpu, FreeMemoryMb = freeMb, RunningCalls = running }, local);

    [Fact]
    public void Placement_skips_hosts_missing_a_capability_and_prefers_idle_ones()
    {
        var hosts = new[]
        {
            C("local-default", ["shell", "desktop"], cpu: 5, local: true),
            C("busy", ["shell", "docker"], cpu: 90, running: 4),
            C("idle", ["shell", "docker"], cpu: 3),
        };
        Assert.Equal("idle", PlacementService.Choose(["docker"], hosts, needsContainer: true));
        Assert.Equal("local-default", PlacementService.Choose(["desktop"], hosts, needsContainer: false));
        Assert.Equal("local-default", PlacementService.Choose(["gpu"], hosts, needsContainer: false)); // nobody qualifies: stay local
    }

    [Fact]
    public void Required_capabilities_follow_the_bot()
    {
        var bot = new BotDefinition { KernelFunctions = ["files", "shell", "desktop"], Container = new ContainerProfile { Image = "python:3.12-slim" } };
        Assert.Equal(["shell", "desktop", "docker"], PlacementService.Required(bot));
    }

    [Fact]
    public void Secrets_are_hashed_and_compared_in_constant_time()
    {
        var hash = HostRegistry.Hash("mbh_secret");
        Assert.NotEqual("mbh_secret", hash);
        Assert.True(HostRegistry.Matches("mbh_secret", hash));
        Assert.False(HostRegistry.Matches("mbh_other", hash));
        Assert.False(HostRegistry.Matches("anything", ""));
    }

    [Theory]
    [InlineData("python", "winget", true, "Python.Python.3.13")]
    [InlineData("nodejs", "winget", true, "OpenJS.NodeJS.LTS")]
    [InlineData("python3", "apt", false, "python3 python3-pip python3-venv")]
    [InlineData("git", "brew", false, "brew install git")]
    [InlineData("ripgrep", "choco", true, "choco install ripgrep -y")]
    public void Install_commands_map_common_tools_to_each_package_manager(string tool, string manager, bool windows, string expected) =>
        Assert.Contains(expected, string.Join("\n", Prerequisites.Commands(tool, null, manager, windows)));

    [Fact]
    public void Dotnet_installs_per_user_without_admin()
    {
        var win = string.Join("\n", Prerequisites.Commands(".NET SDK", "10.0", "winget", windows: true));
        Assert.Contains("dotnet-install.ps1", win);
        Assert.Contains("-Channel 10.0", win);
        Assert.Contains("LOCALAPPDATA", win);
        Assert.Contains("dotnet-install.sh", string.Join("\n", Prerequisites.Commands("dotnet", null, "apt", windows: false)));
    }

    [Fact]
    public void Container_commands_mount_only_the_workspace_with_quotas()
    {
        var ctx = new FunctionExecutionContext
        {
            Bot = new BotDefinition { Id = "dockie" }, TaskId = "t", ThreadId = "th", WorkspacePath = "/tmp/ws", Services = new ServiceCollection().BuildServiceProvider(),
        };
        var psi = RunShellFunction.ContainerCommand(new ContainerProfile { Image = "python:3.12-slim", Cpus = 1.5, MemoryMb = 512, Network = false }, ctx, "python app.py");
        var args = string.Join(' ', psi.ArgumentList);
        Assert.Equal("docker", psi.FileName);
        Assert.Contains("--cpus 1.5 --memory 512m", args);
        Assert.Contains("-v /tmp/ws:/workspace", args);
        Assert.Contains("--network none", args);
        Assert.EndsWith("python:3.12-slim sh -lc python app.py", args);
    }
}

/// <summary>The whole host path over a real WebSocket: enroll with a token, connect, and run a bot's tool on the "host".</summary>
public sealed class HostProtocolTests : IClassFixture<HostProtocolTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-hosts-" + Guid.NewGuid().ToString("N")[..8]);
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Marbots:DataDirectory", DataDir);
    }

    private readonly Fixture _fx;
    public HostProtocolTests(Fixture fx) => _fx = fx;

    [Fact]
    public async Task Enrolled_host_runs_a_bots_tools_and_serves_its_files()
    {
        var http = _fx.CreateClient();
        // Enrollment token → host id + secret; the token works once.
        var enrollment = await (await http.PostAsJsonAsync("/api/v1/hosts/enrollments", new CreateEnrollmentRequest("lab-pc", 10))).Content.ReadFromJsonAsync<CreateEnrollmentResult>();
        var hello = new HostHello { Name = "lab-pc", Os = "TestOS", Capabilities = ["shell", "files"], Functions = ["write_file", "read_file"] };
        var enrolled = await (await http.PostAsJsonAsync("/api/v1/hosts/enroll", new HostEnrollmentRequest(enrollment!.Token, hello))).Content.ReadFromJsonAsync<HostEnrollmentResult>();
        Assert.StartsWith("host-lab-pc", enrolled!.HostId);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/v1/hosts/enroll", new HostEnrollmentRequest(enrollment.Token, hello))).StatusCode);

        // A wrong secret is refused.
        var bad = _fx.Server.CreateWebSocketClient();
        bad.ConfigureRequest = r => { r.Headers[HostProtocol.HostIdHeader] = enrolled.HostId; r.Headers[HostProtocol.HostSecretHeader] = "nope"; };
        await Assert.ThrowsAnyAsync<Exception>(() => bad.ConnectAsync(new Uri(_fx.Server.BaseAddress, "api/v1/hosts/connect"), default));

        // The simulated host: answers invoke frames by running the real kernel function in its own folder.
        var hostDir = Path.Combine(_fx.DataDir, "fake-host");
        var ws = _fx.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => { r.Headers[HostProtocol.HostIdHeader] = enrolled.HostId; r.Headers[HostProtocol.HostSecretHeader] = enrolled.Secret; };
        using var socket = await ws.ConnectAsync(new Uri(_fx.Server.BaseAddress, "api/v1/hosts/connect"), default);
        await Send(socket, new HostFrame { Type = HostProtocol.Frames.Hello, Hello = hello });
        var seenInvokes = 0;
        using var stop = new CancellationTokenSource();
        var hostLoop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var frame = await HostConnectionManager.ReceiveAsync(socket, stop.Token);
                if (frame is null) break;
                var ws2 = Path.Combine(hostDir, frame.Workspace ?? frame.Invoke?.Workspace ?? "default");
                Directory.CreateDirectory(ws2);
                switch (frame.Type)
                {
                    case HostProtocol.Frames.Invoke:
                        Interlocked.Increment(ref seenInvokes);
                        var fn = KernelCatalog.CreateDefault().First(f => f.Descriptor.Name == frame.Invoke!.Function);
                        using (var args = JsonDocument.Parse(frame.Invoke!.Arguments))
                        {
                            var result = await fn.InvokeAsync(new FunctionCall(frame.Invoke.CallId, fn.Descriptor.Name, args.RootElement.Clone()),
                                new FunctionExecutionContext { Bot = new BotDefinition { Id = frame.Invoke.BotId }, TaskId = frame.Invoke.TaskId, ThreadId = frame.Invoke.ThreadId, WorkspacePath = ws2, Services = new ServiceCollection().BuildServiceProvider() }, default);
                            await Send(socket, new HostFrame { Type = HostProtocol.Frames.Result, RequestId = frame.RequestId, Result = result });
                        }
                        break;
                    case HostProtocol.Frames.ListFiles:
                        await Send(socket, new HostFrame
                        {
                            Type = HostProtocol.Frames.ListFiles, RequestId = frame.RequestId,
                            Files = Directory.EnumerateFiles(ws2, "*", SearchOption.AllDirectories).Select(f => new HostFileEntry(Path.GetRelativePath(ws2, f).Replace('\\', '/'), new FileInfo(f).Length, DateTimeOffset.UtcNow)).ToList(),
                        });
                        break;
                    case HostProtocol.Frames.ReadFile:
                        await Send(socket, new HostFrame { Type = HostProtocol.Frames.ReadFile, RequestId = frame.RequestId, Data = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(ws2, frame.Path!))) });
                        break;
                }
            }
        });

        var connections = _fx.Services.GetRequiredService<HostConnectionManager>();
        for (var i = 0; i < 200 && !connections.IsOnline(enrolled.HostId); i++) await Task.Delay(50);
        Assert.True(connections.IsOnline(enrolled.HostId));

        // Put a bot on the host; its write_file runs there, not on the control plane.
        var bots = _fx.Services.GetRequiredService<BotRegistry>();
        var bot = await bots.CreateAsync(new BotDefinition { Name = "Remo", HostRef = enrolled.HostId, KernelFunctions = ["files"] });
        var mock = _fx.Services.GetRequiredService<ModelRouter>().Mock;
        mock.EnqueueTool("write_file", """{"path":"report.md","content":"# Made on lab-pc"}""");
        mock.EnqueueText("Done.");
        var engine = _fx.Services.GetRequiredService<MarbotsEngine>();
        var thread = await engine.CreateThreadAsync(bot.Id);
        var task = await engine.WaitAsync((await engine.SendAsync(thread.Id, "write the report")).Id, TimeSpan.FromSeconds(45));
        Assert.Equal(TaskState.Completed, task.State);
        Assert.Equal(1, seenInvokes);
        Assert.False(File.Exists(Path.Combine(engine.WorkspaceFor(thread.Id), "report.md")));

        // The thread's files include the host's, and download through the server.
        var files = await http.GetFromJsonAsync<List<JsonElement>>($"/api/v1/threads/{thread.Id}/files");
        var remote = Assert.Single(files!, f => f.GetProperty("path").GetString() == "report.md");
        Assert.Equal(enrolled.HostId, remote.GetProperty("host").GetString());
        Assert.Equal("# Made on lab-pc", await http.GetStringAsync($"/api/v1/threads/{thread.Id}/files/report.md?host={enrolled.HostId}"));

        var hosts = await http.GetFromJsonAsync<List<JsonElement>>("/api/v1/hosts");
        Assert.Contains(hosts!, h => h.GetProperty("id").GetString() == enrolled.HostId && h.GetProperty("status").GetString() == "Online");

        await stop.CancelAsync();
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        try { await hostLoop; } catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
    }

    private static Task Send(WebSocket socket, HostFrame frame) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, MarbotsJsonContext.Default.HostFrame), WebSocketMessageType.Text, true, default);
}

public class PrerequisiteLanguagePackageTests
{
    [Theory]
    [InlineData("pip:openpyxl", "winget", true, "python -m pip install --user --disable-pip-version-check openpyxl")]
    [InlineData("pptxgenjs", "npm", true, "npm install -g pptxgenjs")]
    [InlineData("dotnet-tool:dotnet-ef", "winget", true, "dotnet tool update -g dotnet-ef")]
    public void Language_packages_use_their_own_installer(string name, string manager, bool windows, string expected) =>
        Assert.Equal(expected, Marbots.Kernel.Prerequisites.Commands(name, null, manager, windows).Single());
}
