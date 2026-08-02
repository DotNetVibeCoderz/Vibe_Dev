// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Permissions;

/// <summary>
/// Persistent allow / ask / deny lists. Each entry is a rule string of the form
/// <c>Tool</c> or <c>Tool(argument-pattern)</c>, for example:
/// <c>Read</c>, <c>Bash(git status)</c>, <c>Bash(npm run *)</c>, <c>Write(src/**)</c>.
/// Deny wins over allow.
/// </summary>
public sealed class PermissionRules
{
    public List<string> Allow { get; set; } = [];
    public List<string> Ask { get; set; } = [];
    public List<string> Deny { get; set; } = [];

    public PermissionRules Clone() => new()
    {
        Allow = [.. Allow],
        Ask = [.. Ask],
        Deny = [.. Deny],
    };
}
