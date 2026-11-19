using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's Cursed City on a fake city: gear unused members, open levels upward, strip gear not used next, use the keys.</summary>
public class CursedCityActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeCursedCityGame Game, AutoplayEngine Engine) Make(bool dryRun = false, TimeSpan? stopAfter = null, Func<CursedCityPlaySettings, CursedCityPlaySettings>? cc = null)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, CursedCity = (cc ?? (s => s))(new CursedCityPlaySettings { Enabled = true }) },
        };
        var game = new FakeCursedCityGame();
        if (stopAfter is { } after) host.OnDelay = h => { if (h.UtcNow - Start >= after) h.Stop = true; };
        return (host, game, new AutoplayEngine([new CursedCityActivity(game)], new FakeArenaGame(() => host.UtcNow), host, dryRun));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public void A_level_costs_its_start_and_finish_keys_and_at_least_one()
    {
        var stage = new RaidExtractor.CcStage(1, "d", null, "Hard", 1, 5, new() { ["CursedCity_Key"] = 1 }, new() { ["CursedCity_Key"] = 1 }, false, false, false, [], [], null, 0, 0, 0, []);
        Assert.Equal(2, CursedCityActivity.Cost(new CcLevel(stage, "", new CcTeam(1, [], true, null, 0), true, false)));
        Assert.Equal(1, CursedCityActivity.Cost(new CcLevel(stage with { StartPrice = [], FinishPrice = [] }, "", new CcTeam(1, [], true, null, 0), true, false)));
    }

    [Theory]
    [InlineData(0.29, false)]
    [InlineData(0.31, true)]
    public async Task A_level_is_played_only_when_the_simulator_gives_over_30_percent(double rate, bool tried)
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(3));
        game.Keys = 2;
        game.Forecast = rate;
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 6, Team = [(1, false), (2, false)], Open = true });
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(tried, game.Calls.Contains("fight 6"));
        Assert.Single(game.Forecasts);
        if (tried) Assert.Contains(host.Events, e => e.Kind == "battle" && e.Text.Contains("[team 1,2]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_highest_level_that_passes_in_simulation_is_played_one_search_per_level_per_day()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(3));
        game.Keys = 1;
        game.ForecastByLevel[8] = 0.10;
        game.ForecastByLevel[7] = 0.45;
        game.ForecastByLevel[6] = 0.90;
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 6, Team = [(1, false), (2, false)], Open = true });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 7, Team = [(1, false), (3, false)], Open = true });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 8, Team = [(1, false), (4, false)], Open = true });
        await engine.RunAsync(once: false, Limit());
        Assert.Contains("fight 7", game.Calls);
        Assert.DoesNotContain("fight 8", game.Calls);
        Assert.DoesNotContain("fight 6", game.Calls);
        Assert.Equal([(8, true), (7, true)], game.Simulations.Take(2));
        Assert.Contains(host.Events, e => e.Kind == "sim" && e.Text.Contains("simulated win 45%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_beaten_level_is_gated_too_and_no_champion_level_rule_blocks_it()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(3));
        game.Keys = 1;
        game.Forecast = 0.2;
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 5, Team = [(1, false), (2, false)], Wins = 3, Open = true });
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain("fight 5", game.Calls);
        Assert.Contains(host.Events, e => e.Kind == "sim" && e.Text.Contains("beaten", StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_opens_levels_upward_gearing_unused_members_and_stripping_those_the_next_level_does_not_use()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(3));
        game.Keys = 3;
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 5, Team = [(1, false), (2, true)], Wins = 1 });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 6, Team = [(1, false), (2, true)], Open = true });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 7, Team = [(1, false), (3, true)], Open = false });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 8, Team = [(4, true), (3, true)], Open = false });
        await engine.RunAsync(once: false, Limit());

        Assert.Equal(["gear 2", "fight 6", "remove 2", "gear 3", "fight 7", "gear 4", "fight 8", "remove 3,4"], game.Calls);
        Assert.Equal(3, host.Events.Count(e => e.Kind == "battle" && e.Text.StartsWith("Victory", StringComparison.Ordinal)));
        Assert.Equal(3, host.Last!.Areas.Single().Counters["Levels opened"]);
        Assert.Equal(0, host.Last.Areas.Single().Counters["Geared now"]);
    }

    [Fact]
    public async Task Idle_keys_go_to_beaten_levels_with_the_strong_team_and_two_losses_end_it()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(3));
        host.Settings = host.Settings with { Rules = new BotRules { CursedCityReplayIdleKeys = true } };
        game.Keys = 8;
        game.SpareTeamNotReady = true;
        game.WinsUpTo = 0;
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 5, Team = [(1, false)], Wins = 1 });
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight 5", "fight 5"], game.Calls.Where(c => c.StartsWith("fight", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_level_lost_twice_is_skipped_and_the_keys_go_to_the_highest_beaten_level()
    {
        var (_, game, engine) = Make(stopAfter: TimeSpan.FromHours(3), cc: s => s with { GearUnused = false, LossesToSkip = 2 });
        game.Keys = 4;
        game.WinsUpTo = 5;
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 5, Team = [(1, false)], Wins = 1 });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 6, Team = [(1, false)] });
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight 6", "fight 6", "fight 5", "fight 5"], game.Calls);
    }

    [Fact]
    public async Task A_dry_run_shows_the_gear_and_the_battle_and_changes_nothing()
    {
        var (_, game, engine) = Make(dryRun: true);
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 6, Team = [(1, false), (2, true)] });
        var why = await engine.RunAsync(once: true, Limit());
        Assert.StartsWith("nothing to do now", why);
        Assert.Equal(["show-gear 2", "show-fight 6"], game.Calls);
        Assert.Equal(5, game.Keys);
    }
}
