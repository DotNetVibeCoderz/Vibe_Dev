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

    /// <summary>
    /// The raw call arguments.
    ///
    /// EN: passed through so the host can present each tool in the form that makes the risk
    /// legible — a real diff for an edit, the command for a shell call. Core deliberately does not
    /// decide that: what an approval prompt should look like is a question about the terminal, not
    /// about the agent.
    /// ID: diteruskan agar host bisa menampilkan tiap tool dalam bentuk yang membuat risikonya
    /// terbaca — diff untuk penyuntingan, perintah untuk shell. Core sengaja tidak memutuskan itu.
    /// </summary>
    public System.Text.Json.JsonElement? Arguments { get; init; }

    public required ToolCapability Capability { get; init; }

    /// <summary>Rule string that "always allow" would persist, e.g. <c>Bash(npm run build:*)</c>.</summary>
    public string? SuggestedRule { get; init; }
}
