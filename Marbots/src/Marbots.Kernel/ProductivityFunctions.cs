using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;

namespace Marbots.Kernel;

public sealed class RememberFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "remember", "Save a durable fact, preference or procedure to your long-term memory so you can recall it in future conversations. Never store secrets.",
        Schema(("content", "string", "The fact to remember, written as a standalone sentence", true),
               ("kind", "string", "semantic | episodic | procedural | relational | artifact", false),
               ("tags", "string", "Comma-separated tags", false)),
        "memory", PermissionCategory.WorkspaceWrite, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!ctx.Bot.LongTermMemory) return FunctionResult.Fail("Long-term memory is disabled for this bot.");
        var content = call.Require("content");
        if (SecretScanner.LooksSensitive(content)) return FunctionResult.Fail("Refused: the content looks like a secret or credential.");
        var store = (IMemoryStore)ctx.Services.GetService(typeof(IMemoryStore))!;
        var kind = Enum.TryParse<MemoryKind>(call.GetString("kind"), true, out var k) ? k : MemoryKind.Semantic;
        var record = new MemoryRecord
        {
            Owner = ctx.Bot.Id,
            Kind = kind,
            Content = content,
            Source = $"task:{ctx.TaskId}",
            Tags = (call.GetString("tags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        };
        await store.WriteAsync(record, ct);
        if (ctx.Services.GetService(typeof(IEventBus)) is IEventBus bus)
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.MemoryWritten, BotId = ctx.Bot.Id, TaskId = ctx.TaskId, ThreadId = ctx.ThreadId, Message = content }, ct);
        return FunctionResult.Ok($"Remembered ({kind}).");
    }
}

public sealed class RecallFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "recall", "Search your long-term memory (and shared workspace memory) for relevant facts.",
        Schema(("query", "string", "What to look for", true)),
        "memory", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var store = (IMemoryStore)ctx.Services.GetService(typeof(IMemoryStore))!;
        var matches = await store.SearchAsync(new MemoryQuery(call.Require("query"), [ctx.Bot.Id, WellKnown.SharedMemoryOwner], 8), ct);
        if (matches.Count == 0) return FunctionResult.Ok("No matching memories.");
        var sb = new StringBuilder();
        foreach (var m in matches)
            sb.Append("- [").Append(m.Record.Kind).Append(", ").Append(m.Record.CreatedAt.ToString("yyyy-MM-dd")).Append("] ").AppendLine(m.Record.Content);
        return FunctionResult.Ok(sb.ToString());
    }
}

/// <summary>Task-scoped todo lists, visible in the UI timeline.</summary>
public sealed class TodoBoard
{
    private readonly ConcurrentDictionary<string, List<TodoItem>> _lists = new();

    public IReadOnlyList<TodoItem> Get(string taskId) => _lists.TryGetValue(taskId, out var l) ? l : [];

    public void Set(string taskId, List<TodoItem> items) => _lists[taskId] = items;

    public void Remove(string taskId) => _lists.TryRemove(taskId, out _);
}

public sealed class TodoWriteFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "todo_write", "Replace your todo list for the current task. Use it to plan multi-step work and to show progress.",
        """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"text":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","done"]}},"required":["text","status"]}}},"required":["items"]}""",
        "todo", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var items = new List<TodoItem>();
        if (call.Arguments.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                items.Add(new TodoItem
                {
                    Text = e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    Status = e.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending",
                });
            }
        }
        var board = (TodoBoard)ctx.Services.GetService(typeof(TodoBoard))!;
        board.Set(ctx.TaskId, items);
        if (ctx.Services.GetService(typeof(IEventBus)) is IEventBus bus)
        {
            await bus.PublishAsync(new AgentEvent
            {
                Type = EventTypes.TodoUpdated, BotId = ctx.Bot.Id, TaskId = ctx.TaskId, ThreadId = ctx.ThreadId,
                Message = $"{items.Count(i => i.Status == "done")}/{items.Count} done",
                Data = JsonSerializer.Serialize(items, MarbotsJsonContext.Default.ListTodoItem),
            }, ct);
        }
        return FunctionResult.Ok($"Todo list updated ({items.Count} items).");
    }
}

public static class SecretScanner
{
    private static readonly string[] Markers = ["-----BEGIN", "sk-", "api_key=", "apikey=", "password=", "AKIA", "ghp_", "xoxb-", "Bearer "];

    public static bool LooksSensitive(string text)
    {
        foreach (var m in Markers)
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class KernelCatalog
{
    /// <summary>All built-in kernel functions. Each bot enables packs (files, search, shell, web, memory, todo).</summary>
    public static IReadOnlyList<IKernelFunction> CreateDefault() =>
    [
        new ReadFileFunction(), new WriteFileFunction(), new EditFileFunction(), new ListFilesFunction(), new DeleteFileFunction(),
        new GrepFunction(),
        new RunShellFunction(),
        new WebFetchFunction(), new WebSearchFunction(),
        new RememberFunction(), new RecallFunction(),
        new TodoWriteFunction(),
    ];

    public static readonly IReadOnlyList<(string Pack, string Description)> Packs =
    [
        ("files", "Read, write, edit, list and delete files in the workspace"),
        ("search", "Regex search across workspace files"),
        ("shell", "Run PowerShell / bash commands (asks for approval by default)"),
        ("web", "Web search and page fetching"),
        ("memory", "Remember and recall long-term facts"),
        ("todo", "Plan and track multi-step work"),
    ];
}
