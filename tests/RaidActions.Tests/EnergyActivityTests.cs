using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's Energy on a fake account (plan/energy.md): the priority list, run limits, Super Raid, waiting, inbox, gems.</summary>
public class EnergyActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeEnergyGame Game, AutoplayEngine Engine) Make(EnergyPlaySettings energy, bool dryRun = false, TimeSpan? stopAfter = null, bool smart = false)
    {
        // The rows' own Super Raid box unless a test is about the rule.
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = energy, Rules = new BotRules { SmartSuperRaid = smart } } };
        var game = new FakeEnergyGame(() => host.UtcNow);
        if (stopAfter is { } after) host.OnDelay = h => { if (h.UtcNow - Start >= after) h.Stop = true; };
        return (host, game, new AutoplayEngine([new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow), host, dryRun));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static EnergyTarget Row(string id, string difficulty = "Normal", int? stage = null, int? runs = null, bool super = false) =>
        new() { Id = id, Difficulty = difficulty, Stage = stage, RunsPerDay = runs, SuperRaid = super };

    private static EnergyPlaySettings On(params EnergyTarget[] rows) => new() { Enabled = true, Targets = [.. rows] };

    [Fact]
    public async Task A_row_s_own_team_gets_a_gear_review_before_its_first_run_and_then_every_few_hours()
    {
        var row = Row("kael", "Hard") with { Team = [1, 3], GearEveryHours = 2 };
        var (host, game, engine) = Make(On(row), stopAfter: TimeSpan.FromHours(5));
        game.Energy = 300;
        await engine.RunAsync(once: false, Limit());
        Assert.Equal("gear 1,3", game.Calls[0]);
        Assert.InRange(game.Calls.Count(c => c.StartsWith("gear", StringComparison.Ordinal)), 2, 3); // at the start, after 2 h, after 4 h
        Assert.Contains(host.Events, e => e.Kind == "gear" && e.Text.StartsWith("row kael:", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_that_can_start_from_the_result_screen_go_on_in_one_turn_until_the_energy_is_used()
    {
        var (host, game, engine) = Make(On(Row("farm", "Hard")));
        game.Energy = 300;
        game.Chain = true;
        await engine.RunAsync(once: true, Limit());
        var turns = host.Events.Where(e => e.Area == EnergyActivity.AreaId && e.Kind == "turn").ToList();
        // 300 energy, 24 a run: 12 runs, all from the result screen in one turn (2026-11-06: no more stop at 5).
        Assert.Contains(turns, e => e.Text.Contains("(12 runs from the result screen)", StringComparison.Ordinal));

        var (other, single, once) = Make(On(Row("farm", "Hard")));
        single.Energy = 300;
        await once.RunAsync(once: true, Limit());
        Assert.DoesNotContain(other.Events, e => e.Kind == "turn" && e.Text.Contains("from the result screen", StringComparison.Ordinal)); // the long way: one a turn
    }

    /// <summary>An arena whose refresh comes at a time: it wants to act from then until it has played once.</summary>
    private sealed class RefreshAt(DateTime at) : IActivity
    {
        public bool Played { get; private set; }
        public string Id => "classic-arena";
        public string Name => "Classic Arena";
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Played ? Decision.Off("played") : ctx.Utc >= at ? Decision.Now("the free refresh is here") : Decision.Wait(at, "refresh later");
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct) { Played = true; return Task.FromResult("refreshed"); }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Waiting, "", null, [], []);
    }

    [Fact]
    public async Task An_arena_refresh_that_comes_due_ends_the_chain_after_the_battle()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0, ThinkMinMs = 60_000, ThinkMaxMs = 60_000 }, Energy = On(Row("farm", "Hard")) } };
        var game = new FakeEnergyGame(() => host.UtcNow) { Energy = 300, Chain = true };
        var arena = new RefreshAt(Start + TimeSpan.FromMinutes(5));
        host.OnDelay = h => { if (arena.Played) h.Stop = true; };
        var engine = new AutoplayEngine([arena, new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow) { Energy = 50 }, host, false);
        await engine.RunAsync(once: false, Limit());

        Assert.True(arena.Played);
        // The chain ends at the refresh and the arena's turn is next (energy below its cap: no "full" turn first).
        var chainEnd = Assert.Single(host.Events, e => e.Kind == "chain");
        Assert.Contains("Classic Arena", chainEnd.Text);
        var after = host.Events.SkipWhile(e => e != chainEnd).Where(e => e.Kind == "turn").Skip(1).First();
        Assert.Equal("classic-arena", after.Area);
        var before = host.Events.TakeWhile(e => e != chainEnd).Count(e => e.Kind == "battle");
        Assert.InRange(before, 2, 6);   // some runs in a row, not all 12: the refresh came first
    }

    [Fact]
    public async Task A_row_without_its_own_team_or_without_the_setting_gets_no_gear_review()
    {
        var (_, game, engine) = Make(On(Row("a", "Hard") with { GearEveryHours = 2 }, Row("b") with { Team = [1] }), stopAfter: TimeSpan.FromHours(1));
        game.Energy = 100;
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("gear", StringComparison.Ordinal));
    }

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
    public async Task Smart_super_raid_farms_gear_double_while_energy_is_plentiful_then_runs_normal()
    {
        var (_, game, engine) = Make(On(Row("farm")), stopAfter: TimeSpan.FromMinutes(10), smart: true);
        game.Energy = 100;
        await engine.RunAsync(once: false, Limit());
        var runs = game.Calls.Where(c => c.StartsWith("run", StringComparison.Ordinal)).ToList();
        Assert.Equal("run Normal 25 super", runs[0]);                                  // 100 of 124: plenty
        Assert.Contains("run Normal 25", runs);                                        // below half the cap: normal runs
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
