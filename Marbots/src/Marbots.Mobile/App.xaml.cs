namespace Marbots.Mobile;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "Marbots" };
#if WINDOWS || MACCATALYST
        // On a desktop the phone app opens in a phone-sized window.
        window.Width = 400;
        window.Height = 720;
#endif
        return window;
    }
}
