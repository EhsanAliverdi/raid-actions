using RaidActions.Snapshots;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Tests;

public class SnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static GearSnapshot Take() =>
        GearSnapshot.Take(TestData.Load(TestData.PlanJson()), TestData.Export(), new DateTimeOffset(2026, 10, 23, 13, 5, 0, TimeSpan.FromHours(11)));

    [Fact]
    public void Covers_every_champion_and_piece_the_plan_touches()
    {
        var s = Take();
        Assert.Equal([1, 2, 3], s.Champions.Select(c => c.Id));
        Assert.Equal(new Dictionary<string, int> { ["Gloves"] = 201, ["Weapon"] = 202 }, s.Champions[0].Gear);
        Assert.Equal([100, 200, 201, 202, 300, 400], s.Pieces.Select(p => p.ArtifactId));
        Assert.Null(s.Pieces[0].Owner);
        Assert.Equal("Brute", s.Pieces.Single(p => p.ArtifactId == 300).OwnerName);
        Assert.Contains("Aria (#1): Weapon #202, Gloves #201", s.Restore);
        Assert.Contains("Pieces in the inventory: Helmet #100", s.Restore);
    }

    [Fact]
    public void Saves_and_loads_and_the_account_copy_opens_in_raid_extractor()
    {
        var s = Take();
        var path = s.Save(_dir, TestData.Export());
        Assert.EndsWith("20261023-130500-0123456789abcdef0123456789abcdef.json", path);
        Assert.Equivalent(s, GearSnapshot.Load(path));
        var account = History.Load(path.Replace(".json", ".account.json.gz"));
        Assert.Equal(TestData.FreshExportedAt, account.Meta.ExportedAt);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Plan_snapshot_locator_only_opens_history_files()
    {
        Assert.Null(PlanSnapshotLocator.Load(_dir, null).Export);
        Assert.Contains("not a history file", PlanSnapshotLocator.Load(_dir, "..\\..\\secret.json.gz").Problem);
        Assert.Contains("does not exist", PlanSnapshotLocator.Load(_dir, "20261023-120000.json.gz").Problem);
        var saved = History.Save(_dir, TestData.MadeFrom());
        var (export, problem) = PlanSnapshotLocator.Load(_dir, Path.GetFileName(saved));
        Assert.Null(problem);
        Assert.Equal(TestData.PlanExportedAt, export!.Meta.ExportedAt);
    }
}
