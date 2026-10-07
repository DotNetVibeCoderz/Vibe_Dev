namespace Marbots.Abstractions;

/// <summary>Push platforms: Firebase Cloud Messaging (Android/web), Apple Push Notification service, or an ntfy topic.</summary>
public static class PushPlatforms
{
    public const string Fcm = "fcm";
    public const string Apns = "apns";
    public const string Ntfy = "ntfy";
    public static readonly IReadOnlyList<string> All = [Fcm, Apns, Ntfy];
}

/// <summary>What a device is notified about.</summary>
public static class PushTopics
{
    /// <summary>A bot is waiting for an approval.</summary>
    public const string Approvals = "approvals";
    /// <summary>A chat task finished.</summary>
    public const string Completed = "completed";
    /// <summary>A chat task failed, timed out or was cancelled.</summary>
    public const string Failed = "failed";
    public static readonly IReadOnlyList<string> All = [Approvals, Completed, Failed];
}

/// <summary>A device (or ntfy topic) that receives push notifications for this tenant.</summary>
public sealed class PushDevice
{
    public string Id { get; set; } = "";
    public string Platform { get; set; } = PushPlatforms.Ntfy;
    /// <summary>FCM registration token, APNs device token (hex) or ntfy topic name.</summary>
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Topics { get; set; } = [.. PushTopics.All];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSentAt { get; set; }
    public string? LastError { get; set; }
}

public sealed record RegisterPushDeviceRequest(string Platform, string Token, string? Name = null, List<string>? Topics = null);
/// <summary>Which push platforms this server can deliver to, and the ntfy server for topic subscriptions.</summary>
public sealed record PushConfig(bool Fcm, bool Apns, string NtfyServer);
public sealed record PushTestResult(int Sent, int Failed, IReadOnlyList<string> Errors);
