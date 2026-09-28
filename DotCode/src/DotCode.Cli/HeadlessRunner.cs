using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;

namespace DotCode.Cli;

/// <summary>Non-interactive mode (<c>dotcode -p</c>): runs one prompt (or a stream-json conversation from stdin) and
/// prints text, a JSON result, or newline-delimited JSON events.</summary>
public static class HeadlessRunner
{
    private static readonly Lock OutGate = new();

    public static async Task<int> RunAsync(AgentRuntime runtime, AgentSession session, CliOptions options, CancellationToken ct)
    {
        var format = options.OutputFormat.ToLowerInvariant();
        var stdout = Console.Out;
        var permissionDenials = new List<string>();

        session.Sink = new DelegateEventSink(e =>
        {
            if (e is ToolCompletedEvent { IsError: true } tc && tc.Output.Contains("was not granted", StringComparison.Ordinal)) permissionDenials.Add(tc.Name);
            switch (format)
            {
                case "stream-json":
                    if (!options.IncludePartialMessages && e is AssistantTextDeltaEvent or AssistantThinkingDeltaEvent or ToolProgressEvent) return;
                    WriteLine(stdout, JsonSerializer.Serialize(e, AbstractionsJsonContext.Default.AgentEvent));
                    break;
                case "text" when options.Runtime.Verbose:
                    switch (e)
                    {
                        case ToolStartedEvent ts: Err($"● {ts.DisplayName}"); break;
                        case ToolCompletedEvent tc2: Err($"  ⎿  {(tc2.IsError ? "Error: " : "")}{tc2.Summary}"); break;
                        case NoticeEvent n: Err($"[{n.Level}] {n.Text}"); break;
                        case RetryEvent r: Err($"Retrying ({r.Attempt}/{r.MaxAttempts}) in {r.DelaySeconds:0.#}s: {r.Reason}"); break;
                    }
                    break;
                case "text":
                    if (e is ErrorEvent err) Err($"Error: {err.Message}");
                    else if (e is RetryEvent r2) Err($"Retrying ({r2.Attempt}/{r2.MaxAttempts}) in {r2.DelaySeconds:0.#}s: {r2.Reason}");
                    break;
            }
        });

        if (format == "stream-json")
            WriteLine(stdout, JsonSerializer.Serialize<AgentEvent>(new SessionStartedEvent(session.Model.Qualified, runtime.Cwd,
                [.. session.GetTools().Select(t => t.Name)], session.Mode.ToString()) { SessionId = session.Id }, AbstractionsJsonContext.Default.AgentEvent));

        await session.FireSessionStartAsync(options.Continue || options.Resume ? "resume" : "startup", ct).ConfigureAwait(false);

        if (options.InputFormat == "stream-json")
        {
            // Each stdin line: {"type":"user","message":{"role":"user","content":"..."}} or {"prompt":"..."}
            TurnResult? last = null;
            while (await Console.In.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                var json = DotCodeJson.Parse(line);
                var text = json.GetString("prompt") ?? json.GetProp("message")?.GetString("content") ?? ExtractContent(json.GetProp("message")?.GetProp("content"));
                if (string.IsNullOrEmpty(text)) continue;
                last = await session.RunTurnAsync(text, null, ct).ConfigureAwait(false);
                WriteResult(stdout, session, last, permissionDenials, "stream-json");
            }
            return last?.IsError == true ? 1 : 0;
        }

        var prompt = options.Prompt ?? "";
        if (Console.IsInputRedirected)
        {
            var piped = await Console.In.ReadToEndAsync(ct).ConfigureAwait(false);
            if (piped.Trim().Length > 0) prompt = prompt.Length > 0 ? $"{prompt}\n\n{piped}" : piped;
        }
        if (prompt.Trim().Length == 0)
        {
            Console.Error.WriteLine("Error: Input must be provided either through stdin or as a prompt argument when using --print");
            return 1;
        }

        var expanded = await CommandExpander.ExpandAsync(session, prompt, ct).ConfigureAwait(false);
        var result = await session.RunTurnAsync(expanded.Prompt, null, ct).ConfigureAwait(false);
        switch (format)
        {
            case "json":
            case "stream-json":
                WriteResult(stdout, session, result, permissionDenials, format);
                break;
            default:
                if (result.IsError && result.Text.Length == 0) Console.Error.WriteLine($"Error: {result.Error}");
                else WriteLine(stdout, result.Text);
                break;
        }
        return result.IsError ? 1 : 0;
    }

    /// <summary>Reads piped stdin. When a prompt argument was given, an idle stdin (an open pipe with no data, as in
    /// some CI runners and agent shells) is ignored after a short wait instead of blocking forever.</summary>
    private static async Task<string> ReadStdinAsync(bool waitForever, CancellationToken ct)
    {
        var read = Console.In.ReadToEndAsync(ct);
        if (waitForever) return await read.ConfigureAwait(false);
        var done = await Task.WhenAny(read, Task.Delay(1500, ct)).ConfigureAwait(false);
        return done == read ? await read.ConfigureAwait(false) : "";
    }

    private static string? ExtractContent(JsonElement? content)
    {
        if (content is not { ValueKind: JsonValueKind.Array } arr) return null;
        var sb = new StringBuilder();
        foreach (var p in arr.EnumerateArray()) if (p.GetString("type") == "text") sb.Append(p.GetString("text"));
        return sb.ToString();
    }

    private static void WriteResult(TextWriter stdout, AgentSession session, TurnResult result, List<string> denials, string format)
    {
        var json = DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "result");
            w.WriteString("subtype", result.IsError ? "error_during_execution" : result.StopReason == StopReason.MaxTokens ? "error_max_turns" : "success");
            w.WriteBoolean("is_error", result.IsError);
            w.WriteNumber("duration_ms", (long)result.Duration.TotalMilliseconds);
            w.WriteNumber("duration_api_ms", (long)session.ApiDuration.TotalMilliseconds);
            w.WriteNumber("num_turns", result.ModelCalls);
            w.WriteString("result", result.Text);
            if (result.Error is not null) w.WriteString("error", result.Error);
            w.WriteString("session_id", session.Id);
            w.WriteString("model", session.Model.Qualified);
            w.WriteNumber("total_cost_usd", session.TotalCostUsd);
            w.WriteStartObject("usage");
            w.WriteNumber("input_tokens", result.Usage.InputTokens);
            w.WriteNumber("output_tokens", result.Usage.OutputTokens);
            w.WriteNumber("cache_read_input_tokens", result.Usage.CacheReadTokens);
            w.WriteNumber("cache_creation_input_tokens", result.Usage.CacheWriteTokens);
            w.WriteNumber("reasoning_tokens", result.Usage.ReasoningTokens);
            w.WriteEndObject();
            w.WriteStartArray("permission_denials");
            foreach (var d in denials) w.WriteStringValue(d);
            w.WriteEndArray();
            w.WriteEndObject();
        });
        WriteLine(stdout, format == "json" ? JsonSerializer.Serialize(json, AbstractionsJsonContext.Default.JsonElement) : json.GetRawText());
    }

    private static void WriteLine(TextWriter w, string text)
    {
        lock (OutGate) { w.WriteLine(text); w.Flush(); }
    }

    private static void Err(string text)
    {
        lock (OutGate) Console.Error.WriteLine(text);
    }
}
