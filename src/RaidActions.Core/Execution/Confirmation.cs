using RaidActions.Plans;
using RaidActions.Verification;

namespace RaidActions.Execution;

/// <summary>What the user confirms before a run: the steps in order, the donors and what they lose, and the pieces that go to the inventory.</summary>
public static class Confirmation
{
    /// <param name="mode">Null for a check only (no run).</param>
    public static List<string> Lines(LoadedPlan loaded, CheckReport check, RunMode? mode, string driverName)
    {
        var plan = loaded.Plan;
        var lines = new List<string>
        {
            mode switch
            {
                RunMode.DryRun => $"DRY RUN (navigates and highlights, never equips) with the {driverName} executor",
                RunMode.Real => $"REAL RUN (equips) with the {driverName} executor",
                _ => $"CHECK ONLY (no run) with the {driverName} executor",
            },
            $"Plan \"{plan.Name}\"  id {plan.PlanId}  game {plan.GameVersion}  sha256 {loaded.Sha256[..12]}",
            "",
            "Steps:",
        };
        foreach (var s in check.Steps)
        {
            var from = s.Step.FromChampionId is null ? "the inventory" : $"{s.Step.FromName} (#{s.Step.FromChampionId})";
            var state = s.State == StepState.Done ? "  [already done: skipped]" : "";
            var displaced = s.Displaced is { } d ? $"; #{d} goes to the inventory" : "";
            lines.Add($"  {s.Step.Number}. {s.Step.Slot} #{s.Step.ArtifactId}: {from} → {s.Step.ToName} (#{s.Step.ToChampionId}){displaced}{state}");
            lines.Add($"     {s.Step.Piece}");
        }

        var donors = check.Pending.Where(s => s.Step.FromChampionId is not null).GroupBy(s => (s.Step.FromChampionId, s.Step.FromName)).ToList();
        lines.Add("");
        lines.Add(donors.Count == 0 ? "Donors: none (every piece comes from the inventory)." : "Donors (lose these pieces):");
        foreach (var g in donors)
            lines.Add($"  {g.Key.FromName} (#{g.Key.FromChampionId}): " + string.Join(", ", g.Select(s => $"{s.Step.Slot} #{s.Step.ArtifactId}")));

        var warnings = check.Issues.Where(i => i.Severity == Severity.Warning).ToList();
        if (warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Warnings:");
            lines.AddRange(warnings.Select(w => "  " + w.Message));
        }
        return lines;
    }

    /// <summary>What the user types (CLI) to confirm: the first 8 characters of the plan id.</summary>
    public static string Code(Plan plan) => plan.PlanId.Length <= 8 ? plan.PlanId : plan.PlanId[..8];

    public static bool Matches(Plan plan, string? typed) =>
        typed is not null && (typed.Trim() == plan.PlanId || typed.Trim() == Code(plan));
}
