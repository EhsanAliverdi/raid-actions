using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>Which open task may keep an account's turn going past its time.</summary>
public static class TurnHold
{
    /// <summary>
    /// Only today's work holds a turn (2026-11-12), and only work that an area wanting to act now can really do: 2026-11-20 11:37 a vault put-back of Faction Wars
    /// (waiting for Tag Team) held the turn for up to 15 minutes while the bot looped between other areas. No acting area: the turn ends (the task stays open for the next turn).
    /// </summary>
    public static TaskRow? Pick(IReadOnlyList<TaskRow> open, DateTime now, IReadOnlySet<string> acting)
    {
        bool Recent(TaskRow t) => (t.UpdatedUtc ?? t.CreatedUtc) is { } at && now - at < TimeSpan.FromHours(6);
        bool Can(TaskRow t) => acting.Contains(t.Area);
        return open.FirstOrDefault(t => t.State == TodoState.InProgress && t.Kind != "quest" && Recent(t) && Can(t) && (t.DeadlineUtc is not { } d || d > now))
            ?? open.FirstOrDefault(t => t.FollowUpDue(now) && Recent(t) && Can(t));
    }
}
