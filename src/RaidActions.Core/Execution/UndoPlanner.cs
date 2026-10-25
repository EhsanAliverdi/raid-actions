using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaidActions.Plans;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Execution;

/// <summary>
/// The undo of executed steps, as a plan file in the planner's format (schema 1) so the same checks and driver can run it: for a
/// step that took a piece from the inventory and replaced piece D, the undo equips D on the same champion again (the step's piece
/// goes back to the inventory). Written to output/undo/&lt;run&gt;/plans with the read it was made from in output/undo/&lt;run&gt;/history.
/// Steps that cannot be undone with Equip alone (no replaced piece: it would need Remove; a piece taken from a donor) are refused.
/// </summary>
public static class UndoPlanner
{
    public sealed record Result(string? PlanPath, string? Problem, IReadOnlyList<PlanStep> Steps);

    /// <param name="checkedBefore">The check made before the run: its steps say which piece each step replaced.</param>
    /// <param name="now">A fresh read after the run: the undo is made from it.</param>
    public static Result Write(Plan original, CheckReport checkedBefore, IReadOnlyCollection<int> done, SavedExport now, string folder, DateTimeOffset at)
    {
        var artifacts = now.Artifacts.ToDictionary(a => a.Id);
        var champions = now.Champions.ToDictionary(c => c.Id);
        var undo = new List<PlanStep>();
        foreach (var sc in checkedBefore.Steps.Where(s => done.Contains(s.Step.Number)).OrderByDescending(s => s.Step.Number))
        {
            var s = sc.Step;
            if (s.FromChampionId is not null) return new(null, $"Step {s.Number} took #{s.ArtifactId} from {s.FromName}: its undo is not supported (it would take the piece back from {s.ToName}).", []);
            if (sc.Displaced is not { } d) return new(null, $"Step {s.Number} filled an empty slot: its undo would need Remove, which is not allowed.", []);
            if (!artifacts.TryGetValue(d, out var back)) return new(null, $"Step {s.Number}: the replaced piece #{d} is no longer in the account.", []);
            if (back.OwnerHeroId is not null) return new(null, $"Step {s.Number}: the replaced piece #{d} is no longer in the inventory (on #{back.OwnerHeroId}).", []);
            undo.Add(new PlanStep(undo.Count + 1, d, s.Slot, $"{s.Slot} #{d} (the piece step {s.Number} replaced)", null, "the inventory", s.ToChampionId, s.ToName, s.ToChampionId));
        }
        if (undo.Count == 0) return new(null, "No executed step to undo.", []);

        // Pieces: the ones moved, and everything the touched champions wear now; their owner after the undo, the way the game does it.
        var touched = undo.Select(u => u.ToChampionId).ToHashSet();
        var ids = undo.Select(u => u.ArtifactId).Concat(now.Artifacts.Where(a => a.OwnerHeroId is { } o && touched.Contains(o)).Select(a => a.Id)).Distinct().ToList();
        var state = new GearState(ids.Select(i => (i, artifacts[i].Slot, artifacts[i].OwnerHeroId)));
        foreach (var u in undo) state.Apply(u);

        Directory.CreateDirectory(Path.Combine(folder, "plans"));
        var snapshotFile = Path.GetFileName(History.Save(folder, now));
        var planId = Guid.NewGuid().ToString("N");
        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["planId"] = planId,
            ["name"] = $"Undo of \"{original.Name}\": step{(done.Count == 1 ? "" : "s")} {string.Join(", ", done.Order())}",
            ["created"] = at.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["gameVersion"] = now.Meta.GameVersion,
            ["exportedAt"] = now.Meta.ExportedAt,
            ["snapshotId"] = snapshotFile,
            ["undoOf"] = new JsonObject { ["planId"] = original.PlanId, ["steps"] = new JsonArray(done.Order().Select(n => (JsonNode)n).ToArray()) },
            ["steps"] = new JsonArray(undo.Select(u => (JsonNode)new JsonObject
            {
                ["number"] = u.Number, ["artifactId"] = u.ArtifactId, ["slot"] = u.Slot, ["piece"] = u.Piece, ["fromChampionId"] = null,
                ["fromName"] = u.FromName, ["toChampionId"] = u.ToChampionId, ["toName"] = u.ToName, ["expectedOwnerAfter"] = u.ExpectedOwnerAfter,
            }).ToArray()),
            ["pieces"] = new JsonArray(ids.Order().Select(i =>
            {
                var a = artifacts[i];
                return (JsonNode)new JsonObject
                {
                    ["artifactId"] = a.Id, ["slot"] = a.Slot, ["owner"] = a.OwnerHeroId, ["level"] = a.Level, ["rank"] = a.Rank, ["ascension"] = a.AscendLevel,
                    ["expectedOwner"] = state.OwnerOf(a.Id), ["fingerprint"] = RaidExtractor.Battle.PlanPiece.FingerprintOf(a),
                };
            }).ToArray()),
        };
        var path = Path.Combine(folder, "plans", $"undo-{original.PlanId[..8]}-steps-{string.Join("-", done.Order())}-{at:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new(path, null, undo);
    }
}
