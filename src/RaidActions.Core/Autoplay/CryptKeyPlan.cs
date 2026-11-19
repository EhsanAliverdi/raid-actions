using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>
/// Faction Wars key management (Ehsan 2026-11-20): Super Raid is allowed on a stage already beaten; a stage not beaten yet is one battle.
/// Keys needed = the stage's cost x the multiplier (the game's BattlePrice(BattleMultiplier None/Double/Triple), MultiRunContext); the multiplier
/// steps down (x3, x2, off) until the spendable keys pay for it. A single battle that cannot be paid: a cheaper beaten stage if keys remain, else none.
/// </summary>
public static class CryptKeyPlan
{
    /// <param name="Cheaper">Played a lower stage than the one wanted because the keys did not pay for it.</param>
    public sealed record Run(int Stage, int Multiplier, bool Cheaper)
    {
        public bool SuperRaid => Multiplier > 1;
    }

    /// <param name="spendable">Keys minus the Clan vs Clan hold.</param>
    /// <param name="maxMultiplier">1 (off), 2 or 3.</param>
    public static Run? Next(CryptPlan c, int spendable, int wantedStage, bool newLevel, int maxMultiplier)
    {
        if (spendable <= 0) return null;
        if (newLevel)
        {
            // Not beaten yet (a push): a single battle, never a Super Raid (the game locks the box).
            if (spendable >= c.Cost(c.NextStage)) return new Run(c.NextStage, 1, false);
            return Cheaper(c, spendable, Math.Max(1, c.HighestPassed));
        }
        var stage = Math.Clamp(wantedStage, 1, c.StageCount);
        var beaten = stage <= c.HighestPassed;
        for (var m = beaten ? Math.Clamp(maxMultiplier, 1, 3) : 1; m >= 1; m--)
            if (spendable >= c.Cost(stage) * m) return new Run(stage, m, false);
        return Cheaper(c, spendable, stage);
    }

    /// <summary>The highest beaten stage (not above <paramref name="top"/>) the keys pay for as a single battle.</summary>
    private static Run? Cheaper(CryptPlan c, int keys, int top)
    {
        for (var s = Math.Min(top, Math.Max(1, c.HighestPassed)); s >= 1; s--)
            if (c.Cost(s) <= keys) return new Run(s, 1, s != top);
        return null;
    }

    /// <summary>Whether the result screen's Replay may be pressed: the keys cover the next battle (cost x multiplier).</summary>
    public static bool MayReplay(int keysLeft, int stageCost, int multiplier) => keysLeft >= stageCost * Math.Max(1, multiplier);
}
