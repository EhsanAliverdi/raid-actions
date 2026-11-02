using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Holds: what an area learned survives a restart (Ehsan 2026-11-03: "we might start app 30 times in one day").</summary>
public class HoldTests
{
    private static readonly DateTime Start = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    private static (PlayContext Ctx, FakeHost Host) Make()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true } };
        return (new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add), host);
    }

    [Fact]
    public void A_hold_comes_back_after_a_restart_and_ends_on_time()
    {
        var (ctx, host) = Make();
        ctx.Hold("doom-tower:climb", Start.AddDays(1), "the Hard floor was lost");
        ctx.Hold("hydra", Start.AddHours(2), "no key");

        var (again, host2) = Make();
        again.RestoreHolds(host.Events);
        Assert.Equal("the Hard floor was lost", again.Held("doom-tower:climb")?.Why);
        Assert.NotNull(again.Held("hydra"));

        host2.UtcNow = Start.AddHours(3);
        Assert.Null(again.Held("hydra"));
        Assert.NotNull(again.Held("doom-tower:climb"));
    }

    [Fact]
    public void The_latest_hold_wins_and_a_release_lifts_it()
    {
        var (ctx, host) = Make();
        ctx.Hold("chimera", Start.AddHours(5), "no key");
        host.UtcNow = Start.AddMinutes(1);
        ctx.Hold("chimera", Start.AddHours(1), "checked again");

        var (again, _) = Make();
        again.RestoreHolds(host.Events);
        Assert.Equal("checked again", again.Held("chimera")?.Why);
        again.Release("chimera");
        Assert.Null(again.Held("chimera"));
    }
}
