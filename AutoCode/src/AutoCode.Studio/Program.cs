// Auto Code — Gravicode Studios (Kang Fadhil)

using Avalonia;

namespace AutoCode.Studio;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Referenced by the Avalonia designer as well as by Main; it must stay parameterless.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
