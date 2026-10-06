using System.Numerics;
using Marbots.Abstractions;

namespace Marbots.Desktop.Office;

/// <summary>What a bot's body is doing; each maps to an animation clip of the robot model.</summary>
public enum BotActivity { Idle, Walking, Typing, Thinking, Waving, Talking }

/// <summary>An area of the office floor (metres; X to the right, Z towards the viewer).</summary>
public sealed record OfficeZone(string Id, string Label, Vector2 Center, Vector2 Size, Vector3 Color);

/// <summary>
/// The office floor plan, the same rooms as the web Office view: stations along the back wall, a row of desks in the
/// middle and the meeting room, manager's office and approval desk at the front.
/// </summary>
public static class OfficeLayout
{
    public const float Width = 30f;
    public const float Depth = 22f;

    /// <summary>Walkways between the back stations and the desks, and between the desks and the front rooms.</summary>
    public const float BackAisleZ = -3.6f;
    public const float FrontAisleZ = 3.2f;

    public static readonly IReadOnlyList<OfficeZone> Zones =
    [
        new("web", "Research desk · web", new(-10.5f, -7.5f), new(6.5f, 5f), new(0.30f, 0.55f, 0.95f)),
        new("library", "Library · files & skills", new(-3.5f, -7.5f), new(6.5f, 5f), new(0.55f, 0.40f, 0.85f)),
        new("workshop", "Workshop · shell", new(3.5f, -7.5f), new(6.5f, 5f), new(0.95f, 0.55f, 0.25f)),
        new("mcp", "Tool room · MCP", new(10.5f, -7.5f), new(6.5f, 5f), new(0.20f, 0.75f, 0.65f)),
        new("meeting", "Meeting room · delegation", new(-9.5f, 7.2f), new(9f, 5.5f), new(0.35f, 0.45f, 0.95f)),
        new("manager", "Manager's office", new(0f, 7.2f), new(8f, 5.5f), new(0.85f, 0.70f, 0.30f)),
        new("approval", "Approval desk", new(9.5f, 7.2f), new(9f, 5.5f), new(0.95f, 0.35f, 0.45f)),
    ];

    public static OfficeZone Zone(string id) => Zones.First(z => z.Id == id);

    /// <summary>Desk position of worker <paramref name="index"/> of <paramref name="count"/> (two rows when crowded).</summary>
    public static Vector2 Desk(int index, int count)
    {
        var perRow = count <= 8 ? Math.Max(count, 1) : (count + 1) / 2;
        var row = index / perRow;
        var col = index % perRow;
        var spacing = Math.Min(3.4f, 26f / perRow);
        var x = (col - (perRow - 1) / 2f) * spacing;
        return new Vector2(x, count <= 8 ? 0f : -1.2f + row * 2.6f);
    }

    /// <summary>Where the manager (Boss Man) sits.</summary>
    public static Vector2 ManagerDesk => Zone("manager").Center + new Vector2(0f, -0.4f);

    /// <summary>Spot <paramref name="slot"/> inside a zone, so bots sharing a station do not overlap.</summary>
    public static Vector2 Slot(OfficeZone zone, int slot)
    {
        var cols = Math.Max(1, (int)(zone.Size.X / 1.6f));
        var col = slot % cols;
        var row = slot / cols;
        var x = zone.Center.X + (col - (cols - 1) / 2f) * 1.5f;
        var z = zone.Center.Y + 0.6f - row * 1.5f;
        return new Vector2(x, z);
    }

    /// <summary>The station for a tool, mirroring the web Office view.</summary>
    public static string ZoneForTool(string tool) => tool switch
    {
        "web_search" or "web_fetch" => "web",
        "run_shell" => "workshop",
        "delegate_tasks" or "list_bots" or "create_bot" or "list_templates" or "schedule_task" => "meeting",
        "remember" or "recall" or "todo_write" or "get_task" => "desk",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "mcp",
        _ => "library",
    };
}

/// <summary>One bot on the floor: where it is, where it is heading and what it does once there.</summary>
public sealed class OfficeAgent(string id, string name, Vector2 home)
{
    public const float WalkSpeed = 1.7f;

    private readonly Queue<Vector2> _path = new();

    public string Id { get; } = id;
    public string Name { get; set; } = name;
    public Vector2 Home { get; set; } = home;
    public Vector2 Position { get; set; } = home;

    /// <summary>Facing angle around the vertical axis (radians, 0 = towards -Z, the back wall).</summary>
    public float Heading { get; set; }

    public string Zone { get; private set; } = "desk";
    public string Label { get; private set; } = "idle";
    public BotActivity StationActivity { get; private set; } = BotActivity.Idle;
    public DateTimeOffset Since { get; private set; } = DateTimeOffset.UtcNow;
    public Vector2 Destination { get; private set; } = home;

    public bool IsMoving => _path.Count > 0;
    public BotActivity Activity => IsMoving ? BotActivity.Walking : StationActivity;

    /// <summary>Sends the bot to <paramref name="target"/>, walking along the aisles instead of through furniture.</summary>
    public void GoTo(string zone, Vector2 target, BotActivity activity, string label)
    {
        Zone = zone;
        Label = label;
        StationActivity = activity;
        Since = DateTimeOffset.UtcNow;
        if (Vector2.Distance(Destination, target) < 0.05f) return;
        Destination = target;
        _path.Clear();
        foreach (var p in Route(Position, target)) _path.Enqueue(p);
    }

    /// <summary>Teleports without walking (used when the office is first shown).</summary>
    public void Place(string zone, Vector2 target, BotActivity activity, string label)
    {
        GoTo(zone, target, activity, label);
        _path.Clear();
        Position = target;
    }

    public void Update(float dt)
    {
        var step = WalkSpeed * dt;
        while (step > 0 && _path.TryPeek(out var next))
        {
            var delta = next - Position;
            var distance = delta.Length();
            if (distance > 1e-4f) Heading = MathF.Atan2(delta.X, -delta.Y);
            if (distance <= step)
            {
                Position = next;
                step -= distance;
                _path.Dequeue();
            }
            else
            {
                Position += delta / distance * step;
                step = 0;
            }
        }
        if (!IsMoving) Heading = FacingAt(Zone);
    }

    /// <summary>Desks and back-wall stations face the back wall; the front rooms face the viewer.</summary>
    private float FacingAt(string zone) => zone is "meeting" or "manager" or "approval" ? MathF.PI : 0f;

    internal static IEnumerable<Vector2> Route(Vector2 from, Vector2 to)
    {
        if (Vector2.Distance(from, to) < 1.2f || Band(from) == Band(to) && Band(from) != 0)
        {
            yield return to;
            yield break;
        }
        // Leave the current area through the nearest aisle, walk along it, then enter the destination.
        var exitZ = Band(from) switch { < 0 => OfficeLayout.BackAisleZ, > 0 => OfficeLayout.FrontAisleZ, _ => Band(to) < 0 ? OfficeLayout.BackAisleZ : OfficeLayout.FrontAisleZ };
        var entryZ = Band(to) switch { < 0 => OfficeLayout.BackAisleZ, > 0 => OfficeLayout.FrontAisleZ, _ => exitZ };
        yield return new Vector2(from.X, exitZ);
        if (Math.Abs(exitZ - entryZ) > 0.01f)
        {
            // Cross between the aisles at the open end of the desk row.
            var sideX = from.X + to.X < 0 ? -OfficeLayout.Width / 2 + 1.2f : OfficeLayout.Width / 2 - 1.2f;
            yield return new Vector2(sideX, exitZ);
            yield return new Vector2(sideX, entryZ);
        }
        yield return new Vector2(to.X, entryZ);
        yield return to;
    }

    /// <summary>-1 for the back stations, 0 for the desk row, 1 for the front rooms.</summary>
    private static int Band(Vector2 p) => p.Y < OfficeLayout.BackAisleZ ? -1 : p.Y > OfficeLayout.FrontAisleZ ? 1 : 0;
}

/// <summary>
/// Turns the Marbots event stream into positions and activities for every bot. Pure logic: the 3D view only reads
/// <see cref="Agents"/> and <see cref="Links"/> every frame.
/// </summary>
public sealed class OfficeDirector
{
    private readonly Dictionary<string, OfficeAgent> _agents = [];
    private readonly List<OfficeLink> _links = [];
    private List<string> _workers = [];

    public IReadOnlyCollection<OfficeAgent> Agents => _agents.Values;

    /// <summary>Recent delegations (manager → worker), shown as beams for a few seconds.</summary>
    public IReadOnlyList<OfficeLink> Links => _links;

    public OfficeAgent? Find(string botId) => _agents.GetValueOrDefault(botId);

    /// <summary>Synchronises the roster: new bots appear at their desk, removed bots leave.</summary>
    public void SetBots(IEnumerable<BotDefinition> bots)
    {
        var active = bots.Where(b => b.Status != BotStatus.Archived).ToList();
        _workers = active.Where(b => b.Id != WellKnown.BossManId).Select(b => b.Id).ToList();
        foreach (var gone in _agents.Keys.Except(active.Select(b => b.Id)).ToList()) _agents.Remove(gone);
        foreach (var b in active)
        {
            var home = HomeOf(b.Id);
            if (_agents.TryGetValue(b.Id, out var agent))
            {
                agent.Name = b.Name;
                var wasHome = agent.Zone == "desk" && agent.Destination == agent.Home;
                agent.Home = home;
                if (wasHome) agent.GoTo("desk", home, agent.StationActivity, agent.Label);
            }
            else
            {
                agent = new OfficeAgent(b.Id, b.Name, home);
                agent.Place(b.Id == WellKnown.BossManId ? "manager" : "desk", home, BotActivity.Idle, "idle");
                _agents[b.Id] = agent;
            }
        }
    }

    private Vector2 HomeOf(string botId) => botId == WellKnown.BossManId
        ? OfficeLayout.ManagerDesk
        : OfficeLayout.Desk(Math.Max(0, _workers.IndexOf(botId)), _workers.Count);

    /// <summary>Applies one event. Returns true when it changed what is on screen.</summary>
    public bool Apply(AgentEvent e, DateTimeOffset? now = null)
    {
        if (e.BotId is null || !_agents.TryGetValue(e.BotId, out var agent)) return false;
        switch (e.Type)
        {
            case EventTypes.ToolCallStarted:
                var tool = e.Message?.Split(' ', 2)[0] ?? "";
                var zone = OfficeLayout.ZoneForTool(tool);
                if (zone == "desk") SendHome(agent, BotActivity.Typing, tool);
                else Send(agent, zone, zone == "meeting" ? BotActivity.Talking : BotActivity.Typing, tool);
                return true;
            case EventTypes.ApprovalRequested:
                Send(agent, "approval", BotActivity.Waving, "waiting for approval");
                return true;
            case EventTypes.AgentThinkingStarted:
                SendHome(agent, BotActivity.Thinking, "thinking");
                return true;
            case EventTypes.AssistantDelta:
                if (agent.IsMoving || agent.Zone != "desk" && agent.Zone != "manager") SendHome(agent, BotActivity.Typing, "writing");
                else if (agent.StationActivity != BotActivity.Typing) agent.GoTo(agent.Zone, agent.Destination, BotActivity.Typing, "writing");
                return true;
            case EventTypes.TaskDelegated when e.Data is not null:
                _links.Add(new OfficeLink(e.Data, e.BotId, now ?? DateTimeOffset.UtcNow));
                return true;
            case EventTypes.TaskStateChanged when e.Data is "Completed" or "Failed" or "Cancelled":
            case EventTypes.BotStateChanged when e.Message is "Ready":
                SendHome(agent, BotActivity.Idle, "idle");
                return true;
            default:
                return false;
        }
    }

    public void Update(float dt, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        _links.RemoveAll(l => t - l.At > TimeSpan.FromSeconds(6));
        foreach (var a in _agents.Values) a.Update(dt);
    }

    private void SendHome(OfficeAgent agent, BotActivity activity, string label) =>
        agent.GoTo(agent.Id == WellKnown.BossManId ? "manager" : "desk", agent.Home, activity, label);

    private void Send(OfficeAgent agent, string zoneId, BotActivity activity, string label)
    {
        var zone = OfficeLayout.Zone(zoneId);
        var taken = _agents.Values.Where(o => o != agent && o.Zone == zoneId).Select(o => o.Destination).ToList();
        var slot = 0;
        while (taken.Any(p => Vector2.Distance(p, OfficeLayout.Slot(zone, slot)) < 0.1f)) slot++;
        agent.GoTo(zoneId, OfficeLayout.Slot(zone, slot), activity, label);
    }
}

public sealed record OfficeLink(string From, string To, DateTimeOffset At);
