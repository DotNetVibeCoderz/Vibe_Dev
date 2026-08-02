// Auto Code — Gravicode Studios (Kang Fadhil)

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AutoCode.Studio.ViewModels;

namespace AutoCode.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Copies the profiles as shell exports.
    ///
    /// EN: handled here rather than in the view model because the clipboard belongs to the window,
    /// and reaching for it from a view model would mean handing the view model a reference to the
    /// UI it is meant to be independent of.
    /// ID: ditangani di sini, bukan di view model, karena clipboard milik jendela.
    /// </summary>
    private async void OnCopyEnvironment(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel model || Clipboard is null)
            return;

        var script = model.BuildEnvironmentScript(powershell: OperatingSystem.IsWindows());

        // Avalonia 12 replaced SetTextAsync with a data-transfer object.
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(script));

        await Clipboard.SetDataAsync(transfer);

        model.Status = OperatingSystem.IsWindows()
            ? "Copied as PowerShell exports. Paste into your profile to keep keys out of files."
            : "Copied as shell exports. Paste into your profile to keep keys out of files.";
    }
}
