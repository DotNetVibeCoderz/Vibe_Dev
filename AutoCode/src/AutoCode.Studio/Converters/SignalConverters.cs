// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using AutoCode.Studio.ViewModels;

namespace AutoCode.Studio.Converters;

/// <summary>
/// Maps a channel's state to its colour.
///
/// EN: these three hues are the only colour in the application. Everything else is greyscale, so
/// that colour anywhere on screen means exactly one thing — the state of a connection.
/// ID: ketiga warna ini adalah satu-satunya warna di aplikasi. Sisanya abu-abu, sehingga warna di
/// mana pun di layar berarti satu hal saja: status sebuah koneksi.
/// </summary>
public sealed class SignalBrushConverter : IValueConverter
{
    private static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#3A4157"));
    private static readonly IBrush Testing = new SolidColorBrush(Color.Parse("#45C4B0"));
    private static readonly IBrush Verified = new SolidColorBrush(Color.Parse("#F5A33C"));
    private static readonly IBrush Faulted = new SolidColorBrush(Color.Parse("#E5484D"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SignalState.Testing => Testing,
            SignalState.Verified => Verified,
            SignalState.Faulted => Faulted,
            _ => Unknown,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a channel's state to a single glyph for the signal dial.</summary>
public sealed class SignalGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SignalState.Testing => "◐",
            SignalState.Verified => "●",
            SignalState.Faulted => "✕",
            _ => "○",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
