using RaidActions.Execution;

namespace RaidActions.App;

/// <summary>
/// Where things are. raid-extractor's output: --extractor-output, else the RAID_EXTRACTOR_OUTPUT environment variable, else
/// raid-extractor/output next to this repo. This app's output (logs, snapshots, screenshots; gitignored): --output, else
/// output/ in the repo (or next to the exe when it runs outside the repo).
/// </summary>
public static class AppPaths
{
    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "RaidActions.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    /// <summary>raid-extractor's folder: RaidExtractorRoot environment variable, else next to this repo.</summary>
    public static string RaidExtractorRoot() =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("RaidExtractorRoot") ?? Path.Combine(Root(), "..", "raid-extractor"));

    /// <summary>This repo's config folder (ui-offsets.json, ui-layout.json).</summary>
    public static string Config() => Path.Combine(Root(), "config");

    public static SessionPaths Resolve(string? extractorOutput, string? output)
    {
        var root = Root();
        var ext = extractorOutput ?? Environment.GetEnvironmentVariable("RAID_EXTRACTOR_OUTPUT")
            ?? Path.GetFullPath(Path.Combine(root, "..", "raid-extractor", "output"));
        return new SessionPaths(Path.GetFullPath(ext), Path.GetFullPath(output ?? Path.Combine(root, "output")));
    }

    /// <summary>
    /// For a plan file: when no folder is given and the plan sits in raid-extractor's layout (&lt;output&gt;\plans\x.json), that
    /// output folder, so its history snapshots and account.json are the ones the plan was made with.
    /// </summary>
    public static SessionPaths ForPlan(string planPath, string? extractorOutput, string? output)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(planPath));
        if (extractorOutput is null && dir is not null && string.Equals(Path.GetFileName(dir), "plans", StringComparison.OrdinalIgnoreCase))
            extractorOutput = Path.GetDirectoryName(dir);
        return Resolve(extractorOutput, output);
    }
}
