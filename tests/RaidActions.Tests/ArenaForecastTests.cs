using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;
using RaidActions.App.Play;

namespace RaidActions.Tests;

/// <summary>The Classic Arena forecast pick (Ehsan 2026-11-23): chooses among opponents under the cap, skips predicted losses, falls back to today's pick.</summary>
[Collection("ArenaForecastSwitch")]
public class ArenaForecastTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc);

    private static ArenaOpponentInfo Opp(long id, int power) => new(id, $"P{id}", power, OpponentStatus.None, true);

    [Fact]
    public void Highest_forecast_first_and_under_ninety_skipped()
    {
        var list = new[] { Opp(1, 10_000), Opp(2, 20_000), Opp(3, 30_000), Opp(4, 40_000) };
        var rates = new Dictionary<long, double> { [1] = 0.93, [2] = 0.99, [3] = 0.8, [4] = 0.99 };
        var r = ArenaForecastPick.Choose(list, o => new ArenaForecast(rates[o.UserId], 30));
        Assert.Equal([2L, 4L, 1L], r.Order.Select(o => o.UserId));
        Assert.Equal([3L], r.Skipped.Select(o => o.UserId));
        Assert.Contains("forecast P2 (20,000): 99% over 30 runs, build: none", r.Lines[1].Replace('\u00a0', ' '));
    }

    [Fact]
    public void Exactly_the_threshold_is_fought_and_just_under_is_not()
    {
        var r = ArenaForecastPick.Choose([Opp(1, 1)], _ => new ArenaForecast(ArenaForecastSwitch.MinRate, 30));
        Assert.Single(r.Order);
        Assert.Equal(0.9, ArenaForecastSwitch.MinRate);
        Assert.Empty(ArenaForecastPick.Choose([Opp(1, 1)], _ => new ArenaForecast(0.89, 30)).Order);
    }

    [Fact]
    public void No_forecast_keeps_todays_pick_and_says_why()
    {
        var list = new[] { Opp(1, 1_000), Opp(2, 2_000) };
        var r = ArenaForecastPick.Choose(list, _ => new ArenaForecast(null, 0, "team not read"));
        Assert.Equal([1L, 2L], r.Order.Select(o => o.UserId));
        Assert.Empty(r.Skipped);
        Assert.All(r.Lines, l => Assert.Contains("not computed (team not read)", l));
    }

    [Fact]
    public void The_pick_is_always_a_subset_of_the_eligible_list()
    {
        var list = new[] { Opp(1, 90_000), Opp(2, 80_000) };
        var r = ArenaForecastPick.Choose(list, o => o.UserId == 1 ? new ArenaForecast(0.99, 30) : null);
        Assert.All(r.Order.Concat(r.Skipped), o => Assert.Contains(o, list));
    }

    private static (ClassicArenaActivity Arena, FakeArenaGame Game, FakeHost Host, PlayContext Ctx) Setup(Func<int, ArenaForecast>? forecast, int cap = 100_000)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true, MaxOpponentPower = cap, UpToMyPower = false } } };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync, ForecastOf = forecast };
        var ctx = new PlayContext(host.Settings, game, host, false, host.Events.Add);
        return (new ClassicArenaActivity(), game, host, ctx);
    }

    [Fact]
    public async Task Off_by_default_nothing_is_forecast()
    {
        ArenaForecastSwitch.Enabled = false;
        var (arena, game, host, ctx) = Setup(_ => new ArenaForecast(0.0, 30));
        await arena.RunTurnAsync(ctx, CancellationToken.None);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("forecast", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("fight ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_the_best_forecast_is_fought_first_and_the_cap_holds()
    {
        ArenaForecastSwitch.Enabled = true;
        try
        {
            var (arena, game, host, ctx) = Setup(t => new ArenaForecast(t == 1003 ? 0.99 : 0.95, 30), cap: 100_000);
            await arena.RunTurnAsync(ctx, CancellationToken.None);
            var fought = game.Calls.Single(c => c.StartsWith("fight ", StringComparison.Ordinal));
            var id = long.Parse(fought[6..]);
            var idx = game.Board.ToList().FindIndex(o => o.UserId == id);
            Assert.True(game.Board[idx].Power <= 100_000, "the cap is never exceeded");
            Assert.Equal(1003, 1000 + idx);
            Assert.Contains(host.Events, e => e.Kind == "forecast" && e.Text.Contains("over 30 runs", StringComparison.Ordinal));
        }
        finally { ArenaForecastSwitch.Enabled = false; }
    }

    [Fact]
    public async Task On_with_every_forecast_under_half_nobody_is_fought()
    {
        ArenaForecastSwitch.Enabled = true;
        try
        {
            var (arena, game, host, ctx) = Setup(_ => new ArenaForecast(0.1, 30, null, "real"));
            await arena.RunTurnAsync(ctx, CancellationToken.None);
            Assert.DoesNotContain(game.Calls, c => c.StartsWith("fight ", StringComparison.Ordinal));
            Assert.Contains(host.Events, e => e.Kind == "forecast" && e.Text.Contains("skipped", StringComparison.Ordinal));
            // The skipped ones leave the list, so the turn does not repeat itself.
            Assert.DoesNotContain("opponent", arena.Decide(ctx).Why.Split(" under ")[0].Replace("nobody", ""), StringComparison.Ordinal);
        }
        finally { ArenaForecastSwitch.Enabled = false; }
    }

    [Fact]
    public async Task On_without_a_forecast_the_normal_pick_fights()
    {
        ArenaForecastSwitch.Enabled = true;
        try
        {
            var (arena, game, host, ctx) = Setup(null);
            await arena.RunTurnAsync(ctx, CancellationToken.None);
            Assert.Contains(game.Calls, c => c.StartsWith("fight ", StringComparison.Ordinal));
            Assert.Contains(host.Events, e => e.Kind == "forecast" && e.Text.Contains("not computed", StringComparison.Ordinal));
        }
        finally { ArenaForecastSwitch.Enabled = false; }
    }
}

/// <summary>The engine side on a real exported account (skipped when raid-extractor's output is not there).</summary>
public class ArenaForecasterSmokeTests
{
    [Fact, Trait("Category", "Slow")]
    public void A_maxed_enemy_team_forecasts_lower_than_a_level_one_team()
    {
        var root = Environment.GetEnvironmentVariable("RaidExtractorRoot") ?? @"F:\MyGithub\raid-extractor";
        var output = Path.Combine(root, "output");
        var data = RaidExtractor.Exporter.LoadSaved(output);
        var gd = RaidExtractor.GameDataLookup.Load(output);
        if (data is null || !gd.IsLoaded) return;
        var team = data.Champions.OrderByDescending(c => c.Power ?? 0).Take(4).Select(c => c.Id).ToList();
        var weak = ArenaForecaster.WinRate(data, gd, output, team, new([new(1, 10430, 1, 1)]), 5);
        var strong = ArenaForecaster.WinRate(data, gd, output, team, new([new(1, 10430, 60, 6), new(2, 10430, 60, 6), new(3, 10430, 60, 6), new(4, 10430, 60, 6)]), 5);
        Assert.NotNull(weak.Rate);
        Assert.NotNull(strong.Rate);
        Assert.True(weak.Rate >= strong.Rate, $"weak {weak.Rate}, strong {strong.Rate}");
    }
}

/// <summary>The enemy built from a HeroSlotSetup with gear (percent / flat bonuses, skills, leader) and the three build modes.</summary>
public class ArenaEnemyBuildTests
{
    private static ArenaEnemyBuild Gear()
    {
        var pct = new double[11]; var flat = new double[11];
        pct[1] = 0.25; pct[3] = 0.40; flat[1] = 8000; flat[4] = 30;   // HP +25% and +8000, DEF +40%, SPD +30
        return new ArenaEnemyBuild(pct, flat, [new SkillLevel(104301, 4)], [new SkillLevel(100005, 1)], [500123], true);
    }

    [Fact]
    public void Mode_is_real_with_gear_power_scaled_with_power_and_none_without()
    {
        var geared = new ArenaEnemyTeam([new(1, 10430, 50, 5, null, Gear())], 0, 90_000);
        var bare = new ArenaEnemyTeam([new(1, 10430, 50, 5)], 0, 90_000);
        var noPower = new ArenaEnemyTeam([new(1, 10430, 50, 5)]);
        Assert.Equal("real", ArenaForecaster.Mode(geared, canScale: true));
        Assert.Equal("power-scaled", ArenaForecaster.Mode(bare, canScale: true));
        Assert.Equal("none", ArenaForecaster.Mode(bare, canScale: false));
        Assert.Equal("none", ArenaForecaster.Mode(noPower, canScale: true));
        // One hero without gear in a geared team: not "real".
        Assert.Equal("power-scaled", ArenaForecaster.Mode(new ArenaEnemyTeam([new(1, 10430, 50, 5, null, Gear()), new(2, 10431, 50, 5)], 0, 90_000), true));
    }

    [Fact]
    public void A_slot_with_gear_carries_the_summed_bonuses_skills_and_leader()
    {
        var gs = RaidExtractor.Core.GameBattle.GameSim.Data;
        var gd = RaidExtractor.GameDataLookup.Load(Path.Combine(Environment.GetEnvironmentVariable("RaidExtractorRoot") ?? @"F:\MyGithub\raid-extractor", "output"));
        var slot = ArenaForecaster.Slot(gs, gd, new ArenaEnemyHero(2, 10430, 50, 5, null, Gear()), 0, 1, 1);
        Assert.Equal(1, slot.TeamLeaderSlot);
        Assert.True(slot.HasModifiers);
        Assert.Equal(0.25, slot.SlotPercent![RaidExtractor.Core.GameBattle.StatKind.Health].ToDouble(), 6);
        Assert.Equal(0.40, slot.SlotPercent![RaidExtractor.Core.GameBattle.StatKind.Defence].ToDouble(), 6);
        Assert.Equal(8000, slot.SlotFlat![RaidExtractor.Core.GameBattle.StatKind.Health].ToDouble(), 3);
        Assert.Equal(30, slot.SlotFlat![RaidExtractor.Core.GameBattle.StatKind.Speed].ToDouble(), 3);
        Assert.Contains((104301, 4), slot.Skills);
        Assert.Contains((100005, 1), slot.Skills);
        Assert.Contains((500123, 1), slot.Skills);
    }

    [Fact]
    public void Power_scaling_raises_only_hp_attack_and_defence()
    {
        var gs = RaidExtractor.Core.GameBattle.GameSim.Data;
        var gd = RaidExtractor.GameDataLookup.Load(Path.Combine(Environment.GetEnvironmentVariable("RaidExtractorRoot") ?? @"F:\MyGithub\raid-extractor", "output"));
        var slot = ArenaForecaster.Slot(gs, gd, new ArenaEnemyHero(1, 10430, 50, 5), 0, null, 2.5);
        Assert.Equal(1.5, slot.SlotPercent![RaidExtractor.Core.GameBattle.StatKind.Attack].ToDouble(), 6);
        Assert.Equal(0, slot.SlotPercent![RaidExtractor.Core.GameBattle.StatKind.Speed].ToDouble(), 6);
        Assert.Equal(0, slot.TeamLeaderSlot);
    }
}
