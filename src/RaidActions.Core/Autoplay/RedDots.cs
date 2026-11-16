namespace RaidActions.Autoplay;

/// <summary>What the bot does about a red dot on the village (Ehsan 2026-11-16: "be aware of all red dots and act on them; log a warning when you cannot").</summary>
/// <param name="Job">The chores job that answers it (inbox, free, checkin, pass, pit); null: ignored by rule or not handled.</param>
/// <param name="Ignore">Why it is left on purpose; null with a null job: not handled (a warning).</param>
public sealed record RedDotAction(string? Job, string? Ignore);

/// <summary>The village's red dots by the button they sit on (seen 2026-11-16 on Smasher8465).</summary>
public static class RedDots
{
    // The Shop is the game's "Bank" (BankOverlay): its dot can sit on a bare "Indicator" node (2026-11-16), so the dot's own name counts too.
    public static RedDotAction For(string button, string? dot = null) => dot == "BankIndicator_h" ? new("free", null) : button switch
    {
        "InboxButton" => new("inbox", null),
        "ShopButton" => new("free", null),
        "AllianceButton" => new("checkin", null),
        // The side menu's Plarium Points and Progression Rewards are claimed by the pass job (ClaimMenuRewardsAsync).
        // The Events button's dot (2026-11-17): the pass job ends with the events, and claims only on the cards with a dot.
        "BattlePass_h" or "ChallengesButton_h" or "PlariumProgram_h" or "MilestoneReward" or "EventsButton" => new("pass", null),
        "AcademyButton" or "GuardianRingButton" => new("pit", null),
        "PlayerAvatarContainer" => new(null, "a new player-card background (cosmetic)"),
        "ReferralProgram_h" => new(null, "use your referral link (nothing to claim)"),
        "CollectionButton" => new(null, "Index: a new champion not looked at (nothing to claim)"),
        "HeroesButton" => new(null, "a champion's masteries, relic or blessing (not built yet)"),
        "RaidShow_h" => new(null, "the Raid Show banner"),
        "WorldMapButton" => new(null, "Battle: something can be played (energy, keys); the play areas spend them"),
        "QuestButton" => new(null, "a quest to claim: the Free rewards area claims the quests"),
        _ => new(null, null),
    };
}
