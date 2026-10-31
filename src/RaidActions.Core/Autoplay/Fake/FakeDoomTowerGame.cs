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

    public Task<DoomTowerMap> ReadDoomTowerAsync(CancellationToken ct) => Task.FromResult(new DoomTowerMap([.. Stages], Gold, Silver));

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
