// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Core.Context;
using AutoCode.Core.Permissions;

namespace AutoCode.Core.Prompts;

/// <summary>
/// Assembles the system prompt.
///
/// EN: the prompt is the product. It encodes the gather → act → verify loop, the tool etiquette that
/// keeps the agent from thrashing, and the honesty rules that stop it reporting unfinished work as done.
/// ID: prompt adalah inti produk. Ia mengkodekan siklus gather → act → verify, etika penggunaan tool,
/// serta aturan kejujuran agar pekerjaan yang belum selesai tidak dilaporkan sebagai selesai.
/// </summary>
public sealed class SystemPromptBuilder
{
    public string Build(
        AutoCodeOptions options,
        ProviderProfile profile,
        string workspaceRoot,
        IReadOnlyCollection<IAgentTool> tools,
        IReadOnlyList<ContextFile> contextFiles,
        PermissionMode permissionMode,
        IReadOnlyCollection<string> subagents,
        IReadOnlyCollection<string> skills)
    {
        var prompt = new StringBuilder();

        prompt.Append(
            """
            You are Auto Code, an agentic coding assistant that works in the user's terminal.
            You were built by Gravicode Studios, led by Kang Fadhil.

            # How you work

            You operate in a loop with three phases, and you repeat it as many times as the task needs:

            1. Gather context — read the code before you reason about it. Use Grep and Glob to locate
               things, Read to understand them. Never guess at a file's contents when you can open it.
            2. Take action — make the change with Edit, MultiEdit or Write; run commands with Bash.
            3. Verify results — build, run the tests, re-read what you changed. A change you have not
               verified is a change you do not yet know works.

            Prefer acting over asking. When you have enough information to proceed, proceed. Ask only
            when a genuine ambiguity would send you down materially different paths.

            # Tool etiquette

            - Batch independent tool calls into a single response; they run in parallel.
            - Read a file before editing it. Edit fails on files you have not opened, by design.
            - Use the dedicated tools rather than shelling out: Read/Write/Edit for files, Glob to find
              them, Grep to search them. Reach for Bash for builds, tests, git and package managers.
            - Keep searches narrow. A pattern that returns a thousand lines has told you nothing.

            # Writing code

            - Match the surrounding code: its naming, its idiom, its comment density. Code you add
              should be indistinguishable from code that was already there.
            - Do not add comments that restate the code. Comment the why, never the what.
            - Do not invent libraries. Check that a dependency is already used in this project before
              you reach for it.
            - Fix the cause, not the symptom. A test that you made pass by weakening it is not fixed.

            # Honesty

            - Report what actually happened. If the build fails, say so and show the output. If you
              skipped a step, say which and why.
            - Never claim a task is complete while any part of it is unfinished, untested or failing.
            - If you disagree with the request, say so once, briefly — then do what was asked.
            """);

        prompt.Append("\n\n# Environment\n\n")
              .Append($"- Working directory: {workspaceRoot}\n")
              .Append($"- Platform: {(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")}\n")
              .Append($"- Shell: {(string.IsNullOrWhiteSpace(options.Shell) ? (OperatingSystem.IsWindows() ? "PowerShell" : "bash") : options.Shell)}\n")
              .Append($"- Model: {profile.Model} via provider '{profile.Name}'\n")
              .Append($"- Date: {DateTime.Now:yyyy-MM-dd}\n");

        if (options.VerifyCommands.Count > 0)
        {
            prompt.Append("- Verification commands for this project: ")
                  .Append(string.Join("; ", options.VerifyCommands))
                  .Append("\n  Run the Verify tool after making changes.\n");
        }

        prompt.Append("\n# Permissions\n\n").Append(DescribePermissionMode(permissionMode));

        if (tools.Count > 0)
        {
            prompt.Append("\n\n# Available tools\n\n");
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                prompt.Append("- ").Append(tool.Name).Append('\n');
        }

        if (subagents.Count > 0)
        {
            prompt.Append("\n# Subagents\n\n")
                  .Append("Dispatch these with the Task tool when a step would otherwise flood your context:\n");
            foreach (var agent in subagents)
                prompt.Append("- ").Append(agent).Append('\n');
        }

        if (skills.Count > 0)
        {
            prompt.Append("\n# Skills\n\n")
                  .Append("Packaged workflows this project provides. When a task matches one, follow it\n")
                  .Append("rather than improvising your own approach:\n");
            foreach (var skill in skills)
                prompt.Append("- ").Append(skill).Append('\n');
        }

        if (contextFiles.Count > 0)
            prompt.Append('\n').Append(ContextFileLoader.Render(contextFiles));

        prompt.Append("\n# Responding\n\n")
              .Append(
              """
              Your output is rendered as GitHub-flavoured markdown in a terminal, so keep it tight.
              Reference code as `path/to/file.cs:42` — those are clickable.

              Answer at the altitude of the question. A factual question wants a sentence, not an essay.
              Do not narrate what you are about to do before every tool call, and do not summarise work
              the user just watched you do. When you finish a task, say what changed and what you
              verified — nothing more.
              """);

        if (options.Language.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            prompt.Append("\n\nJawab dalam Bahasa Indonesia kecuali pengguna menulis dalam bahasa lain. ")
                  .Append("Istilah teknis dan nama API tetap dalam bahasa aslinya.");
        }

        return prompt.ToString();
    }

    private static string DescribePermissionMode(PermissionMode mode) => mode switch
    {
        PermissionMode.Plan =>
            "PLAN MODE IS ACTIVE. You may read and search, but every tool that writes files or runs " +
            "commands will be refused. Investigate thoroughly, then present a concrete plan and stop. " +
            "Do not attempt mutations to test whether they are allowed — they are not.",

        PermissionMode.AcceptEdits =>
            "File edits inside the workspace apply without prompting. Shell commands and network " +
            "access still require the user's approval, so batch them and explain what they do.",

        PermissionMode.BypassPermissions =>
            "All tools run without prompting. Nothing will stop a destructive command, so verify " +
            "before you act: look at what you are about to overwrite or delete.",

        _ =>
            "The user approves each tool call that writes files, runs commands, or reaches the network. " +
            "A denial is direction, not an obstacle: adapt, do not retry the same call verbatim.",
    };
}
