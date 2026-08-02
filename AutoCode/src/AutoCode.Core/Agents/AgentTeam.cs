// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using AutoCode.Core.Abstractions;

namespace AutoCode.Core.Agents;

/// <summary>How a team's members relate to each other.</summary>
public enum TeamMode
{
    /// <summary>Every member works the same brief at once; results are merged.</summary>
    Parallel = 0,

    /// <summary>Each member receives the previous member's output as input.</summary>
    Sequential = 1,
}

/// <summary>A named group of subagents dispatched together.</summary>
public sealed class TeamDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public TeamMode Mode { get; set; } = TeamMode.Parallel;
    public List<string> Members { get; set; } = [];

    /// <summary>Optional member that reconciles the others' output. Parallel mode only.</summary>
    public string? Synthesizer { get; set; }
}

/// <summary>
/// Coordinates several subagents on one goal.
///
/// EN: parallel mode fans the same brief out to every member and merges what comes back — useful
/// when independent perspectives are the point, as in review. Sequential mode pipes each member's
/// output into the next, which is what you want when the work has stages.
/// ID: mode paralel mengirim brief yang sama ke semua anggota lalu menggabungkan hasilnya. Mode
/// sekuensial mengalirkan keluaran satu anggota menjadi masukan anggota berikutnya.
/// </summary>
public sealed class AgentTeamCoordinator(
    IReadOnlyDictionary<string, TeamDefinition> teams,
    ISubagentDispatcher dispatcher)
{
    public IReadOnlyCollection<string> AvailableTeams => [.. teams.Keys];

    public bool TryGet(string name, out TeamDefinition team) => teams.TryGetValue(name, out team!);

    public async Task<string> RunAsync(string teamName, string brief, CancellationToken cancellationToken)
    {
        if (!teams.TryGetValue(teamName, out var team))
            return $"Error: no team named '{teamName}'.";

        var members = team.Members
            .Where(m => dispatcher.AvailableAgents.Contains(m, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (members.Count == 0)
            return $"Team '{teamName}' has no members that resolve to a defined subagent.";

        return team.Mode == TeamMode.Sequential
            ? await RunSequentialAsync(team, members, brief, cancellationToken).ConfigureAwait(false)
            : await RunParallelAsync(team, members, brief, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunParallelAsync(
        TeamDefinition team,
        List<string> members,
        string brief,
        CancellationToken cancellationToken)
    {
        var tasks = members.ToDictionary(
            member => member,
            member => dispatcher.RunAsync(member, brief, $"{team.Name} · {member}", cancellationToken));

        await Task.WhenAll(tasks.Values).ConfigureAwait(false);

        var combined = new StringBuilder();
        foreach (var (member, task) in tasks)
        {
            combined.Append("## ").Append(member).Append("\n\n")
                    .Append(task.Result.Trim()).Append("\n\n");
        }

        if (string.IsNullOrWhiteSpace(team.Synthesizer))
            return combined.ToString().TrimEnd();

        var synthesisBrief =
            $"""
            Several agents worked the following brief independently. Reconcile their reports into one
            answer: keep what they agree on, call out where they conflict, and drop anything neither
            of them supports with evidence.

            # Original brief

            {brief}

            # Reports

            {combined}
            """;

        return await dispatcher
            .RunAsync(team.Synthesizer, synthesisBrief, $"{team.Name} · synthesis", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> RunSequentialAsync(
        TeamDefinition team,
        List<string> members,
        string brief,
        CancellationToken cancellationToken)
    {
        var current = brief;
        var trail = new StringBuilder();

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var output = await dispatcher
                .RunAsync(member, current, $"{team.Name} · {member}", cancellationToken)
                .ConfigureAwait(false);

            trail.Append("## ").Append(member).Append("\n\n").Append(output.Trim()).Append("\n\n");

            current =
                $"""
                # Original brief

                {brief}

                # Output from the previous stage ({member})

                {output}

                Continue the work from here.
                """;
        }

        return trail.ToString().TrimEnd();
    }
}
