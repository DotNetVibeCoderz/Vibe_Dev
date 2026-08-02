// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;

namespace AutoCode.Tools;

/// <summary>Maintains the visible plan for the current turn.</summary>
public sealed class TodoWriteTool : ToolBase
{
    public override string Name => "TodoWrite";

    public override string Description =>
        """
        Create and update the task list for the current piece of work.

        Use it when a task needs three or more distinct steps, and skip it for trivial one-step work.
        Mark exactly one task in_progress at a time, flip it to completed the moment it is genuinely
        done, and never mark something completed while its tests fail or its implementation is partial.
        """;

    public override ToolCapability Capability => ToolCapability.None;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "description": "The full task list, replacing any previous one.",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "description": "Imperative description, e.g. \"Add auth middleware\"." },
                  "activeForm": { "type": "string", "description": "Present continuous form, e.g. \"Adding auth middleware\"." },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                },
                "required": ["content", "status"]
              }
            }
          },
          "required": ["todos"]
        }
        """;

    public override string Summarize(JsonElement arguments) => "TodoWrite";

    protected override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var raw = invocation.TryGetArray("todos");
        var items = new List<TodoItem>(raw.Count);

        foreach (var entry in raw)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var content = entry.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

            if (content.Length == 0)
                continue;

            var status = entry.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : "pending";

            var activeForm = entry.TryGetProperty("activeForm", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;

            items.Add(new TodoItem(content, status switch
            {
                "in_progress" => TodoStatus.InProgress,
                "completed" => TodoStatus.Completed,
                _ => TodoStatus.Pending,
            }, activeForm));
        }

        var inProgress = items.Count(i => i.Status == TodoStatus.InProgress);
        if (inProgress > 1)
            return ValueTask.FromResult(ToolResult.Fail("Only one task may be in_progress at a time."));

        invocation.Services.Todos.Replace(items);

        var done = items.Count(i => i.Status == TodoStatus.Completed);
        return ValueTask.FromResult(ToolResult.Ok(
            $"Task list updated: {done}/{items.Count} complete.",
            $"TodoWrite → {done}/{items.Count} complete"));
    }
}

/// <summary>Fetches a URL and hands the text back to the model.</summary>
public sealed class WebFetchTool : ToolBase
{
    public override string Name => "WebFetch";

    public override string Description =>
        """
        Fetch a URL over HTTP(S) and return its textual content.

        - HTML is reduced to readable text; JSON and plain text come back verbatim.
        - Content is truncated at 60000 characters.
        - Only http and https are allowed.
        """;

    public override ToolCapability Capability => ToolCapability.AccessesNetwork;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Absolute http(s) URL to fetch." },
            "max_length": { "type": "integer", "description": "Maximum characters to return. Defaults to 60000." }
          },
          "required": ["url"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"WebFetch({Peek(arguments, "url") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var url = invocation.GetString("url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ToolResult.Fail($"'{url}' is not an absolute http(s) URL.");
        }

        var maxLength = Math.Clamp(invocation.TryGetInt("max_length") ?? 60_000, 500, 500_000);

        using var response = await invocation.Services.Http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return ToolResult.Fail($"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? HtmlToText(body)
            : body;

        if (text.Length > maxLength)
            text = text[..maxLength] + $"\n\n… truncated at {maxLength:N0} characters.";

        return ToolResult.Ok(text, $"WebFetch({uri.Host}) → {text.Length:N0} chars");
    }

    /// <summary>
    /// Strips markup down to readable prose. Deliberately regex-free and dependency-free:
    /// the goal is feeding a model, not rendering a page.
    /// </summary>
    internal static string HtmlToText(string html)
    {
        var output = new StringBuilder(html.Length / 2);
        var skipDepth = 0;
        var i = 0;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var close = html.IndexOf('>', i);
                if (close < 0)
                    break;

                var tag = html[(i + 1)..close].TrimStart('/').Split([' ', '\t', '\n', '\r', '>'], 2)[0].ToLowerInvariant();
                var isClosing = html[i + 1] == '/';

                if (tag is "script" or "style" or "head" or "noscript" or "svg")
                    skipDepth = isClosing ? Math.Max(0, skipDepth - 1) : skipDepth + 1;
                else if (skipDepth == 0 && tag is "p" or "br" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                    output.Append('\n');

                i = close + 1;
                continue;
            }

            if (skipDepth == 0)
                output.Append(html[i]);

            i++;
        }

        var text = System.Net.WebUtility.HtmlDecode(output.ToString());

        // Collapse the run of blank lines that markup stripping inevitably leaves behind.
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

        return string.Join('\n', lines);
    }
}

/// <summary>Dispatches an isolated subagent.</summary>
public sealed class TaskTool : ToolBase
{
    public override string Name => "Task";

    public override string Description =>
        """
        Run a specialised subagent in its own context and return only its final report.

        Use it when a step would otherwise flood the main context — sweeping a large codebase for a
        pattern, or researching an area before deciding what to change. The subagent cannot see this
        conversation, so its prompt must be self-contained.
        """;

    public override ToolCapability Capability => ToolCapability.ReadsFiles | ToolCapability.WritesFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "subagent_type": { "type": "string", "description": "Name of the subagent to run." },
            "description": { "type": "string", "description": "Short (3-5 word) label for the task." },
            "prompt": { "type": "string", "description": "Self-contained instructions for the subagent." }
          },
          "required": ["subagent_type", "prompt"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"Task({Peek(arguments, "subagent_type") ?? "?"}: {Peek(arguments, "description") ?? "…"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var dispatcher = invocation.Services.Subagents;

        if (dispatcher is null)
            return ToolResult.Fail("Subagents are not available in this context (nested dispatch is not permitted).");

        var agentName = invocation.GetString("subagent_type");
        var prompt = invocation.GetString("prompt");
        var description = invocation.TryGetString("description");

        if (!dispatcher.AvailableAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase))
        {
            var available = dispatcher.AvailableAgents.Count == 0
                ? "none are defined"
                : string.Join(", ", dispatcher.AvailableAgents);

            return ToolResult.Fail($"Unknown subagent '{agentName}'. Available: {available}.");
        }

        var report = await dispatcher
            .RunAsync(agentName, prompt, description, cancellationToken)
            .ConfigureAwait(false);

        return ToolResult.Ok(report, $"Task({agentName}) → {report.Length:N0} chars");
    }
}

/// <summary>Embedding-backed semantic search over the workspace.</summary>
public sealed class CodeSearchTool : ToolBase
{
    public override string Name => "CodeSearch";

    public override string Description =>
        """
        Search the codebase by meaning rather than by literal text.

        Use it when you know what a piece of code does but not what it is called — "where do we
        validate refresh tokens" will find the code even when none of those words appear in it.
        Fall back to Grep when you know the exact symbol.
        """;

    public override ToolCapability Capability => ToolCapability.ReadsFiles;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Natural-language description of the code you want." },
            "limit": { "type": "integer", "description": "Maximum number of snippets. Defaults to 10." }
          },
          "required": ["query"]
        }
        """;

    public override string Summarize(JsonElement arguments) =>
        $"CodeSearch({Peek(arguments, "query") ?? "?"})";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var index = invocation.Services.SemanticIndex;

        if (index is null)
            return ToolResult.Fail("The semantic index is disabled. Enable enableSemanticIndex in settings, or use Grep.");

        if (!index.IsReady)
            await index.BuildAsync(null, cancellationToken).ConfigureAwait(false);

        var query = invocation.GetString("query");
        var limit = Math.Clamp(invocation.TryGetInt("limit") ?? 10, 1, 50);

        var hits = await index.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);

        if (hits.Count == 0)
            return ToolResult.Ok("No semantically similar code was found.");

        var output = new StringBuilder();
        foreach (var hit in hits)
        {
            output.Append($"--- {hit.RelativePath}:{hit.StartLine}-{hit.EndLine} (score {hit.Score:F3})\n")
                  .Append(hit.Snippet.TrimEnd())
                  .Append("\n\n");
        }

        return ToolResult.Ok(output.ToString(), $"CodeSearch({query}) → {hits.Count} snippets");
    }
}

/// <summary>
/// Runs the project's configured verification commands.
/// This is the "verify results" phase of the loop made explicit and callable.
/// </summary>
public sealed class VerifyTool(AutoCodeOptions options) : ToolBase
{
    public override string Name => "Verify";

    public override string Description =>
        $"""
        Run this project's verification commands and report the result.

        Configured commands: {string.Join("; ", options.VerifyCommands)}

        Call this after making changes, before reporting work as complete. If a command fails, read
        the output and fix the cause rather than reporting success.
        """;

    public override ToolCapability Capability => ToolCapability.ExecutesCommands;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "only": { "type": "string", "description": "Run just the configured command containing this substring." }
          }
        }
        """;

    public override string Summarize(JsonElement arguments) => "Verify()";

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var filter = invocation.TryGetString("only");

        var commands = filter is { Length: > 0 }
            ? options.VerifyCommands.Where(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList()
            : options.VerifyCommands;

        if (commands.Count == 0)
            return ToolResult.Fail("No verification commands are configured (settings: verifyCommands).");

        var report = new StringBuilder();
        var failures = 0;

        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (fileName, arguments) = BashTool.BuildShellInvocation(options, command);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = invocation.WorkspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var ok = process.ExitCode == 0;
            if (!ok) failures++;

            report.Append(ok ? "PASS  " : "FAIL  ").Append(command).Append('\n');

            if (!ok)
            {
                var detail = (stdout + "\n" + stderr).Trim();
                if (detail.Length > 8_000)
                    detail = detail[^8_000..];
                report.Append(detail).Append("\n\n");
            }
        }

        var summary = $"{commands.Count - failures}/{commands.Count} verification command(s) passed.";

        return failures == 0
            ? ToolResult.Ok($"{summary}\n\n{report}", summary)
            : ToolResult.Fail($"{summary}\n\n{report}");
    }
}
