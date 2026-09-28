using System.Diagnostics;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Permissions;
using DotCode.Engine.Sandbox;

namespace DotCode.Tests;

public sealed class SandboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-sbx-" + Guid.NewGuid().ToString("n")[..8]);

    public SandboxTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DOTCODE_CONFIG_DIR", Path.Combine(_dir, ".cfg"));
    }

    public void Dispose()
    {
        ShellSandbox.OverrideKind = null;
        try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private AgentRuntime Runtime(string sandboxJson, string script = """{"responses":[]}""", bool bypass = false)
    {
        var scriptPath = Path.Combine(_dir, "script.json");
        File.WriteAllText(scriptPath, script);
        var settings = $$$"""{"providers":{"mock":{"type":"mock","script":{{{JsonSerializer.Serialize(scriptPath, TestJson.Default.String)}}}}},"autoCompact":false,"sandbox":{{{sandboxJson}}}}""";
        return AgentRuntime.Create(new RuntimeOptions { Cwd = _dir, Model = "mock:scripted", SettingsJson = settings, NoMcp = true, PersistSession = false, DangerouslySkipPermissions = bypass });
    }

    private static JsonElement Cmd(string command, bool disable = false) =>
        DotCodeJson.Parse(disable ? $$"""{"command":{{JsonSerializer.Serialize(command, TestJson.Default.String)}},"dangerously_disable_sandbox":true}""" : $$"""{"command":{{JsonSerializer.Serialize(command, TestJson.Default.String)}}}""");

    [Fact]
    public void Bubblewrap_arguments_confine_writes_and_hide_credentials()
    {
        var writable = Path.Combine(_dir, "work");
        var hidden = Path.Combine(_dir, "secrets");
        Directory.CreateDirectory(writable);
        Directory.CreateDirectory(hidden);
        var args = ShellSandbox.BubblewrapArgs(new SandboxPolicy([writable, Path.Combine(_dir, "missing")], [hidden], DenyNetwork: true, null, null), writable);
        Assert.Equal(["--ro-bind", "/", "/"], args.Take(3));
        var joined = string.Join(' ', args);
        Assert.Contains($"--bind {writable} {writable}", joined);
        Assert.DoesNotContain("missing", joined);                 // only existing paths are bound
        Assert.Contains($"--tmpfs {hidden}", joined);
        Assert.True(args.IndexOf("--tmpfs") > args.IndexOf("--bind")); // masks win over binds
        Assert.Contains("--unshare-net", args);
        Assert.Equal(["--chdir", writable], args.TakeLast(2));
    }

    [Fact]
    public void Seatbelt_profile_denies_writes_outside_writable_paths()
    {
        var profile = ShellSandbox.SeatbeltProfile(new SandboxPolicy(["/Users/dev/app", "/tmp"], ["/Users/dev/.ssh"], DenyNetwork: true, null, null));
        Assert.StartsWith("(version 1)\n(allow default)\n(deny file-write*)", profile);
        Assert.Contains("(subpath \"/Users/dev/app\")", profile);
        Assert.Contains("(subpath \"/private/tmp\")", profile);
        Assert.Contains("(deny file-read* file-write*\n  (subpath \"/Users/dev/.ssh\")", profile);
        Assert.Contains("(deny network-outbound (remote ip))", profile);
        Assert.DoesNotContain("network", ShellSandbox.SeatbeltProfile(new SandboxPolicy(["/a"], [], false, null, null)));
    }

    [Fact]
    public async Task Sandboxed_shell_commands_are_auto_allowed_only_with_file_system_isolation()
    {
        await using var runtime = Runtime("""{"enabled":true,"excludedCommands":["docker"]}""");
        var session = runtime.CreateSession(persist: false);
        var shell = session.FindTool("Bash") ?? session.FindTool("PowerShell")!;
        PermissionCheck Check(JsonElement input) => session.Permissions.Evaluate(shell, input, PermissionMode.Default, session);

        ShellSandbox.OverrideKind = SandboxKind.Bubblewrap;
        var allowed = Check(Cmd("npm install && npm test"));
        Assert.Equal(PermissionBehavior.Allow, allowed.Behavior);
        Assert.Equal(PermissionEngine.SandboxedReason, allowed.Reason);
        Assert.Equal(PermissionBehavior.Ask, Check(Cmd("npm install", disable: true)).Behavior);   // escape hatch asks
        Assert.Equal(PermissionBehavior.Ask, Check(Cmd("docker build .")).Behavior);             // excluded command
        Assert.Contains("DotCode sandbox", ShellSandbox.FailureHint(ShellSandbox.Plan(session, "touch /etc/x", false), "touch: cannot touch '/etc/x': Read-only file system", true));

        session.Permissions.AddSessionRule("Bash(rm:*)", PermissionBehavior.Deny);
        session.Permissions.AddSessionRule("PowerShell(rm:*)", PermissionBehavior.Deny);
        Assert.Equal(PermissionBehavior.Deny, Check(Cmd("rm -rf build")).Behavior);               // deny rules still win

        ShellSandbox.OverrideKind = SandboxKind.JobObject;                                         // no FS isolation → normal prompts
        Assert.Equal(PermissionBehavior.Ask, Check(Cmd("npm install")).Behavior);
        ShellSandbox.OverrideKind = SandboxKind.None;
        Assert.False(ShellSandbox.Plan(session, "npm install", false).Active);
    }

    [Fact]
    public async Task Escape_hatch_can_be_disabled_by_policy()
    {
        await using var runtime = Runtime("""{"enabled":true,"allowUnsandboxedCommands":false}""");
        var session = runtime.CreateSession(persist: false);
        var shell = session.FindTool("Bash") ?? session.FindTool("PowerShell")!;
        Assert.Contains("disabled by policy", shell.Validate(Cmd("npm install", disable: true), session));
        Assert.Null(shell.Validate(Cmd("npm install"), session));
    }

    [Fact]
    public void Windows_job_object_contains_and_kills_the_process_tree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi)!;
        var job = JobObject.Attach(process, new SandboxPolicy([], [], false, 512L * 1024 * 1024, 16));
        Assert.NotNull(job);
        Assert.True(JobObject.IsInJob(process));
        job!.Dispose();                                   // closing the job kills everything in it
        Assert.True(process.WaitForExit(5000));
    }

    [Fact]
    public async Task Os_sandbox_blocks_writes_outside_the_working_directory()
    {
        // Runs where bubblewrap (Linux) or sandbox-exec (macOS) works. CI sets DOTCODE_REQUIRE_SANDBOX on its Linux and
        // macOS jobs so the test cannot silently skip there.
        if (!ShellSandbox.IsolatesFileSystem(ShellSandbox.Available))
        {
            Assert.False(Environment.GetEnvironmentVariable("DOTCODE_REQUIRE_SANDBOX") == "1", $"an OS sandbox is required but not usable ({ShellSandbox.Available})");
            return;
        }
        var outside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"dotcode-escape-{Guid.NewGuid():n}.txt");
        await using var runtime = Runtime("""{"enabled":true}""", $$$"""
            {"responses":[
              {"toolCalls":[{"name":"Bash","input":{"command":"echo inside > inside.txt; echo outside > {{{outside}}}"}}]},
              {"text":"done"}
            ]}
            """);
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("write files");
        var result = session.Messages.SelectMany(m => m.ToolResults).Single().TextContent;
        Assert.True(File.Exists(Path.Combine(_dir, "inside.txt")), result);
        Assert.False(File.Exists(outside), result);
        Assert.True(result.Contains("Read-only file system") || result.Contains("Operation not permitted"), result);
        Assert.Contains("DotCode sandbox", result);
    }
}
