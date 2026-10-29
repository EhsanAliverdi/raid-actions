using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Cursed City asks of the game (the live game: a fresh account read, Manage team's gear commands, the Cursed City screens).</summary>
public interface ICursedCityGame
{
    /// <summary>The plan from a fresh account read (<see cref="CursedCityPlanner"/>): keys, levels, the next level and its team.</summary>
    Task<CursedCityPlan> PlanAsync(int lossesToSkip, IReadOnlySet<int> skip, CancellationToken ct);

    /// <summary>Gears these unused champions from the inventory (Prepare) for the level; vault members are taken out first.</summary>
    Task<string> GearAsync(CcLevel level, IReadOnlyList<int> ids, bool dryRun, CancellationToken ct);

    /// <summary>Takes all gear off these champions (they stay in the champion list).</summary>
    Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct);

    /// <summary>One battle of the level with its team (the team screen set to exactly those champions); a dry run shows Start.</summary>
    Task<BattleOutcome> FightAsync(CcLevel level, bool dryRun, CancellationToken ct);
}

/// <summary>
/// Cursed City (Auto Play, plan/cursed-city.md, Ehsan 2026-10-29): each turn, a fresh plan (keys, levels, the next level and the team
/// built for its criterion); the gear of champions this bot geared that the next level does not use is removed; the next team's unused
/// members (no gear, not locked or frozen) are geared from the inventory; one battle. Keys used up: the remaining gear it added comes
/// off. A level lost <see cref="CursedCityPlaySettings.LossesToSkip"/> times in the rotation is skipped by the planner.
/// </summary>
public sealed class CursedCityActivity(ICursedCityGame game) : IActivity
{
    public const string AreaId = "cursed-city";
    public string Id => AreaId;
    public string Name => "Cursed City";

    private static readonly TimeSpan PlanStale = TimeSpan.FromMinutes(10);
    private CursedCityPlan? _plan;
    private DateTime _planAt;
    private readonly HashSet<int> _geared = [];
    private readonly HashSet<int> _dryShown = [];
    private int _battles, _wins, _losses, _opened;

    public bool IsEnabled(AutoplaySettings settings) => settings.CursedCity.Enabled;

    /// <summary>The champions this bot geared and has not stripped yet (from today's events, so a restart still removes them).</summary>
    public void Restore(IEnumerable<AutoplayEvent> events)
    {
        foreach (var e in events.Where(e => e.Area == AreaId))
        {
            var ids = System.Text.RegularExpressions.Regex.Matches(e.Text, @"#(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            if (e.Kind == "gear") foreach (var id in ids) _geared.Add(id);
            if (e.Kind == "remove") foreach (var id in ids) _geared.Remove(id);
        }
    }

    public Decision Decide(PlayContext ctx)
    {
        var s = ctx.Settings.CursedCity;
        if (_plan is null || ctx.Utc - _planAt > PlanStale) return Decision.Now("reading Cursed City");
        if (Next(s, ctx.DryRun) is { } why) return Decision.Now(why);
        return Decision.Wait(ctx.Utc + TimeSpan.FromHours(1), _plan.Next is null ? _plan.Why : $"{Math.Floor(_plan.Keys):0} key(s): waiting for keys");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings.CursedCity;
        _plan = await game.PlanAsync(Math.Max(1, s.LossesToSkip), _dryShown, ct);
        _planAt = ctx.Utc;
        var level = _plan.Next;
        var playable = level is not null && Spendable(s) >= Cost(level) && level.Team.Feasible && !_dryShown.Contains(level.Stage.Id);

        // Gear this bot added that the next level does not use (or all of it when no level is played now): off again.
        var keep = playable ? level!.Team.Members.Select(m => m.Id).ToHashSet() : [];
        var strip = _geared.Where(id => !keep.Contains(id)).ToList();
        if (s.RemoveGearAfter && strip.Count > 0 && !ctx.DryRun)
        {
            var done = await game.RemoveGearAsync(strip, ctx.DryRun, ct);
            foreach (var id in strip) _geared.Remove(id);
            ctx.Event(AreaId, "remove", $"gear off {string.Join(", ", strip.Select(i => $"#{i}"))} (not in the next level's team). {done}");
            return "gear removed";
        }
        if (!playable) return "";

        var gearNow = s.GearUnused ? level!.Team.Members.Where(m => m.GetsGear && !_geared.Contains(m.Id)).Select(m => m.Id).ToList() : [];
        if (gearNow.Count > 0)
        {
            await ctx.ThinkAsync(ct);
            var done = await game.GearAsync(level!, gearNow, ctx.DryRun, ct);
            if (!ctx.DryRun) foreach (var id in gearNow) _geared.Add(id);
            ctx.Event(AreaId, "gear", $"{CursedCityPlanner.Where(level!.Stage)}: {string.Join(", ", level.Team.Members.Where(m => gearNow.Contains(m.Id)).Select(m => $"{m.Name} #{m.Id}"))}. {done}");
            if (!ctx.DryRun) return "team geared";
        }

        await ctx.ThinkAsync(ct);
        var outcome = await game.FightAsync(level!, ctx.DryRun, ct);
        var where = CursedCityPlanner.Where(level!.Stage);
        if (outcome.Result == BattleResult.Retry)
        {
            // The game locks some champions in the city: plan again without them (their stand-ins get gear first).
            ctx.Event(AreaId, "team", $"{where}: {outcome.Text}");
            _planAt = DateTime.MinValue;
            return "";
        }
        if (outcome.Result == BattleResult.NotStarted)
        {
            _dryShown.Add(level.Stage.Id);
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{where}: {outcome.Text}");
            return "";
        }
        _battles++;
        if (outcome.Result == BattleResult.Victory)
        {
            _wins++;
            if (!level.Stage.Beaten) _opened++;
        }
        else _losses++;
        ctx.Event(AreaId, "battle", $"{outcome.Result} {where} ({level.Criterion}; team {string.Join(", ", level.Team.Members.Select(m => m.Name))}). {outcome.Text}");
        _plan = _plan with { Next = null }; // read again next turn: results and keys changed
        _planAt = DateTime.MinValue;
        return $"{outcome.Result}: {where}";
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.CursedCity;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles, ["Wins"] = _wins, ["Losses"] = _losses, ["Levels opened"] = _opened, ["Geared now"] = _geared.Count };
        if (_plan is { } p) counters["Keys"] = (int)Math.Floor(p.Keys);
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next try"] = u;
        var detail = decision.Why + (_plan?.Next is { } n ? $"; next: {CursedCityPlanner.Where(n.Stage)} ({n.Criterion})" : "");
        return new AreaStatus(Id, Name, s.Enabled, state, detail, decision.UntilUtc, counters, timers);
    }

    /// <summary>Why to act now, or null: a level to play with keys for it, or gear to take back off.</summary>
    private string? Next(CursedCityPlaySettings s, bool dryRun)
    {
        if (_plan is null) return null;
        var level = _plan.Next;
        var playable = level is not null && Spendable(s) >= Cost(level) && level.Team.Feasible && !_dryShown.Contains(level.Stage.Id);
        if (playable) return $"{CursedCityPlanner.Where(level!.Stage)}: {level.Criterion}";
        if (s.RemoveGearAfter && !dryRun && _geared.Count > 0) return "keys used: taking the added gear back off";
        return null;
    }

    private double Spendable(CursedCityPlaySettings s) => Math.Floor(_plan?.Keys ?? 0) - Math.Max(0, s.KeepKeys);

    /// <summary>Keys a battle of the level costs (start and finish price; 1 when the level names none).</summary>
    public static double Cost(CcLevel l) =>
        Math.Max(1, l.Stage.StartPrice.GetValueOrDefault("CursedCity_Key") + l.Stage.FinishPrice.GetValueOrDefault("CursedCity_Key"));
}
