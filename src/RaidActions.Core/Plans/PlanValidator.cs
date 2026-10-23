namespace RaidActions.Plans;

/// <summary>
/// Checks a plan on its own, without the account: the steps are numbered, every move is possible and unambiguous, and the end state
/// the plan records (pieces[].expectedOwner) follows from its steps. Refuses rather than repairs: a plan that fails is never changed.
/// </summary>
public static class PlanValidator
{
    public static List<PlanIssue> Validate(Plan plan)
    {
        var issues = new List<PlanIssue>();
        var steps = plan.Steps;
        if (steps.Count == 0)
        {
            issues.Add(PlanIssue.Error(IssueCode.NoSteps, "The plan has no steps."));
            return issues;
        }

        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Number != i + 1)
            {
                issues.Add(PlanIssue.Error(IssueCode.StepNumbering, $"Steps must be numbered 1..{steps.Count} in order; position {i + 1} is step {steps[i].Number}."));
                break;
            }

        var pieces = new Dictionary<int, PlanPiece>();
        foreach (var p in plan.Pieces)
        {
            if (!pieces.TryAdd(p.ArtifactId, p)) issues.Add(PlanIssue.Error(IssueCode.BadValue, $"pieces lists artifact #{p.ArtifactId} twice."));
            if (!Plan.Slots.Contains(p.Slot)) issues.Add(PlanIssue.Error(IssueCode.UnknownSlot, $"pieces: artifact #{p.ArtifactId} has unknown slot \"{p.Slot}\"."));
        }

        var moved = new HashSet<int>();
        var filled = new HashSet<(int, string)>();
        foreach (var s in steps)
        {
            var n = s.Number;
            if (!Plan.Slots.Contains(s.Slot)) issues.Add(PlanIssue.Error(IssueCode.UnknownSlot, $"Unknown slot \"{s.Slot}\".", n));
            if (s.ArtifactId <= 0) issues.Add(PlanIssue.Error(IssueCode.BadValue, $"artifactId {s.ArtifactId} is not a valid id.", n));
            if (s.ToChampionId <= 0) issues.Add(PlanIssue.Error(IssueCode.BadValue, $"toChampionId {s.ToChampionId} is not a valid id.", n));
            if (s.FromChampionId is <= 0) issues.Add(PlanIssue.Error(IssueCode.BadValue, $"fromChampionId {s.FromChampionId} is not a valid id (null = inventory).", n));
            if (s.FromChampionId == s.ToChampionId) issues.Add(PlanIssue.Error(IssueCode.SameChampion, $"Moves #{s.ArtifactId} from {s.ToName} to the same champion.", n));
            if (s.ExpectedOwnerAfter != s.ToChampionId)
                issues.Add(PlanIssue.Error(IssueCode.ExpectedOwnerMismatch, $"expectedOwnerAfter is #{s.ExpectedOwnerAfter}, but the step puts the piece on #{s.ToChampionId}.", n));
            if (!moved.Add(s.ArtifactId)) issues.Add(PlanIssue.Error(IssueCode.PieceMovedTwice, $"Artifact #{s.ArtifactId} is moved by more than one step.", n));
            if (!filled.Add((s.ToChampionId, s.Slot))) issues.Add(PlanIssue.Error(IssueCode.SlotFilledTwice, $"{s.ToName}'s {s.Slot} is filled by more than one step.", n));

            // Taking a piece from a slot an earlier step already refilled: by then the piece is in the inventory, so "from" would be wrong.
            if (s.FromChampionId is { } from && steps.TakeWhile(e => e.Number < n).FirstOrDefault(e => e.ToChampionId == from && e.Slot == s.Slot) is { } earlier)
                issues.Add(PlanIssue.Error(IssueCode.DonorSlotReplacedEarlier,
                    $"Takes #{s.ArtifactId} from {s.FromName}'s {s.Slot}, but step {earlier.Number} already put another piece there (this piece would be in the inventory by then).", n));

            if (!pieces.TryGetValue(s.ArtifactId, out var piece))
                issues.Add(PlanIssue.Error(IssueCode.PieceNotListed, $"Artifact #{s.ArtifactId} is not in pieces, so its state when the plan was made is unknown.", n));
            else
            {
                if (piece.Slot != s.Slot) issues.Add(PlanIssue.Error(IssueCode.PieceSlotMismatch, $"The step says {s.Slot}; pieces says #{s.ArtifactId} is a {piece.Slot}.", n));
                if (piece.Owner != s.FromChampionId)
                    issues.Add(PlanIssue.Error(IssueCode.PieceOwnerMismatch, $"The step takes #{s.ArtifactId} from {Who(s.FromChampionId)}; pieces has it on {Who(piece.Owner)}.", n));
            }
        }

        // The end state the plan records must be what its steps produce.
        if (!issues.HasErrors())
        {
            var state = new GearState(plan.Pieces.Select(p => (p.ArtifactId, p.Slot, p.Owner)));
            foreach (var s in steps) state.Apply(s);
            foreach (var p in plan.Pieces)
                if (state.OwnerOf(p.ArtifactId) != p.ExpectedOwner)
                    issues.Add(PlanIssue.Error(IssueCode.PieceOwnerMismatch,
                        $"pieces says #{p.ArtifactId} ends on {Who(p.ExpectedOwner)}, but the steps leave it on {Who(state.OwnerOf(p.ArtifactId))}."));
        }
        return issues;
    }

    internal static string Who(int? id) => id is { } i ? $"champion #{i}" : "the inventory";
}
