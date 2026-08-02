// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Core.Utilities;

namespace AutoCode.Tools;

/// <summary>The set of tools a session exposes to the model.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public IReadOnlyCollection<IAgentTool> Tools
    {
        get
        {
            lock (_gate)
                return [.. _tools.Values];
        }
    }

    public void Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        lock (_gate)
            _tools[tool.Name] = tool;
    }

    public bool TryGet(string name, out IAgentTool tool)
    {
        lock (_gate)
            return _tools.TryGetValue(name, out tool!);
    }

    public IReadOnlyList<IAgentTool> Filter(IReadOnlyCollection<string>? allowedNames)
    {
        lock (_gate)
        {
            if (allowedNames is null || allowedNames.Count == 0)
                return [.. _tools.Values];

            return [.. _tools.Values.Where(t => allowedNames.Any(pattern => Glob.IsMatch(pattern, t.Name)))];
        }
    }

    /// <summary>
    /// Builds the standard toolset. <paramref name="options"/> gates the tools that are only useful
    /// when their backing feature is switched on.
    /// </summary>
    public static ToolRegistry CreateDefault(AutoCodeOptions options)
    {
        var registry = new ToolRegistry();

        registry.Register(new ReadTool());
        registry.Register(new WriteTool());
        registry.Register(new EditTool());
        registry.Register(new MultiEditTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new ListTool());
        registry.Register(new BashTool(options));
        registry.Register(new TodoWriteTool());
        registry.Register(new WebFetchTool());
        registry.Register(new TaskTool());

        if (options.EnableSemanticIndex)
            registry.Register(new CodeSearchTool());

        if (options.VerifyCommands.Count > 0)
            registry.Register(new VerifyTool(options));

        return registry;
    }
}
