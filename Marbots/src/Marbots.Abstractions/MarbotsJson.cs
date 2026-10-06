using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marbots.Abstractions;

/// <summary>Source-generated JSON metadata for every persisted or transported contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(BotDefinition))]
[JsonSerializable(typeof(List<BotDefinition>))]
[JsonSerializable(typeof(BotTemplate))]
[JsonSerializable(typeof(List<BotTemplate>))]
[JsonSerializable(typeof(ChatThread))]
[JsonSerializable(typeof(List<ChatThread>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(TaskRecord))]
[JsonSerializable(typeof(List<TaskRecord>))]
[JsonSerializable(typeof(ApprovalRequest))]
[JsonSerializable(typeof(List<ApprovalRequest>))]
[JsonSerializable(typeof(MemoryRecord))]
[JsonSerializable(typeof(List<MemoryRecord>))]
[JsonSerializable(typeof(SkillInfo))]
[JsonSerializable(typeof(List<SkillInfo>))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(List<McpServerConfig>))]
[JsonSerializable(typeof(ScheduleJob))]
[JsonSerializable(typeof(List<ScheduleJob>))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(List<HostInfo>))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(List<AgentEvent>))]
[JsonSerializable(typeof(ModelProfile))]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(WorkspaceSettings))]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class MarbotsJsonContext : JsonSerializerContext
{
    private static MarbotsJsonContext? _indented;

    /// <summary>Indented variant for human-facing exports (.marbot manifests).</summary>
    public static MarbotsJsonContext Indented => _indented ??= new MarbotsJsonContext(new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}
