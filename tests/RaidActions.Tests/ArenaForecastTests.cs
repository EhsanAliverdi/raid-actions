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
    public void Highest_forecast_first_and_under_half_skipped()
    {
        var list = new[] { Opp(1, 10_000), Opp(2, 20_000), Opp(3, 30_000), Opp(4, 40_000) };
        var rates = new Dictionary<long, double> { [1] = 0.6, [2] = 0.9, [3] = 0.3, [4] = 0.9 };
        var r = ArenaForecastPick.Choose(list, o => new ArenaForecast(rates[o.UserId], 30));
        Assert.Equal([2L, 4L, 1L], r.Order.Select(o => o.UserId));
        Assert.Equal([3L], r.Skipped.Select(o => o.UserId));
        Assert.Contains("forecast P2 (20,000): 90% over 30 runs", r.Lines[1].Replace('\u00a0', ' '));
    }

    [Fact]
    public void Exactly_half_is_fought()
    {
        var r = ArenaForecastPick.Choose([Opp(1, 1)], _ => new ArenaForecast(0.5, 30));
        Assert.Single(r.Order);
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
            var (arena, game, host, ctx) = Setup(t => new ArenaForecast(t == 1003 ? 0.95 : 0.7, 30), cap: 100_000);
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
            var (arena, game, host, ctx) = Setup(_ => new ArenaForecast(0.1, 30));
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
        var weak = ArenaForecaster.WinRate(data, gd, output, team, [new(1, 10430, 1, 1)], 5);
        var strong = ArenaForecaster.WinRate(data, gd, output, team, [new(1, 10430, 60, 6), new(2, 10430, 60, 6), new(3, 10430, 60, 6), new(4, 10430, 60, 6)], 5);
        Assert.NotNull(weak.Rate);
        Assert.NotNull(strong.Rate);
        Assert.True(weak.Rate >= strong.Rate, $"weak {weak.Rate}, strong {strong.Rate}");
    }
}
