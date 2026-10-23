using System.Text.Json;
using System.Text.Json.Nodes;
using RaidExtractor;

namespace RaidActions.Execution.Fake;

/// <summary>
/// A made-up account and plan for trying the app with the fake executor: written in raid-extractor's layout (account.json,
/// history/&lt;snapshot&gt;, plans/). Invented champions and ids, game version <see cref="GameVersion"/> and a plan id starting with
/// <see cref="PlanIdPrefix"/>, so it can never match a real account; <see cref="PlanSession"/> also refuses it for a driver that sends input.
/// </summary>
public static class SampleData
{
    public const string GameVersion = "0.0.0-sample";
    public const string PlanIdPrefix = "sample-";
    public const string PlanId = "sample-00000000000000000000000001";
    private const string MadeAt = "2026-01-01T12:00:00+00:00";
    private const string SnapshotId = "20260101-120000.json.gz";

    private static StatOut Stat(string stat, double value, bool absolute) => new(stat, absolute, value, 0, 0, $"{stat} {value}");

    private static ArtifactOut Art(int id, string slot, int? owner, string set = "Speed") =>
        new(id, slot, set, 4, set, "Epic", 5, 12, null, Stat("hp", 0.33, false), [Stat("spd", 9, true), Stat("acc", 20, true)], 0, null, owner);

    private static ChampionOut Champ(int id, string name) =>
        new(id, id * 10 + 6, id * 10, name, "Epic", "BannerLords", "Force", "Attack", 60, 0, 6, 0, 0, 0, null, false, false, false, null,
            [], [], [], [], [], id);

    public static SavedExport Account()
    {
        List<ArtifactOut> arts =
        [
            Art(9001, "Helmet", null), Art(9002, "Gloves", 902), Art(9003, "Gloves", 901), Art(9004, "Weapon", 901),
            Art(9005, "Boots", 902), Art(9006, "Chest", 903), Art(9007, "Chest", null), Art(9008, "Shield", 902),
        ];
        var champs = new[] { Champ(901, "Sample Knight"), Champ(902, "Sample Donor"), Champ(903, "Sample Archer") }
            .Select(c => c with { Artifacts = arts.Where(a => a.OwnerHeroId == c.Id).ToDictionary(a => a.Slot, a => a.Id) }).ToList();
        return new SavedExport(new ExportMeta(MadeAt, GameVersion, new ExportCounts(champs.Count, arts.Count, 0), "Migrated"),
            new AccountOut("Sample", 1, 0, 0, []), null, null, null, champs, arts, [], new SkippedOut([], [], []));
    }

    private static JsonObject Step(int n, int artifact, string slot, int? from, string fromName, int to, string toName) => new()
    {
        ["number"] = n, ["artifactId"] = artifact, ["slot"] = slot, ["piece"] = $"{slot}: Speed 5★ +12 Epic (sample)", ["fromChampionId"] = from,
        ["fromName"] = fromName, ["toChampionId"] = to, ["toName"] = toName, ["expectedOwnerAfter"] = to,
    };

    private static JsonObject Piece(ArtifactOut a, int? expected) => new()
    {
        ["artifactId"] = a.Id, ["slot"] = a.Slot, ["owner"] = a.OwnerHeroId, ["level"] = a.Level, ["rank"] = a.Rank, ["ascension"] = a.AscendLevel, ["expectedOwner"] = expected,
    };

    public static JsonObject PlanJson()
    {
        var byId = Account().Artifacts.ToDictionary(a => a.Id);
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["planId"] = PlanId, ["name"] = "Sample plan (fake account)", ["created"] = "2026-01-01T12:05:00+00:00",
            ["gameVersion"] = GameVersion, ["exportedAt"] = MadeAt, ["snapshotId"] = SnapshotId,
            ["steps"] = new JsonArray(
                Step(1, 9001, "Helmet", null, "the inventory", 901, "Sample Knight"),
                Step(2, 9007, "Chest", null, "the inventory", 903, "Sample Archer"),
                Step(3, 9002, "Gloves", 902, "Sample Donor", 901, "Sample Knight"),
                Step(4, 9005, "Boots", 902, "Sample Donor", 903, "Sample Archer")),
            ["pieces"] = new JsonArray(
                Piece(byId[9001], 901), Piece(byId[9002], 901), Piece(byId[9003], null), Piece(byId[9004], 901), Piece(byId[9005], 903),
                Piece(byId[9006], null), Piece(byId[9007], 903), Piece(byId[9008], 902)),
            ["ticked"] = new JsonArray(),
        };
    }

    /// <summary>Writes &lt;folder&gt;/account.json, history/&lt;snapshot&gt; and plans/sample-plan.json. Returns the plan path.</summary>
    public static string Write(string extractorOutput)
    {
        var account = Account();
        Directory.CreateDirectory(Path.Combine(extractorOutput, "plans"));
        File.WriteAllText(Path.Combine(extractorOutput, "account.json"), JsonSerializer.Serialize(account, Exporter.Json));
        var saved = History.Save(extractorOutput, account);
        if (Path.GetFileName(saved) != SnapshotId) throw new InvalidOperationException($"Sample snapshot saved as {saved}, expected {SnapshotId}.");
        var plan = Path.Combine(extractorOutput, "plans", "sample-plan.json");
        File.WriteAllText(plan, PlanJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return plan;
    }
}
