using System.Diagnostics;
using System.Diagnostics.Metrics;
using Marbots.Abstractions;

namespace Marbots.Runtime;

/// <summary>Where telemetry goes. Nothing is exported unless an endpoint is set here or in OTEL_EXPORTER_OTLP_ENDPOINT.</summary>
public sealed class MarbotsTelemetryOptions
{
    /// <summary>OTLP collector, e.g. http://localhost:4317 (gRPC) or http://localhost:4318 (HTTP).</summary>
    public string? OtlpEndpoint { get; set; }
    /// <summary>grpc (default) or http/protobuf.</summary>
    public string Protocol { get; set; } = "grpc";
    /// <summary>Secret name (Marbots:Secrets / environment) whose value is sent as OTLP headers, e.g. "api-key=…".</summary>
    public string? HeadersSecret { get; set; }
    public string ServiceName { get; set; } = "marbots";
    public bool Traces { get; set; } = true;
    public bool Metrics { get; set; } = true;
    public bool Logs { get; set; } = true;
}

/// <summary>
/// OpenTelemetry-compatible instrumentation (System.Diagnostics): one ActivitySource and one Meter, both named "Marbots".
/// Spans follow the OpenTelemetry GenAI semantic conventions (invoke_agent, chat, execute_tool) plus marbots.* attributes;
/// the server exports them over OTLP when Marbots:Telemetry:OtlpEndpoint (or OTEL_EXPORTER_OTLP_ENDPOINT) is set.
/// </summary>
public static class MarbotsTelemetry
{
    public const string Name = "Marbots";

    private static readonly string Version = typeof(MarbotsTelemetry).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly ActivitySource Source = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    /// <summary>gen_ai.client.token.usage — tokens per model call, tagged with gen_ai.token.type (input/output).</summary>
    public static readonly Histogram<long> TokenUsage = Meter.CreateHistogram<long>("gen_ai.client.token.usage", "{token}", "Tokens used per model call");
    /// <summary>gen_ai.client.operation.duration — model call latency.</summary>
    public static readonly Histogram<double> ModelDuration = Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "s", "Model call duration");
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("marbots.tool.calls", "{call}", "Tool calls by tool and outcome");
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>("marbots.tool.duration", "s", "Tool call duration");
    public static readonly Counter<long> Tasks = Meter.CreateCounter<long>("marbots.tasks", "{task}", "Finished tasks by state");
    public static readonly Histogram<double> TaskDuration = Meter.CreateHistogram<double>("marbots.task.duration", "s", "Task duration from start to finish");
    public static readonly Counter<long> Delegations = Meter.CreateCounter<long>("marbots.delegations", "{task}", "Sub-tasks delegated by Boss Man and other managers");
    public static readonly Counter<decimal> Cost = Meter.CreateCounter<decimal>("marbots.cost", "USD", "Estimated model cost");

    public static Activity? StartAgent(BotDefinition bot, TaskRecord task, string tenant)
    {
        var a = Source.StartActivity($"invoke_agent {bot.Name}", ActivityKind.Internal);
        if (a is null) return null;
        a.SetTag("gen_ai.operation.name", "invoke_agent");
        a.SetTag("gen_ai.agent.id", bot.Id);
        a.SetTag("gen_ai.agent.name", bot.Name);
        a.SetTag("marbots.tenant", tenant);
        a.SetTag("marbots.task.id", task.Id);
        a.SetTag("marbots.thread.id", task.ThreadId);
        a.SetTag("marbots.task.depth", task.Depth);
        return a;
    }

    public static Activity? StartModelCall(string setting) =>
        Source.StartActivity("chat", ActivityKind.Client)?.SetTag("gen_ai.operation.name", "chat").SetTag("marbots.model.setting", setting);

    public static void EndModelCall(Activity? a, ModelProfile profile, ModelUsage usage, double seconds, string tenant, string botId, Exception? error = null)
    {
        TagList tags = new() { { "gen_ai.operation.name", "chat" }, { "gen_ai.provider.name", profile.Provider }, { "gen_ai.request.model", profile.Model }, { "marbots.tenant", tenant } };
        ModelDuration.Record(seconds, tags);
        if (error is null)
        {
            var input = tags;
            input.Add("gen_ai.token.type", "input");
            TokenUsage.Record(usage.InputTokens, input);
            var output = tags;
            output.Add("gen_ai.token.type", "output");
            TokenUsage.Record(usage.OutputTokens, output);
            var cost = usage.InputTokens * profile.InputCostPerMTok / 1_000_000m + usage.OutputTokens * profile.OutputCostPerMTok / 1_000_000m;
            if (cost > 0) Cost.Add(cost, new TagList { { "marbots.tenant", tenant }, { "gen_ai.agent.id", botId } });
        }
        if (a is null) return;
        a.DisplayName = $"chat {profile.Model}";
        a.SetTag("gen_ai.provider.name", profile.Provider);
        a.SetTag("gen_ai.request.model", profile.Model);
        a.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
        a.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        if (error is not null) Fail(a, error);
    }

    public static Activity? StartTool(FunctionDescriptor d, string callId) =>
        Source.StartActivity($"execute_tool {d.Name}", ActivityKind.Internal)?
            .SetTag("gen_ai.operation.name", "execute_tool")
            .SetTag("gen_ai.tool.name", d.Name)
            .SetTag("gen_ai.tool.call.id", callId)
            .SetTag("marbots.tool.pack", d.Pack)
            .SetTag("marbots.tool.category", d.Category.ToString());

    public static void EndTool(Activity? a, FunctionDescriptor d, bool success, double seconds, string tenant)
    {
        TagList tags = new() { { "gen_ai.tool.name", d.Name }, { "marbots.tool.pack", d.Pack }, { "marbots.outcome", success ? "ok" : "error" }, { "marbots.tenant", tenant } };
        ToolCalls.Add(1, tags);
        ToolDuration.Record(seconds, tags);
        if (a is null) return;
        a.SetTag("marbots.outcome", success ? "ok" : "error");
        if (!success) a.SetStatus(ActivityStatusCode.Error);
    }

    public static void TaskFinished(TaskRecord task, string tenant)
    {
        TagList tags = new() { { "marbots.task.state", task.State.ToString() }, { "gen_ai.agent.id", task.BotId }, { "marbots.tenant", tenant }, { "marbots.task.root", task.Depth == 0 } };
        Tasks.Add(1, tags);
        if (task.StartedAt is { } s) TaskDuration.Record((DateTimeOffset.UtcNow - s).TotalSeconds, tags);
    }

    public static void Fail(Activity? a, Exception ex)
    {
        if (a is null) return;
        a.SetStatus(ActivityStatusCode.Error, ex.Message);
        a.SetTag("error.type", ex.GetType().FullName);
    }
}
