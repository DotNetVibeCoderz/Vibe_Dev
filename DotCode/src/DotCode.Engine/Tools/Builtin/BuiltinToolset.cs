using DotCode.Engine.Agent;

namespace DotCode.Engine.Tools.Builtin;

public static class BuiltinToolset
{
    public static List<Tool> Create(AgentRuntime runtime) =>
    [
        new AgentTool { Registry = runtime.Extensions },
        new BashTool(),
        new BashOutputTool(),
        new KillShellTool(),
        new PowerShellTool(),
        new GlobTool(),
        new GrepTool(),
        new LspTool(),
        new ReadTool(),
        new EditTool(),
        new WriteTool(),
        new NotebookEditTool(),
        new WebFetchTool(),
        new WebSearchTool(),
        new TodoWriteTool(),
        new SkillTool { Registry = runtime.Extensions },
        new AskUserQuestionTool(),
        new ExitPlanModeTool(),
    ];
}
