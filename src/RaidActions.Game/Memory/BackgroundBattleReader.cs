using RaidActions.Autoplay;

namespace RaidActions.Game.Memory;

/// <summary>
/// The game's background battle (read-only): AppModel._userWrapper (Core's offsets) → UserWrapper.BackgroundAutoBattle (0x168, manual) → AutoBattleWrapperReadOnly
/// { State (AutoBattleState), _report (AutoBattleReport: StageId, FirstStageId, BattleCount, VictoryBattlesCount, IsAutoClimb), BattleData.BackgroundBattleStats
/// (LastStageId, FirstStageId, CompletedBattles, WonBattles, IsAutoClimb) }. Nothing is returned while the state is UnStarted.
/// The client keeps no end time or run total of its own here; the bot re-reads each turn.
/// </summary>
public sealed class BackgroundBattleReader(IGameObjects g)
{
    private const string AppModel = "Client.Model.AppModel";
    private const string UserWrapper = "Client.Model.Guard.UserWrapper";
    private const string Wrapper = "Client.Model.Gameplay.AutoBattle.AutoBattleWrapperReadOnly";
    private const string Report = "Client.Model.Gameplay.AutoBattle.AutoBattleReport";
    private const string BattleData = "SharedModel.Meta.Battle.UserBattleData";
    private const string Stats = "SharedModel.Meta.Battle.AutoBattle.UserBackgroundBattleStats";
    public const string StateEnum = "Client.Model.Gameplay.AutoBattle.AutoBattleState";

    public IReadOnlyList<BackgroundBattleInfo> Read()
    {
        var app = g.Root("AppModel", AppModel);
        var user = g.Ref(app, AppModel, "_userWrapper");
        if (user == 0) throw new UiReadException("AppModel._userWrapper is null.");
        var wrapper = g.Ref(user, UserWrapper, "BackgroundAutoBattle");
        if (wrapper == 0) return [];
        var state = g.EnumName(StateEnum, g.Int(wrapper, Wrapper, "State")) ?? throw new UiReadException("BackgroundAutoBattle.State: unknown AutoBattleState.");
        if (state == "UnStarted") return [];
        int stage = 0, first = 0, done = 0, won = 0;
        var climb = false;
        var report = g.Ref(wrapper, Wrapper, "_report");
        if (report != 0)
        {
            (stage, first, done, won, climb) = (g.Int(report, Report, "StageId"), g.Int(report, Report, "FirstStageId"),
                g.Int(report, Report, "BattleCount"), g.Int(report, Report, "VictoryBattlesCount"), g.Bool(report, Report, "IsAutoClimb"));
        }
        // The persisted stats (they survive a restart of the client) fill what the report does not have.
        var data = g.Ref(wrapper, Wrapper, "BattleData");
        var stats = data == 0 ? 0 : g.Ref(data, BattleData, "BackgroundBattleStats");
        if (stats != 0)
        {
            if (stage == 0) stage = g.Int(stats, Stats, "LastStageId");
            if (first == 0) first = g.Int(stats, Stats, "FirstStageId");
            done = Math.Max(done, g.Int(stats, Stats, "CompletedBattles"));
            won = Math.Max(won, g.Int(stats, Stats, "WonBattles"));
            climb |= g.Bool(stats, Stats, "IsAutoClimb");
        }
        return [new BackgroundBattleInfo(stage, first, climb ? "DoomTower" : "", state, done, won, climb)];
    }
}
