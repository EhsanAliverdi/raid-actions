using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's Faction Wars on a fake game: strip yesterday, gear, next unbeaten stage, farm after a defeat, remove gear after.</summary>
public class FactionWarsActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 6, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeFactionWarsGame Game, AutoplayEngine Engine) Make(Func<FactionWarsSettings, FactionWarsSettings>? fw = null, bool dryRun = false,
        TimeSpan? stopAfter = null, bool botTeam = true)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 },
                FactionWars = (fw ?? (s => s))(new FactionWarsSettings { Enabled = true }),
                // The team picker decides the gearing (plan/team-prep.md): the bot's pick, geared up (unless a test switches the card's gearing off).
                Teams = botTeam ? new() { ["faction-wars"] = new AreaTeams { Default = new TeamChoice { Mode = TeamMode.BotChooses, GearUp = true } } } : [],
            },
        };
        var game = new FakeFactionWarsGame(() => host.UtcNow) { Delay = host.DelayAsync };
        if (stopAfter is { } after) host.OnDelay = h => { if (h.UtcNow - Start >= after) h.Stop = true; };
        var arena = new FakeArenaGame(() => host.UtcNow);
        return (host, game, new AutoplayEngine([new FactionWarsActivity(game)], arena, host, dryRun));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task A_day_strips_yesterday_then_gears_fights_the_next_stages_and_removes_the_gear_of_unlocked_members()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(2));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 3, HighestPassed = 10, Team = [1, 2, 3, 4, 5], Locked = [3] });
        game.Strip = [new StripChampion(9, "Old", "Skinwalkers", 6)];
        await engine.RunAsync(once: false, Limit());

        Assert.Equal(["remove 9", "gear SacredOrder", "fight SacredOrder 11", "fight SacredOrder 12", "fight SacredOrder 13", "remove 1,2,4,5"], game.Calls);
        Assert.Equal(3, host.Events.Count(e => e.Kind == "battle" && e.Text.StartsWith("Victory", StringComparison.Ordinal)));
        Assert.Contains(host.Events, e => e.Kind == "strip" && e.Text.Contains("Old (6)", StringComparison.Ordinal));
        Assert.Equal(AreaState.Waiting, host.Last!.Areas.Single().State); // done for today: the next day's crypts
    }

    [Theory]
    [InlineData(0.49, false)]
    [InlineData(0.51, true)]
    public async Task A_new_stage_is_tried_only_when_the_simulation_gives_more_than_50_percent(double rate, bool tried)
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(2), botTeam: false);
        game.Forecast = rate;
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 1, HighestPassed = 8, Team = [1, 2, 3] });
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(tried, game.Calls.Contains("fight OgrynTribes 9"));
        Assert.Equal(!tried, host.Events.Any(e => e.Kind == "sim" && e.Text.Contains(rate.ToString("P0"), StringComparison.Ordinal)));
        Assert.Single(game.Forecasts.Where(f => f == "OgrynTribes 9"));
        if (!tried) Assert.Contains(game.Calls, c => c.StartsWith("fight OgrynTribes 8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_blocked_stage_is_simulated_again_on_the_next_day_and_the_battle_logs_its_team()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(30), botTeam: false);
        game.Forecast = 0.4;
        game.BattleTime = TimeSpan.FromHours(2);
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 30, HighestPassed = 8, Team = [1, 2, 3] });
        await engine.RunAsync(once: false, Limit());
        Assert.True(game.Forecasts.Count(f => f == "OgrynTribes 9") >= 2, string.Join(";", game.Forecasts));
        Assert.Contains(host.Events, e => e.Kind == "battle" && e.Text.Contains("[team 1,2,3]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_forecast_is_no_push_the_crypt_farms_its_highest_beaten_stage()
    {
        // 2026-11-20, KaelStormVex: the crypt had no team set, the forecast came back empty and Barbarians stage 5 was pushed and lost, a key each.
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(2), botTeam: false);
        game.Forecast = null;
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 4, HighestPassed = 8, Team = [1, 2, 3] });
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain("fight OgrynTribes 9", game.Calls);
        Assert.Contains(host.Events, e => e.Kind == "sim" && e.Text.Contains("no simulation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_defeat_at_the_next_stage_farms_the_highest_beaten_stage_for_the_rest_of_the_day()
    {
        // With a team of the player's (the bot choosing retries the same stage with its next team instead).
        var (_, game, engine) = Make(stopAfter: TimeSpan.FromHours(2), botTeam: false);
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 4, HighestPassed = 8, Team = [1, 2, 3], WinsUpTo = 9 });
        await engine.RunAsync(once: false, Limit());
        // Farming after the defeat is a Super Raid while the keys pay for one (Ehsan 2026-11-11).
        Assert.Equal(["fight OgrynTribes 9", "fight OgrynTribes 10", "fight OgrynTribes 9 super"],
            game.Calls.Where(c => c.StartsWith("fight", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_crypt_beaten_to_the_top_is_farmed_with_Super_Raids_and_the_last_key_goes_to_a_normal_run()
    {
        var (_, game, engine) = Make(stopAfter: TimeSpan.FromHours(2));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 5, HighestPassed = 21, Team = [1, 2, 3], WinsUpTo = 21 });
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight OgrynTribes 21 super", "fight OgrynTribes 21 super", "fight OgrynTribes 21"],
            game.Calls.Where(c => c.StartsWith("fight", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_dry_run_shows_each_step_and_changes_nothing()
    {
        var (_, game, engine) = Make(dryRun: true);
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 12, HighestPassed = 10, Team = [1, 2] });
        game.Strip = [new StripChampion(9, "Old", "Skinwalkers", 6)];
        var why = await engine.RunAsync(once: true, Limit());
        Assert.StartsWith("nothing to do now", why);
        Assert.Equal(["show-remove 9", "show-gear SacredOrder", "show-fight SacredOrder 11"], game.Calls);
        Assert.Equal(12, game.Open[0].Keys);
    }

    [Fact]
    public async Task Without_keys_nothing_is_stripped_or_geared_and_kept_keys_are_left()
    {
        var (_, game, engine) = Make(fw: s => s with { KeepKeys = 2 }, stopAfter: TimeSpan.FromHours(1));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 0, HighestPassed = 8, Team = [1] });
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 3, HighestPassed = 10, Team = [2] });
        game.Strip = [new StripChampion(9, "Old", "Skinwalkers", 6)];
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["remove 9", "gear SacredOrder", "fight SacredOrder 11", "remove 2"], game.Calls);
        Assert.Equal(2, game.Open[1].Keys);
    }

    [Fact]
    public async Task Gearing_a_crypt_team_writes_the_task_and_its_return_follow_up_which_the_keys_close()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(2));
        var book = new MemoryTaskBook();
        host.Tasks = book;
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 2, HighestPassed = 10, Team = [1, 2, 3], Locked = [3] });
        await engine.RunAsync(once: false, Limit());

        var gear = Assert.Single(book.All.Values, t => t.Kind == "gear");
        Assert.Equal(TodoState.Done, gear.State);
        var back = Assert.Single(book.All.Values, t => t.Kind == "return");
        Assert.Equal(gear.Key, back.Parent);
        Assert.Equal("1,2", back.Checkpoint);
        Assert.Equal(TodoState.Done, back.State);
        Assert.StartsWith("keys used", back.Why);
    }

    [Fact]
    public async Task A_gear_run_stopped_half_way_stays_in_progress_with_why_and_is_geared_again()
    {
        var (host, game, engine) = Make(stopAfter: TimeSpan.FromHours(2));
        var book = new MemoryTaskBook();
        host.Tasks = book;
        game.FailGear = 1;
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 1, HighestPassed = 10, Team = [1, 2] });
        await engine.RunAsync(once: false, Limit());

        Assert.Equal(2, game.Calls.Count(c => c == "gear SacredOrder"));
        Assert.Equal(TodoState.Done, Assert.Single(book.All.Values, t => t.Kind == "gear").State);
    }

    [Fact]
    public async Task A_dry_run_writes_no_task()
    {
        var (host, game, engine) = Make(dryRun: true);
        var book = new MemoryTaskBook();
        host.Tasks = book;
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 2, HighestPassed = 10, Team = [1, 2] });
        await engine.RunAsync(once: true, Limit());
        Assert.Empty(book.All);
    }
}
