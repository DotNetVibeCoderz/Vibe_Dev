namespace DotCode.Tui.Input;

/// <summary>Ctrl+R reverse incremental search over prompt history (newest first, case-insensitive substring).</summary>
public sealed class HistorySearch(IReadOnlyList<string> history, string original)
{
    public string Query { get; private set; } = "";
    /// <summary>Index into the history of the current match, or -1.</summary>
    public int MatchIndex { get; private set; } = -1;
    /// <summary>True when the last query change or Ctrl+R found nothing (the previous match stays shown).</summary>
    public bool Failed { get; private set; }
    /// <summary>Input text before the search started (restored on cancel).</summary>
    public string Original { get; } = original;

    public string? Match => MatchIndex >= 0 ? history[MatchIndex] : null;

    public void Type(string text)
    {
        Query += text;
        // Keep the current match while it still matches, like readline.
        Find(MatchIndex >= 0 ? MatchIndex : history.Count - 1, skipText: null);
    }

    public void Backspace()
    {
        if (Query.Length == 0) return;
        Query = Query[..^1];
        if (Query.Length == 0) { MatchIndex = -1; Failed = false; return; }
        Find(history.Count - 1, skipText: null);
    }

    /// <summary>Next older match (Ctrl+R again); duplicates of the current match are skipped.</summary>
    public void Older()
    {
        if (Query.Length == 0 || MatchIndex <= 0) { Failed = Query.Length > 0; return; }
        Find(MatchIndex - 1, skipText: Match);
    }

    private void Find(int from, string? skipText)
    {
        for (var i = Math.Min(from, history.Count - 1); i >= 0; i--)
        {
            var h = history[i];
            if (skipText is not null && h == skipText) continue;
            if (h.Contains(Query, StringComparison.OrdinalIgnoreCase))
            {
                MatchIndex = i;
                Failed = false;
                return;
            }
        }
        Failed = true;
    }
}
