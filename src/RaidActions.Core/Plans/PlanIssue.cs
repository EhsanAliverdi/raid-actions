namespace RaidActions.Plans;

public enum Severity { Warning, Error }

public enum IssueCode
{
    // Reading the file
    FileUnreadable, InvalidJson, MissingField, WrongType, BadValue, UnsupportedSchema, UnversionedPlan,
    // Structure (no account needed)
    NoSteps, StepNumbering, UnknownSlot, SameChampion, PieceMovedTwice, SlotFilledTwice, DonorSlotReplacedEarlier,
    ExpectedOwnerMismatch, PieceNotListed, PieceSlotMismatch, PieceOwnerMismatch, TicksIgnored,
    // Against a fresh read of the account
    GameVersionMismatch, AccountOlderThanPlan, PartialRead, ArtifactGone, ArtifactSlotChanged, ArtifactUpgraded, ArtifactRerolled,
    SnapshotMissing, PieceMoved, ChampionGone, ChampionInVault, ChampionBusy, FactionMismatch, NothingToDo,
}

/// <summary>One finding. <see cref="Step"/> is the plan step number it is about, if any.</summary>
public sealed record PlanIssue(Severity Severity, IssueCode Code, string Message, int? Step = null)
{
    public static PlanIssue Error(IssueCode code, string message, int? step = null) => new(Severity.Error, code, message, step);
    public static PlanIssue Warning(IssueCode code, string message, int? step = null) => new(Severity.Warning, code, message, step);
    public override string ToString() => $"{(Severity == Severity.Error ? "ERROR" : "warning")} {(Step is { } s ? $"step {s}: " : "")}{Message} [{Code}]";
}

public static class PlanIssues
{
    public static bool HasErrors(this IEnumerable<PlanIssue> issues) => issues.Any(i => i.Severity == Severity.Error);
}
