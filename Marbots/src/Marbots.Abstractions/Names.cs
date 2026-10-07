namespace Marbots.Abstractions;

/// <summary>Kernel function packs a bot can enable (<see cref="BotDefinition.KernelFunctions"/>). Use these instead of raw strings.</summary>
public static class KernelPacks
{
    public const string Files = "files";
    public const string Search = "search";
    public const string Shell = "shell";
    public const string Web = "web";
    public const string Memory = "memory";
    public const string Todo = "todo";
    public const string Agents = "agents";
    /// <summary>Computer use: screenshots, mouse and keyboard on the bot's computer (Windows hosts).</summary>
    public const string Desktop = "desktop";
    /// <summary>spawn_subagents: parallel temporary copies of the bot.</summary>
    public const string Subagents = "subagents";
    /// <summary>Boss Man only.</summary>
    public const string Management = "management";

    public static readonly IReadOnlyList<string> All = [Files, Search, Shell, Web, Memory, Todo, Agents, Desktop, Subagents];
}

/// <summary>Permission profiles (<see cref="BotDefinition.PermissionProfile"/>).</summary>
public static class PermissionProfiles
{
    public const string ReadOnly = "read-only";
    public const string WorkspaceWrite = "workspace-write";
    public const string DeveloperSafe = "developer-safe";
    public const string Autonomous = "autonomous";
    /// <summary>Boss Man's profile.</summary>
    public const string Manager = "manager";

    public static readonly IReadOnlyList<string> All = [ReadOnly, WorkspaceWrite, DeveloperSafe, Autonomous, Manager];
}

/// <summary>Builds model settings for <see cref="BotDefinition.ModelProfile"/>.</summary>
public static class ModelRef
{
    /// <summary>Follow the workspace default model.</summary>
    public const string Default = "default";

    /// <summary>A direct provider/model pair, e.g. <c>ModelRef.Of("azure", "gpt-5.6-luna")</c>.</summary>
    public static string Of(string provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (provider.Contains('/')) throw new ArgumentException("Provider names cannot contain '/'.", nameof(provider));
        return $"{provider}/{model}";
    }

    /// <summary>A named model profile configured in Settings.</summary>
    public static string Profile(string name) => name;
}
