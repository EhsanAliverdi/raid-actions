using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>The Doom Tower map as the game shows it: its floors, rooms and bosses, and the keys.</summary>
public sealed record DoomTowerMap(IReadOnlyList<DtStage> Stages, double Gold, double Silver);

/// <summary>What Doom Tower asks of the game (the live game: village → Battle → Doom Tower → the map → a floor → team → Start).</summary>
public interface IDoomTowerGame
{
    /// <summary>The map opened (or kept open) and read, with the keys from the account.</summary>
    Task<DoomTowerMap> ReadDoomTowerAsync(CancellationToken ct);

    /// <summary>One battle of the stage with the team the game shows there; a dry run shows Start only.</summary>
    Task<BattleOutcome> FightDoomTowerAsync(DtStage stage, bool dryRun, CancellationToken ct);

    /// <summary>One floor of the Hard tower, the one the game offers; a boss floor takes a gold key for a win.</summary>
    Task<BattleOutcome> ClimbDoomTowerAsync(bool dryRun, CancellationToken ct);
}

/// <summary>
/// Doom Tower (Auto Play, plan/doom-tower.md): silver keys on the secret rooms, gold keys on the highest boss beaten
/// (<see cref="DoomTowerRules.Pick"/>), a free floor when climbing is on; a quest asking for silver keys takes the kept ones too. Keys
/// regenerate slowly, so it checks again every hour; a turn is one battle.
/// </summary>
public sealed class DoomTowerActivity(IDoomTowerGame game) : IActivity
{
    public const string AreaId = "doom-tower";
    public string Id => AreaId;
    public string Name => "Doom Tower";

    private static readonly TimeSpan Recheck = TimeSpan.FromHours(1);
    private DoomTowerMap? _map;
    private DateTime _readAt = DateTime.MinValue;
    private readonly HashSet<int> _skip = [];
    private int _battles, _wins, _losses;
    private string _last = "";

    public bool IsEnabled(AutoplaySettings settings) => settings.DoomTower.Enabled;

    private static bool SilverQuest(PlayContext ctx) => ctx.QuestLeft(QuestNeedKind.DoomTower) > 0;

    private (DtStage? Stage, string Why) Pick(PlayContext ctx) =>
        _map is null ? (null, "not read") : DoomTowerRules.Pick([.. _map.Stages.Where(s => !_skip.Contains(s.Id))], _map.Gold, _map.Silver, ctx.Settings.DoomTower, SilverQuest(ctx));

    public Decision Decide(PlayContext ctx)
    {
        if (_map is null || ctx.Utc - _readAt >= Recheck) return Decision.Now("reading the Doom Tower");
        var (stage, why) = Pick(ctx);
        if (stage is null && ClimbDue(ctx)) return Decision.Now("climbing the Hard tower");
        return stage is not null ? Decision.Now(why) : Decision.Wait(_readAt + Recheck, why);
    }

    /// <summary>Climbing rests after a loss until tomorrow: the next floor is too strong for now, and a loss must not repeat all day.</summary>
    private DateTime _climbAfter = DateTime.MinValue;

    private bool ClimbDue(PlayContext ctx) => ctx.Settings.DoomTower.Climb && ctx.Utc >= _climbAfter;

    private async Task<string> Climb(PlayContext ctx, CancellationToken ct)
    {
        await ctx.ThinkAsync(ct);
        var outcome = await game.ClimbDoomTowerAsync(ctx.DryRun, ct);
        if (outcome.Result is BattleResult.NotStarted or BattleResult.Retry)
        {
            _climbAfter = ctx.Utc + TimeSpan.FromHours(6);
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"Hard tower: {outcome.Text}");
            return "";
        }
        _battles++;
        if (outcome.Result == BattleResult.Victory) _wins++;
        else
        {
            _losses++;
            _climbAfter = ctx.Utc + TimeSpan.FromDays(1);
        }
        _last = $"{outcome.Result} Hard tower floor. {outcome.Text}";
        ctx.Event(AreaId, "battle", _last);
        _readAt = DateTime.MinValue;
        return $"{outcome.Result}: Hard tower floor";
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        _map = await game.ReadDoomTowerAsync(ct);
        _readAt = ctx.Utc;
        var (stage, why) = Pick(ctx);
        if (stage is null) return ClimbDue(ctx) ? await Climb(ctx, ct) : "";
        await ctx.ThinkAsync(ct);
        var outcome = await game.FightDoomTowerAsync(stage, ctx.DryRun, ct);
        var name = DoomTowerRules.Name(stage);
        if (outcome.Result is BattleResult.NotStarted or BattleResult.Retry)
        {
            _skip.Add(stage.Id);
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{name}: {outcome.Text}");
            return "";
        }
        _battles++;
        if (outcome.Result == BattleResult.Victory) _wins++;
        else if (outcome.Result == BattleResult.Defeat) _losses++;
        // A lost room or boss is not tried again today (the planner only picks beaten ones, so a loss means the team is not set).
        if (outcome.Result == BattleResult.Defeat) _skip.Add(stage.Id);
        if (DoomTowerRules.KeyOf(stage) == DoomTowerRules.Silver)
            ctx.QuestNeeds = [.. ctx.QuestNeeds.Select(n => n.Kind == QuestNeedKind.DoomTower ? n with { Remaining = n.Remaining - DoomTowerRules.Cost(stage) } : n).Where(n => n.Remaining > 0)];
        _last = $"{outcome.Result} {name}. {outcome.Text}";
        ctx.Event(AreaId, "battle", _last);
        _readAt = DateTime.MinValue; // keys and the map changed: read again next turn
        return $"{outcome.Result}: {name}";
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.DoomTower;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles, ["Wins"] = _wins, ["Losses"] = _losses };
        if (_map is { } m) { counters["Silver keys"] = (int)Math.Floor(m.Silver); counters["Gold keys"] = (int)Math.Floor(m.Gold); }
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next check"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, _last.Length > 0 ? $"{decision.Why} · last: {_last}" : decision.Why, decision.UntilUtc, counters, timers);
    }
}
