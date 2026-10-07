namespace Marbots.Mobile.Services;

/// <summary>Local (on-device) notifications: approvals waiting and finished tasks.</summary>
public interface INotifier
{
    Task<bool> RequestPermissionAsync();
    void Show(string title, string body);
}

/// <summary>Implemented per platform under Platforms/*/PlatformNotifier.cs.</summary>
public sealed partial class PlatformNotifier : INotifier;
