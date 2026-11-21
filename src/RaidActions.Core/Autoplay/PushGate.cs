using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>Which simulator engine a push is judged by (Ehsan 2026-11-21: a new stage is tried only when the simulator gives over 50%).</summary>
public enum PushEngine
{
    /// <summary>No usable prediction: no push, the highest beaten stage is farmed.</summary>
    None,
    /// <summary>The old engine (StageSim), validated against real fights: Dragon's Lair, Ice Golem, the affinity Keeps, the Campaign.</summary>
    Old,
    /// <summary>The ported engine (GameSimBuilder), where plan/simulator-port.md says it is fit: Arcane Keep, Iron Twins.</summary>
    New,
}

/// <summary>A forecast of one unbeaten stage: the old engine's rate and the new engine's (null: that engine did not run), as fractions 0-1; Why for the log.</summary>
public sealed record PushForecast(double? OldRate, double? NewRate, string Why);

public static class PushGate
{
    /// <summary>The rate a forecast must beat (fraction).</summary>
    public const double Threshold = 0.5;

    public static PushEngine EngineFor(string area, string region) =>
        area == "Story" ? PushEngine.Old
        : region is "DragonsLair" or "IceGolemCave" or "VoidKeep" or "SpiritKeep" or "MagicKeep" or "ForceKeep" or EnergyPlaces.AnyKeep ? PushEngine.Old
        : region is "ArcaneKeep" or EnergyPlaces.AnyIronTwins || region.EndsWith("DoubleAscendDungeon", StringComparison.Ordinal) ? PushEngine.New
        : PushEngine.None; // Spider, Fire Knight, Hydra, the rest: no trusted forecast

    /// <summary>The stage is one the account has not beaten yet (a farm of a beaten stage is never gated).</summary>
    public static bool IsUnbeaten(EnergyRun run, IReadOnlyList<AreaProgressOut> progress)
    {
        var d = progress.FirstOrDefault(a => a.Area == run.Target.Area)?.Regions.FirstOrDefault(r => r.Region == run.Region)?
            .Difficulties.FirstOrDefault(x => EnergyPlaces.SameDifficulty(run.Difficulty, x.Difficulty));
        if (d is null) return true;
        return !(d.Results.Any(r => r.Number == run.Number && r.Passed) || d.HighestPassed is { } top && run.Number <= top);
    }

    /// <summary>The rate the engine's decision rests on; null when that engine gave none.</summary>
    public static double? Decisive(PushEngine engine, PushForecast f) => engine == PushEngine.Old ? f.OldRate : f.NewRate;
}
