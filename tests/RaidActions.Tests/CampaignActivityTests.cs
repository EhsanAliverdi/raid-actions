using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Campaign food farming through Energy on a fake account (plan/campaign.md): farmer + food, rank-ups, Mystery shards.</summary>
public class CampaignActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);
    private const int Kael = 1;

    private static ChampionOut Hero(int id, int rank, int level, string rarity = "Common", bool locked = false) =>
        new(id, 100 + id, 100, $"Hero {id}", rarity, "BannerLords", "Force", "Attack", level, 0, rank, 0, 0, 0, null, locked, false, false, null,
            [], [], [], [], [], 0);

    private static (FakeHost Host, FakeEnergyGame Game, AutoplayEngine Engine) Make(EnergyPlaySettings energy, TimeSpan runFor, bool dryRun = false)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Energy = energy } };
        var game = new FakeEnergyGame(() => host.UtcNow);
        game.Costs.Add(new StageCostOut(1_123_003, "Story", "Region12", "Brimstone Path", "Brutal", 3, new() { ["Energy"] = 8 }));
        host.OnDelay = h => { if (h.UtcNow - Start >= runFor) h.Stop = true; };
        return (host, game, new AutoplayEngine([new EnergyActivity(game)], new FakeArenaGame(() => host.UtcNow), host, dryRun));
    }

    private static readonly EnergyTarget Farm = new()
    {
        Id = "farm", Area = "Story", Region = "Region12", Difficulty = "Brutal", Stage = 3, Team = [Kael], Food = true,
    };

    private static EnergyPlaySettings On(params EnergyTarget[] rows) =>
        new() { Enabled = true, Targets = [.. rows], Sources = [new() { Resource = "Energy", UseInbox = false }] };

    [Fact]
    public async Task Kael_farms_12_3_brutal_with_food_ranks_it_up_and_opens_mystery_shards_when_it_runs_out()
    {
        var (host, game, engine) = Make(On(Farm), TimeSpan.FromHours(3));
        game.Energy = 130;
        game.Champions = [Hero(Kael, 6, 60, "Rare", locked: true), Hero(10, 1, 1), Hero(11, 1, 1), Hero(12, 1, 1), Hero(13, 1, 1), Hero(50, 3, 1, "Rare")];
        game.MysteryShards = 2;
        await engine.RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

        var runs = game.Calls.Where(c => c.StartsWith("run Brutal 3", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Contains($"team {Kael}", r));                    // Kael always leads
        Assert.Contains(game.Calls, c => c.StartsWith("rank-up", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("summon", StringComparison.Ordinal));
        Assert.DoesNotContain(runs, r => r.Contains(",50", StringComparison.Ordinal));  // the Rare is never food
        Assert.Contains(game.Champions!, c => c.Id == 50 && c.Rank == 3);              // ... nor ranked up
        Assert.Contains(game.Champions!, c => c.Rarity == "Common" && c.Rank == 3);  // 1★ food became 2★, then 3★ food
        Assert.Contains(host.Events, e => e.Kind == "rankup");
        Assert.Equal(0, game.MysteryShards);
    }

    [Fact]
    public async Task A_dry_run_shows_the_rank_up_and_spends_nothing()
    {
        var (_, game, engine) = Make(On(Farm), TimeSpan.FromMinutes(20), dryRun: true);
        game.Champions = [Hero(Kael, 6, 60, "Rare", locked: true), Hero(10, 1, 10), Hero(11, 1, 1)];
        await engine.RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Contains("show rank-up", game.Calls);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("rank-up", StringComparison.Ordinal));
        Assert.Contains(game.Champions!, c => c.Id == 10 && c.Rank == 1);
    }

    [Fact]
    public async Task Without_food_or_shards_the_farm_steps_aside_and_dragons_lair_gets_the_energy()
    {
        var dl = new EnergyTarget { Id = "dl", Area = "Dungeon", Region = "DragonsLair", Difficulty = "Hard", Stage = 5 };
        var (_, game, engine) = Make(On(Farm, dl), TimeSpan.FromMinutes(10));
        game.Energy = 100;
        game.Champions = [Hero(Kael, 6, 60, "Rare", locked: true), Hero(50, 3, 1, "Rare")];
        await engine.RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("run Brutal", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("run Hard 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Campaign_drops_are_sold_when_enough_have_come_and_only_when_switched_on()
    {
        var (host, game, engine) = Make(On(Farm) with { SellGear = new GearSellSettings { Enabled = true, Every = 5 } }, TimeSpan.FromMinutes(30));
        game.Energy = 130;
        game.DropsPerRun = 2;
        game.Champions = [Hero(Kael, 6, 60, "Rare", locked: true), .. Enumerable.Range(10, 8).Select(i => Hero(i, 1, 1))];
        await engine.RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Contains(game.Calls, c => c.StartsWith("sell ", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Kind == "sell");

        var (_, off, offEngine) = Make(On(Farm), TimeSpan.FromMinutes(30));
        off.Energy = 130;
        off.DropsPerRun = 2;
        off.Champions = [Hero(Kael, 6, 60, "Rare", locked: true), .. Enumerable.Range(10, 8).Select(i => Hero(i, 1, 1))];
        await offEngine.RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.DoesNotContain(off.Calls, c => c.StartsWith("sell", StringComparison.Ordinal));
    }
}
