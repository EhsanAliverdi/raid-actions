using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>The Doom Tower map as the game shows it: its floors, rooms and bosses, and the keys.</summary>
public sealed record DoomTowerMap(IReadOnlyList<DtStage> Stages, double Gold, double Silver);

/// <summary>What Doom Tower asks of the game (the live game: village → Battle → Doom Tower → the map → a floor → team → Start).</summary>
public interface IDoomTowerGame : IGearDropGame
{
    /// <summary>The map opened (or kept open) and read, with the keys from the account.</summary>
    Task<DoomTowerMap> ReadDoomTowerAsync(CancellationToken ct);

    /// <summary>One battle of the stage with the team the game shows there; a dry run shows Start only.</summary>
    Task<BattleOutcome> FightDoomTowerAsync(DtStage stage, bool dryRun, CancellationToken ct);

    /// <summary>One floor of the Normal or Hard tower, the one the game offers (its gold banner); a boss floor takes a gold key for a win.</summary>
    Task<BattleOutcome> ClimbDoomTowerAsync(bool hard, bool dryRun, CancellationToken ct);
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
        _map is null ? (null, "not read") : DoomTowerRules.Pick([.. _map.Stages.Where(s => !_skip.Contains(s.Id) && ctx.Held($"{AreaId}:stage:{s.Id}") is null)], _map.Gold, _map.Silver, ctx.Settings.DoomTower, SilverQuest(ctx));

    private sealed record Saved(DoomTowerMap? Map, DateTime ReadAt, List<int> Skip, DateTime ClimbAfter, DateTime NormalAfter = default);

    // Kept for the next bot on the account (a rotation's restart): the map is not opened again before its hour.
    public string? SaveState() => _map is null ? null : System.Text.Json.JsonSerializer.Serialize(new Saved(_map, _readAt, [.. _skip], _climbAfter, _normalAfter), AutoplayJson.Options);

    public void LoadState(string json, DateTime utc)
    {
        if (System.Text.Json.JsonSerializer.Deserialize<Saved>(json, AutoplayJson.Options) is not { Map: not null } s) return;
        (_map, _readAt, _climbAfter, _normalAfter) = (s.Map, s.ReadAt, s.ClimbAfter, s.NormalAfter);
        _skip.UnionWith(s.Skip);
    }

    public Decision Decide(PlayContext ctx)
    {
        // No key the settings let it use, seen from the village: the tower is not opened at all (Ehsan 2026-11-10: "you went to doom tower while there is
        // no key there"). Floors and bosses take gold keys, secret rooms silver.
        if (NoKeys(ctx) is { } none) return Decision.Wait(ctx.Utc + Recheck, none);
        if (_map is null || ctx.Utc - _readAt >= Recheck) return Decision.Now("reading the Doom Tower");
        var (stage, why) = Pick(ctx);
        if (stage is null && NormalDue(ctx)) return Decision.Now("climbing the Normal tower");
        if (stage is null && ClimbDue(ctx)) return Decision.Now("climbing the Hard tower");
        return stage is not null ? Decision.Now(why) : Decision.Wait(_readAt + Recheck, why);
    }

    /// <summary>Climbing rests after a loss until tomorrow: the next floor is too strong for now, and a loss must not repeat all day.</summary>
    private DateTime _climbAfter = DateTime.MinValue;

    private const string ClimbKey = AreaId + ":climb";

    private bool ClimbDue(PlayContext ctx) => ctx.Settings.DoomTower.Climb && ctx.Utc >= _climbAfter && ctx.Held(ClimbKey) is null && GoldFree(ctx);

    /// <summary>
    /// The Normal tower's free floors (Ehsan 2026-11-10: "on main account yesterday you did not play doomtower normal"; a new tower left BlackLord65 with
    /// nothing beaten, so its 15 silver and 10 gold keys had nothing to go on): climbed whenever Doom Tower is on, before Hard. A floor costs nothing (a boss
    /// floor a gold key, for a win only). No floor to play rests it 12 hours (the tower done, or the banner not seen); a loss a day.
    /// </summary>
    private DateTime _normalAfter = DateTime.MinValue;

    private const string NormalKey = AreaId + ":climb-normal";

    private bool NormalDue(PlayContext ctx) => ctx.Utc >= _normalAfter && ctx.Held(NormalKey) is null && GoldFree(ctx);

    /// <summary>A gold key above the kept ones (a floor takes one; Ehsan 2026-11-10: with none left, Start on a floor does nothing). Unknown counts as free.</summary>
    private bool GoldFree(PlayContext ctx)
    {
        var s = ctx.Settings.DoomTower;
        var gold = ctx.Resource(DoomTowerRules.Gold) ?? _map?.Gold;
        return gold is null || s.UseGold && gold.Value - s.KeepGold >= 1;
    }

    /// <summary>Why the tower has nothing to do with the keys the account has now (from the last resources read); null when a key can be used or none was read.</summary>
    private static string? NoKeys(PlayContext ctx)
    {
        var s = ctx.Settings.DoomTower;
        if (ctx.Resource(DoomTowerRules.Gold) is not { } gold || ctx.Resource(DoomTowerRules.Silver) is not { } silver) return null;
        var goldOk = s.UseGold && gold - s.KeepGold >= 1;
        var silverOk = (s.UseSilver || SilverQuest(ctx)) && silver - (SilverQuest(ctx) ? 0 : s.KeepSilver) >= 1;
        return goldOk || silverOk ? null : $"no key to use (gold {gold:0}, silver {silver:0}): the tower is not opened until one comes";
    }

    private async Task<string> Climb(PlayContext ctx, bool hard, CancellationToken ct)
    {
        var tower = hard ? "Hard" : "Normal";
        var key = hard ? ClimbKey : NormalKey;
        void Rest(DateTime until, string why)
        {
            if (hard) _climbAfter = until; else _normalAfter = until;
            if (!ctx.DryRun) ctx.Hold(key, until, why);
        }
        await ctx.ThinkAsync(ct);
        var outcome = await game.ClimbDoomTowerAsync(hard, ctx.DryRun, ct);
        if (outcome.Result is BattleResult.NotStarted or BattleResult.Retry)
        {
            Rest(ctx.Utc + TimeSpan.FromHours(hard ? 6 : 12), $"{tower} tower: {outcome.Text}");
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{tower} tower: {outcome.Text}");
            return "";
        }
        _battles++;
        if (outcome.Result == BattleResult.Victory) _wins++;
        else
        {
            _losses++;
            // The same team would lose again (Ehsan 2026-11-03: "it tries with exactly the same champions and loses on wave one").
            Rest(ctx.Utc + TimeSpan.FromDays(1), $"the {tower} floor was lost: tried again in a day");
        }
        _last = $"{outcome.Result} {tower} tower floor. {outcome.Text}";
        ctx.Event(AreaId, "battle", _last);
        if (outcome.Result == BattleResult.Victory) await game.GearAfterAsync(ctx, AreaId, AreaId, ct);
        _readAt = DateTime.MinValue;
        return $"{outcome.Result}: {tower} tower floor";
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        await game.GearAfterAsync(ctx, AreaId, null, ct);
        _map = await game.ReadDoomTowerAsync(ct);
        _readAt = ctx.Utc;
        var (stage, why) = Pick(ctx);
        if (stage is null) return NormalDue(ctx) ? await Climb(ctx, false, ct) : ClimbDue(ctx) ? await Climb(ctx, true, ct) : "";
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
        if (outcome.Result == BattleResult.Defeat)
        {
            _skip.Add(stage.Id);
            ctx.Hold($"{AreaId}:stage:{stage.Id}", ClanBossRules.NextReset(ctx.Utc), $"{name} was lost");
        }
        if (DoomTowerRules.KeyOf(stage) == DoomTowerRules.Silver)
            ctx.QuestNeeds = [.. ctx.QuestNeeds.Select(n => n.Kind == QuestNeedKind.DoomTower ? n with { Remaining = n.Remaining - DoomTowerRules.Cost(stage) } : n).Where(n => n.Remaining > 0)];
        _last = $"{outcome.Result} {name}. {outcome.Text}";
        ctx.Event(AreaId, "battle", _last);
        if (outcome.Result == BattleResult.Victory) await game.GearAfterAsync(ctx, AreaId, AreaId, ct);
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
