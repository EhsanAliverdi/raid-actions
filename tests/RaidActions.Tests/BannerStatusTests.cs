using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>The banner's status fields (task, next, goal, switch) as the engine writes them.</summary>
public class BannerStatusTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_area_that_reports_nothing_shows_its_name_as_step_1_of_1_and_the_turn_and_switch_are_written()
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true }, Pacing = new PacingSettings { SessionMinutes = 0 } },
            Turn = new TurnView("Smasher8465", "killer8465", Start.AddMinutes(30)),
        };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(20)) h.Stop = true; };
        await new AutoplayEngine([new ClassicArenaActivity()], game, host, false).RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

        var s = host.Playing.Last();
        Assert.Equal("Classic Arena", s.Task!.Task);
        Assert.Equal(1, s.Task.Step);
        Assert.Equal(1, s.Task.Steps);
        Assert.Equal("Smasher8465", s.Goal!.Who);
        Assert.True(s.Goal.Planned >= s.Goal.Done);
        Assert.Equal("killer8465", s.Switch!.To);
        Assert.Equal(Start.AddMinutes(30), s.Switch.AtUtc);
        Assert.True(s.Switch.Earlier);
        // Old readers: the old fields are still there.
        Assert.NotNull(s.Detail);
    }

    [Fact]
    public async Task An_activity_report_reaches_the_status_and_clears_with_the_turn()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true }, Pacing = new PacingSettings { SessionMinutes = 0 } } };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(5)) h.Stop = true; };
        var reporter = new ReportingActivity();
        await new AutoplayEngine([reporter], game, host, false).RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        var t = host.Playing.Select(p => p.Task).First(t => t is { Step: 3 })!;
        Assert.Equal("Faction Wars (Dark Elves): gearing up Kael", t.Task);
        Assert.Equal(7, t.Steps);
        Assert.Equal("moving 2 pieces", t.Detail);
    }

    private sealed class ReportingActivity : IActivity
    {
        public string Id => "fake-report";
        public string Name => "Fake";
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("test");
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
        {
            ctx.Report("Faction Wars (Dark Elves): gearing up Kael", "crypt stage 6, sim 64%", 3, 7, "moving 2 pieces");
            return Task.FromResult("done");
        }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }
}
