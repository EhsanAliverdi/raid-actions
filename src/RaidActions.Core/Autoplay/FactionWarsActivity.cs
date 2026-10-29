using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Faction Wars asks of the game (the live game: a fresh account read, Manage team's gear commands, the crypt screens).</summary>
public interface IFactionWarsGame
{
    /// <summary>Today's plan from a fresh account read (<see cref="FactionWarsPlanner"/>): open crypts with keys, teams, who to strip.</summary>
    Task<FactionWarsDay> PlanDayAsync(CancellationToken ct);

    /// <summary>Gears the crypt's team (Prepare, unequipped gear; vault members taken out first). Returns what was done.</summary>
    Task<string> GearTeamAsync(CryptPlan crypt, bool dryRun, CancellationToken ct);

    /// <summary>Takes all gear off these champions and leaves them in the Reserve Vault (remove-gear --then reserve).</summary>
    Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct);

    /// <summary>One battle of <paramref name="stage"/> in the crypt with the crypt's team (checked on the team screen); a dry run shows Start.</summary>
    Task<BattleOutcome> FightCryptAsync(CryptPlan crypt, int stage, bool dryRun, CancellationToken ct);
}

/// <summary>
/// Faction Wars (Auto Play, Ehsan 2026-10-29). Each UTC day, in this order: yesterday's crypt teams stripped (their unlocked champions
/// that still wear gear go to the Reserve Vault: the gear is free for today); each open crypt with keys: its team geared, then one battle
/// per turn at the next unbeaten stage (after a defeat there, the highest beaten stage for the rest of the day); keys used up: the team's
/// unlocked champions to the Reserve Vault without gear. Locked champions and the Clan Boss team are never touched (the planner and the
/// gear commands skip them).
/// </summary>
public sealed class FactionWarsActivity(IFactionWarsGame game) : IActivity
{
    public const string AreaId = "faction-wars";
    public string Id => AreaId;
    public string Name => "Faction Wars";

    private static readonly TimeSpan PlanStale = TimeSpan.FromMinutes(15);

    private FactionWarsDay? _day;
    private DateTime _dayAt;
    private DateOnly _date;
    private bool _stripped;
    private readonly HashSet<string> _geared = [], _removed = [], _dryShown = [];
    /// <summary>After a defeat: the stage to play for the rest of the day (one below the lost one; lower again after each defeat).</summary>
    private readonly Dictionary<string, int> _farmAt = [];
    private readonly Dictionary<string, int> _defeatsInARow = [];
    private int _battles, _wins, _losses;

    public bool IsEnabled(AutoplaySettings settings) => settings.FactionWars.Enabled;

    /// <summary>
    /// Today's state from the events already logged today (a restarted bot keeps the day: who is geared, stripped, the stage after a
    /// defeat, the counters). Crypts are keyed by their shown name, as in the events.
    /// </summary>
    public void Restore(IEnumerable<AutoplayEvent> today, DateOnly utcDay)
    {
        _date = utcDay;
        foreach (var e in today.Where(e => e.Area == AreaId && DateOnly.FromDateTime(e.Utc) == utcDay))
        {
            var name = e.Text.Split(':')[0];
            switch (e.Kind)
            {
                case "strip": _stripped = true; break;
                case "gear": _geared.Add(name); break;
                case "remove": _removed.Add(name); break;
                case "battle" when System.Text.RegularExpressions.Regex.Match(e.Text, @"^(\w+) vs (.+) stage (\d+)") is { Success: true } m:
                {
                    var crypt = m.Groups[2].Value;
                    var stage = int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    _battles++;
                    if (m.Groups[1].Value == "Victory") { _wins++; _defeatsInARow[crypt] = 0; }
                    else
                    {
                        _losses++;
                        _defeatsInARow[crypt] = _defeatsInARow.GetValueOrDefault(crypt) + 1;
                        _farmAt[crypt] = Math.Max(1, stage - 1);
                    }
                    break;
                }
            }
        }
    }

    public Decision Decide(PlayContext ctx)
    {
        NewDay(ctx);
        if (_day is null || ctx.Utc - _dayAt > PlanStale || _day.UtcDay != DateOnly.FromDateTime(ctx.Utc)) return Decision.Now("reading today's crypts");
        return Next(ctx.Settings.FactionWars, ctx.DryRun) is { } step ? Decision.Now(step.Why)
            : Decision.Wait(NextUtcDay(ctx.Utc), _day.Open.Count == 0 ? "no crypt open today" : "today's keys are used: next crypts at the new day (UTC)");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        NewDay(ctx);
        _day = await game.PlanDayAsync(ct);
        _dayAt = ctx.Utc;
        var s = ctx.Settings.FactionWars;
        if (Next(s, ctx.DryRun) is not { } step) return "";
        await ctx.ThinkAsync(ct);
        switch (step.Kind)
        {
            case "strip":
            {
                var ids = _day.Strip.Select(x => x.Id).ToList();
                var done = await game.RemoveGearAsync(ids, ctx.DryRun, ct);
                _stripped = true;
                ctx.Event(AreaId, "strip", $"yesterday's teams: {string.Join(", ", _day.Strip.Select(x => $"{x.Name} ({x.Pieces})"))} → Reserve Vault without gear. {done}");
                return $"stripped {ids.Count} of yesterday's champions";
            }
            case "gear":
            {
                var done = await game.GearTeamAsync(step.Crypt!, ctx.DryRun, ct);
                _geared.Add(step.Crypt!.Name);
                ctx.Event(AreaId, "gear", $"{step.Crypt.Name}: {string.Join(", ", step.Crypt.TeamNames)}. {done}");
                return $"{step.Crypt.Name} team geared";
            }
            case "remove":
            {
                var c = step.Crypt!;
                var done = c.Removable.Count == 0 ? "every member is locked: gear kept" : await game.RemoveGearAsync(c.Removable, ctx.DryRun, ct);
                _removed.Add(c.Name);
                ctx.Event(AreaId, "remove", $"{c.Name}: keys used; the team's gear off, into the Reserve Vault. {done}");
                return $"{c.Name}: gear removed";
            }
            default:
            {
                var c = step.Crypt!;
                var stage = Stage(c, s);
                var outcome = await game.FightCryptAsync(c, stage, ctx.DryRun, ct);
                if (outcome.Result == BattleResult.NotStarted)
                {
                    // A dry run's shown Start, or (real) not enough keys for the stage: the crypt is done for today either way.
                    _dryShown.Add(c.Name);
                    ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "keys", ctx.DryRun ? $"would fight {c.Name} stage {stage}: {outcome.Text}" : $"{c.Name} stage {stage}: {outcome.Text}");
                    return "";
                }
                _battles++;
                if (outcome.Result == BattleResult.Victory) { _wins++; _defeatsInARow[c.Name] = 0; }
                else
                {
                    _losses++;
                    _defeatsInARow[c.Name] = _defeatsInARow.GetValueOrDefault(c.Name) + 1;
                    // A defeat (it costs no key, seen 2026-10-29): one stage lower for the rest of the day (Ehsan: after a defeat at the next
                    // stage, farm the highest beaten one; it can still lose, so each defeat steps down again until one is won).
                    _farmAt[c.Name] = Math.Max(1, Math.Min(stage - 1, Math.Max(1, c.HighestPassed)));
                }
                ctx.Event(AreaId, "battle", $"{outcome.Result} vs {c.Name} stage {stage}. {outcome.Text}");
                return $"{outcome.Result}: {c.Name} stage {stage}";
            }
        }
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.FactionWars;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles, ["Wins"] = _wins, ["Losses"] = _losses };
        foreach (var c in _day?.Open ?? []) counters[$"{c.Name} keys"] = c.Keys;
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next try"] = u;
        timers["New day (UTC)"] = NextUtcDay(ctx.Utc);
        var detail = decision.Why + (_day?.Open.Count > 0 ? "; open: " + string.Join(", ", _day.Open.Select(c => $"{c.Name} {c.Keys} keys, stage {Stage(c, s)}")) : "");
        return new AreaStatus(Id, Name, s.Enabled, state, detail, decision.UntilUtc, counters, timers);
    }

    private sealed record Step(string Kind, CryptPlan? Crypt, string Why);

    /// <summary>What to do next, in the day's order; null when nothing is left today.</summary>
    private Step? Next(FactionWarsSettings s, bool dryRun)
    {
        if (_day is null) return null;
        var playable = _day.Open.Where(c => c.Keys > s.KeepKeys && c.Team.Count > 0 && !_dryShown.Contains(c.Name)
            && _defeatsInARow.GetValueOrDefault(c.Name) < 6).ToList();
        if (s.StripYesterdaysTeams && !_stripped && _day.Strip.Count > 0 && playable.Count > 0)
            return new("strip", null, $"freeing yesterday's gear: {_day.Strip.Count} champion(s) to the Reserve Vault");
        foreach (var c in playable)
        {
            if (s.GearTeam && !_geared.Contains(c.Name)) return new("gear", c, $"{c.Name}: gearing the team");
            return new("fight", c, $"{c.Name}: stage {Stage(c, s)} ({c.Keys} keys)");
        }
        if (s.RemoveGearAfter && !dryRun)
            foreach (var c in _day.Open.Where(c => _geared.Contains(c.Name) && !_removed.Contains(c.Name) && (c.Keys <= s.KeepKeys || _dryShown.Contains(c.Name))))
                return new("remove", c, $"{c.Name}: keys used, removing the team's gear");
        return null;
    }

    private int Stage(CryptPlan c, FactionWarsSettings s) =>
        _farmAt.TryGetValue(c.Name, out var farm) ? farm : s.Stage == CryptStage.FarmHighest ? Math.Max(1, c.HighestPassed) : c.NextStage;

    private void NewDay(PlayContext ctx)
    {
        var today = DateOnly.FromDateTime(ctx.Utc);
        if (today == _date) return;
        _date = today;
        _stripped = false;
        _geared.Clear(); _removed.Clear(); _farmAt.Clear(); _dryShown.Clear(); _defeatsInARow.Clear();
        _battles = _wins = _losses = 0;
        _day = null;
    }

    private static DateTime NextUtcDay(DateTime utc) => utc.Date.AddDays(1).AddMinutes(3);
}
