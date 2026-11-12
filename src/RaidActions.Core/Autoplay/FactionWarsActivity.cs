using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Faction Wars asks of the game (the live game: a fresh account read, Manage team's gear commands, the crypt screens).</summary>
public interface IFactionWarsGame : IGearDropGame
{
    /// <summary>Today's plan from a fresh account read (<see cref="FactionWarsPlanner"/>): open crypts with keys, teams, who to strip.</summary>
    Task<FactionWarsDay> PlanDayAsync(CancellationToken ct);

    /// <summary>Gears the crypt's team (Prepare, unequipped gear; vault members taken out first). Returns what was done.</summary>
    Task<string> GearTeamAsync(CryptPlan crypt, bool dryRun, CancellationToken ct);

    /// <summary>Takes all gear off these champions and leaves them in the Reserve Vault (remove-gear --then reserve).</summary>
    Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct);

    /// <summary>One battle of <paramref name="stage"/> in the crypt with the crypt's team (checked on the team screen); a dry run shows Start.</summary>
    Task<BattleOutcome> FightCryptAsync(CryptPlan crypt, int stage, bool dryRun, CancellationToken ct, bool superRaid = false);
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
                // "Demonspawn stage 9: not enough keys ...": the crypt stays done for today after a restart too.
                case "keys": _dryShown.Add(name.Split(" stage ")[0]); break;
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
        await game.GearAfterAsync(ctx, AreaId, null, ct);
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
                // Earlier days' return tasks are done by the strip (it takes every champion of those teams still wearing gear).
                if (!ctx.DryRun && ctx.Host.Tasks is { } book)
                    foreach (var t in book.OpenTasks().Where(t => t.Area == AreaId && t.Kind == "return" && t.DeadlineUtc <= ctx.Utc))
                        book.Set(t.Key, TodoState.Done, $"done by the new day's strip: {done}");
                ctx.Event(AreaId, "strip", $"yesterday's teams: {string.Join(", ", _day.Strip.Select(x => $"{x.Name} ({x.Pieces})"))} → Reserve Vault without gear. {done}");
                return $"stripped {ids.Count} of yesterday's champions";
            }
            case "gear":
            {
                var book = ctx.DryRun ? null : ctx.Host.Tasks;
                var day = DateOnly.FromDateTime(ctx.Utc);
                var gearKey = $"fw-gear:{step.Crypt!.Name}:{day:yyyy-MM-dd}";
                book?.Put(new TaskRow(0, gearKey, "gear", AreaId, $"Gear the {step.Crypt.Name} team: {string.Join(", ", step.Crypt.TeamNames)}", AreaId,
                    TodoState.InProgress, DeadlineUtc: NextUtcDay(ctx.Utc), Checkpoint: $"team {string.Join(",", step.Crypt.Team)}; out of a vault {string.Join(",", step.Crypt.InVault)}"));
                string done;
                try { done = await game.GearTeamAsync(step.Crypt, ctx.DryRun, ct); }
                catch (Exception e) when (book is not null && e is not OperationCanceledException)
                {
                    // Stopped half way: the task stays in progress with why, so the next turn (or the next bot) knows and gears again.
                    book.Set(gearKey, TodoState.InProgress, $"stopped: {e.Message}");
                    throw;
                }
                book?.Set(gearKey, TodoState.Done, done);
                // The follow-up: the champions taken out of a vault (and the gear lent to the team) go back when the keys are used or the day ends.
                if (book is not null && step.Crypt.Removable.Count > 0)
                    book.Put(new TaskRow(0, $"fw-return:{step.Crypt.Name}:{day:yyyy-MM-dd}", "return", AreaId,
                        $"Back to the Reserve Vault without gear: {Names(step.Crypt, step.Crypt.Removable)}", "follow-up", TodoState.Blocked,
                        DeadlineUtc: NextUtcDay(ctx.Utc), Parent: gearKey, Trigger: "the crypt's keys are used, or its day ends (then the next day's strip)",
                        Checkpoint: string.Join(",", step.Crypt.Removable)));
                _geared.Add(step.Crypt!.Name);
                _removed.Remove(step.Crypt!.Name);
                _farmAt.Remove(step.Crypt!.Name); // defeats before (gear) do not count against it
                _defeatsInARow[step.Crypt!.Name] = 0;
                ctx.Event(AreaId, "gear", $"{step.Crypt.Name}: {string.Join(", ", step.Crypt.TeamNames)}. {done}");
                return $"{step.Crypt.Name} team geared";
            }
            case "remove":
            {
                var c = step.Crypt!;
                var done = c.Removable.Count == 0 ? "every member is locked: gear kept" : await game.RemoveGearAsync(c.Removable, ctx.DryRun, ct);
                _removed.Add(c.Name);
                if (!ctx.DryRun) ctx.Host.Tasks?.Set($"fw-return:{c.Name}:{DateOnly.FromDateTime(ctx.Utc):yyyy-MM-dd}", TodoState.Done, $"keys used: {done}");
                ctx.Event(AreaId, "remove", $"{c.Name}: keys used; the team's gear off, into the Reserve Vault. {done}");
                return $"{c.Name}: gear removed";
            }
            default:
            {
                var c = step.Crypt!;
                var (stage, super) = Run(c, s) ?? (Stage(c, s), false);
                // A quest asks for a Faction Wars boss (Ehsan 2026-10-31: "it should go to the highest boss I beat"): the highest boss stage
                // already beaten (bosses are stages 7, 14, 21).
                if (ctx.QuestLeft(QuestNeedKind.FactionWarsBoss) > 0 && c.HighestPassed >= 7)
                {
                    (stage, super) = (c.HighestPassed / 7 * 7, false);
                    ctx.Event(AreaId, "quest", $"{c.Name}: a quest asks for a boss: stage {stage}");
                }
                var outcome = await game.FightCryptAsync(c, stage, ctx.DryRun, ct, super);
                if (outcome.Result == BattleResult.NotStarted && super && !ctx.DryRun)
                {
                    // The keys did not pay for a Super Raid (a cost not read right): normal runs for this crypt today.
                    _noSuper.Add(c.Name);
                    ctx.Event(AreaId, "keys", $"{c.Name} stage {stage}: no Super Raid ({outcome.Text}); normal runs from now on today");
                    return "";
                }
                if (outcome.Result == BattleResult.NotStarted)
                {
                    // A dry run's shown Start, or (real) not enough keys for the stage: the crypt is done for today either way.
                    _dryShown.Add(c.Name);
                    ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "keys", ctx.DryRun ? $"would fight {c.Name} stage {stage}: {outcome.Text}" : $"{c.Name} stage {stage}: {outcome.Text}");
                    return "";
                }
                _battles++;
                if (outcome.Result == BattleResult.Victory)
                {
                    _wins++;
                    _defeatsInARow[c.Name] = 0;
                    // The quest is met for now (the next quest pass reads the real progress).
                    if (stage % 7 == 0) ctx.QuestNeeds.RemoveAll(n => n.Kind == QuestNeedKind.FactionWarsBoss && n.Remaining <= 1);
                }
                else
                {
                    _losses++;
                    _defeatsInARow[c.Name] = _defeatsInARow.GetValueOrDefault(c.Name) + 1;
                    // A defeat (it costs no key, seen 2026-10-29): one stage lower for the rest of the day (Ehsan: after a defeat at the next
                    // stage, farm the highest beaten one; it can still lose, so each defeat steps down again until one is won).
                    // The bot choosing the team (Teams card): the same stage again with the next team (a loss costs no key; Ehsan 2026-11-12).
                    var botHere = ctx.Settings.Teams.GetValueOrDefault($"faction-wars:{c.Faction}")?.Default is { } own ? own.Mode == TeamMode.BotChooses
                        : ctx.Settings.Teams.GetValueOrDefault("faction-wars")?.Default is { Mode: TeamMode.BotChooses };
                    if (!botHere)
                        _farmAt[c.Name] = Math.Max(1, Math.Min(stage - 1, Math.Max(1, c.HighestPassed)));
                }
                var asSuper = super && !outcome.Text.Contains(", normal run", StringComparison.Ordinal);
                ctx.Event(AreaId, "battle", $"{outcome.Result} vs {c.Name} stage {stage}{(asSuper ? " (Super Raid)" : "")}. {outcome.Text}");
                if (outcome.Result == BattleResult.Victory) await game.GearAfterAsync(ctx, AreaId, AreaId, ct);
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

    /// <summary>The names of these team members (the crypt's team order), for a task's title.</summary>
    private static string Names(CryptPlan c, IEnumerable<int> ids)
    {
        var name = c.Team.Zip(c.TeamNames).ToDictionary(x => x.First, x => x.Second);
        return string.Join(", ", ids.Select(id => name.GetValueOrDefault(id, $"#{id}")));
    }

    private sealed record Step(string Kind, CryptPlan? Crypt, string Why);

    /// <summary>What to do next, in the day's order; null when nothing is left today.</summary>
    private Step? Next(FactionWarsSettings s, bool dryRun)
    {
        if (_day is null) return null;
        var playable = _day.Open.Where(c => c.Keys > s.KeepKeys && Run(c, s) is not null && c.Team.Count > 0 && !_dryShown.Contains(c.Name)
            && _defeatsInARow.GetValueOrDefault(c.Name) < 6).ToList();
        if (s.StripYesterdaysTeams && !_stripped && _day.Strip.Count > 0 && playable.Count > 0)
            return new("strip", null, $"freeing yesterday's gear: {_day.Strip.Count} champion(s) to the Reserve Vault");
        foreach (var c in playable)
        {
            // Not geared yet, or its gear came off earlier and keys came back (they regenerate): gear it (again).
            if (s.GearTeam && (!_geared.Contains(c.Name) || _removed.Contains(c.Name))) return new("gear", c, $"{c.Name}: gearing the team");
            return new("fight", c, $"{c.Name}: stage {Run(c, s)!.Value.Stage}{(Run(c, s)!.Value.SuperRaid ? " Super Raid" : "")} ({c.Keys} keys)");
        }
        if (s.RemoveGearAfter && !dryRun)
            foreach (var c in _day.Open.Where(c => _geared.Contains(c.Name) && !_removed.Contains(c.Name) && (c.Keys <= s.KeepKeys || Run(c, s) is null || _dryShown.Contains(c.Name))))
                return new("remove", c, $"{c.Name}: keys used, removing the team's gear");
        return null;
    }

    /// <summary>The next run with the keys there are (<see cref="CryptKeys"/>); null: no stage the keys pay for.</summary>
    private (int Stage, bool SuperRaid)? Run(CryptPlan c, FactionWarsSettings s)
    {
        var newLevel = !_farmAt.ContainsKey(c.Name) && s.Stage != CryptStage.FarmHighest && c.HighestPassed < c.StageCount;
        return CryptKeys.Next(c, c.Keys - Math.Max(0, s.KeepKeys), newLevel ? c.NextStage : Stage(c, s), newLevel,
            s.SuperRaid && !_noSuper.Contains(c.Name) ? SuperRaidMultiplier : 1);
    }

    private readonly HashSet<string> _noSuper = [];

    /// <summary>A Super Raid's keys: twice the stage's (the game's DoubleRun, as in Campaign and dungeons).</summary>
    public const int SuperRaidMultiplier = 2;

    private int Stage(CryptPlan c, FactionWarsSettings s) =>
        _farmAt.TryGetValue(c.Name, out var farm) ? farm : s.Stage == CryptStage.FarmHighest ? Math.Max(1, c.HighestPassed) : c.NextStage;

    private void NewDay(PlayContext ctx)
    {
        var today = DateOnly.FromDateTime(ctx.Utc);
        if (today == _date) return;
        _date = today;
        _stripped = false;
        _geared.Clear(); _removed.Clear(); _farmAt.Clear(); _noSuper.Clear(); _dryShown.Clear(); _defeatsInARow.Clear();
        _battles = _wins = _losses = 0;
        _day = null;
    }

    private static DateTime NextUtcDay(DateTime utc) => utc.Date.AddDays(1).AddMinutes(3);
}
