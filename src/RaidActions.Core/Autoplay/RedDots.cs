namespace RaidActions.Autoplay;

/// <summary>What the bot does about a red dot on the village (Ehsan 2026-11-16: "be aware of all red dots and act on them; log a warning when you cannot").</summary>
/// <param name="Job">The chores job that answers it (inbox, free, checkin, pass, pit); null: ignored by rule or not handled.</param>
/// <param name="Ignore">Why it is left on purpose; null with a null job: not handled (a warning).</param>
public sealed record RedDotAction(string? Job, string? Ignore);

/// <summary>The village's red dots by the button they sit on (seen 2026-11-16 on Smasher8465).</summary>
public static class RedDots
{
    public static RedDotAction For(string button) => button switch
    {
        "InboxButton" => new("inbox", null),
        "ShopButton" => new("free", null),
        "AllianceButton" => new("checkin", null),
        "BattlePass_h" or "ChallengesButton_h" => new("pass", null),
        "AcademyButton" or "GuardianRingButton" => new("pit", null),
        "PlayerAvatarContainer" => new(null, "a new player-card background (cosmetic)"),
        "ReferralProgram_h" => new(null, "use your referral link (nothing to claim)"),
        "CollectionButton" => new(null, "Index: a new champion not looked at (nothing to claim)"),
        "HeroesButton" => new(null, "a champion's masteries, relic or blessing (not built yet)"),
        "RaidShow_h" => new(null, "the Raid Show banner"),
        _ => new(null, null),
    };
}
