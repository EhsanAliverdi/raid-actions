using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's Energy on a fake account (plan/energy.md): the priority list, run limits, Super Raid, waiting, inbox, gems.</summary>
public class EnergyActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeEnergyGame Game, AutoplayEngine Engine) Make(EnergyPlaySettings energy, bool dryRun = false, TimeSpan? stopAfter = null)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = energy } };
        var game = new FakeEnergyGame(() => host.UtcNow);
        if (stopAfter is { } after) host.OnDelay = h => { if (h.UtcNow - Start >= after) h.Stop = true; };
        return (host, game, new AutoplayEngine([new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow), host, dryRun));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static EnergyTarget Row(string id, string difficulty = "Normal", int? stage = null, int? runs = null, bool super = false) =>
        new() { Id = id, Difficulty = difficulty, Stage = stage, RunsPerDay = runs, SuperRaid = super };

    private static EnergyPlaySettings On(params EnergyTarget[] rows) => new() { Enabled = true, Targets = [.. rows] };

    [Fact]
    public async Task The_first_row_plays_its_runs_then_the_next_row_takes_the_rest_of_the_energy()
    {
        var (host, game, engine) = Make(On(Row("hard", "Hard", runs: 2), Row("normal")), stopAfter: TimeSpan.FromMinutes(30));
        game.Energy = 100;
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["run Hard 5", "run Hard 5"], game.Calls.Take(2));                 // 2 × 24
        Assert.All(game.Calls.Skip(2).Where(c => c.StartsWith("run")), c => Assert.Equal("run Normal 25", c)); // then 16 each
        Assert.True(game.Energy < 16 + 11);                                            // the rest spent (plus what came back in 30 minutes)
        Assert.Contains(host.Events, e => e.Kind == "battle" && e.Text.Contains("[row hard]"));
    }

    [Fact]
    public async Task Out_of_energy_it_waits_for_regeneration_and_never_buys_with_gems_unless_switched_on()
    {
        var (host, game, engine) = Make(On(Row("normal")) with { Sources = [new() { Resource = "Energy", UseInbox = false }] }, stopAfter: TimeSpan.FromHours(2));
        game.Energy = 10;
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain("refill", game.Calls);
        Assert.Equal(1000, game.Gems);
        var runs = game.Calls.Count(c => c == "run Normal 25");
        Assert.InRange(runs, 2, 3);                                                     // 10 + 40 regenerated in 2 hours: 16 a run
        Assert.Contains(host.Events, e => e.Kind == "battle");
    }

    [Fact]
    public async Task Super_raid_spends_the_multiplier_times_the_energy()
    {
        var (_, game, engine) = Make(On(Row("super", runs: 2, super: true)), stopAfter: TimeSpan.FromMinutes(10));
        game.Energy = 100;
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["run Normal 25 super", "run Normal 25 super"], game.Calls);
        Assert.InRange(game.Energy, 100 - 64, 100 - 64 + 4);
    }

    [Fact]
    public async Task Waiting_for_energy_it_takes_the_inbox_first_then_a_gem_refill_within_the_limits()
    {
        var sources = new List<EnergySource> { new() { Resource = "Energy", UseInbox = true, BuyWithGems = true, MaxRefillsPerDay = 1, MaxGemsPerRefill = 40, KeepGems = 500 } };
        var (host, game, engine) = Make(On(Row("normal")) with { Sources = sources }, stopAfter: TimeSpan.FromMinutes(40));
        game.Energy = 5;
        game.InboxEnergy = 50;
        await engine.RunAsync(once: false, Limit());
        var inbox = game.Calls.IndexOf("inbox");
        var refill = game.Calls.IndexOf("refill");
        Assert.True(inbox >= 0 && refill > inbox);                                      // the inbox before any gem
        Assert.Single(game.Calls, "refill");                                            // one refill a day
        Assert.Equal(970, game.Gems);
        Assert.Contains(host.Events, e => e.Kind == "refill" && e.Text.StartsWith("bought 130 Energy for 30 gems"));
    }

    [Fact]
    public async Task A_refill_above_the_allowed_price_is_not_bought()
    {
        var sources = new List<EnergySource> { new() { Resource = "Energy", UseInbox = false, BuyWithGems = true, MaxRefillsPerDay = 3, MaxGemsPerRefill = 20 } };
        var (host, game, engine) = Make(On(Row("normal")) with { Sources = sources }, stopAfter: TimeSpan.FromMinutes(20));
        game.Energy = 5;
        game.RefillPrice = 50;
        await engine.RunAsync(once: false, Limit());
        Assert.Single(game.Calls, "refill refused");                                    // asked once, not again today
        Assert.Equal(1000, game.Gems);
        Assert.Contains(host.Events, e => e.Kind == "refill" && e.Text.Contains("more than the 20 allowed"));
    }

    [Fact]
    public async Task A_dry_run_shows_the_run_and_spends_nothing()
    {
        var (host, game, engine) = Make(On(Row("normal", runs: 3)), dryRun: true, stopAfter: TimeSpan.FromMinutes(10));
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["show Normal 25"], game.Calls);
        Assert.Equal(100, game.Energy, 0);
        Assert.Contains(host.Events, e => e.Kind == "dry-run");
    }

    [Fact]
    public void A_restart_keeps_today_s_runs_refills_and_learnt_costs()
    {
        var day = DateOnly.FromDateTime(Start.ToLocalTime());
        var activity = new EnergyActivity(new FakeEnergyGame(() => Start));
        activity.Restore(
        [
            new AutoplayEvent(Start, EnergyActivity.AreaId, "battle", "Victory Dragon's Lair Hard stage 5: 24 Energy [row hard]. 60 s"),
            new AutoplayEvent(Start, EnergyActivity.AreaId, "battle", "Defeat Dragon's Lair Hard stage 5: 24 Energy [row hard]. 60 s"),
            new AutoplayEvent(Start, EnergyActivity.AreaId, "refill", "bought 130 Energy for 30 gems (1 of 2 today)"),
            new AutoplayEvent(Start, EnergyActivity.AreaId, "cost", "Dungeon/DragonsLair/Normal/3 costs 8 Energy (seen on a run)"),
        ], day);
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Energy = On(Row("hard", "Hard", runs: 2), Row("normal")) } };
        var status = activity.Status(new PlayContext(host.Settings, new FakeArenaGame(() => Start), host, false), Decision.Off("test"));
        Assert.Equal(2, status.Counters["Dragon's Lair Hard runs"]);
        Assert.Equal(1, status.Counters["Refills"]);
        Assert.Equal(1, status.Counters["Wins"]);
    }
}
