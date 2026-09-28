using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCode.TermCapture;

// Usage: TermCapture <script.json> [--only name1,name2]
// Runs a program inside a real Windows pseudo console, plays scripted keystrokes and saves screenshots
// (HTML + PNG rendered by headless Edge/Chrome) of the emulated terminal screen.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: TermCapture <script.json>");
    return 2;
}
var script = JsonDocument.Parse(File.ReadAllText(args[0]), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement;
var scriptDir = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
string Expand(string s) => Regex.Replace(s, @"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
string Prop(string name, string def) => script.TryGetProperty(name, out var v) ? Expand(v.GetString() ?? def) : def;

var cols = script.TryGetProperty("cols", out var c) ? c.GetInt32() : 120;
var rows = script.TryGetProperty("rows", out var r) ? r.GetInt32() : 36;
var outDir = Path.GetFullPath(Path.Combine(scriptDir, Prop("outDir", ".")));
Directory.CreateDirectory(outDir);
var cwd = Path.GetFullPath(Path.Combine(scriptDir, Prop("cwd", ".")));
Directory.CreateDirectory(cwd);
var env = new Dictionary<string, string?>();
if (script.TryGetProperty("env", out var envEl))
    foreach (var p in envEl.EnumerateObject()) env[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : Expand(p.Value.GetString() ?? "");
env["DOTCODE_COLUMNS"] = null;
var title = Prop("title", "DotCode");
var bg = Prop("background", "#0C0C0C");
var fg = Prop("foreground", "#CCCCCC");
var logPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(args[0]) + ".log");

var screen = new VtScreen(cols, rows);
using var pty = new PseudoConsole((short)cols, (short)rows);
pty.Start(Prop("command", "cmd.exe"), cwd, env);

var gate = new Lock();
var lastOutput = DateTime.UtcNow;
var raw = new StringBuilder();
var reader = Task.Run(async () =>
{
    var decoder = Encoding.UTF8.GetDecoder();
    var buffer = new byte[16384];
    var chars = new char[32768];
    while (true)
    {
        int n;
        try { n = await pty.Output.ReadAsync(buffer); }
        catch { break; }
        if (n <= 0) break;
        var cn = decoder.GetChars(buffer, 0, n, chars, 0);
        var s = new string(chars, 0, cn);
        lock (gate)
        {
            screen.Feed(s);
            raw.Append(s);
            lastOutput = DateTime.UtcNow;
        }
    }
});

void Send(string s)
{
    var bytes = Encoding.UTF8.GetBytes(s);
    pty.Input.Write(bytes);
    pty.Input.Flush();
}

string KeySeq(string key) => key.ToLowerInvariant() switch
{
    "enter" or "return" => "\r",
    "esc" or "escape" => "\u001b",
    "tab" => "\t",
    "shift+tab" => "\u001b[Z",
    "up" => "\u001b[A",
    "down" => "\u001b[B",
    "right" => "\u001b[C",
    "left" => "\u001b[D",
    "backspace" => "\u007f",
    "ctrl+c" => "\u0003",
    "ctrl+d" => "\u0004",
    "ctrl+o" => "\u000f",
    "ctrl+t" => "\u0014",
    "ctrl+l" => "\u000c",
    "space" => " ",
    _ => key,
};

var sw = Stopwatch.StartNew();
var failures = 0;
foreach (var step in script.GetProperty("steps").EnumerateArray())
{
    if (step.TryGetProperty("type", out var type))
    {
        var text = Expand(type.GetString() ?? "");
        var delay = step.TryGetProperty("delay", out var d) ? d.GetInt32() : 12;
        foreach (var ch in text) { Send(ch == '\n' ? "\r" : ch.ToString()); Thread.Sleep(delay); }
    }
    else if (step.TryGetProperty("paste", out var paste)) Send(Expand(paste.GetString() ?? "").Replace("\n", "\r"));
    else if (step.TryGetProperty("key", out var key))
    {
        var times = step.TryGetProperty("times", out var t) ? t.GetInt32() : 1;
        for (var i = 0; i < times; i++) { Send(KeySeq(key.GetString()!)); Thread.Sleep(80); }
    }
    else if (step.TryGetProperty("sleep", out var sl)) Thread.Sleep(sl.GetInt32());
    else if (step.TryGetProperty("waitFor", out var wf))
    {
        var pattern = new Regex(wf.GetString()!, RegexOptions.IgnoreCase);
        var timeout = step.TryGetProperty("timeout", out var to) ? to.GetInt32() : 30_000;
        var ok = SpinWait.SpinUntil(() => { lock (gate) return pattern.IsMatch(screen.Text()); }, timeout);
        if (!ok)
        {
            failures++;
            Console.Error.WriteLine($"[{sw.Elapsed:mm\\:ss}] timeout waiting for /{wf.GetString()}/");
            lock (gate) Console.Error.WriteLine(screen.Text());
        }
        else Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] saw /{wf.GetString()}/");
    }
    else if (step.TryGetProperty("waitGone", out var wg))
    {
        var pattern = new Regex(wg.GetString()!, RegexOptions.IgnoreCase);
        var timeout = step.TryGetProperty("timeout", out var to) ? to.GetInt32() : 120_000;
        // Optional auto-responders: [{"when":"Do you want","keys":"1"}] — answer prompts while waiting.
        var responders = step.TryGetProperty("respond", out var resp)
            ? resp.EnumerateArray().Select(x => (Re: new Regex(x.GetProperty("when").GetString()!, RegexOptions.IgnoreCase), Keys: x.GetProperty("keys").GetString()!)).ToList()
            : [];
        var lastResponse = DateTime.MinValue;
        var ok = SpinWait.SpinUntil(() =>
        {
            string text;
            lock (gate) text = screen.Text();
            if ((DateTime.UtcNow - lastResponse).TotalMilliseconds > 1500)
                foreach (var (re, keys) in responders)
                    if (re.IsMatch(text))
                    {
                        Thread.Sleep(400);
                        Send(KeySeq(keys));
                        lastResponse = DateTime.UtcNow;
                        Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] auto-responded '{keys}' to /{re}/");
                        break;
                    }
            return !pattern.IsMatch(text);
        }, timeout);
        Console.WriteLine(ok ? $"[{sw.Elapsed:mm\\:ss}] gone /{wg.GetString()}/" : $"[{sw.Elapsed:mm\\:ss}] still present /{wg.GetString()}/ after {timeout}ms");
        if (!ok) failures++;
    }
    else if (step.TryGetProperty("waitIdle", out var wi))
    {
        var idle = wi.GetInt32();
        var max = step.TryGetProperty("timeout", out var to) ? to.GetInt32() : 60_000;
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < max)
        {
            DateTime last;
            lock (gate) last = lastOutput;
            if ((DateTime.UtcNow - last).TotalMilliseconds >= idle) break;
            Thread.Sleep(50);
        }
    }
    else if (step.TryGetProperty("snapshot", out var snap))
    {
        var name = snap.GetString()!;
        string html;
        var full = step.TryGetProperty("full", out var fl) && fl.GetBoolean();
        var tail = step.TryGetProperty("tail", out var tl) ? tl.GetInt32() : 0;
        var skip = step.TryGetProperty("skip", out var sk) ? sk.GetInt32() : 0;
        int renderedRows;
        lock (gate) html = HtmlRenderer.Render(screen, title, bg, fg, step.TryGetProperty("caption", out var cap) ? cap.GetString() : null, full, tail, skip, out renderedRows);
        var htmlPath = Path.Combine(outDir, name + ".html");
        File.WriteAllText(htmlPath, html);
        var png = Path.Combine(outDir, name + ".png");
        HtmlRenderer.Screenshot(htmlPath, png, cols, renderedRows + (step.TryGetProperty("caption", out _) ? 2 : 0));
        lock (gate) File.WriteAllText(Path.Combine(outDir, name + ".txt"), screen.Text());
        Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] snapshot {name} → {png}");
    }
}

lock (gate) File.WriteAllText(logPath, raw.ToString());
pty.Dispose();
return failures == 0 ? 0 : 1;

internal static class HtmlRenderer
{
    public static int UsedRows(VtScreen screen)
    {
        var last = screen.Rows - 1;
        while (last > 0 && screen.RowText(last).Length == 0 && !(screen.CursorVisible && screen.CursorY == last)) last--;
        return Math.Min(screen.Rows, last + 2);
    }

    public static string Render(VtScreen screen, string title, string bg, string fg, string? caption, bool full, int tail, int skip, out int renderedRows)
    {
        var usedVisible = UsedRows(screen);
        var rows = full ? screen.Scrollback.Concat(Enumerable.Range(0, usedVisible).Select(screen.Row)).ToList() : Enumerable.Range(0, usedVisible).Select(screen.Row).ToList();
        var cursorOffset = full ? screen.Scrollback.Count : 0;
        if (skip > 0) { rows = rows.Skip(skip).ToList(); cursorOffset -= skip; }
        if (tail > 0 && rows.Count > tail) { cursorOffset -= rows.Count - tail; rows = rows.Skip(rows.Count - tail).ToList(); }
        // Drop leading blank rows.
        while (rows.Count > 1 && VtScreen.RowText(rows[0]).Length == 0) { rows.RemoveAt(0); cursorOffset--; }
        renderedRows = rows.Count;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
        sb.Append($"html,body{{margin:0;background:#1e1e1e;}}");
        sb.Append(".win{display:inline-block;margin:0;border-radius:10px;overflow:hidden;box-shadow:0 8px 30px rgba(0,0,0,.45);border:1px solid #3a3a3a}");
        sb.Append(".bar{height:34px;background:#2b2b2b;display:flex;align-items:center;padding:0 14px;font:13px 'Segoe UI',system-ui,sans-serif;color:#ddd;gap:8px}");
        sb.Append(".dot{width:12px;height:12px;border-radius:50%;display:inline-block}");
        sb.Append($".term{{background:{bg};color:{fg};padding:10px 14px 14px 14px;font-family:'Cascadia Mono','Cascadia Code',Consolas,'DejaVu Sans Mono',monospace;font-size:14px;line-height:18px;white-space:pre}}");
        sb.Append(".row{height:18px}.b{font-weight:700}.i{font-style:italic}.u{text-decoration:underline}.s{text-decoration:line-through}.d{opacity:.6}");
        sb.Append(".cur{outline:1px solid currentColor}.c{display:inline-block;width:1ch;overflow:visible;text-align:center}");
        sb.Append(".cap{font:13px 'Segoe UI',system-ui,sans-serif;color:#aaa;padding:6px 14px;background:#252525}");
        sb.Append("</style></head><body><div class='win'><div class='bar'>");
        sb.Append("<span class='dot' style='background:#ff5f57'></span><span class='dot' style='background:#febc2e'></span><span class='dot' style='background:#28c840'></span>");
        sb.Append("<span style='margin-left:10px'>").Append(WebUtility.HtmlEncode(title)).Append("</span></div><div class='term'>");
        for (var ri = 0; ri < rows.Count; ri++)
        {
            sb.Append("<div class='row'>");
            var row = rows[ri];
            var y = ri - cursorOffset;
            var x = 0;
            while (x < screen.Cols)
            {
                var isCursor = screen.CursorVisible && y == screen.CursorY && x == screen.CursorX;
                var style = row[x].Style;
                var text = new StringBuilder(row[x].Text);
                x++;
                if (!isCursor)
                    while (x < screen.Cols && row[x].Style == style && !(screen.CursorVisible && y == screen.CursorY && x == screen.CursorX))
                        text.Append(row[x++].Text);
                sb.Append(Span(style, text.ToString(), bg, fg, isCursor));
            }
            sb.Append("</div>");
        }
        sb.Append("</div>");
        if (caption is not null) sb.Append("<div class='cap'>").Append(WebUtility.HtmlEncode(caption)).Append("</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string Hex(int c) => $"#{c:X6}";

    private static string Span(CellStyle s, string text, string bg, string fg, bool cursor)
    {
        var fgc = s.Fg >= 0 ? Hex(s.Fg) : fg;
        var bgc = s.Bg >= 0 ? Hex(s.Bg) : null;
        if (s.Inverse) (fgc, bgc) = (bgc ?? bg, fgc);
        if (cursor) (fgc, bgc) = (bgc ?? bg, fgc);
        var cls = new List<string>();
        if (s.Bold) cls.Add("b");
        if (s.Italic) cls.Add("i");
        if (s.Underline) cls.Add("u");
        if (s.Strike) cls.Add("s");
        if (s.Dim) cls.Add("d");
        var style = $"color:{fgc}" + (bgc is null ? "" : $";background:{bgc}");
        // Non-ASCII glyphs are pinned to the cell grid so font fallback widths can't shift the layout.
        var body = new StringBuilder();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var g = (string)e.Current;
            if (g.Length == 1 && g[0] < 0x2500 && g[0] != '●' && g[0] != '•') body.Append(WebUtility.HtmlEncode(g));
            else body.Append("<span class='c'>").Append(WebUtility.HtmlEncode(g)).Append("</span>");
        }
        return $"<span class='{string.Join(' ', cls)}' style='{style}'>{body}</span>";
    }

    public static void Screenshot(string html, string png, int cols, int rows)
    {
        var browser = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        }.FirstOrDefault(File.Exists);
        if (browser is null) return;
        var width = (int)Math.Ceiling(cols * 8.25) + 32;
        var height = rows * 18 + 34 + 26;
        var profile = Path.Combine(Path.GetTempPath(), "termcapture-profile");
        var psi = new ProcessStartInfo(browser)
        {
            ArgumentList =
            {
                "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
                $"--user-data-dir={profile}", "--force-device-scale-factor=1.5",
                $"--window-size={width},{height}", $"--screenshot={png}", new Uri(html).AbsoluteUri,
            },
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) p.Kill(true);
    }
}
