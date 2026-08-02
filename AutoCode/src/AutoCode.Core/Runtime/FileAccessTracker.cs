// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Security.Cryptography;
using System.Text;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Utilities;

namespace AutoCode.Core.Runtime;

/// <summary>
/// Remembers what the agent has read, so an edit can be refused when the agent is patching
/// a file it never opened, or one that changed on disk since it looked.
/// </summary>
public sealed class FileAccessTracker : IFileAccessTracker
{
    private readonly Dictionary<string, byte[]> _hashes = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly Lock _gate = new();

    public void RecordRead(string absolutePath, string content) => Record(absolutePath, content);

    public void RecordWrite(string absolutePath, string content) => Record(absolutePath, content);

    public bool HasRead(string absolutePath)
    {
        lock (_gate)
            return _hashes.ContainsKey(absolutePath);
    }

    public bool HasChangedSinceRead(string absolutePath, string currentContent)
    {
        lock (_gate)
        {
            if (!_hashes.TryGetValue(absolutePath, out var seen))
                return false;

            return !seen.AsSpan().SequenceEqual(Hash(currentContent));
        }
    }

    public IReadOnlyCollection<string> ReadFiles
    {
        get
        {
            lock (_gate)
                return [.. _hashes.Keys];
        }
    }

    private void Record(string absolutePath, string content)
    {
        lock (_gate)
            _hashes[absolutePath] = Hash(content);
    }

    private static byte[] Hash(string content) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(content));
}

/// <summary>Plain in-memory task list backing the TodoWrite tool.</summary>
public sealed class TodoList : ITodoList
{
    private IReadOnlyList<TodoItem> _items = [];

    public IReadOnlyList<TodoItem> Items => _items;

    public event Action<IReadOnlyList<TodoItem>>? Changed;

    public void Replace(IReadOnlyList<TodoItem> items)
    {
        _items = items;
        Changed?.Invoke(items);
    }
}

/// <summary>Default <see cref="IAgentServices"/> implementation for a live session.</summary>
public sealed class AgentServices : IAgentServices, IDisposable
{
    private readonly HttpClient _http;
    private bool _ownsHttp;

    public AgentServices(
        Configuration.AutoCodeOptions options,
        string workspaceRoot,
        IAgentUserInterface ui,
        HttpClient? http = null)
    {
        Options = options;
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        Ui = ui;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        if (_ownsHttp)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCode/1.0 (+Gravicode Studios)");
    }

    public Configuration.AutoCodeOptions Options { get; }

    public string WorkspaceRoot { get; }

    public IAgentUserInterface Ui { get; }

    public IFileAccessTracker Files { get; } = new FileAccessTracker();

    public ITodoList Todos { get; } = new TodoList();

    public ISubagentDispatcher? Subagents { get; set; }

    public ISemanticCodeIndex? SemanticIndex { get; set; }

    public HttpClient Http => _http;

    /// <summary>Resolves and validates a tool path argument against the workspace.</summary>
    public string ResolvePath(string path) => WorkspacePath.Resolve(WorkspaceRoot, path);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
            _ownsHttp = false;
        }
    }
}
