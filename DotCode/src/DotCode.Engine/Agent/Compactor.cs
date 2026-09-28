using System.Text;
using DotCode.Abstractions;
using DotCode.Engine.Hooks;
using DotCode.Engine.Util;

namespace DotCode.Engine.Agent;

/// <summary>Summarizes the conversation window into a single continuation message when context runs low
/// (auto) or on /compact. The full transcript stays on disk; only the model's window is replaced.</summary>
public static class Compactor
{
    private const string Instructions = """
        Your task is to create a detailed summary of the conversation so far. This summary will replace the conversation history, so it must preserve everything needed to continue the work without losing context.

        Structure the summary with these sections:
        1. Primary request and intent: what the user asked for, in detail, including all explicit requirements.
        2. Key technical concepts: technologies, frameworks and patterns involved.
        3. Files and code sections: every file examined, created or modified, why it matters, and important code snippets (with paths).
        4. Errors and fixes: problems encountered, how they were fixed, and any user feedback on them.
        5. Problem solving: what was solved and what is still being investigated.
        6. All user messages: list every non-tool user message (they capture changing intent).
        7. Pending tasks: work explicitly requested that is not finished.
        8. Current work: precisely what was being done immediately before this summary, with file names and snippets.
        9. Next step: the next action directly in line with the most recent request (quote the latest instruction verbatim). Only include it if it follows from the user's explicit requests.

        Respond with the summary only, in markdown.
        """;

    public static async Task CompactAsync(AgentSession session, string? customInstructions, bool automatic, CancellationToken ct)
    {
        var window = PromptBuilder.WindowMessages(session);
        if (window.Count == 0) return;
        var before = session.EstimateContextTokens();

        if (session.Runtime.Hooks.Has(HookEvents.PreCompact))
            await session.Runtime.Hooks.RunAsync(HookEvents.PreCompact, null, w =>
            {
                w.WriteString("trigger", automatic ? "auto" : "manual");
                w.WriteString("custom_instructions", customInstructions ?? "");
            }, session.Id, session.Store?.FilePath ?? "", ct).ConfigureAwait(false);

        session.Emit(new NoticeEvent(NoticeLevel.Info, automatic ? "Context is almost full — compacting conversation…" : "Compacting conversation…"));

        var messages = PromptBuilder.RepairPairing(window);
        var prompt = Instructions + (customInstructions is { Length: > 0 } ci ? "\n\nAdditional instructions from the user:\n" + ci : "");
        messages.Add(Message.User(prompt));
        var model = session.Model;
        var tools = session.GetTools().Select(t => t.ToSchema()).ToList();
        var request = new ModelRequest
        {
            Model = model.Model,
            System = [new SystemBlock("You are a helpful AI assistant tasked with summarizing conversations for a coding agent.")],
            Messages = messages,
            Tools = tools,
            ToolChoice = ToolChoice.None,
            MaxOutputTokens = Math.Min(model.Capabilities.MaxOutputTokens, 16_000),
        };

        var summary = new StringBuilder();
        Usage usage = Usage.Zero;
        await foreach (var ev in model.Provider.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case ContentBlockCompleted { Part: TextPart t }: summary.Append(t.Text); break;
                case MessageStopped { Usage: { } u }: usage = u; break;
            }
        }
        if (summary.Length == 0) throw new InvalidOperationException("Compaction produced an empty summary");

        var text = new StringBuilder();
        text.Append("This session is being continued from a previous conversation that ran out of context. The summary below covers the earlier portion of the conversation.\n\n");
        text.Append(summary.ToString().Trim());
        if (session.Todos.Count > 0)
        {
            text.Append("\n\nCurrent todo list:\n");
            foreach (var t in session.Todos) text.Append("- [").Append(t.Status switch { TodoStatus.Completed => "x", TodoStatus.InProgress => "~", _ => " " }).Append("] ").Append(t.Content).Append('\n');
        }
        var recentFiles = session.FileState.Paths.TakeLast(5).ToList();
        if (recentFiles.Count > 0) text.Append("\n\nRecently accessed files (re-read them before editing): ").Append(string.Join(", ", recentFiles));
        text.Append("\n\nContinue the conversation from where it left off without asking the user further questions; resume the last task you were working on.");

        session.AppendMessage(new Message
        {
            Role = Role.User,
            Content = [new TextPart(text.ToString())],
            IsMeta = true,
            IsCompactSummary = true,
        });
        var after = TextUtil.EstimateTokens(text.ToString()) + PromptBuilder.EstimateSystemTokens(session);
        session.ResetContextTracking(after);
        session.Emit(new ContextCompactedEvent(before, after, automatic));
        _ = usage;
    }
}
