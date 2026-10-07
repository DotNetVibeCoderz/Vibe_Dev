using System.Collections.Concurrent;
using System.Text.Json;
using Marbots.Abstractions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Marbots.AgentHost;

public enum LinkState { Connecting, Connected, Offline }

/// <summary>
/// What the person sitting at this computer sees: who is working on it right now, what just happened, how loaded the
/// machine is, and a clear warning while a bot drives the mouse and keyboard. Falls back to plain log lines when the
/// output is redirected (service logs, CI).
/// </summary>
internal sealed class HostConsole
{
    // Palette: the robot's eyes carry the state; everything else stays quiet.
    private static readonly Color Indigo = new(0x6D, 0x7B, 0xFF);
    private static readonly Color Teal = new(0x4F, 0xE3, 0xE0);
    private static readonly Color Amber = new(0xFF, 0xB5, 0x47);
    private static readonly Color Coral = new(0xFF, 0x6B, 0x6B);
    private static readonly Color Slate = new(0x8A, 0x91, 0xB8);

    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly HashSet<string> DesktopTools = ["mouse_click", "type_text", "press_keys"];

    private readonly ConcurrentDictionary<string, Running> _running = new();
    private readonly ConcurrentQueue<Done> _recent = new();
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private volatile LinkState _link = LinkState.Connecting;
    private volatile string _linkNote = "";
    private HostMetrics? _metrics;
    private IReadOnlyList<string> _capabilities = [];

    public HostConsole(string hostName, string hostId, string server)
    {
        HostName = hostName;
        HostId = hostId;
        Server = new Uri(server).Authority;
    }

    public string HostName { get; }
    public string HostId { get; }
    public string Server { get; }
    public bool Interactive { get; } = !Console.IsOutputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

    private sealed record Running(string Bot, string Tool, string What, DateTimeOffset Since);
    private sealed record Done(DateTimeOffset At, string Bot, string Tool, string What, bool Ok, TimeSpan Took, string? Note);

    // ---------------- state updates (thread-safe) ----------------

    public void SetLink(LinkState state, string note = "")
    {
        _link = state;
        _linkNote = note;
        if (!Interactive) Plain($"{state}{(note.Length > 0 ? ": " + note : "")}");
    }

    public void SetCapabilities(IReadOnlyList<string> caps) => _capabilities = caps;
    public void SetMetrics(HostMetrics metrics) => _metrics = metrics;

    public void Started(string requestId, HostInvoke invoke)
    {
        _running[requestId] = new Running(invoke.BotName, invoke.Function, Describe(invoke), DateTimeOffset.Now);
        if (!Interactive) Plain($"{invoke.BotName}: {invoke.Function} {Describe(invoke)}");
    }

    public void Finished(string requestId, FunctionResult result)
    {
        if (!_running.TryRemove(requestId, out var r)) return;
        var note = result.Success ? null : FirstLine(result.Content.Replace("ERROR: ", "", StringComparison.Ordinal));
        _recent.Enqueue(new Done(DateTimeOffset.Now, r.Bot, r.Tool, r.What, result.Success, DateTimeOffset.Now - r.Since, note));
        while (_recent.Count > 40 && _recent.TryDequeue(out _)) { }
        if (!Interactive) Plain($"{r.Bot}: {r.Tool} {(result.Success ? "ok" : "failed: " + note)} ({(DateTimeOffset.Now - r.Since).TotalSeconds:0.0}s)");
    }

    public void Note(string message)
    {
        _recent.Enqueue(new Done(DateTimeOffset.Now, "", "", message, true, TimeSpan.Zero, null));
        if (!Interactive) Plain(message);
    }

    private static void Plain(string message) => Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");

    private static string Describe(HostInvoke invoke)
    {
        try
        {
            using var doc = JsonDocument.Parse(invoke.Arguments);
            var root = doc.RootElement;
            foreach (var key in new[] { "command", "path", "name", "pattern", "text", "keys" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return Short(v.GetString() ?? "", 52);
            if (root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y)) return $"at {x},{y}";
        }
        catch (JsonException) { }
        return "";
    }

    private static string FirstLine(string s) => Short(s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "", 60);
    private static string Short(string s, int n) { s = s.ReplaceLineEndings(" ").Trim(); return s.Length <= n ? s : s[..(n - 1)] + "…"; }

    // ---------------- rendering ----------------

    /// <summary>Renders until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!Interactive) { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } return; }
        Console.Title = $"Marbots host · {HostName}";
        var frame = 0;
        await AnsiConsole.Live(Render(0)).AutoClear(false).Overflow(VerticalOverflow.Ellipsis).StartAsync(async live =>
        {
            while (!ct.IsCancellationRequested)
            {
                live.UpdateTarget(Render(frame++));
                try { await Task.Delay(120, ct); } catch (OperationCanceledException) { break; }
            }
        });
    }

    private IRenderable Render(int frame)
    {
        var width = Math.Max(72, Math.Min(Console.WindowWidth, 140));
        var rows = new List<IRenderable> { Header(frame) };
        var driving = _running.Values.FirstOrDefault(r => DesktopTools.Contains(r.Tool));
        if (driving is not null)
            rows.Add(new Panel(new Markup($"[bold {Hex(Amber)}]{Esc(driving.Bot)} is using the mouse and keyboard on this computer.[/] [{Hex(Slate)}]Please don't type until it finishes, or press Ctrl+C to stop the host.[/]"))
                .Border(BoxBorder.Heavy).BorderColor(Amber).Expand());

        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn().Width((width - 6) * 3 / 5));
        grid.AddColumn(new GridColumn().Width((width - 6) * 2 / 5).PadLeft(3));
        grid.AddRow(Section("At work now", WorkNow(frame)), Section("This machine", Machine()));
        rows.Add(grid);
        rows.Add(Section("Lately", Lately()));
        rows.Add(Footer());
        return new Padder(new Rows(rows), new Padding(1, 0, 1, 0)) { Expand = false };
    }

    private IRenderable Header(int frame)
    {
        var (eye, said) = _link switch
        {
            LinkState.Connected => (frame % 40 < 37 ? Teal : Slate, $"connected to {Server}"), // a blink every few seconds
            LinkState.Connecting => (frame % 8 < 4 ? Amber : Slate, $"reaching {Server}…"),
            _ => (Coral, $"offline: {_linkNote}"),
        };
        var up = DateTimeOffset.Now - _started;
        var left = new Markup($"[bold {Hex(eye)}]◉◉[/]  [bold]marbots host[/]  [{Hex(Indigo)}]{Esc(HostName)}[/]  [{Hex(Slate)}]{Esc(said)}[/]");
        var right = new Markup($"[{Hex(Slate)}]up {(up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m {up.Seconds}s")}[/]");
        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn());
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow(left, right);
        return new Rows(new Text(""), grid, new Rule().RuleStyle(new Style(Slate)));
    }

    private static IRenderable Section(string title, IRenderable body) =>
        new Rows(new Markup($"[bold]{title}[/]"), new Padder(body, new Padding(0, 0, 0, 1)));

    private IRenderable WorkNow(int frame)
    {
        var list = _running.Values.OrderBy(r => r.Since).ToList();
        if (list.Count == 0) return new Markup($"[{Hex(Slate)}]Nobody is working here right now. Bots placed on {Esc(HostName)} run their files, shell and desktop tools here.[/]");
        var t = new Table().NoBorder().HideHeaders().Expand();
        t.AddColumn(new TableColumn("").Width(2));
        t.AddColumn(new TableColumn("").NoWrap());
        t.AddColumn(new TableColumn("").NoWrap());
        t.AddColumn(new TableColumn(""));
        t.AddColumn(new TableColumn("").RightAligned().NoWrap());
        foreach (var r in list)
        {
            var color = DesktopTools.Contains(r.Tool) || r.Tool == "screenshot" ? Amber : Teal;
            t.AddRow(new Markup($"[{Hex(color)}]{Spinner[frame % Spinner.Length]}[/]"), new Markup($"[bold]{Esc(r.Bot)}[/]"),
                new Markup($"[{Hex(Indigo)}]{Esc(r.Tool)}[/]"), new Markup($"[{Hex(Slate)}]{Esc(r.What)}[/]"),
                new Markup($"{(DateTimeOffset.Now - r.Since).TotalSeconds:0}s"));
        }
        return t;
    }

    private IRenderable Machine()
    {
        var m = _metrics;
        var rows = new List<IRenderable>();
        if (m is null) rows.Add(new Markup($"[{Hex(Slate)}]Measuring…[/]"));
        else
        {
            rows.Add(Meter("host CPU", m.CpuPercent, $"{m.CpuPercent:0}%"));
            rows.Add(new Markup($"[{Hex(Slate)}]free memory[/]  {m.FreeMemoryMb / 1024.0:0.0} GB"));
            rows.Add(new Markup($"[{Hex(Slate)}]free disk[/]    {m.FreeDiskMb / 1024.0:0} GB"));
        }
        var caps = _capabilities.Where(c => !c.StartsWith("pkg:", StringComparison.Ordinal)).ToList();
        var managers = _capabilities.Where(c => c.StartsWith("pkg:", StringComparison.Ordinal)).Select(c => c[4..]).ToList();
        rows.Add(new Text(""));
        // Wrap whole chips, never inside one.
        var max = Math.Max(20, (Math.Min(Console.WindowWidth, 140) - 6) * 2 / 5 - 2);
        var line = new List<string>();
        var used = 0;
        foreach (var c in caps)
        {
            if (used > 0 && used + c.Length + 3 > max)
            {
                rows.Add(new Markup(string.Join(" ", line)));
                line.Clear();
                used = 0;
            }
            line.Add($"[black on {Hex(c == "desktop" ? Amber : Teal)}] {Esc(c)} [/]");
            used += c.Length + 3;
        }
        if (line.Count > 0) rows.Add(new Markup(string.Join(" ", line)));
        if (managers.Count > 0) rows.Add(new Markup($"[{Hex(Slate)}]installs with {Esc(string.Join(", ", managers))}[/]"));
        return new Rows(rows);
    }

    private static IRenderable Meter(string label, double pct, string value)
    {
        const int cells = 16;
        var filled = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100 * cells);
        var color = pct > 85 ? Coral : pct > 60 ? Amber : Teal;
        return new Markup($"[{Hex(Slate)}]{label}[/]     [{Hex(color)}]{new string('█', filled)}[/][{Hex(Slate)}]{new string('░', cells - filled)}[/] {value}");
    }

    private IRenderable Lately()
    {
        var items = _recent.Reverse().Take(10).ToList();
        if (items.Count == 0) return new Markup($"[{Hex(Slate)}]Finished calls show up here with how long they took.[/]");
        var t = new Table().NoBorder().HideHeaders().Expand();
        t.AddColumn(new TableColumn("").Width(2));
        t.AddColumn(new TableColumn("").NoWrap());
        t.AddColumn(new TableColumn("").NoWrap());
        t.AddColumn(new TableColumn("").NoWrap());
        t.AddColumn(new TableColumn(""));
        t.AddColumn(new TableColumn("").RightAligned().NoWrap());
        foreach (var d in items)
        {
            if (d.Tool.Length == 0)
            {
                t.AddRow(new Markup($"[{Hex(Slate)}]·[/]"), new Markup($"[{Hex(Slate)}]{d.At:HH:mm}[/]"), new Text(""), new Text(""), new Markup($"[{Hex(Slate)}]{Esc(d.What)}[/]"), new Text(""));
                continue;
            }
            t.AddRow(new Markup(d.Ok ? $"[{Hex(Teal)}]✓[/]" : $"[{Hex(Coral)}]✗[/]"), new Markup($"[{Hex(Slate)}]{d.At:HH:mm}[/]"),
                new Markup(Esc(d.Bot)), new Markup($"[{Hex(Indigo)}]{Esc(d.Tool)}[/]"),
                new Markup(d.Ok ? $"[{Hex(Slate)}]{Esc(d.What)}[/]" : $"[{Hex(Coral)}]{Esc(d.Note ?? d.What)}[/]"),
                new Markup($"[{Hex(Slate)}]{(d.Took.TotalSeconds < 1 ? $"{d.Took.TotalMilliseconds:0}ms" : $"{d.Took.TotalSeconds:0.0}s")}[/]"));
        }
        return t;
    }

    private IRenderable Footer()
    {
        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn());
        grid.AddColumn(new GridColumn().RightAligned());
        grid.AddRow(new Markup($"[{Hex(Slate)}]Ctrl+C stops the host. Workspaces live in {Esc(HostConfig.WorkspacesDir)}[/]"),
            new Markup($"[{Hex(Slate)}]Gravicode Studios[/]"));
        return new Rows(new Rule().RuleStyle(new Style(Slate)), grid);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static string Esc(string s) => Markup.Escape(s);

    // ---------------- one-shot screens ----------------

    public static void Banner(string subtitle)
    {
        AnsiConsole.MarkupLine($"\n[bold {Hex(Teal)}]◉◉[/]  [bold]marbots host[/]  [{Hex(Slate)}]{Esc(subtitle)}[/]\n");
    }

    public static void Step(bool ok, string text) =>
        AnsiConsole.MarkupLine(ok ? $"  [{Hex(Teal)}]✓[/] {Esc(text)}" : $"  [{Hex(Coral)}]✗[/] {Esc(text)}");

    public static void Error(string text) => AnsiConsole.MarkupLine($"  [{Hex(Coral)}]{Esc(text)}[/]");

    public static void Status(HostConfig? cfg, IReadOnlyList<string> caps)
    {
        Banner(cfg is null ? "not enrolled yet" : cfg.HostId);
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Slate).HideHeaders();
        t.AddColumn("");
        t.AddColumn("");
        t.AddRow(new Markup($"[{Hex(Slate)}]Server[/]"), new Markup(Esc(cfg?.Server ?? "—")));
        t.AddRow(new Markup($"[{Hex(Slate)}]Workspaces[/]"), new Markup(Esc(HostConfig.WorkspacesDir)));
        t.AddRow(new Markup($"[{Hex(Slate)}]Can do[/]"), new Markup(Esc(string.Join(", ", caps))));
        t.AddRow(new Markup($"[{Hex(Slate)}]Log[/]"), new Markup(Esc(Path.Combine(HostConfig.Dir, "host.log"))));
        AnsiConsole.Write(t);
        if (cfg is null) AnsiConsole.MarkupLine($"\n  [{Hex(Slate)}]Enroll with[/] marbots-host enroll --server <url> --token <one-time token>");
    }

    public static void Help()
    {
        Banner("lets Marbots bots work on this computer");
        var t = new Table().NoBorder().HideHeaders();
        t.AddColumn(new TableColumn("").PadRight(4).NoWrap());
        t.AddColumn("");
        t.AddRow(new Markup("[bold]enroll[/] --server <url> --token <t> [[--name <n>]]"), new Markup($"[{Hex(Slate)}]join a Marbots server with a one-time token[/]"));
        t.AddRow(new Markup("[bold]  [/][[--server-ca <pem>]]"), new Markup($"[{Hex(Slate)}]trust a private CA for the server's TLS certificate[/]"));
        t.AddRow(new Markup("[bold]renew[/]"), new Markup($"[{Hex(Slate)}]get a new client certificate (mutual TLS)[/]"));
        t.AddRow(new Markup("[bold]run[/]"), new Markup($"[{Hex(Slate)}]connect and work; reconnects by itself[/]"));
        t.AddRow(new Markup("[bold]status[/]"), new Markup($"[{Hex(Slate)}]where this host connects and what it can do[/]"));
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"\n  [{Hex(Slate)}]Created by Gravicode Studios, led by Kang Fadhil[/]\n");
    }
}
