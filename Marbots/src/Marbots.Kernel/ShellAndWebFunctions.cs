using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Marbots.Abstractions;

namespace Marbots.Kernel;

public sealed partial class RunShellFunction : KernelFunctionBase
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public override FunctionDescriptor Descriptor { get; } = new(
        "run_shell",
        IsWindows
            ? "Run a PowerShell command in the workspace folder and return stdout/stderr and the exit code. Use for builds, tests, python/node scripts, git, etc."
            : "Run a bash command in the workspace folder and return stdout/stderr and the exit code. Use for builds, tests, python/node scripts, git, etc.",
        Schema(("command", "string", "The command line to execute", true),
               ("timeout_seconds", "integer", "Timeout in seconds (default 120, max 600)", false)),
        "shell", PermissionCategory.ProcessExecution, RiskLevel.High, 600);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var command = call.Require("command");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(call.GetInt("timeout_seconds", 120), 1, 600));
        Directory.CreateDirectory(ctx.WorkspacePath);
        var psi = IsWindows
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-lc", command } };
        psi.WorkingDirectory = ctx.WorkspacePath;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["MARBOTS_BOT"] = ctx.Bot.Id;
        psi.Environment["MARBOTS_TASK"] = ctx.TaskId;

        using var proc = new Process { StartInfo = psi };
        var output = new BoundedBuffer(40_000);
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return FunctionResult.Fail($"Command timed out after {timeout.TotalSeconds:0}s.\n{output}");
        }
        proc.WaitForExit(); // flush async readers
        var text = output.ToString();
        return new FunctionResult(proc.ExitCode == 0, $"exit code: {proc.ExitCode}\n{(text.Length == 0 ? "(no output)" : text)}");
    }

    /// <summary>Keeps the head and the tail of long outputs; the end of a build log is usually what matters.</summary>
    private sealed class BoundedBuffer(int max)
    {
        private readonly StringBuilder _head = new();
        private readonly Queue<string> _tail = new();
        private int _tailLen;
        private long _dropped;
        private readonly Lock _lock = new();

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                if (_head.Length < max / 2) { _head.AppendLine(line); return; }
                _tail.Enqueue(line);
                _tailLen += line.Length + 1;
                while (_tailLen > max / 2 && _tail.Count > 0) { _tailLen -= _tail.Dequeue().Length + 1; _dropped++; }
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                var sb = new StringBuilder(_head.ToString());
                if (_dropped > 0) sb.Append("…[").Append(_dropped).AppendLine(" lines omitted]…");
                foreach (var l in _tail) sb.AppendLine(l);
                return sb.ToString().TrimEnd();
            }
        }
    }
}

internal static class SharedHttp
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MarbotsBot/0.1; +https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots)");
        return c;
    }
}

public sealed partial class WebFetchFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "web_fetch", "Download a web page (http/https) and return its readable text content.",
        Schema(("url", "string", "Absolute http(s) URL", true),
               ("max_chars", "integer", "Maximum characters to return (default 20000)", false)),
        "web", PermissionCategory.Network, RiskLevel.Low, 60);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!Uri.TryCreate(call.Require("url"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return FunctionResult.Fail("Only absolute http(s) URLs are allowed.");
        using var resp = await SharedHttp.Client.GetAsync(uri, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        var text = type.Contains("html", StringComparison.OrdinalIgnoreCase) ? HtmlToText(body) : body;
        var max = Math.Clamp(call.GetInt("max_chars", 20_000), 500, 100_000);
        return new FunctionResult(resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode} {uri}\n\n{Truncate(text, max)}");
    }

    public static string HtmlToText(string html)
    {
        var s = ScriptStyle().Replace(html, " ");
        s = BlockTags().Replace(s, "\n");
        s = Tags().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|head)[\s\S]*?</\1>", RegexOptions.IgnoreCase)] private static partial Regex ScriptStyle();
    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BlockTags();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\f\v]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\s*\n\s*\n\s*")] private static partial Regex BlankLines();
}

public sealed partial class WebSearchFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "web_search", "Search the web and return the top results (title, url, snippet).",
        Schema(("query", "string", "Search query", true),
               ("max_results", "integer", "Number of results (default 6)", false)),
        "web", PermissionCategory.Network, RiskLevel.Low, 60);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var query = call.Require("query");
        var max = Math.Clamp(call.GetInt("max_results", 6), 1, 15);
        var secrets = ctx.Services.GetService(typeof(ISecretProvider)) as ISecretProvider;
        var tavily = secrets?.Get("TAVILY_API_KEY");
        return tavily is { Length: > 0 }
            ? await TavilyAsync(query, max, tavily, ct)
            : await DuckDuckGoAsync(query, max, ct);
    }

    private static async Task<FunctionResult> TavilyAsync(string query, int max, string key, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            w.WriteNumber("max_results", max);
            w.WriteEndObject();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = new ByteArrayContent(buffer.ToArray()) { Headers = { ContentType = new("application/json") } },
        };
        req.Headers.Authorization = new("Bearer", key);
        using var resp = await SharedHttp.Client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return FunctionResult.Fail($"Search failed: HTTP {(int)resp.StatusCode}");
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var sb = new StringBuilder();
        var i = 0;
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            sb.Append(++i).Append(". ").AppendLine(r.GetProperty("title").GetString())
              .Append("   ").AppendLine(r.GetProperty("url").GetString())
              .Append("   ").AppendLine(Shorten(r.TryGetProperty("content", out var c) ? c.GetString() : ""));
        }
        return FunctionResult.Ok(i == 0 ? "No results." : sb.ToString());
    }

    private static async Task<FunctionResult> DuckDuckGoAsync(string query, int max, CancellationToken ct)
    {
        var html = await SharedHttp.Client.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ct);
        var sb = new StringBuilder();
        var i = 0;
        foreach (Match m in Result().Matches(html))
        {
            var url = WebUtility.HtmlDecode(m.Groups["url"].Value);
            var uddg = Regex.Match(url, @"uddg=([^&]+)");
            if (uddg.Success) url = Uri.UnescapeDataString(uddg.Groups[1].Value);
            var title = WebFetchFunction.HtmlToText(m.Groups["title"].Value);
            var snippet = WebFetchFunction.HtmlToText(m.Groups["snippet"].Value);
            sb.Append(++i).Append(". ").AppendLine(title).Append("   ").AppendLine(url).Append("   ").AppendLine(Shorten(snippet));
            if (i >= max) break;
        }
        return FunctionResult.Ok(i == 0 ? "No results (the search backend may be rate limiting). Try web_fetch on a known URL." : sb.ToString());
    }

    private static string Shorten(string? s) => s is null ? "" : s.Length > 300 ? s[..300] + "…" : s;

    [GeneratedRegex("""<a[^>]+class="result__a"[^>]+href="(?<url>[^"]+)"[^>]*>(?<title>[\s\S]*?)</a>[\s\S]*?class="result__snippet"[^>]*>(?<snippet>[\s\S]*?)</a>""")]
    private static partial Regex Result();
}
