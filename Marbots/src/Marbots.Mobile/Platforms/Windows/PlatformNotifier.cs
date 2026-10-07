using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Marbots.Mobile.Services;

public sealed partial class PlatformNotifier
{
    private bool _registered;

    public Task<bool> RequestPermissionAsync()
    {
        try
        {
            if (!_registered) AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _registered = false;
        }
        return Task.FromResult(_registered);
    }

    public void Show(string title, string body)
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification()); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
