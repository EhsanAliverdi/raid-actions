using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using RaidActions.Plans;
using PlanModel = RaidActions.Plans.Plan;
using RaidExtractor;

namespace RaidActions.Snapshots;

public sealed record SnapshotPlanRef(string PlanId, string Name, string Sha256, string? Path);
public sealed record SnapshotChampion(int Id, string? Name, Dictionary<string, int> Gear);
public sealed record SnapshotPiece(int ArtifactId, string Slot, int? Owner, string? OwnerName, string Description);

/// <summary>
/// The gear of everything a plan touches, taken before the first input, so it can always be put back by hand (docs/SNAPSHOT.md).
/// Saved next to the full account read in raid-extractor's history format (<c>.account.json.gz</c>).
/// </summary>
public sealed record GearSnapshot(string Kind, int SchemaVersion, string TakenAt, string GameVersion, string AccountExportedAt,
    SnapshotPlanRef Plan, List<SnapshotChampion> Champions, List<SnapshotPiece> Pieces, List<string> Restore)
{
    public const string KindName = "raid-actions.gear-snapshot";
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DictionaryKeyPolicy = null,
    };

    /// <summary>Every champion a step names or that wears a plan piece now, with all their gear; every plan piece and its owner now.</summary>
    public static GearSnapshot Take(LoadedPlan loaded, SavedExport fresh, DateTimeOffset now)
    {
        var plan = loaded.Plan;
        var artifacts = fresh.Artifacts.ToDictionary(a => a.Id);
        var champions = fresh.Champions.ToDictionary(c => c.Id);
        var pieceIds = plan.Pieces.Select(p => p.ArtifactId).Union(plan.Steps.Select(s => s.ArtifactId)).Where(artifacts.ContainsKey).Order().ToList();
        var championIds = plan.Steps.SelectMany(s => new[] { s.FromChampionId, s.ToChampionId }).OfType<int>()
            .Concat(pieceIds.Select(i => artifacts[i].OwnerHeroId).OfType<int>()).Distinct().Where(champions.ContainsKey).Order().ToList();

        var team = championIds.Select(i => new SnapshotChampion(i, champions[i].Name, new Dictionary<string, int>(champions[i].Artifacts))).ToList();
        // Pieces: the plan's, plus everything the snapshot champions wear (so a manual restore never meets an unknown piece).
        var allIds = pieceIds.Union(team.SelectMany(c => c.Gear.Values)).Where(artifacts.ContainsKey).Order();
        var pieces = allIds.Select(i => artifacts[i]).Select(a => new SnapshotPiece(a.Id, a.Slot, a.OwnerHeroId,
            a.OwnerHeroId is { } o && champions.TryGetValue(o, out var c) ? c.Name : null, Describe(a))).ToList();

        var restore = team.Select(c => $"{c.Name} (#{c.Id}): " + string.Join(", ",
            PlanModel.Slots.Where(c.Gear.ContainsKey).Select(s => $"{s} #{c.Gear[s]}")) + (c.Gear.Count == 0 ? "no gear" : "")).ToList();
        restore.Add("Pieces in the inventory: " + string.Join(", ", pieces.Where(p => p.Owner is null).Select(p => $"{p.Slot} #{p.ArtifactId}").DefaultIfEmpty("none")));

        return new(KindName, Version, now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), fresh.Meta.GameVersion, fresh.Meta.ExportedAt,
            new SnapshotPlanRef(plan.PlanId, plan.Name, loaded.Sha256, loaded.Path), team, pieces, restore);
    }

    public static string Describe(ArtifactOut a) =>
        $"{a.Slot}: {a.SetName ?? a.Set} {a.Rank}★ +{a.Level} {a.Rarity} | {a.Primary.Display}";

    /// <summary>Writes &lt;stamp&gt;-&lt;planId&gt;.json and the full read as &lt;stamp&gt;-&lt;planId&gt;.account.json.gz. Returns the snapshot path.</summary>
    public string Save(string folder, SavedExport fresh)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTimeOffset.Parse(TakenAt, CultureInfo.InvariantCulture).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = Path.Combine(folder, $"{stamp}-{Plan.PlanId}");
        WriteAtomic(baseName + ".account.json.gz", s =>
        {
            using var gz = new GZipStream(s, CompressionLevel.SmallestSize, leaveOpen: true);
            JsonSerializer.Serialize(gz, fresh, Exporter.Json);
        });
        WriteAtomic(baseName + ".json", s => JsonSerializer.Serialize(s, this, Json));
        return baseName + ".json";
    }

    public static GearSnapshot Load(string path) =>
        JsonSerializer.Deserialize<GearSnapshot>(File.ReadAllText(path), Json) is { Kind: KindName } s ? s : throw new InvalidDataException($"{path} is not a gear snapshot.");

    private static void WriteAtomic(string path, Action<Stream> write)
    {
        var tmp = path + ".tmp";
        using (var f = File.Create(tmp)) write(f);
        File.Move(tmp, path, overwrite: true);
    }
}
