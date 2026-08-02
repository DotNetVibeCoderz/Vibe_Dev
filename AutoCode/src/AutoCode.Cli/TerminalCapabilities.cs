// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoCode.Cli;

/// <summary>
/// Turns on the console features Windows leaves off by default.
///
/// EN: the classic Windows console has supported ANSI escapes and 24-bit colour for years, but only
/// once a process asks for it. Nothing asks on your behalf, so a CLI that skips this step is
/// detected as a sixteen-colour terminal and renders its gradients as flat approximations — on the
/// console most Windows users still get by double-clicking an executable.
///
/// Windows Terminal negotiates this itself, which is why the difference only shows up in conhost.
///
/// ID: konsol klasik Windows sudah mendukung escape ANSI dan warna 24-bit bertahun-tahun, tetapi
/// hanya setelah sebuah proses memintanya. Tidak ada yang memintakan untuk kita, sehingga CLI yang
/// melewatkan langkah ini terdeteksi sebagai terminal enam belas warna.
/// </summary>
internal static class TerminalCapabilities
{
    private const int StandardOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [SupportedOSPlatform("windows")]
    public static void EnableVirtualTerminal()
    {
        // A redirected stream has no console mode to set, and asking for one fails.
        if (Console.IsOutputRedirected)
            return;

        try
        {
            var handle = GetStdHandle(StandardOutputHandle);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return;

            if (!GetConsoleMode(handle, out var mode))
                return;

            if ((mode & EnableVirtualTerminalProcessing) != 0)
                return;

            // A failure here is not worth reporting: the interface degrades to fewer colours,
            // which is exactly what would have happened anyway.
            _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not a Windows console after all.
        }
    }

    // DllImport rather than LibraryImport: the source generator it uses requires AllowUnsafeBlocks
    // across the whole project, which is too broad a concession for three console calls.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
