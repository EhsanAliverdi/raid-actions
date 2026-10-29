using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's Faction Wars on a fake game: strip yesterday, gear, next unbeaten stage, farm after a defeat, remove gear after.</summary>
public class FactionWarsActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 6, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeFactionWarsGame Game, AutoplayEngine Engine) Make(Func<FactionWarsSettings, FactionWarsSettings>? fw = null, bool dryRun = false,
        TimeSpan? stopAfter = null)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 },
                FactionWars = (fw ?? (s => s))(new FactionWarsSettings { Enabled = true }),
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

    [Fact]
    public async Task A_defeat_at_the_next_stage_farms_the_highest_beaten_stage_for_the_rest_of_the_day()
    {
        var (_, game, engine) = Make(stopAfter: TimeSpan.FromHours(2));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 4, HighestPassed = 8, Team = [1, 2, 3], WinsUpTo = 9 });
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight OgrynTribes 9", "fight OgrynTribes 10", "fight OgrynTribes 9", "fight OgrynTribes 9"],
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
}
