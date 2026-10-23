namespace RaidActions.Plans;

/// <summary>One step: move <see cref="ArtifactId"/> from <see cref="FromChampionId"/> (null = inventory) onto <see cref="ToChampionId"/>.</summary>
/// <param name="Piece">Display text only (the planner's one-line description); never used to find or check the piece.</param>
public sealed record PlanStep(int Number, int ArtifactId, string Slot, string Piece, int? FromChampionId, string FromName,
    int ToChampionId, string ToName, int ExpectedOwnerAfter);

/// <summary>A piece the plan moves or relies on, as it was when the plan was made, and its owner once the plan is done (null = inventory).</summary>
public sealed record PlanPiece(int ArtifactId, string Slot, int? Owner, int Level, int Rank, int Ascension, int? ExpectedOwner);

/// <summary>
/// A plan file as raid-extractor writes it (schema version 1, output/plans/*.json). Only what the actioner needs is kept; the
/// planner's predictions, teams, locks and origin are informational and read only for display (<see cref="LocksName"/>).
/// </summary>
public sealed record Plan(int SchemaVersion, string PlanId, string Name, string Created, string GameVersion, string ExportedAt,
    string? SnapshotId, IReadOnlyList<PlanStep> Steps, IReadOnlyList<PlanPiece> Pieces, string? LocksName)
{
    /// <summary>The newest plan schema this app executes.</summary>
    public const int SupportedVersion = 1;

    public static readonly string[] Slots = ["Weapon", "Helmet", "Shield", "Gloves", "Chest", "Boots", "Ring", "Cloak", "Banner"];
    public static readonly string[] Accessories = ["Ring", "Cloak", "Banner"];
}

/// <summary>A plan as loaded: where from, the SHA-256 of the file's bytes (for logs), and the parsed plan.</summary>
public sealed record LoadedPlan(string? Path, string Sha256, Plan Plan);
