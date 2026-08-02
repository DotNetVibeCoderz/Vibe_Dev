// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Permissions;

namespace AutoCode.Core.Abstractions;

/// <summary>
/// The host surface the agent talks to. Keeping this an interface is what lets the same loop
/// drive an interactive Spectre.Console REPL, a headless <c>--print</c> run, and a JSON stream.
/// </summary>
public interface IAgentUserInterface
{
    /// <summary>Emits a streamed event from the agent loop.</summary>
    ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the user to approve a pending tool call.
    /// Implementations running non-interactively must answer from policy, never block.
    /// </summary>
    ValueTask<PermissionDecision> RequestPermissionAsync(
        PermissionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Everything the approval prompt needs to describe what is about to happen.</summary>
public sealed record PermissionRequest
{
    public required string ToolName { get; init; }

    /// <summary>Human-readable one-liner, e.g. <c>Bash(npm run build)</c>.</summary>
    public required string Summary { get; init; }

    /// <summary>Optional detail body: a diff for edits, the full command for shell calls.</summary>
    public string? Detail { get; init; }

    public required ToolCapability Capability { get; init; }

    /// <summary>Rule string that "always allow" would persist, e.g. <c>Bash(npm run build:*)</c>.</summary>
    public string? SuggestedRule { get; init; }
}
