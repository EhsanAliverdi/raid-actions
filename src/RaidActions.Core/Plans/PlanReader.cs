using System.Security.Cryptography;
using System.Text.Json;

namespace RaidActions.Plans;

/// <summary>
/// Reads a plan file field by field, so a bad file says exactly which field is wrong (<c>steps[2].slot</c>) instead of failing as a
/// whole. Unknown fields are ignored (the planner adds informational ones). Never changes or fills in anything: a missing required
/// field is an error.
/// </summary>
public static class PlanReader
{
    public sealed record Result(LoadedPlan? Plan, List<PlanIssue> Issues)
    {
        public bool Ok => Plan is not null && !Issues.HasErrors();
    }

    public static Result ReadFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, [PlanIssue.Error(IssueCode.FileUnreadable, $"Cannot read {path}: {e.Message}")]);
        }
        return Read(bytes, path);
    }

    public static Result Read(string json, string? path = null) => Read(System.Text.Encoding.UTF8.GetBytes(json), path);

    public static Result Read(byte[] bytes, string? path = null)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException e) { return new(null, [PlanIssue.Error(IssueCode.InvalidJson, $"Not valid JSON: {e.Message}")]); }

        using (doc)
        {
            var issues = new List<PlanIssue>();
            var f = new Fields(issues);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(null, [PlanIssue.Error(IssueCode.WrongType, "The plan must be a JSON object.")]);

            // Version first: an unversioned (pre-v1) or newer plan is refused before anything else is read.
            if (!root.TryGetProperty("schemaVersion", out var ver))
                return new(null, [PlanIssue.Error(IssueCode.UnversionedPlan,
                    "The plan has no schemaVersion: it was saved before plan files recorded the pieces' state, so it cannot be checked. Re-plan in raid-extractor.")]);
            if (ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var version))
                return new(null, [PlanIssue.Error(IssueCode.WrongType, "schemaVersion must be an integer.")]);
            if (version < 1 || version > Plan.SupportedVersion)
                return new(null, [PlanIssue.Error(IssueCode.UnsupportedSchema, $"Plan schema version {version}; this app executes version {Plan.SupportedVersion}.")]);

            var planId = f.String(root, "planId", "planId");
            var name = f.String(root, "name", "name");
            var created = f.String(root, "created", "created");
            var gameVersion = f.String(root, "gameVersion", "gameVersion");
            var exportedAt = f.String(root, "exportedAt", "exportedAt");
            var snapshotId = f.OptString(root, "snapshotId", "snapshotId");
            if (exportedAt is not null && !DateTimeOffset.TryParse(exportedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                issues.Add(PlanIssue.Error(IssueCode.BadValue, $"exportedAt is not a date: \"{exportedAt}\"."));
            if (gameVersion is "") issues.Add(PlanIssue.Error(IssueCode.BadValue, "gameVersion is empty."));
            if (planId is "") issues.Add(PlanIssue.Error(IssueCode.BadValue, "planId is empty."));

            var steps = new List<PlanStep>();
            foreach (var (s, p) in f.Array(root, "steps", "steps"))
            {
                var number = f.Int(s, "number", p);
                var artifactId = f.Int(s, "artifactId", p);
                var slot = f.String(s, "slot", p);
                var piece = f.String(s, "piece", p);
                var from = f.OptInt(s, "fromChampionId", p);
                var fromName = f.String(s, "fromName", p);
                var to = f.Int(s, "toChampionId", p);
                var toName = f.String(s, "toName", p);
                var after = f.Int(s, "expectedOwnerAfter", p);
                if (number is null || artifactId is null || slot is null || piece is null || fromName is null || to is null || toName is null || after is null || from.Missing)
                    continue;
                steps.Add(new PlanStep(number.Value, artifactId.Value, slot, piece, from.Value, fromName, to.Value, toName, after.Value));
            }

            var pieces = new List<PlanPiece>();
            foreach (var (s, p) in f.Array(root, "pieces", "pieces"))
            {
                var id = f.Int(s, "artifactId", p);
                var slot = f.String(s, "slot", p);
                var owner = f.OptInt(s, "owner", p);
                var level = f.Int(s, "level", p);
                var rank = f.Int(s, "rank", p);
                var asc = f.Int(s, "ascension", p);
                var expected = f.OptInt(s, "expectedOwner", p);
                if (id is null || slot is null || level is null || rank is null || asc is null || owner.Missing || expected.Missing) continue;
                pieces.Add(new PlanPiece(id.Value, slot, owner.Value, level.Value, rank.Value, asc.Value, expected.Value));
            }

            if (root.TryGetProperty("ticked", out var ticked) && ticked.ValueKind == JsonValueKind.Array && ticked.GetArrayLength() > 0)
                issues.Add(PlanIssue.Warning(IssueCode.TicksIgnored,
                    $"The plan has {ticked.GetArrayLength()} ticked step(s). Ticks are the planner's checklist; what is done is read from the account, not from ticks."));

            string? locksName = root.TryGetProperty("locks", out var locks) && locks.ValueKind == JsonValueKind.Object
                && locks.TryGetProperty("name", out var ln) && ln.ValueKind == JsonValueKind.String ? ln.GetString() : null;

            if (issues.HasErrors() || planId is null || name is null || created is null || gameVersion is null || exportedAt is null)
                return new(null, issues);
            return new(new LoadedPlan(path, sha, new Plan(version, planId, name, created, gameVersion, exportedAt, snapshotId, steps, pieces, locksName)), issues);
        }
    }

    /// <summary>Typed field access that records one issue per bad field and returns null for it.</summary>
    private sealed class Fields(List<PlanIssue> issues)
    {
        public readonly record struct Opt(int? Value, bool Missing);

        private bool Get(JsonElement obj, string name, string path, out JsonElement value)
        {
            if (obj.TryGetProperty(name, out value)) return true;
            issues.Add(PlanIssue.Error(IssueCode.MissingField, $"{Path(path, name)} is missing."));
            return false;
        }

        private static string Path(string path, string name) => path == name ? name : $"{path}.{name}";

        public string? String(JsonElement obj, string name, string path)
        {
            if (!Get(obj, name, path, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            issues.Add(PlanIssue.Error(IssueCode.WrongType, $"{Path(path, name)} must be a string."));
            return null;
        }

        public string? OptString(JsonElement obj, string name, string path)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            issues.Add(PlanIssue.Error(IssueCode.WrongType, $"{Path(path, name)} must be a string or null."));
            return null;
        }

        public int? Int(JsonElement obj, string name, string path)
        {
            if (!Get(obj, name, path, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            issues.Add(PlanIssue.Error(IssueCode.WrongType, $"{Path(path, name)} must be an integer."));
            return null;
        }

        /// <summary>A field that must be present but may be null (e.g. fromChampionId: null = the inventory).</summary>
        public Opt OptInt(JsonElement obj, string name, string path)
        {
            if (!Get(obj, name, path, out var v)) return new Opt(null, true);
            if (v.ValueKind == JsonValueKind.Null) return new Opt(null, false);
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return new Opt(i, false);
            issues.Add(PlanIssue.Error(IssueCode.WrongType, $"{Path(path, name)} must be an integer or null."));
            return new Opt(null, true);
        }

        public IEnumerable<(JsonElement Item, string Path)> Array(JsonElement obj, string name, string path)
        {
            if (!Get(obj, name, path, out var v)) yield break;
            if (v.ValueKind != JsonValueKind.Array)
            {
                issues.Add(PlanIssue.Error(IssueCode.WrongType, $"{path} must be an array."));
                yield break;
            }
            var i = 0;
            foreach (var item in v.EnumerateArray())
            {
                var p = $"{path}[{i++}]";
                if (item.ValueKind != JsonValueKind.Object) issues.Add(PlanIssue.Error(IssueCode.WrongType, $"{p} must be an object."));
                else yield return (item, p);
            }
        }
    }
}
