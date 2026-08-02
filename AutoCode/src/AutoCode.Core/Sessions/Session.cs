// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Sessions;

/// <summary>
/// The conversation state for one Auto Code run: everything <c>--resume</c> needs to pick it back up.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];

    /// <summary>Short human label, generated from the first user message.</summary>
    public string Title { get; set; } = "";

    public string WorkspaceRoot { get; set; } = "";

    public string ProviderName { get; set; } = "";

    public string ModelId { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }

    /// <summary>The transcript, excluding the system prompt (which is rebuilt on every load).</summary>
    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>Summaries produced by earlier compactions, kept so history is never silently lost.</summary>
    public List<string> CompactionSummaries { get; set; } = [];

    [JsonIgnore]
    public int UserTurnCount => Messages.Count(m => m.Role == ChatRole.User);

    /// <summary>First line of the first user message, trimmed for display.</summary>
    public string DeriveTitle()
    {
        var first = Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text;

        if (string.IsNullOrWhiteSpace(first))
            return "(empty session)";

        var line = first.ReplaceLineEndings(" ").Trim();
        return line.Length <= 60 ? line : line[..60] + "…";
    }
}
