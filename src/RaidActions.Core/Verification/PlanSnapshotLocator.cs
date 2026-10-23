using RaidExtractor;

namespace RaidActions.Verification;

/// <summary>Finds the export a plan was made from: raid-extractor saves it to output/history/&lt;snapshotId&gt; before writing the plan.</summary>
public static class PlanSnapshotLocator
{
    /// <summary>The snapshot export, or null (with the reason) when the plan names none, the name is not a plain history file, or it is missing.</summary>
    public static (SavedExport? Export, string? Problem) Load(string raidExtractorOutput, string? snapshotId)
    {
        if (string.IsNullOrEmpty(snapshotId)) return (null, "The plan names no snapshot.");
        if (snapshotId != Path.GetFileName(snapshotId) || !snapshotId.EndsWith(".json.gz", StringComparison.Ordinal))
            return (null, $"snapshotId \"{snapshotId}\" is not a history file name.");
        var path = Path.Combine(History.Folder(raidExtractorOutput), snapshotId);
        if (!File.Exists(path)) return (null, $"{path} does not exist.");
        try { return (History.Load(path), null); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return (null, $"{path}: {e.Message}"); }
    }
}
