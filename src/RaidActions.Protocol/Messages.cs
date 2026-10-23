using System.Text.Json.Serialization;

namespace RaidActions.Protocol;

/// <summary>
/// Every message: a <c>type</c>, the protocol version <c>v</c>, an optional <c>id</c> chosen by the sender, and <c>replyTo</c> = the
/// id of the request a reply answers. One message per line (docs/PROTOCOL.md).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(LoadPlan), "loadPlan")]
[JsonDerivedType(typeof(Validate), "validate")]
[JsonDerivedType(typeof(Start), "start")]
[JsonDerivedType(typeof(Pause), "pause")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(Abort), "abort")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(PlanLoaded), "planLoaded")]
[JsonDerivedType(typeof(Validated), "validated")]
[JsonDerivedType(typeof(Started), "started")]
[JsonDerivedType(typeof(Progress), "progress")]
[JsonDerivedType(typeof(Paused), "paused")]
[JsonDerivedType(typeof(Resumed), "resumed")]
[JsonDerivedType(typeof(Result), "result")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record Message
{
    [JsonRequired] public int V { get; init; } = ProtocolInfo.Version;
    public string? Id { get; init; }
    public string? ReplyTo { get; init; }
}

public static class ProtocolInfo
{
    /// <summary>The protocol version. A peer with another version gets an error and the connection is closed.</summary>
    public const int Version = 1;
    /// <summary>The named pipe RaidActions.exe listens on (phase 6), local machine only.</summary>
    public const string PipeName = "raid-actions-v1";
    /// <summary>Longest accepted line, in bytes (a plan summary with many steps fits easily).</summary>
    public const int MaxLineBytes = 1 << 20;
}

public enum RunMode { Dry, Real }

// ---- Planner → actioner ----

/// <summary>First message on a connection.</summary>
public sealed record Hello(string Client, string ClientVersion) : Message;

/// <summary>Load a plan file (an absolute path on this machine). Replied with <see cref="PlanLoaded"/> or <see cref="Error"/>.</summary>
public sealed record LoadPlan(string Path) : Message;

/// <summary>Check the loaded plan against a fresh read of the account. Replied with <see cref="Validated"/>.</summary>
public sealed record Validate : Message;

/// <summary>
/// Start the loaded plan. <see cref="PlanId"/> and <see cref="Sha256"/> must match the loaded plan (so a stale screen cannot start
/// another plan), and the user must have confirmed the steps and donors in the planner. The actioner checks the account again first.
/// </summary>
public sealed record Start(RunMode Mode, string PlanId, string Sha256) : Message;

public sealed record Pause : Message;
public sealed record Resume : Message;
public sealed record Abort(string? Reason = null) : Message;

// ---- Actioner → planner ----

/// <param name="Executor">"fake" (no game input) or "game".</param>
/// <param name="GameInputEnabled">Whether real runs can send input to the game at all (off by default).</param>
public sealed record Welcome(string Server, string ServerVersion, string Executor, bool GameInputEnabled) : Message;

public sealed record StepInfo(int Number, int ArtifactId, string Slot, string Piece, int? FromChampionId, string FromName, int ToChampionId, string ToName);

public sealed record PlanLoaded(string PlanId, string Name, string Sha256, string GameVersion, List<StepInfo> Steps, List<IssueInfo> Issues) : Message;

/// <param name="Severity">"error" or "warning".</param>
/// <param name="Code">The actioner's issue code (e.g. "ArtifactUpgraded"); stable, for programs.</param>
public sealed record IssueInfo(string Severity, string Code, string Message, int? Step);

/// <param name="State">"pending" or "done".</param>
/// <param name="Displaced">The piece the recipient wears in that slot now, which goes to the inventory.</param>
public sealed record StepStatus(int Number, string State, int? Displaced);

public sealed record Validated(bool CanStart, List<IssueInfo> Issues, List<StepStatus> Steps) : Message;

public sealed record Started(RunMode Mode, string RunId, string LogPath) : Message;

/// <param name="Action">The action kind (e.g. "OpenChampion", "Equip", "VerifyStep").</param>
/// <param name="State">"started", "ok" or "failed".</param>
public sealed record Progress(int Step, string Action, string Text, string State, string? Detail, DateTimeOffset Time) : Message;

public sealed record Paused : Message;
public sealed record Resumed : Message;

/// <param name="Outcome">"completed", "aborted", "failed" or "refused".</param>
/// <param name="DoneSteps">The steps the account shows as done at the end (read after the run, without input).</param>
/// <param name="FailedStep">The step that stopped the run, if any.</param>
public sealed record Result(string Outcome, List<int> DoneSteps, int? FailedStep, string Message, string? LogPath, string? SnapshotPath, string? ScreenshotPath) : Message;

/// <param name="Code">"badMessage", "versionMismatch", "noPlan", "planMismatch", "busy", "refused", "internal".</param>
public sealed record Error(string Code, string Message) : Message;
