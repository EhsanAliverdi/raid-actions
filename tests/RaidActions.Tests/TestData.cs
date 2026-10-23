using System.Text.Json.Nodes;
using RaidActions.Plans;
using RaidExtractor;

namespace RaidActions.Tests;

/// <summary>
/// A small fake account and a plan for it (no game, no real account data).
/// Champions: Aria #1 (recipient), Brute #2 (donor), Cora #3 (recipient).
/// Plan: 1. Helmet #100 inventory → Aria. 2. Gloves #200 Brute → Aria (Aria's Gloves #201 go to the inventory). 3. Boots #300 Brute → Cora.
/// </summary>
public static class TestData
{
    public const string Version = "11.85.5";
    public const string PlanExportedAt = "2026-10-23T12:00:00+11:00";
    public const string FreshExportedAt = "2026-10-23T13:00:00+11:00";

    public static StatOut Stat(string stat, double value, bool absolute = false, double glyph = 0, int rolls = 0) =>
        new(stat, absolute, value, glyph, rolls, $"{stat} {value}");

    public static ArtifactOut Art(int id, string slot, int? owner, int level = 12, int rank = 5, int asc = 0, string? faction = null, double spd = 5) =>
        new(id, slot, "Speed", 4, "Speed", "Epic", rank, level, faction, Stat("hp", 0.33), [Stat("spd", spd, absolute: true, rolls: 1), Stat("acc", 10, true)],
            asc, null, owner);

    public static ChampionOut Champ(int id, string name, string faction = "BannerLords", bool inVault = false) =>
        new(id, id * 10 + 6, id * 10, name, "Epic", faction, "Force", "Attack", 60, 0, 6, 0, 0, 0, null, false, inVault, false, null,
            [], [], [], [], [], id);

    /// <summary>The account as the plan expects it before any step.</summary>
    public static List<ArtifactOut> Artifacts() =>
    [
        Art(100, "Helmet", null),
        Art(200, "Gloves", 2),
        Art(201, "Gloves", 1),
        Art(202, "Weapon", 1),
        Art(300, "Boots", 2),
        Art(400, "Ring", 3, faction: "BannerLords"),
        Art(999, "Chest", null), // unrelated
    ];

    public static List<ChampionOut> Champions() => [Champ(1, "Aria"), Champ(2, "Brute"), Champ(3, "Cora")];

    /// <summary>An export; each champion's gear follows from the artifacts' owners, as in a real read.</summary>
    public static SavedExport Export(IEnumerable<ArtifactOut>? artifacts = null, IEnumerable<ChampionOut>? champions = null,
        string version = Version, string exportedAt = FreshExportedAt, List<string>? skippedArtifacts = null)
    {
        var arts = (artifacts ?? Artifacts()).ToList();
        var champs = (champions ?? Champions()).Select(c => c with
        {
            Artifacts = arts.Where(a => a.OwnerHeroId == c.Id).ToDictionary(a => a.Slot, a => a.Id),
        }).ToList();
        return new SavedExport(new ExportMeta(exportedAt, version, new ExportCounts(champs.Count, arts.Count, 0), "Migrated"),
            new AccountOut("Tester", 50, 0, 0, []), null, null, null, champs, arts, [], new SkippedOut([], skippedArtifacts ?? [], []));
    }

    /// <summary>The export the plan was made from (its snapshot).</summary>
    public static SavedExport MadeFrom() => Export(exportedAt: PlanExportedAt);

    /// <summary>Moves pieces in an artifact list the way a step does (for "after step n" accounts).</summary>
    public static List<ArtifactOut> After(params int[] steps)
    {
        var arts = Artifacts();
        void Move(int id, int to, string slot)
        {
            for (var i = 0; i < arts.Count; i++)
            {
                if (arts[i].OwnerHeroId == to && arts[i].Slot == slot) arts[i] = arts[i] with { OwnerHeroId = null };
                if (arts[i].Id == id) arts[i] = arts[i] with { OwnerHeroId = to };
            }
        }
        foreach (var s in steps)
            switch (s)
            {
                case 1: Move(100, 1, "Helmet"); break;
                case 2: Move(200, 1, "Gloves"); break;
                case 3: Move(300, 3, "Boots"); break;
            }
        return arts;
    }

    /// <summary>The plan file as raid-extractor writes it (schema version 1), with the informational fields it also writes.</summary>
    public static JsonObject PlanJson() => new()
    {
        ["schemaVersion"] = 1,
        ["planId"] = "0123456789abcdef0123456789abcdef",
        ["name"] = "Test plan",
        ["created"] = "2026-10-23T12:05:00+11:00",
        ["gameVersion"] = Version,
        ["exportedAt"] = PlanExportedAt,
        ["snapshotId"] = "20261023-120000.json.gz",
        ["difficulty"] = "Nightmare",
        ["affinity"] = "Force",
        ["damageNow"] = 1.0,
        ["damageAfter"] = 2.0,
        ["turnsNow"] = 28,
        ["turnsAfter"] = 35,
        ["steps"] = new JsonArray(
            Step(1, 100, "Helmet", null, "the inventory", 1, "Aria"),
            Step(2, 200, "Gloves", 2, "Brute", 1, "Aria"),
            Step(3, 300, "Boots", 2, "Brute", 3, "Cora")),
        ["pieces"] = new JsonArray(
            Piece(100, "Helmet", null, 1),
            Piece(200, "Gloves", 2, 1),
            Piece(201, "Gloves", 1, null),
            Piece(202, "Weapon", 1, 1),
            Piece(300, "Boots", 2, 3),
            Piece(400, "Ring", 3, 3)),
        ["locks"] = new JsonObject { ["name"] = "Arena", ["protectedChampions"] = new JsonArray(), ["frozenChampions"] = new JsonArray(), ["protectedTeams"] = new JsonArray(), ["protectedArtifacts"] = new JsonArray() },
        ["champions"] = new JsonArray(),
        ["origin"] = null,
        ["ticked"] = new JsonArray(),
    };

    public static JsonObject Step(int n, int artifact, string slot, int? from, string fromName, int to, string toName) => new()
    {
        ["number"] = n, ["artifactId"] = artifact, ["slot"] = slot, ["piece"] = $"{slot}: test piece", ["fromChampionId"] = from,
        ["fromName"] = fromName, ["toChampionId"] = to, ["toName"] = toName, ["expectedOwnerAfter"] = to,
    };

    public static JsonObject Piece(int id, string slot, int? owner, int? expected, int level = 12, int rank = 5, int asc = 0) => new()
    {
        ["artifactId"] = id, ["slot"] = slot, ["owner"] = owner, ["level"] = level, ["rank"] = rank, ["ascension"] = asc, ["expectedOwner"] = expected,
    };

    /// <summary>Adds raid-extractor's fingerprint of each piece as the plan was made (as plans saved from 2026-10-23 on carry).</summary>
    public static void AddFingerprints(JsonObject plan)
    {
        var arts = Artifacts().ToDictionary(a => a.Id);
        foreach (var p in plan.Pieces()) p!["fingerprint"] = RaidExtractor.Battle.PlanPiece.FingerprintOf(arts[(int)p["artifactId"]!]);
    }

    public static LoadedPlan Load(JsonObject json)
    {
        var r = PlanReader.Read(json.ToJsonString());
        Assert.True(r.Ok, string.Join("\n", r.Issues));
        return r.Plan!;
    }

    public static Plan Plan(Action<JsonObject>? change = null)
    {
        var j = PlanJson();
        change?.Invoke(j);
        return Load(j).Plan;
    }

    public static JsonArray Steps(this JsonObject plan) => plan["steps"]!.AsArray();
    public static JsonArray Pieces(this JsonObject plan) => plan["pieces"]!.AsArray();
}
