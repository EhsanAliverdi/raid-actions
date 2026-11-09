using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay.Fake;

/// <summary>A Doom Tower for tests: the stages given, a key spent per battle (gold only on a win), every battle won unless listed lost.</summary>
public sealed class FakeDoomTowerGame : IDoomTowerGame
{
    public List<DtStage> Stages { get; } = [];
    public double Gold { get; set; }
    public double Silver { get; set; }
    public HashSet<int> Loses { get; } = [];
    public List<string> Calls { get; } = [];

    /// <summary>How many times the tower was opened to read it.</summary>
    public int Reads { get; private set; }

    public Task<DoomTowerMap> ReadDoomTowerAsync(CancellationToken ct) { Reads++; return Task.FromResult(new DoomTowerMap([.. Stages], Gold, Silver)); }

    /// <summary>Floors climbed; the outcome of the next climb.</summary>
    public int Climbs { get; private set; }
    public BattleResult ClimbResult { get; set; } = BattleResult.Victory;

    /// <summary>The Normal tower is finished (its climb finds no floor) unless set false.</summary>
    public bool NormalDone { get; set; } = true;
    public int NormalClimbs { get; private set; }

    public Task<BattleOutcome> ClimbDoomTowerAsync(bool hard, bool dryRun, CancellationToken ct)
    {
        if (dryRun) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown"));
        if (!hard)
        {
            if (NormalDone) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "no gold floor on the Normal map"));
            NormalClimbs++;
            return Task.FromResult(new BattleOutcome(ClimbResult, "60 s"));
        }
        Climbs++;
        return Task.FromResult(new BattleOutcome(ClimbResult, "60 s"));
    }

    public Task<BattleOutcome> FightDoomTowerAsync(DtStage stage, bool dryRun, CancellationToken ct)
    {
        if (dryRun) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown, not pressed"));
        Calls.Add($"fight {stage.Id}");
        var won = !Loses.Contains(stage.Id);
        var key = DoomTowerRules.KeyOf(stage);
        if (key == DoomTowerRules.Silver) Silver -= DoomTowerRules.Cost(stage);
        if (key == DoomTowerRules.Gold && won) Gold -= DoomTowerRules.Cost(stage);
        return Task.FromResult(new BattleOutcome(won ? BattleResult.Victory : BattleResult.Defeat, "1 s"));
    }
}
