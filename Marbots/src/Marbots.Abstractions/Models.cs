namespace Marbots.Abstractions;

/// <summary>Well-known identifiers used across the platform.</summary>
public static class WellKnown
{
    public const string BossManId = "boss-man";
    public const string LocalHostId = "local-default";
    public const string UserAuthor = "user";
    public const string SharedMemoryOwner = "shared";
    public const string Credits = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";
    public const string CreditsEn = "Created by Gravicode Studios, led by Kang Fadhil";
}

public enum AutoLearnMode { Off, MemoryOnly, SuggestSkills }

public enum BotStatus { Ready, Running, Paused, Archived, Degraded }

public enum TaskState
{
    Queued, Preparing, Running, WaitingForTool, WaitingForAgent, WaitingForHuman,
    Completed, Failed, Cancelled, TimedOut
}

public static class TaskStateExtensions
{
    public static bool IsTerminal(this TaskState s) =>
        s is TaskState.Completed or TaskState.Failed or TaskState.Cancelled or TaskState.TimedOut;
}

public enum DelegationMode { Auto, Suggest, Manual }

public enum RiskLevel { Low, Medium, High, Critical }

public enum PermissionCategory
{
    ReadOnly, WorkspaceWrite, Network, ProcessExecution, DestructiveFilesystem,
    ExternalCommunication, AgentControl, Admin
}

public enum ApprovalState { Pending, Approved, Rejected, Expired }

public enum ApprovalScope { Once, Session }

public enum MemoryKind { Semantic, Episodic, Procedural, Relational, Artifact }

/// <summary>A durable AI teammate. Logical identity, separate from any runtime instance.</summary>
public sealed class BotDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Description { get; set; } = "";
    public string Persona { get; set; } = "";
    public string Color { get; set; } = "#3346D3";
    /// <summary>
    /// Which model this bot uses: "default" (or empty) follows the workspace default model, a profile name
    /// ("coding"), or a direct "provider/model" pair ("azure/gpt-5.6-luna").
    /// </summary>
    public string ModelProfile { get; set; } = "default";
    public bool ShortTermMemory { get; set; } = true;
    public bool LongTermMemory { get; set; } = true;
    public AutoLearnMode AutoLearn { get; set; } = AutoLearnMode.Off;
    public List<string> Skills { get; set; } = [];
    public List<string> McpServers { get; set; } = [];
    public List<string> KernelFunctions { get; set; } = [];
    public string PermissionProfile { get; set; } = "developer-safe";
    public string HostRef { get; set; } = WellKnown.LocalHostId;
    public int MaxSteps { get; set; } = 24;
    public int CompactionThresholdTokens { get; set; } = 24_000;
    public bool IsSystem { get; set; }
    public string? TemplateId { get; set; }
    public BotStatus Status { get; set; } = BotStatus.Ready;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A reusable starting point for creating bots, shown in the Template Gallery.</summary>
public sealed class BotTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Role { get; set; } = "";
    public string Description { get; set; } = "";
    public string Persona { get; set; } = "";
    public string Color { get; set; } = "#3346D3";
    public string ModelProfile { get; set; } = "default";
    public List<string> Skills { get; set; } = [];
    public List<string> McpServers { get; set; } = [];
    public List<string> KernelFunctions { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string PermissionProfile { get; set; } = "developer-safe";
    public AutoLearnMode AutoLearn { get; set; } = AutoLearnMode.Off;
    public bool LongTermMemory { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatThread
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New thread";
    public string BotId { get; set; } = WellKnown.BossManId;
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public string? ParentThreadId { get; set; }
    /// <summary>Rolling summary of compacted history.</summary>
    public string? Summary { get; set; }
    /// <summary>Messages with Seq &lt;= this value are represented by <see cref="Summary"/>.</summary>
    public long SummaryUpToSeq { get; set; }
    /// <summary>Messages with Seq &lt;= this value are hidden from the model after a context reset.</summary>
    public long ContextStartSeq { get; set; }
    public int CompactionCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public long Seq { get; set; }
    /// <summary>user | assistant | tool | system</summary>
    public string Role { get; set; } = "user";
    public string Author { get; set; } = WellKnown.UserAuthor;
    public string Content { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? TaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A durable unit of work, independent of chat threads.</summary>
public sealed class TaskRecord
{
    public string Id { get; set; } = "";
    public string? ParentTaskId { get; set; }
    public string RootTaskId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string BotId { get; set; } = "";
    public string AssignedBy { get; set; } = WellKnown.UserAuthor;
    /// <summary>Delegation depth: 0 for user-initiated tasks.</summary>
    public int Depth { get; set; }
    /// <summary>Where this task's own transcript is stored (the chat thread for root tasks, a task transcript otherwise).</summary>
    public string TranscriptId { get; set; } = "";
    public string Objective { get; set; } = "";
    public List<string> Constraints { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
    public TaskState State { get; set; } = TaskState.Queued;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public string? CurrentActivity { get; set; }
    public int Steps { get; set; }
    /// <summary>The model ("provider/model") that served the latest step of this task.</summary>
    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ApprovalRequest
{
    public string Id { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string BotId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Arguments { get; set; } = "{}";
    public PermissionCategory Category { get; set; }
    public RiskLevel Risk { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Optional human-readable summary (Markdown), e.g. a delegation plan.</summary>
    public string? Summary { get; set; }
    public ApprovalState State { get; set; } = ApprovalState.Pending;
    public ApprovalScope Scope { get; set; } = ApprovalScope.Once;
    public string? ResolvedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class MemoryRecord
{
    public string Id { get; set; } = "";
    public string Owner { get; set; } = "";
    public MemoryKind Kind { get; set; } = MemoryKind.Semantic;
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";
    public double Confidence { get; set; } = 0.8;
    public string Sensitivity { get; set; } = "internal";
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record MemoryQuery(string Text, IReadOnlyList<string> Owners, int Limit = 6);

public sealed record MemoryMatch(MemoryRecord Record, double Score);

public sealed class SkillInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Trust { get; set; } = "Local";
    public string Source { get; set; } = "local";
    public List<string> RequiresTools { get; set; } = [];
    public bool NeedsNetwork { get; set; }
    public bool NeedsShell { get; set; }
    public bool HasScripts { get; set; }
    public bool Pending { get; set; }
}

public sealed class McpServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>stdio | http</summary>
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    /// <summary>Environment variables. Values of the form <c>secret:NAME</c> are resolved from the secret provider.</summary>
    public Dictionary<string, string> Env { get; set; } = [];
    public string? Url { get; set; }
    public string Trust { get; set; } = "Local";
    public string PermissionProfile { get; set; } = "developer";
    public bool Enabled { get; set; } = true;
    public bool IsCatalogEntry { get; set; }
}

public sealed class ScheduleJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BotId { get; set; } = WellKnown.BossManId;
    public string Prompt { get; set; } = "";
    /// <summary>Five-field cron expression (minute hour day month weekday). Empty when <see cref="RunAt"/> is used.</summary>
    public string Cron { get; set; } = "";
    public DateTimeOffset? RunAt { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public bool Enabled { get; set; } = true;
    public string? ThreadId { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? LastTaskId { get; set; }
    public int RunCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HostInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>local | vm | docker | remote</summary>
    public string Kind { get; set; } = "local";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int ProcessorCount { get; set; }
    public long TotalMemoryMb { get; set; }
    public long ProcessWorkingSetMb { get; set; }
    public string AgentVersion { get; set; } = "";
    public string Status { get; set; } = "Online";
    public DateTimeOffset LastHeartbeat { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModelProfile
{
    public string Name { get; set; } = "default";
    public string Provider { get; set; } = "azure";
    public string Model { get; set; } = "gpt-5-mini";
    public List<string> Fallbacks { get; set; } = [];
    public int MaxOutputTokens { get; set; } = 8000;
    public decimal InputCostPerMTok { get; set; } = 0.25m;
    public decimal OutputCostPerMTok { get; set; } = 2m;
}

public sealed class ProviderConfig
{
    public string Name { get; set; } = "";
    /// <summary>azure-openai | openai | mock</summary>
    public string Kind { get; set; } = "openai";
    public string Endpoint { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Models/deployments offered for this provider in the model picker (e.g. gpt-5-mini, gpt-5.6-luna).</summary>
    public List<string> Models { get; set; } = [];
}

/// <summary>Workspace-wide runtime settings changed from the UI, CLI or API.</summary>
public sealed class WorkspaceSettings
{
    public const string SingletonId = "workspace";
    public string Id { get; set; } = SingletonId;
    /// <summary>Allow every "ask" action without a human approval (dangerous).</summary>
    public bool DangerouslySkipApprovals { get; set; }
    /// <summary>Auto: Boss Man delegates on its own. Suggest: every delegation plan waits for the user's approval.</summary>
    public DelegationMode Delegation { get; set; } = DelegationMode.Auto;
    public string? ChangedBy { get; set; }
    public DateTimeOffset? ChangedAt { get; set; }
}

/// <summary>Kinds of external messaging channels.</summary>
public static class ChannelKinds
{
    /// <summary>Embeddable web chat widget served by Marbots.</summary>
    public const string WebChat = "webchat";
    /// <summary>Generic JSON webhook in, HTTP POST out (Zapier, n8n, Power Automate, Teams workflows…).</summary>
    public const string Webhook = "webhook";
    public const string Telegram = "telegram";
    public const string Slack = "slack";
    public const string WhatsApp = "whatsapp";
    public const string Discord = "discord";

    public static readonly IReadOnlyList<string> All = [WebChat, Webhook, Telegram, Slack, WhatsApp, Discord];
}

/// <summary>An external channel (Telegram, Slack, WhatsApp, web chat, webhook…) routed to a bot.</summary>
public sealed class ChannelConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ChannelKinds.WebChat;
    /// <summary>The bot that answers this channel (Boss Man by default).</summary>
    public string BotId { get; set; } = WellKnown.BossManId;
    public bool Enabled { get; set; } = true;
    /// <summary>Non-secret settings, e.g. outboundUrl, phoneNumberId, welcome.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];
    /// <summary>Secret settings by role (token, signingSecret, verifyToken, inboundSecret) → secret name in the secret store.</summary>
    public Dictionary<string, string> SecretRefs { get; set; } = [];
    /// <summary>Optional allow-list of sender ids; empty = everyone.</summary>
    public List<string> AllowedSenders { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long MessagesIn { get; set; }
    public long MessagesOut { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Maps an external conversation (chat id, Slack channel, phone number…) to a Marbots thread.</summary>
public sealed class ChannelConversation
{
    /// <summary><c>{channelId}|{conversationId}</c></summary>
    public string Id { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string? SenderName { get; set; }
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kinds of triggers that start a bot without a person typing.</summary>
public static class TriggerKinds
{
    /// <summary>An HTTP POST to /api/v1/hooks/{id} (authenticated with the trigger secret).</summary>
    public const string Webhook = "webhook";
    /// <summary>A platform event, e.g. a task of bot X completed.</summary>
    public const string Event = "event";
}

/// <summary>Starts a bot from a webhook call or a platform event. Prompt placeholders: {{payload}}, {{bot}}, {{result}}, {{task}}.</summary>
public sealed class TriggerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = TriggerKinds.Webhook;
    /// <summary>Bot that runs the prompt.</summary>
    public string BotId { get; set; } = WellKnown.BossManId;
    public string PromptTemplate { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Webhook: name of the secret (in the secret store) that callers must present.</summary>
    public string? SecretRef { get; set; }
    /// <summary>Event: the event type to react to (TaskStateChanged by default).</summary>
    public string EventType { get; set; } = EventTypes.TaskStateChanged;
    /// <summary>Event: only events of this bot (empty = any bot).</summary>
    public string? SourceBotId { get; set; }
    /// <summary>Event: for TaskStateChanged, the state to react to (Completed by default).</summary>
    public string? EventData { get; set; } = "Completed";
    public string? ThreadId { get; set; }
    public long FireCount { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Usage statistics of a skill, used to evaluate learned skills (promote / roll back).</summary>
public sealed class SkillStats
{
    public string Id { get; set; } = "";
    public long Loads { get; set; }
    public long Successes { get; set; }
    public long Failures { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public double SuccessRate => Successes + Failures == 0 ? 0 : (double)Successes / (Successes + Failures);
}

/// <summary>A todo item maintained by a bot during a task.</summary>
public sealed class TodoItem
{
    public string Text { get; set; } = "";
    /// <summary>pending | in_progress | done</summary>
    public string Status { get; set; } = "pending";
}
