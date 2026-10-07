namespace Marbots.Abstractions;

/// <summary>
/// The control-plane ↔ agent-host protocol: JSON frames over one WebSocket that the host opens to the server
/// (outbound, so hosts behind NAT work). The LLM loop, policy and approvals stay on the control plane; hosts execute the
/// bots' environment tools (files, shell, desktop, containers) in their own workspaces.
/// </summary>
public static class HostProtocol
{
    public const int Version = 1;
    public const string HostIdHeader = "X-Marbots-Host";
    public const string HostSecretHeader = "X-Marbots-Host-Secret";

    /// <summary>Tool packs whose functions run on the bot's host instead of the control plane.</summary>
    public static readonly IReadOnlySet<string> RemotePacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "files", "search", "shell", "desktop" };

    public static class Frames
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Heartbeat = "heartbeat";
        public const string Invoke = "invoke";
        public const string Result = "result";
        public const string ListFiles = "list-files";
        public const string ReadFile = "read-file";
        public const string PutFiles = "put-files";
        public const string Cancel = "cancel";
        public const string Error = "error";
    }
}

/// <summary>One message on the host connection. Only the fields of its <see cref="Type"/> are set.</summary>
public sealed class HostFrame
{
    public string Type { get; set; } = "";
    /// <summary>Correlates a request (invoke, list-files, read-file) with its result.</summary>
    public string? RequestId { get; set; }
    public HostHello? Hello { get; set; }
    public HostMetrics? Metrics { get; set; }
    public HostInvoke? Invoke { get; set; }
    public FunctionResult? Result { get; set; }
    /// <summary>list-files / read-file: workspace slug and path.</summary>
    public string? Workspace { get; set; }
    public string? Path { get; set; }
    public List<HostFileEntry>? Files { get; set; }
    /// <summary>put-files: files to write into the workspace.</summary>
    public List<HostFilePayload>? Upload { get; set; }
    /// <summary>read-file result, base64.</summary>
    public string? Data { get; set; }
    public string? Error { get; set; }
}

public sealed class HostHello
{
    public int ProtocolVersion { get; set; } = HostProtocol.Version;
    public string Name { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int ProcessorCount { get; set; }
    public long TotalMemoryMb { get; set; }
    /// <summary>shell, desktop, docker, playwright, dotnet, node, python, gpu …</summary>
    public List<string> Capabilities { get; set; } = [];
    /// <summary>Function names the host can execute.</summary>
    public List<string> Functions { get; set; } = [];
}

public sealed class HostMetrics
{
    public double CpuPercent { get; set; }
    public long FreeMemoryMb { get; set; }
    public int RunningCalls { get; set; }
    public long FreeDiskMb { get; set; }
}

public sealed class HostInvoke
{
    public string Function { get; set; } = "";
    public string CallId { get; set; } = "";
    /// <summary>Raw JSON arguments.</summary>
    public string Arguments { get; set; } = "{}";
    public string BotId { get; set; } = "";
    public string BotName { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    /// <summary>Workspace folder name on the host (one per thread).</summary>
    public string Workspace { get; set; } = "";
    public ContainerProfile? Container { get; set; }
}

/// <summary>Runs a bot's shell commands in a container (Docker) with quotas, on whichever host it runs.</summary>
public sealed class ContainerProfile
{
    public string Image { get; set; } = "";
    public double Cpus { get; set; } = 1;
    public int MemoryMb { get; set; } = 1024;
    public bool Network { get; set; } = true;
}

/// <summary>A remote host known to the control plane (persisted). Secrets are stored as hashes only.</summary>
public sealed class HostRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>remote | vm | docker</summary>
    public string Kind { get; set; } = "remote";
    public string SecretHash { get; set; } = "";
    public HostHello? LastHello { get; set; }
    public HostMetrics? LastMetrics { get; set; }
    public List<string> Labels { get; set; } = [];
    public bool Disabled { get; set; }
    public DateTimeOffset EnrolledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeen { get; set; }
    /// <summary>How it was installed (e.g. "ssh devuser@192.168.1.20", "manual").</summary>
    public string? InstalledVia { get; set; }
}

/// <summary>A one-time enrollment token (hash) that a new host exchanges for its host id and secret.</summary>
public sealed class HostEnrollment
{
    public string Id { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>Which host a thread's workspace lives on (data locality: later tasks of the thread go to the same host).</summary>
public sealed class ThreadHost
{
    public string Id { get; set; } = "";
    /// <summary>Host chosen by placement for "auto" bots in this thread.</summary>
    public string HostId { get; set; } = "";
    /// <summary>Remote hosts that have files of this thread's workspace.</summary>
    public List<string> Used { get; set; } = [];
}

public sealed record HostFileEntry(string Path, long Size, DateTimeOffset Modified);

public sealed record HostFilePayload(string Path, string Data);

public sealed record HostEnrollmentRequest(string Token, HostHello Hello);
public sealed record HostEnrollmentResult(string HostId, string Secret, string ServerVersion);
public sealed record CreateEnrollmentRequest(string Name, int? ValidMinutes);
public sealed record CreateEnrollmentResult(string Token, DateTimeOffset ExpiresAt, string EnrollCommand);

/// <summary>SSH bootstrap request. The password/key is used for this call only and never stored.</summary>
public sealed class SshBootstrapRequest
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string Name { get; set; } = "";
    /// <summary>URL the new host uses to reach this server (LAN address, not localhost).</summary>
    public string ServerUrl { get; set; } = "";
    /// <summary>Only replace the binary and restart (keeps the enrollment): used for rolling updates.</summary>
    public bool UpdateOnly { get; set; }
}

public sealed class SshBootstrapResult
{
    public bool Success { get; set; }
    public string? HostId { get; set; }
    public List<string> Log { get; set; } = [];
    public string? Error { get; set; }
}
