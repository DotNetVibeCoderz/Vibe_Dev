using Android.App;
using Android.Content;
using Android.OS;

namespace Marbots.Mobile.Services;

public sealed partial class PlatformNotifier
{
    private const string ChannelId = "marbots";
    private int _id = 1000;

    public async Task<bool> RequestPermissionAsync()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted) return false;
        }
        EnsureChannel();
        return true;
    }

    public void Show(string title, string body)
    {
        var context = Platform.AppContext;
        EnsureChannel();
        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        var pending = intent is null ? null : PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = new Notification.Builder(context, ChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new Notification.BigTextStyle().BigText(body))
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetAutoCancel(true);
        if (pending is not null) builder.SetContentIntent(pending);
        (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Notify(Interlocked.Increment(ref _id), builder.Build());
    }

    private static void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = Platform.AppContext.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager?.GetNotificationChannel(ChannelId) is not null) return;
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Marbots", NotificationImportance.High) { Description = "Approvals and finished tasks" });
    }
}
