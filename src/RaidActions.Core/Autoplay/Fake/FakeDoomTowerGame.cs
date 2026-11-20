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

    /// <summary>The Normal climb's team is refused (a champion busy).</summary>
    public bool NormalRefused { get; set; }

    public Task<BattleOutcome> ClimbDoomTowerAsync(bool hard, bool dryRun, CancellationToken ct)
    {
        if (dryRun) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown"));
        if (!hard)
        {
            if (NormalRefused) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "the Teams card's team was refused here"));
            if (NormalDone) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "no gold floor on the Normal map"));
            NormalClimbs++;
            return Task.FromResult(new BattleOutcome(ClimbResult, "60 s"));
        }
        Climbs++;
        return Task.FromResult(new BattleOutcome(ClimbResult, "60 s"));
    }

    /// <summary>Super Raid asked for on each battle, in order; a replay is only offered while the last battle's result screen is "open" (a win or a loss).</summary>
    public List<bool> Supers { get; } = [];
    public bool ResultOpen { get; set; }

    /// <summary>The game does not offer Super Raid (the battle runs normally, one key's worth).</summary>
    public bool NoSuperRaid { get; set; }

    /// <summary>The result screen is not left open (Replay is not offered), so the stage is played the long way.</summary>
    public bool NoResultScreen { get; set; }

    private BattleOutcome Battle(DtStage stage, bool superRaid)
    {
        var won = !Loses.Contains(stage.Id);
        var asSuper = superRaid && !NoSuperRaid;
        Supers.Add(asSuper);
        var key = DoomTowerRules.KeyOf(stage);
        var cost = DoomTowerRules.Cost(stage) * (asSuper ? 2 : 1);
        if (key == DoomTowerRules.Silver) Silver -= cost;
        if (key == DoomTowerRules.Gold && won) Gold -= cost;
        ResultOpen = !NoResultScreen;
        return new BattleOutcome(won ? BattleResult.Victory : BattleResult.Defeat, "1 s" + (superRaid && NoSuperRaid ? ", normal run" : ""));
    }

    public Task<BattleOutcome> FightDoomTowerAsync(DtStage stage, bool dryRun, CancellationToken ct, bool superRaid = false)
    {
        if (dryRun) return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown, not pressed"));
        Calls.Add($"fight {stage.Id}");
        return Task.FromResult(Battle(stage, superRaid));
    }

    public Task<BattleOutcome?> ReplayDoomTowerAsync(DtStage stage, bool superRaid, CancellationToken ct)
    {
        if (!ResultOpen) return Task.FromResult<BattleOutcome?>(null);
        Calls.Add($"replay {stage.Id}");
        return Task.FromResult<BattleOutcome?>(Battle(stage, superRaid));
    }
}
