// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;
using AutoCode.Core.Utilities;

namespace AutoCode.Core.Agents;

/// <summary>A subagent: a named, isolated worker the main agent can dispatch with the Task tool.</summary>
public sealed record AgentDefinition
{
    public required string Name { get; init; }

    /// <summary>When to use this agent. The main agent reads this to decide whether to dispatch it.</summary>
    public required string Description { get; init; }

    /// <summary>System prompt for the isolated run.</summary>
    public required string Prompt { get; init; }

    /// <summary>Tool allow-list. Empty means every tool the session has.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Model override; falls back to the session's model.</summary>
    public string? Model { get; init; }

    /// <summary>Provider profile override; falls back to the session's provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Iteration cap for the subagent's own loop.</summary>
    public int MaxIterations { get; init; } = 40;
}

/// <summary>
/// Loads subagent definitions from disk and configuration.
///
/// EN: file definitions win over configuration ones, and project files win over user files, so a
/// repository can specialise an agent the machine already defines.
/// ID: definisi berkas mengalahkan konfigurasi, dan berkas proyek mengalahkan berkas pengguna.
/// </summary>
public static class AgentDefinitionLoader
{
    public static IReadOnlyDictionary<string, AgentDefinition> Discover(string workspaceRoot, AutoCodeOptions options)
    {
        var agents = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, configured) in options.Agents)
        {
            agents[name] = new AgentDefinition
            {
                Name = name,
                Description = configured.Description,
                Prompt = configured.Prompt,
                Tools = configured.Tools,
                Model = configured.Model,
                Provider = configured.Provider,
                MaxIterations = configured.MaxIterations,
            };
        }

        foreach (var directory in CandidateDirectories(workspaceRoot))
        {
            foreach (var agent in LoadFrom(directory))
                agents[agent.Name] = agent;
        }

        if (agents.Count == 0)
        {
            foreach (var builtIn in BuiltIn)
                agents[builtIn.Name] = builtIn;
        }

        return agents;
    }

    private static IEnumerable<string> CandidateDirectories(string workspaceRoot)
    {
        yield return Path.Combine(ConfigurationLoader.UserHome, "agents");
        yield return Path.Combine(ConfigurationLoader.WorkspaceHome(workspaceRoot), "agents");
        yield return Path.Combine(workspaceRoot, ".claude", "agents");
    }

    public static IEnumerable<AgentDefinition> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
        {
            AgentDefinition? agent = null;

            try
            {
                agent = Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A malformed agent file should not stop the others loading.
            }

            if (agent is not null)
                yield return agent;
        }
    }

    internal static AgentDefinition? Parse(string document, string fallbackName)
    {
        var (fields, body) = FrontMatter.Parse(document);

        if (string.IsNullOrWhiteSpace(body))
            return null;

        return new AgentDefinition
        {
            Name = fields.GetValueOrDefault("name", fallbackName),
            Description = fields.GetValueOrDefault("description", ""),
            Prompt = body,
            Tools = FrontMatter.AsList(fields, "tools"),
            Model = fields.GetValueOrDefault("model"),
            Provider = fields.GetValueOrDefault("provider"),
            MaxIterations = int.TryParse(fields.GetValueOrDefault("max-iterations"), out var max) ? max : 40,
        };
    }

    /// <summary>
    /// The agents Auto Code ships with, used when a workspace defines none of its own.
    /// Each one exists to keep a specific kind of work out of the main context.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> BuiltIn { get; } =
    [
        new()
        {
            Name = "explorer",
            Description =
                "Read-only codebase search. Use when answering a question means sweeping many files " +
                "and you only want the conclusion, not the file dumps.",
            Tools = ["Read", "Glob", "Grep", "List", "CodeSearch"],
            MaxIterations = 30,
            Prompt =
                """
                You are a codebase explorer. You read and search; you never modify anything.

                Work efficiently: start broad with Glob and Grep to find candidates, then Read only
                the parts that matter. Do not read a file end to end when a targeted search answers
                the question.

                Report back with concrete findings: file paths with line numbers, the relevant code,
                and a direct answer to what was asked. Do not speculate about code you did not read,
                and say so explicitly when something could not be found.
                """,
        },
        new()
        {
            Name = "reviewer",
            Description =
                "Reviews changes for correctness bugs. Use after implementing something non-trivial.",
            Tools = ["Read", "Glob", "Grep", "List", "Bash"],
            MaxIterations = 30,
            Prompt =
                """
                You review code for defects that would actually bite in production.

                Look for: logic that is wrong on some input, unhandled error paths, resource leaks,
                race conditions, off-by-one errors, and security issues such as injection or unchecked
                input. Use `git diff` to see what changed.

                Report only findings you can justify with a concrete failure scenario — specific inputs
                or state that produce a wrong result. Style opinions and hypotheticals are noise; leave
                them out. If the change is sound, say so plainly.
                """,
        },
        new()
        {
            Name = "tester",
            Description =
                "Writes and runs tests for existing code. Use when coverage is the goal.",
            Tools = ["Read", "Write", "Edit", "Glob", "Grep", "List", "Bash"],
            MaxIterations = 40,
            Prompt =
                """
                You write tests for code that already exists.

                First read the code under test and the existing test suite, and match its conventions —
                framework, naming, file layout, assertion style. Then write tests that would fail if the
                behaviour regressed: cover the boundaries and the error paths, not just the happy path.

                Run the suite before reporting. A test you have not executed is not a test you can vouch
                for. If a test fails because the code under test is wrong, report that rather than
                weakening the test to make it pass.
                """,
        },
    ];
}
