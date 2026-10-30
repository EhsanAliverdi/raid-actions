using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay.Fake;

/// <summary>
/// A fake account for Energy's tests: Dragon's Lair beaten to Normal 25 and Hard 5, stage costs, energy regenerating 1 per 3 minutes
/// on the host's clock, an inbox with energy, a refill window with a gem price. Records every call.
/// </summary>
public sealed class FakeEnergyGame(Func<DateTime> now) : IEnergyGame
{
    public double Energy { get; set; } = 100;
    public int EnergyCap { get; set; } = 130;
    public double FortressKeys { get; set; }
    public double Gems { get; set; } = 1000;
    public double InboxEnergy { get; set; }
    public int RefillPrice { get; set; } = 30;
    public double RefillAmount { get; set; } = 130;
    public TimeSpan RunTime { get; set; } = TimeSpan.FromMinutes(1);
    public int SuperRaidMultiplier { get; set; } = 2;
    public bool LoseAll { get; set; }
    /// <summary>A run really costs this much instead of the exported price (the learnt cost test).</summary>
    public double? RealCost { get; set; }
    public List<StageCostOut> Costs { get; set; } =
    [
        .. Enumerable.Range(1, 25).Select(n => new StageCostOut(5_206_000 + n, "Dungeon", "DragonsLair", "Dragon's Lair", "None", n, new() { ["Energy"] = n < 20 ? 8 : 16 })),
        .. Enumerable.Range(1, 10).Select(n => new StageCostOut(5_206_100 + n, "Dungeon", "DragonsLair", "Dragon's Lair", "Hard", n, new() { ["Energy"] = 24 })),
    ];
    public List<string> Calls { get; } = [];
    /// <summary>The roster (Campaign food tests); null: not read.</summary>
    public List<ChampionOut>? Champions { get; set; }
    public int MysteryShards { get; set; }
    /// <summary>Levels a run gives each team member.</summary>
    public int LevelsPerRun { get; set; } = 4;

    private DateTime _tick = now();

    private void Regen()
    {
        while (now() - _tick >= TimeSpan.FromMinutes(3))
        {
            _tick += TimeSpan.FromMinutes(3);
            if (Energy < EnergyCap) Energy++;
        }
    }

    public Task<EnergyAccount> ReadAccountAsync(CancellationToken ct)
    {
        Regen();
        var progress = new List<AreaProgressOut>
        {
            new("Dungeon", "Dungeons", [new("DragonsLair", "Dragon's Lair",
            [
                new("None", 25, 25, 75, 25, null, [.. Enumerable.Range(1, 25).Select(n => new StageResultOut(5_206_000 + n, n, true, 3, 5, 0))]),
                new("Hard", 10, 5, 15, 5, null, [.. Enumerable.Range(1, 5).Select(n => new StageResultOut(5_206_100 + n, n, true, 3, 5, 0))]),
            ])]),
        };
        return Task.FromResult(new EnergyAccount(new Dictionary<string, double> { ["Energy"] = Energy, ["Gem"] = Gems, ["DoubleAscend_FortressKey"] = FortressKeys },
            Costs, progress, SuperRaidMultiplier, Champions?.ToList(), MysteryShards));
    }

    public Task<BattleOutcome> RunStageAsync(EnergyRun run, bool dryRun, CancellationToken ct)
    {
        Regen();
        if (dryRun)
        {
            Calls.Add($"show {run.Target.Difficulty} {run.Number}{(run.Target.SuperRaid ? " super" : "")}");
            return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown, not pressed"));
        }
        var cost = (RealCost ?? run.Cost / (run.Target.SuperRaid ? SuperRaidMultiplier : 1)) * (run.Target.SuperRaid ? SuperRaidMultiplier : 1);
        if (run.Resource == "Energy")
        {
            if (Energy < cost) throw new PlayStopException($"not enough energy ({Energy} for {cost}): the game would offer a refill");
            Energy -= cost;
        }
        Calls.Add($"run {run.Difficulty} {run.Number}{(run.Target.SuperRaid ? " super" : "")}{(run.Team is { } team ? " team " + string.Join(",", team) : "")}");
        if (run.Target.Area == "Story") Drops += DropsPerRun;
        if (Champions is not null && run.Team is { } members)
            for (var i = 0; i < Champions.Count; i++)
                if (members.Contains(Champions[i].Id))
                    Champions[i] = Champions[i] with { Level = Math.Min(Champions[i].Rank * 10, Champions[i].Level + LevelsPerRun) };
        return Task.FromResult(new BattleOutcome(LoseAll ? BattleResult.Defeat : BattleResult.Victory, $"{RunTime.TotalSeconds:0} s"));
    }

    public Task<int> RankUpFoodAsync(IReadOnlyList<RankUpPlan> rankUps, IReadOnlySet<int> pool, bool dryRun, CancellationToken ct)
    {
        Calls.Add(dryRun ? "show rank-up" : $"rank-up {string.Join(",", rankUps.Select(r => r.HeroId))}");
        if (dryRun || Champions is null) return Task.FromResult(0);
        var done = 0;
        foreach (var r in rankUps)
        {
            if (!pool.Contains(r.HeroId) || r.FoodIds.Any(f => !pool.Contains(f))) throw new PlayStopException($"#{r.HeroId}: not food");
            Champions.RemoveAll(c => r.FoodIds.Contains(c.Id));
            var i = Champions.FindIndex(c => c.Id == r.HeroId);
            Champions[i] = Champions[i] with { Rank = Champions[i].Rank + 1, Level = 1 };
            done++;
        }
        return Task.FromResult(done);
    }

    public Task<int> SummonMysteryAsync(int count, bool dryRun, CancellationToken ct)
    {
        Calls.Add(dryRun ? "show summon" : $"summon {count}");
        if (dryRun || Champions is null) return Task.FromResult(0);
        var n = Math.Min(count, MysteryShards);
        for (var k = 0; k < n; k++)
        {
            var id = Champions.Max(c => c.Id) + 1;
            Champions.Add(new ChampionOut(id, 1, 1, $"Food {id}", "Common", "BannerLords", "Force", "Attack", 1, 0, 1, 0, 0, 0, null,
                false, false, false, null, [], [], [], [], [], 0));
        }
        MysteryShards -= n;
        return Task.FromResult(n);
    }

    /// <summary>Campaign drops waiting to be sold (each victory adds <see cref="DropsPerRun"/>).</summary>
    public int Drops { get; set; }
    public int DropsPerRun { get; set; }

    public Task<int> SellCampaignGearAsync(GearSellSettings settings, bool dryRun, CancellationToken ct)
    {
        if (Drops < settings.Every) return Task.FromResult(0);
        Calls.Add(dryRun ? "show sell" : $"sell {Drops}");
        if (dryRun) return Task.FromResult(0);
        var n = Drops;
        Drops = 0;
        return Task.FromResult(n);
    }

    public Task<double> CollectInboxAsync(string resource, bool dryRun, CancellationToken ct)
    {
        Calls.Add("inbox");
        var got = resource == "Energy" ? InboxEnergy : 0;
        if (dryRun) return Task.FromResult(0d);
        Energy += got;
        InboxEnergy -= got;
        return Task.FromResult(got);
    }

    public Task<RefillResult> BuyRefillAsync(string resource, int maxGems, bool dryRun, CancellationToken ct)
    {
        if (RefillPrice > maxGems)
        {
            Calls.Add("refill refused");
            return Task.FromResult(new RefillResult(false, RefillPrice, 0, $"the refill costs {RefillPrice} gems, more than the {maxGems} allowed"));
        }
        Calls.Add(dryRun ? "show refill" : "refill");
        if (dryRun) return Task.FromResult(new RefillResult(false, RefillPrice, 0, "dry run"));
        Gems -= RefillPrice;
        Energy += RefillAmount;
        return Task.FromResult(new RefillResult(true, RefillPrice, RefillAmount, "bought"));
    }
}
