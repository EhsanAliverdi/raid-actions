using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay.Fake;

/// <summary>
/// Made-up Cursed City for tests: levels with fixed teams (some members unused: they get gear), keys, battles won up to a level number;
/// the next level is the highest one not beaten and not lost too often, else the highest beaten one. Records every call.
/// </summary>
public sealed class FakeCursedCityGame : ICursedCityGame
{
    public sealed class Level
    {
        public required int Number { get; init; }
        public List<(int Id, bool Unused)> Team { get; init; } = [];
        public int Wins { get; set; }
        public int Losses { get; set; }
        public bool Open { get; set; } = true;
    }

    public List<Level> Levels { get; } = [];
    public double Keys { get; set; } = 5;
    public int WinsUpTo { get; set; } = 99;
    public List<string> Calls { get; } = [];
    private readonly HashSet<int> _wearing = [];

    public Task<CursedCityPlan> PlanAsync(int lossesToSkip, IReadOnlySet<int> skip, bool allowRisky, string? difficulty, CancellationToken ct)
    {
        var levels = Levels.Select(l =>
        {
            var stage = new CcStage(10012000 + l.Number, "CursedCityDistrict1", "Cobblemarket", "Hard", l.Number, l.Team.Count, new() { ["CursedCity_Key"] = 1 }, [],
                false, false, false, [], [], null, l.Wins, l.Losses, l.Wins, []);
            var members = l.Team.Select(m => new CcPick(m.Id, $"C{m.Id}", "Rare", "SacredOrder", "Force", "Attack", 60, 6, 100, true,
                m.Unused && !_wearing.Contains(m.Id), false, false)).ToList();
            return new CcLevel(stage, "at least 1 Force champion", new CcTeam(stage.Id, members, true, null, 500), l.Open, l.Losses >= lossesToSkip && l.Wins == 0);
        }).ToList();
        var open = levels.Where(l => l.Reachable && !l.Stage.Beaten && !l.Unbeatable && !skip.Contains(l.Stage.Id)).OrderByDescending(l => l.Stage.Number).FirstOrDefault();
        var replay = levels.Where(l => l.Stage.Beaten && !skip.Contains(l.Stage.Id)).OrderByDescending(l => l.Stage.Number).FirstOrDefault();
        return Task.FromResult(new CursedCityPlan(Keys, "Hard", levels, open ?? replay, open is not null ? "open" : "again", []));
    }

    public Task<string> GearAsync(CcLevel level, IReadOnlyList<int> ids, bool dryRun, CancellationToken ct)
    {
        Calls.Add($"{(dryRun ? "show-" : "")}gear {string.Join(",", ids)}");
        if (!dryRun) foreach (var id in ids) _wearing.Add(id);
        return Task.FromResult("geared");
    }

    public Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct)
    {
        Calls.Add($"{(dryRun ? "show-" : "")}remove {string.Join(",", ids)}");
        if (!dryRun) foreach (var id in ids) _wearing.Remove(id);
        return Task.FromResult("removed");
    }

    public Task<BattleOutcome> FightAsync(CcLevel level, bool dryRun, CancellationToken ct)
    {
        var l = Levels.Single(x => x.Number == level.Stage.Number);
        if (Keys < 1) throw new PlayStopException("no key");
        if (dryRun)
        {
            Calls.Add($"show-fight {l.Number}");
            return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown"));
        }
        Calls.Add($"fight {l.Number}");
        Keys--;
        var won = l.Number <= WinsUpTo;
        if (won)
        {
            l.Wins++;
            foreach (var next in Levels.Where(x => x.Number == l.Number + 1)) next.Open = true;
        }
        else l.Losses++;
        return Task.FromResult(new BattleOutcome(won ? BattleResult.Victory : BattleResult.Defeat, "on auto"));
    }
}

