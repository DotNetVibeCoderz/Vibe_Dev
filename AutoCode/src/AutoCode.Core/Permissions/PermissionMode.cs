// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Permissions;

/// <summary>How aggressively Auto Code asks before acting.</summary>
public enum PermissionMode
{
    /// <summary>Prompt before every mutating or executing tool call. The safe default.</summary>
    Ask = 0,

    /// <summary>File edits inside the workspace run unattended; shell and network still prompt.</summary>
    AcceptEdits = 1,

    /// <summary>Read-only reconnaissance. Every mutating tool is refused so the agent can only produce a plan.</summary>
    Plan = 2,

    /// <summary>No prompts at all. Intended for sandboxes and CI, never for an untrusted workspace.</summary>
    BypassPermissions = 3,
}
