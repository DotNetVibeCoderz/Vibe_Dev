// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Cli;

/// <summary>One slash command, as the user sees it.</summary>
public sealed record SlashCommand(string Name, string Arguments, string Summary, string Group)
{
    /// <summary>What the user types, with its argument hint.</summary>
    public string Usage => Arguments.Length == 0 ? $"/{Name}" : $"/{Name} {Arguments}";
}

/// <summary>
/// The single list of commands.
///
/// EN: one catalogue, three consumers — the help table, the autocomplete menu, and the dispatcher.
/// Keeping three separate lists is how a CLI ends up advertising a command it no longer has, and
/// completing one it never had.
/// ID: satu katalog untuk tiga pemakai — tabel bantuan, menu autocomplete, dan dispatcher. Tiga
/// daftar terpisah adalah cara sebuah CLI berakhir mengiklankan perintah yang sudah tidak ada.
/// </summary>
public static class SlashCommandCatalog
{
    public const string Context = "Context";
    public const string Session = "Session";
    public const string Configuration = "Configuration";
    public const string Workflow = "Workflow";
    public const string Inspect = "Inspect";

    public static IReadOnlyList<SlashCommand> All { get; } =
    [
        // Context
        new("context", "", "Token usage and the instruction files in effect", Context),
        new("compact", "[instruction]", "Summarise the conversation; the instruction steers what is kept", Context),
        new("clear", "", "Start a fresh conversation", Context),
        new("memory", "[note]", "Review or add to AUTOCODE.md, the project's standing instructions", Context),
        new("btw", "[question]", "Ask on a side thread — answered without touching the main conversation", Context),

        // Session
        new("rename", "<title>", "Name this session", Session),
        new("resume", "", "List saved sessions for this workspace", Session),
        new("sessions", "", "Same as /resume", Session),
        new("branch", "", "Fork the conversation, to explore an alternative", Session),
        new("undo", "[n]", "Drop the last n exchanges and carry on from there", Session),
        new("rewind", "[n]", "Same as /undo", Session),
        new("export", "[path]", "Write the transcript to a markdown file", Session),
        new("recap", "", "One-paragraph summary of this session so far", Session),

        // Configuration
        new("model", "[id]", "Show or switch the model", Configuration),
        new("provider", "[name]", "Show or switch the provider profile", Configuration),
        new("effort", "[level]", "Reasoning depth: off, low, medium, high, max", Configuration),
        new("permissions", "[mode]", "Show or set ask, acceptEdits, plan, bypassPermissions", Configuration),
        new("config", "", "Where settings are read from, and how to edit them", Configuration),
        new("theme", "[name]", "Switch the colour theme: auto, plain", Configuration),
        new("language", "<en|id>", "Interface and reply language", Configuration),

        // Workflow
        new("plan", "", "Enter plan mode: research only, no changes", Workflow),
        new("diff", "[path]", "Show uncommitted changes", Workflow),
        new("code-review", "", "Review the working diff for defects", Workflow),
        new("security-review", "", "Review the working diff for vulnerabilities", Workflow),
        new("init", "", "Generate an AUTOCODE.md for this project", Workflow),
        new("index", "", "Build the semantic code index", Workflow),

        // Inspect
        new("status", "", "Provider, model, workspace, permissions, session", Inspect),
        new("cost", "", "Token and cost accounting", Inspect),
        new("tools", "", "Tools available to the model", Inspect),
        new("agents", "", "Subagents that can be dispatched", Inspect),
        new("teams", "<name> <brief>", "Run an agent team on a brief", Inspect),
        new("fork", "<agent> <brief>", "Hand a side-task to a subagent, off the main conversation", Inspect),
        new("skills", "", "Installed skills", Inspect),
        new("mcp", "", "MCP server connections", Inspect),
        new("tasks", "", "Subagents running right now", Inspect),
        new("about", "", "About Auto Code", Inspect),
        new("help", "", "List these commands", Inspect),
        new("exit", "", "Leave Auto Code", Inspect),
    ];

    public static IReadOnlyList<string> Groups { get; } =
        [Context, Session, Configuration, Workflow, Inspect];

    /// <summary>
    /// Commands whose name starts with the typed prefix, plus any installed skills.
    /// </summary>
    public static IReadOnlyList<SlashCommand> Match(string prefix, IEnumerable<string> skillNames)
    {
        var needle = prefix.TrimStart('/');

        var skills = skillNames.Select(s => new SlashCommand(s, "[arguments]", "Skill", "Skills"));

        return
        [
            .. All.Concat(skills)
                  .Where(c => c.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                  .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The longest prefix every match shares, for Tab completion.
    /// Returns the typed text unchanged when the matches diverge immediately.
    /// </summary>
    public static string CommonPrefix(IReadOnlyList<SlashCommand> matches)
    {
        if (matches.Count == 0)
            return "";

        var candidate = matches[0].Name;

        foreach (var match in matches.Skip(1))
        {
            var length = 0;
            while (length < candidate.Length && length < match.Name.Length &&
                   char.ToLowerInvariant(candidate[length]) == char.ToLowerInvariant(match.Name[length]))
            {
                length++;
            }

            candidate = candidate[..length];

            if (candidate.Length == 0)
                break;
        }

        return candidate;
    }
}
