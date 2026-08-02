// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;

namespace AutoCode.Core.Abstractions;

/// <summary>
/// Per-session service bag handed to every tool. Deliberately small: tools stay testable
/// because everything they touch arrives through here rather than through statics.
/// </summary>
public interface IAgentServices
{
    AutoCodeOptions Options { get; }

    /// <summary>Absolute workspace root for this session.</summary>
    string WorkspaceRoot { get; }

    /// <summary>Terminal/host surface. Tools use it for progress, never for prompting.</summary>
    IAgentUserInterface Ui { get; }

    /// <summary>Tracks which files the agent has read this session, and their content hash at read time.</summary>
    IFileAccessTracker Files { get; }

    /// <summary>The live task list surfaced by the TodoWrite tool.</summary>
    ITodoList Todos { get; }

    /// <summary>Dispatches nested subagents for the Task tool. Null when nesting is not permitted.</summary>
    ISubagentDispatcher? Subagents { get; }

    /// <summary>Embedding-backed code search. Null when the semantic index is disabled.</summary>
    ISemanticCodeIndex? SemanticIndex { get; }

    /// <summary>Shared HTTP client for network-capable tools.</summary>
    HttpClient Http { get; }
}

/// <summary>
/// Records file reads so the Edit tool can refuse to patch a file the agent has not seen,
/// and detect a file that changed underneath it since the read.
/// </summary>
public interface IFileAccessTracker
{
    void RecordRead(string absolutePath, string content);

    void RecordWrite(string absolutePath, string content);

    bool HasRead(string absolutePath);

    /// <summary>True when the file on disk no longer matches what the agent last saw.</summary>
    bool HasChangedSinceRead(string absolutePath, string currentContent);

    IReadOnlyCollection<string> ReadFiles { get; }
}

/// <summary>Status of a single planned step.</summary>
public enum TodoStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
}

public sealed record TodoItem(string Content, TodoStatus Status, string? ActiveForm = null);

/// <summary>The agent's visible plan for the current turn.</summary>
public interface ITodoList
{
    IReadOnlyList<TodoItem> Items { get; }

    void Replace(IReadOnlyList<TodoItem> items);

    event Action<IReadOnlyList<TodoItem>>? Changed;
}

/// <summary>Runs a named subagent to completion in an isolated context and returns its final report.</summary>
public interface ISubagentDispatcher
{
    IReadOnlyCollection<string> AvailableAgents { get; }

    Task<string> RunAsync(
        string agentName,
        string prompt,
        string? description,
        CancellationToken cancellationToken);
}

/// <summary>Semantic (embedding) search over the workspace.</summary>
public interface ISemanticCodeIndex
{
    /// <summary>True once the index has been built at least once.</summary>
    bool IsReady { get; }

    Task BuildAsync(IProgress<string>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<SemanticHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

public sealed record SemanticHit(string RelativePath, int StartLine, int EndLine, string Snippet, double Score);
