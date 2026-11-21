namespace RaidActions.Autoplay;

/// <summary>
/// A game background battle (a multi-battle run the game plays by itself, or the Doom Tower auto-climb), read from the client's state
/// (UserWrapper.BackgroundAutoBattle). <see cref="State"/> is the game's AutoBattleState: Started = running; Finished, EndedOnCurrentBattle, StoppedMultiBattle,
/// EndedAfterStoppedBattle = over, its results waiting to be collected. <see cref="Area"/> is "DoomTower" for an auto-climb, else the stage's area when known.
/// </summary>
public sealed record BackgroundBattleInfo(int StageId, int FirstStageId, string Area, string State, int Completed, int Won, bool AutoClimb, string? Region = null)
{
    public bool Running => State == "Started";

    public bool Finished => State is "Finished" or "EndedOnCurrentBattle" or "StoppedMultiBattle" or "EndedAfterStoppedBattle";

    public override string ToString() =>
        $"stage {StageId}{(FirstStageId != StageId && FirstStageId != 0 ? $" (from {FirstStageId})" : "")} {(AutoClimb ? "Doom Tower auto-climb" : string.IsNullOrEmpty(Area) ? "run" : Area + (Region is null ? "" : " " + Region))}: {State}, {Completed} done ({Won} won)";
}
