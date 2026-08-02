// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Permissions;

/// <summary>Result of an approval prompt.</summary>
public enum PermissionOutcome
{
    /// <summary>Run this one call.</summary>
    Allow = 0,

    /// <summary>Run it, and persist a rule so the same shape never asks again.</summary>
    AllowAlways = 1,

    /// <summary>Refuse this call; the model is told why and may try another route.</summary>
    Deny = 2,

    /// <summary>Refuse and persist a deny rule.</summary>
    DenyAlways = 3,

    /// <summary>Refuse and end the turn — the user wants to redirect the agent.</summary>
    Abort = 4,
}

/// <summary>An approval answer, optionally carrying feedback for the model.</summary>
public sealed record PermissionDecision(PermissionOutcome Outcome, string? Reason = null, string? Rule = null)
{
    public bool IsAllowed => Outcome is PermissionOutcome.Allow or PermissionOutcome.AllowAlways;

    public static PermissionDecision Allow { get; } = new(PermissionOutcome.Allow);

    public static PermissionDecision Deny(string reason) => new(PermissionOutcome.Deny, reason);
}
