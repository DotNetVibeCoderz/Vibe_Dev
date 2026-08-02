// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Permissions;
using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The interactive terminal surface.
///
/// EN: assistant prose is written as it streams rather than buffered and re-rendered, because a
/// response that appears word by word reads as responsive while a response that appears all at once
/// reads as a hang. Tool calls use a two-line shape — the call, then its result indented beneath —
/// so a long run stays scannable.
/// ID: teks asisten ditulis saat streaming, bukan ditahan lalu digambar ulang, agar terasa responsif.
/// Pemanggilan tool memakai dua baris — pemanggilan lalu hasilnya menjorok — agar mudah dipindai.
/// </summary>
public sealed class ConsoleUserInterface(Theme theme, bool showThinking) : IAgentUserInterface
{
    private bool _assistantLineOpen;
    private bool _thinkingOpen;

    /// <summary>Set while a prompt is on screen so streaming output cannot interleave with it.</summary>
    public bool IsPrompting { get; private set; }

    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case AssistantThinkingEvent thinking when showThinking:
                if (!_thinkingOpen)
                {
                    CloseAssistantLine();
                    AnsiConsole.Markup($"[{theme.Thinking}]✻ thinking… [/]");
                    _thinkingOpen = true;
                }

                AnsiConsole.Markup($"[{theme.Thinking}]{Escape(Collapse(thinking.Text))}[/]");
                break;

            case AssistantThinkingEvent:
                break;

            case AssistantTextEvent { IsFinal: true }:
                CloseAssistantLine();
                break;

            case AssistantTextEvent text:
                CloseThinkingLine();

                if (!_assistantLineOpen)
                {
                    AnsiConsole.WriteLine();
                    _assistantLineOpen = true;
                }

                // Raw write: the payload is model prose, not markup, and must not be interpreted.
                Console.Write(text.Text);
                break;

            case ToolCallStartedEvent tool:
                CloseAssistantLine();
                CloseThinkingLine();
                AnsiConsole.MarkupLine($"[{theme.ToolBullet}]⏺[/] [{theme.ToolName}]{Escape(tool.Summary)}[/]");
                break;

            case ToolCallCompletedEvent completed:
                {
                    var colour = completed.Success ? theme.Muted : theme.Error;
                    var glyph = completed.Success ? "⎿" : "✗";
                    var elapsed = completed.ElapsedMs >= 1000 ? $" ({completed.ElapsedMs / 1000.0:F1}s)" : "";

                    AnsiConsole.MarkupLine(
                        $"  [{colour}]{glyph}  {Escape(FirstLine(completed.Display))}{elapsed}[/]");
                    break;
                }

            case ToolCallDeniedEvent denied:
                AnsiConsole.MarkupLine($"  [{theme.Error}]⎿  {Escape(FirstLine(denied.Reason))}[/]");
                break;

            case TodoUpdatedEvent todos:
                RenderTodos(todos.Items);
                break;

            case SubagentStartedEvent started:
                CloseAssistantLine();
                AnsiConsole.MarkupLine($"[{theme.Accent}]◆[/] [{theme.ToolName}]{Escape(started.AgentName)}[/] [{theme.Muted}]{Escape(started.Description)}[/]");
                break;

            case SubagentCompletedEvent completed:
                AnsiConsole.MarkupLine($"  [{theme.Muted}]⎿  {Escape(completed.AgentName)} finished ({completed.ElapsedMs / 1000.0:F1}s)[/]");
                break;

            case CompactionEvent compaction:
                CloseAssistantLine();
                AnsiConsole.MarkupLine(
                    $"[{theme.Muted}]⟳ Compacted context: {compaction.MessagesBefore} → {compaction.MessagesAfter} messages[/]");
                break;

            case NoticeEvent notice:
                CloseAssistantLine();
                var noticeColour = notice.Severity switch
                {
                    NoticeSeverity.Warning => theme.Warning,
                    NoticeSeverity.Success => theme.Success,
                    _ => theme.Muted,
                };
                AnsiConsole.MarkupLine($"[{noticeColour}]{Escape(notice.Message)}[/]");
                break;

            case ErrorEvent error:
                CloseAssistantLine();
                AnsiConsole.MarkupLine($"[{theme.Error}]✗ {Escape(error.Message)}[/]");
                if (error.Detail is { Length: > 0 } detail)
                    AnsiConsole.MarkupLine($"[{theme.Muted}]{Escape(detail)}[/]");
                break;

            case TurnCompletedEvent:
                CloseAssistantLine();
                break;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<PermissionDecision> RequestPermissionAsync(
        PermissionRequest request,
        CancellationToken cancellationToken)
    {
        CloseAssistantLine();
        CloseThinkingLine();
        IsPrompting = true;

        try
        {
            var body = new Rows(
                new Markup($"[{theme.ToolName}]{Escape(request.Summary)}[/]"),
                request.Detail is { Length: > 0 } detail
                    ? new Markup($"[{theme.Muted}]{Escape(Clip(detail, 600))}[/]")
                    : new Markup(""));

            AnsiConsole.Write(new Panel(body)
            {
                Header = new PanelHeader($" {DescribeCapability(request.Capability)} ", Justify.Left),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(theme.AccentColor),
                Padding = new Padding(1, 0, 1, 0),
            });

            const string allowOnce = "Yes";
            var allowAlways = $"Yes, and don't ask again for {request.SuggestedRule ?? request.ToolName}";
            const string deny = "No, tell Auto Code what to do differently";
            const string abort = "No, and stop this turn";

            var choice = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title($"[{theme.Prompt}]Allow this?[/]")
                    .HighlightStyle(new Style(theme.AccentColor))
                    .AddChoices(allowOnce, allowAlways, deny, abort),
                cancellationToken).ConfigureAwait(false);

            if (choice == allowOnce)
                return PermissionDecision.Allow;

            if (choice == allowAlways)
                return new PermissionDecision(PermissionOutcome.AllowAlways, null, request.SuggestedRule);

            if (choice == abort)
                return new PermissionDecision(PermissionOutcome.Abort, "The user stopped the turn.");

            var reason = await AnsiConsole.PromptAsync(
                new TextPrompt<string>($"[{theme.Prompt}]What should it do instead?[/]")
                    .AllowEmpty(),
                cancellationToken).ConfigureAwait(false);

            return new PermissionDecision(
                PermissionOutcome.Deny,
                string.IsNullOrWhiteSpace(reason) ? "The user declined." : reason);
        }
        finally
        {
            IsPrompting = false;
        }
    }

    private void RenderTodos(IReadOnlyList<TodoItem> items)
    {
        CloseAssistantLine();

        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            var (glyph, colour) = item.Status switch
            {
                TodoStatus.Completed => ("✔", theme.Success),
                TodoStatus.InProgress => ("▶", theme.Accent),
                _ => ("○", theme.Muted),
            };

            var text = item.Status == TodoStatus.InProgress && item.ActiveForm is { Length: > 0 }
                ? item.ActiveForm
                : item.Content;

            AnsiConsole.MarkupLine($"  [{colour}]{glyph} {Escape(text)}[/]");
        }
    }

    private void CloseAssistantLine()
    {
        if (!_assistantLineOpen)
            return;

        Console.WriteLine();
        _assistantLineOpen = false;
    }

    private void CloseThinkingLine()
    {
        if (!_thinkingOpen)
            return;

        Console.WriteLine();
        _thinkingOpen = false;
    }

    private static string DescribeCapability(ToolCapability capability)
    {
        if ((capability & ToolCapability.ExecutesCommands) != 0) return "Run command";
        if ((capability & ToolCapability.WritesFiles) != 0) return "Modify files";
        if ((capability & ToolCapability.AccessesNetwork) != 0) return "Network access";
        return "Permission";
    }

    private static string FirstLine(string value)
    {
        var newline = value.IndexOf('\n');
        var line = newline < 0 ? value : value[..newline];
        return Clip(line.TrimEnd(), 160);
    }

    private static string Collapse(string value) => value.ReplaceLineEndings(" ");

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string Escape(string value) => Markup.Escape(value);
}
