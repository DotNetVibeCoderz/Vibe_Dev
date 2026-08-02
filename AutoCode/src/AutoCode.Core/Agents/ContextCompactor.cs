// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Agents;

/// <summary>
/// Summarises old turns when the context window fills up.
///
/// EN: the recent tail is kept verbatim because that is what the agent is actively working on; the
/// older head is replaced by a written summary. Tool-call and tool-result pairs are never split
/// across the boundary — an orphaned tool result is a hard API error on every provider.
/// ID: bagian akhir percakapan disimpan apa adanya, sementara bagian awal diringkas. Pasangan
/// pemanggilan tool dan hasilnya tidak pernah terpisah, karena hasil tool tanpa pasangan
/// menyebabkan galat pada semua provider.
/// </summary>
public sealed class ContextCompactor(IChatClient summarizer)
{
    private const int KeepRecentMessages = 8;

    public async Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count <= KeepRecentMessages + 2)
            return new CompactionResult(messages, null);

        var splitIndex = FindSafeSplit(messages, messages.Count - KeepRecentMessages);

        if (splitIndex <= 1)
            return new CompactionResult(messages, null);

        var head = messages.Take(splitIndex).ToList();
        var tail = messages.Skip(splitIndex).ToList();

        var summary = await SummarizeAsync(head, cancellationToken).ConfigureAwait(false);

        var compacted = new List<ChatMessage>(tail.Count + 1)
        {
            new(ChatRole.User,
                "Here is a summary of the earlier part of this conversation, which was compacted to " +
                "free context. Treat it as an accurate record of what has already happened:\n\n" + summary),
        };

        compacted.AddRange(tail);

        return new CompactionResult(compacted, summary);
    }

    /// <summary>
    /// Walks backwards to a boundary that does not orphan a tool result. A message carrying
    /// <see cref="FunctionResultContent"/> must stay with the assistant turn that requested it.
    /// </summary>
    private static int FindSafeSplit(IReadOnlyList<ChatMessage> messages, int desired)
    {
        var index = Math.Clamp(desired, 0, messages.Count);

        while (index > 0 && index < messages.Count && CarriesToolResults(messages[index]))
            index--;

        return index;
    }

    private static bool CarriesToolResults(ChatMessage message) =>
        message.Contents.Any(c => c is FunctionResultContent);

    private async Task<string> SummarizeAsync(IReadOnlyList<ChatMessage> head, CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();

        foreach (var message in head)
        {
            var role = message.Role == ChatRole.Assistant ? "assistant"
                     : message.Role == ChatRole.User ? "user"
                     : message.Role == ChatRole.Tool ? "tool"
                     : "system";

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        transcript.Append(role).Append(": ").Append(Clip(text.Text, 4_000)).Append('\n');
                        break;

                    case FunctionCallContent call:
                        transcript.Append("assistant: [called ").Append(call.Name).Append(']').Append('\n');
                        break;

                    case FunctionResultContent result:
                        transcript.Append("tool: ").Append(Clip(result.Result?.ToString() ?? "", 1_500)).Append('\n');
                        break;
                }
            }
        }

        var request = new List<ChatMessage>
        {
            new(ChatRole.System,
                """
                You are compacting a coding session's transcript so the work can continue with less context.

                Write a factual summary that preserves, in this order:
                1. What the user asked for, including any constraints they stated.
                2. Files that were read, created or modified, by path, and what changed in each.
                3. Commands that were run and what they returned — especially failures.
                4. Decisions that were made and the reasoning behind them.
                5. What is still outstanding.

                Be specific: keep file paths, symbol names, error messages and numbers verbatim.
                Do not editorialise, do not add advice, and do not claim anything finished that was not.
                """),
            new(ChatRole.User, transcript.ToString()),
        };

        var response = await summarizer
            .GetResponseAsync(request, new ChatOptions { MaxOutputTokens = 2_000 }, cancellationToken)
            .ConfigureAwait(false);

        return response.Text;
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

public sealed record CompactionResult(IReadOnlyList<ChatMessage> Messages, string? Summary);
