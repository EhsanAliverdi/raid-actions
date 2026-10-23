using System.Globalization;
using RaidActions.Plans;
using RaidExtractor;

namespace RaidActions.Verification;

public enum StepState { Pending, Done }

/// <summary>A step as the account shows it now. <see cref="Displaced"/>: the piece the recipient wears in that slot now (goes to the inventory).</summary>
public sealed record StepCheck(PlanStep Step, StepState State, int? Displaced);

/// <summary>The result of checking a plan against a fresh read. The plan may start only when <see cref="CanStart"/>.</summary>
public sealed record CheckReport(List<PlanIssue> Issues, List<StepCheck> Steps)
{
    public bool CanStart => !Issues.HasErrors() && Steps.Any(s => s.State == StepState.Pending);
    public IEnumerable<StepCheck> Pending => Steps.Where(s => s.State == StepState.Pending);
}

/// <summary>
/// The check before a plan runs, and after every step: the plan against a fresh read of the account. Refuses (errors) when the game
/// version differs, a piece is gone, upgraded or rerolled, a piece is anywhere other than where the plan and the steps already done
/// put it, or a champion is gone or in the vault. Never adjusts the plan.
/// </summary>
public static class AccountCheck
{
    /// <param name="fresh">A fresh read of the account (or, for an offline check, a saved export).</param>
    /// <param name="madeFrom">The export the plan was made from (raid-extractor history file named by the plan's snapshotId), or null if
    /// it was not found: then substats cannot be confirmed unchanged and the plan is refused.</param>
    public static CheckReport Check(Plan plan, SavedExport fresh, SavedExport? madeFrom)
    {
        var issues = PlanValidator.Validate(plan);
        if (issues.HasErrors()) return new(issues, []);

        if (!string.Equals(plan.GameVersion, fresh.Meta.GameVersion, StringComparison.Ordinal))
            issues.Add(PlanIssue.Error(IssueCode.GameVersionMismatch, $"The plan was made on game version {plan.GameVersion}; the game is now {fresh.Meta.GameVersion}."));
        if (Time(fresh.Meta.ExportedAt) is { } now && Time(plan.ExportedAt) is { } made && now < made)
            issues.Add(PlanIssue.Error(IssueCode.AccountOlderThanPlan, $"The account read ({fresh.Meta.ExportedAt}) is older than the export the plan was made from ({plan.ExportedAt})."));
        if (fresh.Skipped.Artifacts.Count > 0 || fresh.Skipped.Champions.Count > 0)
            issues.Add(PlanIssue.Warning(IssueCode.PartialRead,
                $"The account read skipped {fresh.Skipped.Champions.Count} champion(s) and {fresh.Skipped.Artifacts.Count} artifact(s); anything the plan needs must still be present."));

        var artifacts = fresh.Artifacts.ToDictionary(a => a.Id);
        var champions = fresh.Champions.ToDictionary(c => c.Id);
        var original = madeFrom?.Artifacts.ToDictionary(a => a.Id);
        if (madeFrom is null)
            issues.Add(PlanIssue.Error(IssueCode.SnapshotMissing,
                $"The export the plan was made from ({plan.SnapshotId ?? "no snapshotId"}) was not found, so substats and glyphs cannot be confirmed unchanged."));
        else if (madeFrom.Meta.ExportedAt != plan.ExportedAt)
            issues.Add(PlanIssue.Error(IssueCode.SnapshotMissing, $"The snapshot {plan.SnapshotId} is from {madeFrom.Meta.ExportedAt}, not {plan.ExportedAt} as the plan says."));

        var stepOf = plan.Steps.ToDictionary(s => s.ArtifactId);
        foreach (var p in plan.Pieces)
        {
            int? n = stepOf.TryGetValue(p.ArtifactId, out var st) ? st.Number : null;
            var label = $"{p.Slot} #{p.ArtifactId}";
            if (!artifacts.TryGetValue(p.ArtifactId, out var a))
            {
                issues.Add(PlanIssue.Error(IssueCode.ArtifactGone, $"{label} is no longer in the account (sold or used){(fresh.Skipped.Artifacts.Count > 0 ? ", or the read skipped it" : "")}.", n));
                continue;
            }
            if (a.Slot != p.Slot) issues.Add(PlanIssue.Error(IssueCode.ArtifactSlotChanged, $"{label} is now a {a.Slot}: not the same piece.", n));
            if (a.Level != p.Level || a.Rank != p.Rank || a.AscendLevel != p.Ascension)
                issues.Add(PlanIssue.Error(IssueCode.ArtifactUpgraded,
                    $"{label} changed since the plan: {p.Rank}★ +{p.Level} asc {p.Ascension} → {a.Rank}★ +{a.Level} asc {a.AscendLevel}.", n));
            if (original is not null && original.TryGetValue(p.ArtifactId, out var o) && Fingerprint(o) != Fingerprint(a))
                issues.Add(PlanIssue.Error(IssueCode.ArtifactRerolled, $"{label}'s set, rarity, main stat, substats or glyphs changed since the plan.", n));
            else if (original is not null && !original.ContainsKey(p.ArtifactId))
                issues.Add(PlanIssue.Error(IssueCode.SnapshotMissing, $"{label} is not in the plan's snapshot export.", n));
        }

        var done = plan.Steps.Where(s => artifacts.TryGetValue(s.ArtifactId, out var a) && a.OwnerHeroId == s.ToChampionId).Select(s => s.Number).ToHashSet();
        issues.AddRange(OwnersAgree(plan, fresh, done));

        foreach (var id in plan.Steps.SelectMany(s => new[] { s.FromChampionId, s.ToChampionId }).OfType<int>().Distinct())
        {
            if (!champions.TryGetValue(id, out var c))
            {
                issues.Add(PlanIssue.Error(IssueCode.ChampionGone, $"Champion #{id} is no longer in the account{(fresh.Skipped.Champions.Count > 0 ? " (or the read skipped it)" : "")}."));
                continue;
            }
            if (c.InVault) issues.Add(PlanIssue.Error(IssueCode.ChampionInVault, $"{c.Name ?? $"#{id}"} is in the vault (storage); gear cannot be changed there."));
            if (c.InBathhouse) issues.Add(PlanIssue.Warning(IssueCode.ChampionBusy, $"{c.Name ?? $"#{id}"} is in the bathhouse."));
        }

        foreach (var s in plan.Steps)
            if (artifacts.TryGetValue(s.ArtifactId, out var a) && a.Faction is { } faction && champions.TryGetValue(s.ToChampionId, out var to) && to.Faction != faction)
                issues.Add(PlanIssue.Error(IssueCode.FactionMismatch, $"#{s.ArtifactId} is a {faction} accessory; {to.Name} is {to.Faction}.", s.Number));

        var steps = plan.Steps.Select(s => new StepCheck(s, done.Contains(s.Number) ? StepState.Done : StepState.Pending,
            done.Contains(s.Number) || !champions.TryGetValue(s.ToChampionId, out var c) ? null
            : c.Artifacts.TryGetValue(s.Slot, out var worn) && worn != s.ArtifactId ? worn : null)).ToList();
        if (!issues.HasErrors() && steps.All(s => s.State == StepState.Done))
            issues.Add(PlanIssue.Warning(IssueCode.NothingToDo, "Every step is already done: the account matches the plan's end state."));
        return new(issues, steps);
    }

    /// <summary>
    /// Every piece the plan knows is exactly where the plan puts it after <paramref name="doneSteps"/> (and nowhere else), and the
    /// recipients wear nothing in the plan's slots that the plan does not know. Used before starting and after every step.
    /// </summary>
    public static List<PlanIssue> OwnersAgree(Plan plan, SavedExport fresh, IReadOnlySet<int> doneSteps)
    {
        var issues = new List<PlanIssue>();
        var artifacts = fresh.Artifacts.ToDictionary(a => a.Id);
        var champions = fresh.Champions.ToDictionary(c => c.Id);
        var state = new GearState(plan.Pieces.Select(p => (p.ArtifactId, p.Slot, p.Owner)));
        foreach (var s in plan.Steps.Where(s => doneSteps.Contains(s.Number))) state.Apply(s);
        var stepOf = plan.Steps.ToDictionary(s => s.ArtifactId);
        string Who(int? id) => id is null ? "the inventory" : champions.TryGetValue(id.Value, out var c) ? $"{c.Name} (#{id})" : $"champion #{id}";

        foreach (var (id, expected) in state.Owners)
        {
            if (!artifacts.TryGetValue(id, out var a)) continue; // reported as ArtifactGone
            if (a.OwnerHeroId == expected) continue;
            int? n = stepOf.TryGetValue(id, out var st) ? st.Number : null;
            var after = doneSteps.Count == 0 ? "" : $" (after step{(doneSteps.Count == 1 ? "" : "s")} {string.Join(", ", doneSteps.Order())})";
            issues.Add(PlanIssue.Error(IssueCode.PieceMoved, $"#{id} should be on {Who(expected)}{after}, but is on {Who(a.OwnerHeroId)}.", n));
        }

        foreach (var s in plan.Steps)
            if (champions.TryGetValue(s.ToChampionId, out var c) && c.Artifacts.TryGetValue(s.Slot, out var worn) && !state.Knows(worn))
                issues.Add(PlanIssue.Error(IssueCode.PieceMoved, $"{c.Name} now wears #{worn} as {s.Slot}, a piece the plan does not know.", s.Number));
        return issues;
    }

    /// <summary>What makes a piece the same piece beyond level, rank and ascension: set, rarity, main stat, substats with rolls, glyphs.</summary>
    internal static string Fingerprint(ArtifactOut a)
    {
        static string S(StatOut? s) => s is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{s.Stat}:{s.Absolute}:{s.Value:R}:{s.Glyph:R}:{s.Rolls}:{s.GlyphRarity}");
        return $"{a.SetKindId}|{a.Rarity}|{a.Faction}|{S(a.Primary)}|{string.Join(",", a.Substats.Select(S))}|{S(a.AscendBonus)}";
    }

    private static DateTimeOffset? Time(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
