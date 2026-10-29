using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay.Fake;

/// <summary>
/// Made-up Faction Wars for tests: crypts with keys and stages beaten, teams, yesterday's champions to strip; battles won up to a stage
/// (<see cref="WinsUpTo"/>), a key spent per battle. Records every call.
/// </summary>
public sealed class FakeFactionWarsGame(Func<DateTime> now) : IFactionWarsGame
{
    public sealed class Crypt
    {
        public required string Faction { get; init; }
        public int Keys { get; set; }
        public int HighestPassed { get; set; }
        public List<int> Team { get; init; } = [];
        public List<int> Locked { get; init; } = [];
        public int WinsUpTo { get; set; } = 99;
    }

    public List<Crypt> Open { get; } = [];
    public List<StripChampion> Strip { get; set; } = [];
    public List<string> Calls { get; } = [];
    public TimeSpan BattleTime { get; set; } = TimeSpan.FromSeconds(60);
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    public Task<FactionWarsDay> PlanDayAsync(CancellationToken ct) => Task.FromResult(new FactionWarsDay(DateOnly.FromDateTime(now()),
        [.. Open.Select(c => new CryptPlan(c.Faction, c.Faction, c.Keys, c.HighestPassed, 21, [.. c.Team], [.. c.Team.Select(i => $"#{i}")], "yours", [],
            [.. c.Team.Except(c.Locked)]))], [.. Strip], []));

    public Task<string> GearTeamAsync(CryptPlan crypt, bool dryRun, CancellationToken ct)
    {
        Calls.Add($"{(dryRun ? "show-" : "")}gear {crypt.Faction}");
        return Task.FromResult("geared");
    }

    public Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct)
    {
        Calls.Add($"{(dryRun ? "show-" : "")}remove {string.Join(",", ids)}");
        if (!dryRun) Strip = [.. Strip.Where(s => !ids.Contains(s.Id))];
        return Task.FromResult("removed");
    }

    public async Task<BattleOutcome> FightCryptAsync(CryptPlan crypt, int stage, bool dryRun, CancellationToken ct)
    {
        var c = Open.Single(x => x.Faction == crypt.Faction);
        if (c.Keys < 1) throw new PlayStopException("no key");
        if (dryRun)
        {
            Calls.Add($"show-fight {c.Faction} {stage}");
            return new BattleOutcome(BattleResult.NotStarted, "Start shown");
        }
        Calls.Add($"fight {c.Faction} {stage}");
        if (Delay is { } d) await d(BattleTime, ct);
        c.Keys--;
        var won = stage <= c.WinsUpTo;
        if (won) c.HighestPassed = Math.Max(c.HighestPassed, stage);
        return new BattleOutcome(won ? BattleResult.Victory : BattleResult.Defeat, "on auto");
    }
}
