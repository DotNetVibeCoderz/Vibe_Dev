using Marbots.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace Marbots.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<INotifier, PlatformNotifier>();
        builder.Services.AddSingleton<MobileSession>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
