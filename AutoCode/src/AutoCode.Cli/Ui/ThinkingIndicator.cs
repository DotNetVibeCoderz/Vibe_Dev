// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The waiting indicator: a spinner, a rotating verb, and an elapsed clock.
///
/// EN: it exists for one specific interval — between sending a request and the first token coming
/// back. That gap is a few seconds of nothing, and a few seconds of nothing is indistinguishable
/// from a hang. The elapsed counter is the part that actually informs: it tells you whether the
/// model is slow or the network is gone.
///
/// The words rotate because a static "Thinking…" stops being read after the second time; a phrase
/// that changes keeps signalling liveness on its own, without a single extra byte of output.
///
/// ID: indikator ini hanya untuk satu jeda — antara permintaan dikirim dan token pertama datang.
/// Beberapa detik hening tidak bisa dibedakan dari aplikasi menggantung. Penghitung waktu adalah
/// bagian yang benar-benar memberi informasi: apakah modelnya lambat atau jaringannya putus.
/// Kata-katanya berganti karena "Thinking…" yang diam berhenti dibaca setelah kali kedua.
/// </summary>
public sealed class ThinkingIndicator(Theme theme) : IDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] FramesAscii = ["-", "\\", "|", "/"];

    /// <summary>
    /// Deliberately absurd and deliberately harmless. The register is a small joke shared with the
    /// user during a wait; nothing here claims the agent is doing something it is not.
    /// </summary>
    private static readonly string[] Words =
    [
        "Moon walking", "Percolating", "Untangling", "Rummaging", "Noodling",
        "Deliberating", "Spelunking", "Marinating", "Triangulating", "Pondering",
        "Wrangling", "Tinkering", "Divining", "Compiling thoughts", "Consulting the rubber duck",
        "Reticulating splines", "Chasing pointers", "Warming up", "Musing", "Scheming",
    ];

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private int _drawnWidth;

    /// <summary>Starts the indicator. Safe to call when one is already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Stops it and wipes the line, so whatever prints next starts from a clean row.</summary>
    public void Stop()
    {
        Task? loop;
        CancellationTokenSource? cancellation;

        lock (_gate)
        {
            loop = _loop;
            cancellation = _cancellation;
            _loop = null;
            _cancellation = null;
        }

        if (loop is null)
            return;

        cancellation?.Cancel();

        try
        {
            loop.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // The loop was cancelled; that is the expected way for it to end.
        }

        cancellation?.Dispose();
        Erase();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var unicode = AnsiConsole.Profile.Capabilities.Unicode;
        var frames = unicode ? Frames : FramesAscii;

        // Seeded per run so a session does not open with the same word every time.
        var word = Words[Random.Shared.Next(Words.Length)];
        var started = DateTime.UtcNow;
        var frame = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var elapsed = DateTime.UtcNow - started;

                // A new verb every four seconds: often enough to read as alive, rarely enough that
                // it does not become a slot machine.
                if (elapsed.TotalSeconds >= 4 && frame % 33 == 0)
                    word = Words[Random.Shared.Next(Words.Length)];

                Draw($"{frames[frame % frames.Length]} {word}… ({elapsed.TotalSeconds:0}s · Ctrl+C to interrupt)");

                frame++;
                await Task.Delay(120, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void Draw(string text)
    {
        lock (_gate)
        {
            if (Console.IsOutputRedirected)
                return;

            try
            {
                var line = "  " + text;
                Console.Write('\r');
                Console.Write(line.PadRight(Math.Max(_drawnWidth, line.Length)));
                Console.Write('\r');
                _drawnWidth = line.Length;

                // Written through Console rather than AnsiConsole: this line is rewritten dozens of
                // times a second, and markup parsing on every frame is waste the user pays for.
            }
            catch (IOException)
            {
                // The console went away mid-write; there is nothing useful to do about it.
            }
        }
    }

    private void Erase()
    {
        lock (_gate)
        {
            if (Console.IsOutputRedirected || _drawnWidth == 0)
                return;

            try
            {
                Console.Write('\r');
                Console.Write(new string(' ', _drawnWidth));
                Console.Write('\r');
                _drawnWidth = 0;
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose() => Stop();
}
