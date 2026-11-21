using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor;
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
        public bool Urgent(PlayContext ctx) => !Played && ctx.Utc >= at;
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
        var chainEnd = Assert.Single(host.Events, e => e.Kind == "chain" && !e.Text.Contains("stop asked"));   // the test ends with a stop: that chain ends too
        Assert.Contains("Classic Arena", chainEnd.Text);
        var after = host.Events.SkipWhile(e => e != chainEnd).Where(e => e.Kind == "turn").Skip(1).First();
        Assert.Equal("classic-arena", after.Area);
        var before = host.Events.TakeWhile(e => e != chainEnd).Count(e => e.Kind == "battle");
        Assert.InRange(before, 2, 6);   // some runs in a row, not all 12: the refresh came first
    }

    /// <summary>An area that always wants a look (an arena's routine read) but is never urgent.</summary>
    private sealed class AlwaysLooking : IActivity
    {
        public int Turns { get; private set; }
        public string Id => "classic-arena";
        public string Name => "Classic Arena";
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("looking at the arena");
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct) { Turns++; return Task.FromResult("looked"); }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Waiting, "", null, [], []);
    }

    [Fact]
    public async Task An_area_that_only_wants_a_look_does_not_end_the_chain()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = On(Row("farm", "Hard")) } };
        var game = new FakeEnergyGame(() => host.UtcNow) { Energy = 300, Chain = true };
        var looking = new AlwaysLooking();
        host.OnDelay = h => { if (game.Calls.Count(c => c.StartsWith("run", StringComparison.Ordinal)) >= 12) h.Stop = true; };
        // Energy first in the list here, so its turn comes; the arena's look must not cut the chain.
        var engine = new AutoplayEngine([new EnergyActivity(game), looking], new FakeArenaGame(() => host.UtcNow) { Energy = 50 }, host, false);
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain(host.Events, e => e.Kind == "chain");
        var first = host.Events.First(e => e.Kind == "turn" && e.Area == EnergyActivity.AreaId);
        // All the energy in one go (300 at 48 a Super Raid: six; since 2026-11-12 a stage won every time is farmed with Super Raid whenever it can be paid).
        Assert.Matches(@"\(([6-9]|\d\d) runs from the result screen\)", first.Text);
    }

    /// <summary>An arena at its token cap that wants a look (2026-11-06: it broke every Campaign chain, and Energy, full too and first, played on).</summary>
    private sealed class FullLooking : IActivity
    {
        public string Id => "classic-arena";
        public string Name => "Classic Arena";
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("looking at the arena");
        public bool Full(PlayContext ctx) => true;
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct) => Task.FromResult("looked");
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Waiting, "", null, [], []);
    }

    [Fact]
    public async Task A_full_area_after_a_full_energy_row_does_not_end_the_chain()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = On(Row("farm", "Hard")) } };
        var game = new FakeEnergyGame(() => host.UtcNow) { Energy = 300, Chain = true };
        host.OnDelay = h => { if (game.Calls.Count(c => c.StartsWith("run", StringComparison.Ordinal)) >= 12) h.Stop = true; };
        var engine = new AutoplayEngine([new EnergyActivity(game), new FullLooking()], new FakeArenaGame(() => host.UtcNow) { Energy = 1000 }, host, false);
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain(host.Events, e => e.Kind == "chain");
    }

    [Fact]
    public async Task Energy_piled_far_over_its_cap_goes_first_and_a_full_area_does_not_end_its_chain()
    {
        // 2026-11-16: KaelStormVex sat on 561 energy while chores and full areas took each turn.
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = On(Row("farm", "Hard")) } };
        var game = new FakeEnergyGame(() => host.UtcNow) { Energy = 561, Chain = true };
        host.OnDelay = h => { if (game.Calls.Count(c => c.StartsWith("run", StringComparison.Ordinal)) >= 12) h.Stop = true; };
        // The full area comes first in the order: without the pile it would take the first turn and break each chain.
        var engine = new AutoplayEngine([new FullLooking(), new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow) { Energy = 561 }, host, false);
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain(host.Events, e => e.Kind == "chain");
        Assert.Equal(EnergyActivity.AreaId, host.Events.First(e => e.Kind == "turn").Area);
    }

    [Fact]
    public async Task A_dungeon_push_that_loses_waits_its_hours_and_the_energy_goes_below()
    {
        // Ehsan 2026-11-17: a dungeon push costs energy on every loss: at most once a day, stop at the loss.
        var push = new EnergyTarget { Id = "push-dragon", Area = "Dungeon", Region = "DragonsLair", Difficulty = "Normal", Stage = 20, Progress = true, LossWaitHours = 24 };
        var (host, game, engine) = Make(On(push, Row("farm")), stopAfter: TimeSpan.FromHours(2));
        game.Energy = 100;
        game.LoseAll = true;
        await engine.RunAsync(once: false, Limit());
        Assert.Single(host.Events, e => e.Kind == "battle" && e.Text.Contains("[row push-dragon]"));
        Assert.Contains(host.Events, e => e.Kind == "hold" && e.Text.Contains("tried again in 24 hour(s)"));
    }

    [Fact]
    public async Task A_dungeon_quest_replays_a_beaten_stage_never_the_push_row_s_next_one()
    {
        // 2026-11-18, killer8465: "Beat a Dungeon Boss" took the Dragon's Lair push row and fought its next, unbeaten stage (lost, 12 energy).
        var push = new EnergyTarget { Id = "push-dragon", Area = "Dungeon", Region = "DragonsLair", Difficulty = "Normal", Stage = 25, Progress = true, LossWaitHours = 24 };
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = On(push, Row("farm")), Quests = new QuestSettings { Enabled = true } },
        };
        var game = new FakeEnergyGame(() => host.UtcNow) { Energy = 100 };
        var engine = new AutoplayEngine([new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false)
            { StartNeeds = [new QuestNeed(QuestNeedKind.DungeonBoss, 1, "Beat a Dungeon Boss 15 times", "Weekly", true)] };
        await engine.RunAsync(once: true, Limit());
        var quest = host.Events.FirstOrDefault(e => e.Kind == "battle" && e.Text.Contains("[row quest-dungeon-any]"));
        Assert.True(quest is not null, string.Join(" / ", host.Events.Select(e => $"{e.Kind}: {e.Text}")));
        Assert.Contains("stage 1:", quest!.Text, StringComparison.Ordinal);   // the cheapest beaten stage, a replay
    }

    private static async Task<IReadOnlyList<AutoplayEvent>> GearQuest(string? setting, params EnergyTarget[] rows)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = On(rows),
                Quests = setting is null ? new QuestSettings { Enabled = true } : new QuestSettings { Enabled = true, GearDungeon = setting } },
        };
        var game = new FakeEnergyGame(() => host.UtcNow) { Energy = 100 };
        var engine = new AutoplayEngine([new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false)
            { StartNeeds = [new QuestNeed(QuestNeedKind.DungeonBoss, 3, "Win 3 Artifacts from clearing the Dungeons", "Daily", true)] };
        await engine.RunAsync(once: true, Limit());
        return [.. host.Events];
    }

    private static EnergyTarget Dungeon(string region, int stage) => new() { Id = "d-" + region, Area = "Dungeon", Region = region, Difficulty = "Hard", Stage = stage };

    [Fact]
    public async Task A_gear_quest_plays_the_configured_dungeon_row_even_when_another_gear_row_comes_first()
    {
        var ev = await GearQuest("IceGolemCave", Dungeon("DragonsLair", 6), Dungeon("IceGolemCave", 4));
        Assert.Contains(ev, e => e.Kind == "quest" && e.Text.Contains("IceGolemCave (the setting)"));
        Assert.True(ev.Any(e => e.Kind == "battle" && e.Text.Contains("[row quest-dungeon-any]") && e.Text.Contains("Ice Golem")), string.Join(" / ", ev.Select(e => $"{e.Kind}: {e.Text}")));
    }

    [Fact]
    public async Task A_gear_quest_plays_Dragon_s_Lair_by_default()
    {
        var ev = await GearQuest(null, Dungeon("IceGolemCave", 4), Dungeon("DragonsLair", 6));
        Assert.Contains(ev, e => e.Kind == "quest" && e.Text.Contains("DragonsLair (the setting)"));
    }

    [Fact]
    public async Task A_gear_quest_falls_back_to_another_gear_dungeon_when_the_configured_one_has_nothing()
    {
        var ev = await GearQuest("FireGolemCave", Dungeon("DragonsLair", 6));
        Assert.Contains(ev, e => e.Kind == "quest" && e.Text.Contains("nothing beaten in FireGolemCave"));
    }

    [Fact]
    public async Task A_lost_push_stage_waits_until_the_team_is_stronger()
    {
        // Ehsan 2026-11-17: the stage lost 3 days ago is not tried again unless the team is 10% stronger.
        var push = new EnergyTarget { Id = "push-dragon", Area = "Dungeon", Region = "DragonsLair", Difficulty = "Normal", Stage = 20, Progress = true, LossWaitHours = 24, RetryPowerPercent = 10 };
        var (host, game, engine) = Make(On(push, Row("farm")), stopAfter: TimeSpan.FromHours(1));
        game.Energy = 100;
        game.PushLossBook[PushLosses.Key("push-dragon", "DragonsLair", "Normal", 20)] = new PushLoss(Start.AddDays(-3), 100_000);
        await engine.RunAsync(once: false, Limit());
        Assert.True(!host.Events.Any(e => e.Kind == "battle" && e.Text.Contains("[row push-dragon]")), string.Join(" / ", host.Events.Select(e => $"{e.Kind}: {e.Text}")));
        Assert.Contains(host.Events, e => e.Kind == "hold" && e.Text.Contains("needs +10%"));
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

    [Fact]
    public async Task A_row_without_a_team_plays_the_bots_full_team_not_the_slots_left_by_the_last_fight()
    {
        var (host, game, engine) = Make(On(Row("keep", "Hard")));
        game.Energy = 100;
        game.Champions = [.. Enumerable.Range(1, 7).Select(i => TestData.Champ(i, "H" + i))];
        await engine.RunAsync(once: true, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        var run = game.Calls.First(c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains(" team ", run);
        Assert.Equal(5, run[(run.IndexOf(" team ", StringComparison.Ordinal) + 6)..].Split(',').Length);
    }

    [Fact]
    public async Task A_start_that_did_nothing_with_enough_energy_is_not_called_a_lack_of_energy()
    {
        var (host, game, engine) = Make(On(Row("farm", "Hard")), stopAfter: TimeSpan.FromMinutes(5));
        game.Energy = 153;
        game.NotStartedText = RaidActions.App.Play.LivePlayGame.StartNothingText(153, 40, "Energy");
        await engine.RunAsync(once: false, Limit());
        Assert.Contains("not an energy problem", RaidActions.App.Play.LivePlayGame.StartNothingText(153, 40, "Energy"));
        Assert.Contains("not enough energy", RaidActions.App.Play.LivePlayGame.StartNothingText(10, 40, "Energy"));
        Assert.DoesNotContain(host.Events, e => e.Text.Contains("not enough energy to start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_on_hold_after_a_start_is_not_tried_again_before_the_hold_ends()
    {
        var (host, game, engine) = Make(On(Row("farm", "Hard")), stopAfter: TimeSpan.FromMinutes(15));
        game.Energy = 100;
        game.NotStartedText = "Start did nothing (not enough energy: Energy 10 of 40)";
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(1, game.Calls.Count(c => c == "start did nothing"));
    }
}

/// <summary>The simulator gate of the dungeon push rows (Ehsan 2026-11-21: a new stage only when the simulator gives over 50%).</summary>
public class PushGateTests
{
    private static readonly DateTime Start = new(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeEnergyGame Game, AutoplayEngine Engine) Make(EnergyTarget row)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = new() { Enabled = true, Targets = [row] } } };
        var game = new FakeEnergyGame(() => host.UtcNow);
        game.Costs.Add(new StageCostOut(5_206_026, "Dungeon", "DragonsLair", "Dragon's Lair", "None", 26, new() { ["Energy"] = 16 }));
        return (host, game, new AutoplayEngine([new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false));
    }

    private static EnergyTarget Push(string region = "DragonsLair", int stage = 26) =>
        new() { Id = "push", Area = "Dungeon", Region = region, Difficulty = "Normal", Stage = stage, Progress = true, LossWaitHours = 24 };

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task A_forecast_over_half_pushes_the_new_stage()
    {
        var (host, game, engine) = Make(Push());
        game.Forecast = _ => new PushForecast(0.62, 0.40, "x");
        await engine.RunAsync(once: true, Limit());
        Assert.Contains(game.Calls, c => c.StartsWith("forecast DragonsLair 26", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Kind == "forecast" && e.Text.Contains("62%", StringComparison.Ordinal) && e.Text.Contains("new 40%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_forecast_of_half_or_less_does_not_push()
    {
        var (host, game, engine) = Make(Push());
        game.Forecast = _ => new PushForecast(0.50, 0.90, "x"); // the old engine is the trusted one for Dragon's Lair
        await engine.RunAsync(once: true, Limit());
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Kind == "hold" && e.Text.Contains("not pushed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_area_with_no_trusted_forecast_is_not_pushed_and_not_forecast()
    {
        var (host, game, engine) = Make(Push("SpiderCave", 10));
        await engine.RunAsync(once: true, Limit());
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("forecast", StringComparison.Ordinal));
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Kind == "hold" && e.Text.Contains("no trusted forecast for Spider's Den", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_beaten_stage_is_not_gated()
    {
        var (_, game, engine) = Make(Push(stage: 20) with { Progress = false });
        game.Forecast = _ => new PushForecast(0.01, 0.01, "x");
        await engine.RunAsync(once: true, Limit());
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("forecast", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_forecast_does_not_push()
    {
        var (host, game, engine) = Make(Push());
        game.Forecast = _ => null;
        await engine.RunAsync(once: true, Limit());
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Kind == "hold" && e.Text.Contains("no forecast", StringComparison.Ordinal));

        var (_, g2, e2) = Make(Push());
        g2.Forecast = _ => throw new InvalidOperationException("boom");
        await e2.RunAsync(once: true, Limit());
        Assert.DoesNotContain(g2.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DragonsLair", PushEngine.Old)]
    [InlineData("IceGolemCave", PushEngine.Old)]
    [InlineData("MagicKeep", PushEngine.Old)]
    [InlineData("ArcaneKeep", PushEngine.New)]
    [InlineData("VoidDoubleAscendDungeon", PushEngine.New)]
    [InlineData("SpiderCave", PushEngine.None)]
    [InlineData("FireGolemCave", PushEngine.None)]
    public void Each_area_has_its_engine(string region, PushEngine engine) => Assert.Equal(engine, PushGate.EngineFor("Dungeon", region));
}
