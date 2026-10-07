using Foundation;
using UserNotifications;

namespace Marbots.Mobile.Services;

public sealed partial class PlatformNotifier
{
    public async Task<bool> RequestPermissionAsync()
    {
        var center = UNUserNotificationCenter.Current;
        center.Delegate ??= new ForegroundDelegate();
        var (granted, _) = await center.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
        return granted;
    }

    public void Show(string title, string body)
    {
        var content = new UNMutableNotificationContent { Title = title, Body = body, Sound = UNNotificationSound.Default };
        var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null);
        UNUserNotificationCenter.Current.AddNotificationRequest(request, _ => { });
    }

    /// <summary>Shows banners while the app is open too.</summary>
    private sealed class ForegroundDelegate : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
            completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
    }
}
