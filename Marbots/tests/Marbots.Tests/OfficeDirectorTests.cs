using System.Numerics;
using Marbots.Abstractions;
using Marbots.Desktop.Office;

namespace Marbots.Tests;

public class OfficeDirectorTests
{
    private static OfficeDirector Office(params string[] workers)
    {
        var d = new OfficeDirector();
        d.SetBots([new BotDefinition { Id = WellKnown.BossManId, Name = "Boss Man" }, .. workers.Select(w => new BotDefinition { Id = w, Name = w })]);
        return d;
    }

    private static AgentEvent Evt(string type, string bot, string? message = null, string? data = null) =>
        new() { Type = type, BotId = bot, Message = message, Data = data };

    private static void Run(OfficeDirector d, float seconds)
    {
        for (var t = 0f; t < seconds; t += 0.05f) d.Update(0.05f);
    }

    [Fact]
    public void Bots_start_at_their_desks_and_the_manager_in_the_office()
    {
        var d = Office("atlas", "wren");
        Assert.Equal(OfficeLayout.ManagerDesk, d.Find(WellKnown.BossManId)!.Position);
        Assert.Equal("manager", d.Find(WellKnown.BossManId)!.Zone);
        Assert.Equal(OfficeLayout.Desk(0, 2), d.Find("atlas")!.Position);
        Assert.All(d.Agents, a => Assert.Equal(BotActivity.Idle, a.Activity));
    }

    [Fact]
    public void Tool_calls_send_the_bot_walking_to_the_matching_station()
    {
        var d = Office("atlas");
        Assert.True(d.Apply(Evt(EventTypes.ToolCallStarted, "atlas", "web_search {\"query\":\"x\"}")));
        var atlas = d.Find("atlas")!;
        Assert.Equal("web", atlas.Zone);
        Assert.Equal(BotActivity.Walking, atlas.Activity);
        Run(d, 20);
        Assert.False(atlas.IsMoving);
        Assert.Equal(BotActivity.Typing, atlas.Activity);
        var zone = OfficeLayout.Zone("web");
        Assert.InRange(atlas.Position.X, zone.Center.X - zone.Size.X / 2, zone.Center.X + zone.Size.X / 2);
        Assert.InRange(atlas.Position.Y, zone.Center.Y - zone.Size.Y / 2, zone.Center.Y + zone.Size.Y / 2);
    }

    [Theory]
    [InlineData("run_shell", "workshop")]
    [InlineData("mcp__github__search", "mcp")]
    [InlineData("delegate_tasks", "meeting")]
    [InlineData("read_file", "library")]
    [InlineData("remember", "desk")]
    public void Tools_map_to_the_same_stations_as_the_web_office(string tool, string zone) =>
        Assert.Equal(zone, OfficeLayout.ZoneForTool(tool));

    [Fact]
    public void Approval_requests_wave_at_the_approval_desk_and_completion_sends_the_bot_home()
    {
        var d = Office("wren");
        d.Apply(Evt(EventTypes.ApprovalRequested, "wren", "run_shell"));
        Run(d, 25);
        var wren = d.Find("wren")!;
        Assert.Equal(("approval", BotActivity.Waving), (wren.Zone, wren.Activity));
        d.Apply(Evt(EventTypes.TaskStateChanged, "wren", data: "Completed"));
        Run(d, 25);
        Assert.Equal(("desk", BotActivity.Idle), (wren.Zone, wren.Activity));
        Assert.True(Vector2.Distance(wren.Home, wren.Position) < 0.01f);
    }

    [Fact]
    public void Bots_sharing_a_station_get_separate_spots()
    {
        var d = Office("a", "b", "c");
        foreach (var id in new[] { "a", "b", "c" }) d.Apply(Evt(EventTypes.ToolCallStarted, id, "run_shell {}"));
        var spots = d.Agents.Where(x => x.Zone == "workshop").Select(x => x.Destination).ToList();
        Assert.Equal(3, spots.Distinct().Count());
    }

    [Fact]
    public void Walks_between_front_and_back_use_the_aisles()
    {
        var route = OfficeAgent.Route(new Vector2(9.5f, 7f), new Vector2(-10.5f, -7.5f)).ToList();
        Assert.Contains(route, p => Math.Abs(p.Y - OfficeLayout.FrontAisleZ) < 0.01f);
        Assert.Contains(route, p => Math.Abs(p.Y - OfficeLayout.BackAisleZ) < 0.01f);
        Assert.Equal(new Vector2(-10.5f, -7.5f), route[^1]);
    }

    [Fact]
    public void Delegation_beams_fade_after_a_few_seconds()
    {
        var d = Office("atlas");
        var t0 = DateTimeOffset.UtcNow;
        d.Apply(Evt(EventTypes.TaskDelegated, "atlas", "research", WellKnown.BossManId), t0);
        Assert.Single(d.Links);
        d.Update(0.1f, t0.AddSeconds(3));
        Assert.Single(d.Links);
        d.Update(0.1f, t0.AddSeconds(7));
        Assert.Empty(d.Links);
    }

    [Fact]
    public void Roster_changes_add_and_remove_robots_and_rearrange_desks()
    {
        var d = Office("atlas", "wren");
        d.SetBots([new BotDefinition { Id = WellKnown.BossManId }, new BotDefinition { Id = "atlas" }, new BotDefinition { Id = "quinn" }, new BotDefinition { Id = "wren", Status = BotStatus.Archived }]);
        Assert.Null(d.Find("wren"));
        Assert.NotNull(d.Find("quinn"));
        Assert.Equal(OfficeLayout.Desk(0, 2), d.Find("atlas")!.Home);
    }

    [Fact]
    public void Streaming_text_brings_the_bot_back_to_its_desk_typing()
    {
        var d = Office("atlas");
        d.Apply(Evt(EventTypes.ToolCallStarted, "atlas", "web_fetch {}"));
        Run(d, 20);
        d.Apply(Evt(EventTypes.AssistantDelta, "atlas", "Hello"));
        Run(d, 20);
        var atlas = d.Find("atlas")!;
        Assert.Equal(("desk", BotActivity.Typing), (atlas.Zone, atlas.Activity));
    }
}
