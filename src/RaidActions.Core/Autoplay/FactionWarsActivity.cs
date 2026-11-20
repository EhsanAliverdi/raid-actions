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

    /// <summary>The simulated win rate of the crypt's team (as geared by the bot) on this stage; null: no forecast (the stage is not in the static data).</summary>
    Task<StageForecast?> ForecastStageAsync(CryptPlan crypt, int stage, CancellationToken ct) => Task.FromResult<StageForecast?>(null);

    /// <summary>The village button's Clan vs Clan phase and countdown (a read of the village, no window opened); null: not known.</summary>
    Task<CvcStatus?> ReadCvcAsync(CancellationToken ct) => Task.FromResult<CvcStatus?>(null);
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
    /// <summary>Crypt+stage pairs simulated today (the push gate runs once per day for each).</summary>
    private readonly HashSet<string> _forecast = [];

    /// <summary>The push gate: a new stage is tried only when the simulation gives the team more than 50%.</summary>
    public const double PushThreshold = 0.5;

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
                case "quest" when e.Text.StartsWith("no Faction Wars boss beatable", StringComparison.Ordinal): _bossGaveUp = true; break;
                case "gear": _geared.Add(name); break;
                case "remove": _removed.Add(name); break;
                // "Demonspawn stage 9: not enough keys ...": the crypt stays done for today after a restart too.
                case "keys": _dryShown.Add(name.Split(" stage ")[0]); break;
                // "Demonspawn stage 22: simulated win 49% ...": the crypt stays on its highest beaten stage after a restart too.
                case "sim" when System.Text.RegularExpressions.Regex.Match(e.Text, @"^(.+) stage (\d+): .*?stays at stage (\d+)") is { Success: true } sm:
                    _farmAt[sm.Groups[1].Value] = int.Parse(sm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    _forecast.Add($"{sm.Groups[1].Value}|{sm.Groups[2].Value}");
                    break;
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
        // The daily Tag Team quest first (Ehsan 2026-11-20: its reward is the day's +6 crypt keys; gear the team once, play once).
        if (ctx.YieldTo?.Invoke(Id) is { } first) return Decision.Wait(ctx.Utc + TimeSpan.FromMinutes(1), first);
        if (_day is null || ctx.Utc - _dayAt > PlanStale || _day.UtcDay != DateOnly.FromDateTime(ctx.Utc)) return Decision.Now("reading today's crypts");
        Sync(ctx);
        if (NeedCvcRead(ctx)) return Decision.Now("reading Clan vs Clan's start");
        return Next(ctx.Settings.FactionWars, ctx.DryRun, ctx.Settings.Teams) is { } step ? Decision.Now(step.Why)
            : HoldDecision(ctx) ?? Decision.Wait(NextUtcDay(ctx.Utc), _day.Open.Count == 0 ? "no crypt open today" : "today's keys are used: next crypts at the new day (UTC)");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        await game.GearAfterAsync(ctx, AreaId, null, ct);
        NewDay(ctx);
        _day = await game.PlanDayAsync(ct);
        _dayAt = ctx.Utc;
        Sync(ctx);
        if (NeedCvcRead(ctx)) await ReadCvcAsync(ctx, ct);
        var s = ctx.Settings.FactionWars;
        await ResolveBossAsync(ctx, ct);
        if (Next(s, ctx.DryRun, ctx.Settings.Teams) is not { } step) return "";
        var text = await RunStepAsync(ctx, step, s, ct);
        // The gear once, then every battle of the crypt in a row while its keys last (Ehsan 2026-11-20), back to base only when something more critical is due.
        if (ctx.DryRun || step.Crypt is not { } crypt || !(step.Kind == "gear" || step.Kind == "fight" && IsBattle(text))) return text;
        var runs = step.Kind == "fight" ? 1 : 0;
        while (runs < MaxChain && (runs == 0 || IsBattle(text)) && DateOnly.FromDateTime(ctx.Utc) == _date) // a new UTC day plans again
        {
            if (ctx.ChainBreak?.Invoke(Id) is { } why)
            {
                ctx.Event(AreaId, "chain", $"{crypt.Name}: back after {runs} battle(s): {why}");
                break;
            }
            if (Next(s, ctx.DryRun, ctx.Settings.Teams) is not { Kind: "fight", Crypt: { } next } more || next.Name != crypt.Name) break;
            var again = await RunStepAsync(ctx, more, s, ct);
            if (again.Length == 0) break;
            text = again;
            runs++;
        }
        return runs > 1 ? $"{text} ({runs} battles in a row)" : text;
    }

    /// <summary>Battles in one turn, as a safety.</summary>
    public const int MaxChain = 30;

    private static bool IsBattle(string text) => text.StartsWith("Victory", StringComparison.Ordinal) || text.StartsWith("Defeat", StringComparison.Ordinal);

    private async Task<string> RunStepAsync(PlayContext ctx, Step step, FactionWarsSettings s, CancellationToken ct)
    {
        await ctx.ThinkAsync(ct);
        var (phase, what, detail) = step.Kind switch
        {
            "strip" => (1, "freeing yesterday's gear", "taking the gear off yesterday's teams, champions to the Reserve Vault"),
            "gear" => (2, $"gearing up the {step.Crypt?.Name} team", step.Crypt is null ? "" : $"{string.Join(", ", step.Crypt.TeamNames)}: moving and upgrading gear"),
            "remove" => (4, $"taking the gear off the {step.Crypt?.Name} team", "the crypt's keys are used"),
            _ => (3, $"fighting the {step.Crypt?.Name} crypt", step.Crypt is null ? "" : $"{step.Crypt.Keys} key(s) left, battles in a row"),
        };
        ctx.Report($"Faction Wars ({step.Crypt?.Name ?? "all crypts"}): {what}", step.Why, phase, 4, detail);
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
                // A quest asks for a Faction Wars boss (stages 7, 14, 21): the highest boss any open crypt's team is simulated to beat (ResolveBossAsync).
                if (_bossPick is { } bp && bp.Crypt == c.Name && ctx.QuestLeft(QuestNeedKind.FactionWarsBoss) > 0)
                {
                    (stage, super) = (bp.Stage, false);
                    ctx.Event(AreaId, "quest", $"{c.Name}: a quest asks for a boss: stage {stage} ({bp.Rate:P0})");
                }
                if (!ctx.DryRun && stage > c.HighestPassed && c.HighestPassed >= 1 && _forecast.Add($"{c.Name}|{stage}"))
                {
                    var fc = await ForecastAsync(c, stage, ct);
                    // No forecast is no push (2026-11-20, KaelStormVex: the crypt had no team set, the forecast came back empty and Barbarians
                    // stage 5 was pushed and lost again and again, a key each): a new stage is tried only on a simulated win over the threshold.
                    if (fc is null || fc.WinRate <= PushThreshold)
                    {
                        _farmAt[c.Name] = c.HighestPassed;
                        ctx.Event(AreaId, "sim", fc is null
                            ? $"{c.Name} stage {stage}: no simulation (no team to simulate): stays at stage {c.HighestPassed} today"
                            : $"{c.Name} stage {stage}: simulated win {fc.WinRate:P0} (needs over {PushThreshold:P0}), stays at stage {c.HighestPassed} today. {fc.Text}");
                        return "";
                    }
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
                // The keys the battle used and the stage beaten, for the next battle of this turn (the next turn plans the day again).
                var keysBefore = c.Keys;
                var spent = c.Cost(stage) * (super && !outcome.Text.Contains(", normal run", StringComparison.Ordinal) ? SuperRaidMultiplier : 1);
                if (_day!.Open.IndexOf(c) is var at and >= 0)
                    _day.Open[at] = c with { Keys = Math.Max(0, c.Keys - spent), HighestPassed = outcome.Result == BattleResult.Victory ? Math.Max(c.HighestPassed, stage) : c.HighestPassed };
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
                var keysAfter = Math.Max(0, keysBefore - spent);
                ctx.Event(AreaId, "battle", $"{outcome.Result} vs {c.Name} stage {stage}{(asSuper ? $" (Super Raid x{SuperRaidMultiplier})" : " (x1)")}, keys {keysBefore} -> {keysAfter}. {outcome.Text}{outcome.TeamTag}");
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
    private Step? Next(FactionWarsSettings s, bool dryRun, IReadOnlyDictionary<string, AreaTeams>? teams = null)
    {
        if (_day is null) return null;
        var playable = _day.Open.Where(c => c.Keys > KeepFor(c, s) && Run(c, s) is not null && c.Team.Count > 0 && !_dryShown.Contains(c.Name)
            && _defeatsInARow.GetValueOrDefault(c.Name) < 6).OrderByDescending(c => _bossPick?.Crypt == c.Name).ToList();
        if (s.StripYesterdaysTeams && !_stripped && _day.Strip.Count > 0 && playable.Count > 0)
            return new("strip", null, $"freeing yesterday's gear: {_day.Strip.Count} champion(s) to the Reserve Vault");
        foreach (var c in playable)
        {
            // Not geared yet, or its gear came off earlier and keys came back (they regenerate): gear it (again).
            // The card's "gear the team", or the faction's own team set to gear up (Ehsan 2026-11-13).
            // The faction's team on the page decides (Ehsan 2026-11-13: one team setup for every area): "the bot picks" with "gear up".
            var choice = teams?.GetValueOrDefault($"faction-wars:{c.Faction}")?.Default ?? teams?.GetValueOrDefault("faction-wars")?.Default;
            if (choice is { Mode: TeamMode.BotChooses, GearUp: true } && (!_geared.Contains(c.Name) || _removed.Contains(c.Name))) return new("gear", c, $"{c.Name}: preparing the team");
            return new("fight", c, $"{c.Name}: stage {Run(c, s)!.Value.Stage}{(Run(c, s)!.Value.SuperRaid ? " Super Raid" : "")} ({c.Keys} keys)");
        }
        if (s.RemoveGearAfter && !dryRun)
            foreach (var c in _day.Open.Where(c => _geared.Contains(c.Name) && !_removed.Contains(c.Name) && (c.Keys <= KeepFor(c, s) || Run(c, s) is null || _dryShown.Contains(c.Name))))
                return new("remove", c, $"{c.Name}: keys used, removing the team's gear");
        return null;
    }

    /// <summary>The next run with the keys there are (<see cref="CryptKeys"/>); null: no stage the keys pay for.</summary>
    private (int Stage, bool SuperRaid)? Run(CryptPlan c, FactionWarsSettings s)
    {
        var newLevel = !_farmAt.ContainsKey(c.Name) && s.Stage != CryptStage.FarmHighest && c.HighestPassed < c.StageCount;
        var run = CryptKeyPlan.Next(c, c.Keys - Math.Max(0, KeepFor(c, s)), newLevel ? c.NextStage : Stage(c, s), newLevel,
            s.SuperRaid && !_noSuper.Contains(c.Name) ? SuperRaidMultiplier : 1);
        return run is null ? null : (run.Stage, run.SuperRaid);
    }

    private readonly HashSet<string> _noSuper = [];

    private readonly Dictionary<string, StageForecast?> _rates = [];
    private (string Crypt, int Stage, double Rate)? _bossPick;
    private bool _bossGaveUp;

    /// <summary>The simulated win rate of a crypt's team at a stage, asked once a day for each pair.</summary>
    private async Task<StageForecast?> ForecastAsync(CryptPlan c, int stage, CancellationToken ct)
    {
        var key = $"{c.Name}|{stage}";
        if (!_rates.TryGetValue(key, out var fc)) _rates[key] = fc = await game.ForecastStageAsync(c, stage, ct);
        return fc;
    }

    /// <summary>
    /// A quest asks for a Faction Wars boss: the highest boss stage (7, 14, 21) over every open crypt whose simulated win rate passes the push gate
    /// (a boss already beaten with no forecast counts too). None does: the quest is given up for the day, logged. Quests that only need keys or materials
    /// take the normal choice.
    /// </summary>
    private async Task ResolveBossAsync(PlayContext ctx, CancellationToken ct)
    {
        if (ctx.QuestLeft(QuestNeedKind.FactionWarsBoss) <= 0) { _bossPick = null; return; }
        if (_bossGaveUp || _day is null || ctx.DryRun || _bossPick is not null) return;
        (string Crypt, int Stage, double Rate)? best = null;
        (string Crypt, int Stage, double Rate)? nearest = null;
        foreach (var c in _day.Open.Where(c => c.Team.Count > 0 && c.Keys > 0))
            for (var stage = 7; stage <= c.StageCount; stage += 7)
            {
                if (stage > c.HighestPassed + 1) break; // not unlocked yet
                var fc = await ForecastAsync(c, stage, ct);
                var rate = fc?.WinRate ?? (stage <= c.HighestPassed ? 1.0 : 0.0);
                if (nearest is null || stage > nearest.Value.Stage || stage == nearest.Value.Stage && rate > nearest.Value.Rate) nearest = (c.Name, stage, rate);
                if (rate > PushThreshold && (best is null || stage > best.Value.Stage || stage == best.Value.Stage && rate > best.Value.Rate)) best = (c.Name, stage, rate);
            }
        if (best is not null) { _bossPick = best; return; }
        _bossGaveUp = true;
        ctx.Event(AreaId, "quest", nearest is { } n ? $"no Faction Wars boss beatable (best: {n.Crypt} stage {n.Stage}, {n.Rate:P0})" : "no Faction Wars boss beatable (no boss stage open)");
    }

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
        _geared.Clear(); _removed.Clear(); _forecast.Clear(); _farmAt.Clear(); _noSuper.Clear(); _rates.Clear(); _bossPick = null; _bossGaveUp = false; _dryShown.Clear(); _defeatsInARow.Clear();
        _battles = _wins = _losses = 0;
        _day = null;
    }

    private CvcStatus? _cvc;
    private DateTime _cvcAt;
    private DateTime _now;
    private bool _questKeys, _tagQuest;
    private DateTime _holdLogged;

    private void Sync(PlayContext ctx)
    {
        _now = ctx.Utc;
        _tagQuest = ctx.QuestLeft(QuestNeedKind.TagArena) > 0;
        _questKeys = ctx.QuestLeft(QuestNeedKind.FactionWars) > 0 || ctx.QuestLeft(QuestNeedKind.FactionWarsBoss) > 0;
    }

    /// <summary>The cached Clan vs Clan state is missing or old, and a crypt has keys to decide about.</summary>
    private bool NeedCvcRead(PlayContext ctx) =>
        _day is not null && _day.Open.Any(c => c.Keys > ctx.Settings.FactionWars.KeepKeys) && ctx.Utc - _cvcAt > TimeSpan.FromMinutes(1)
        && (_cvc is null ? ctx.Utc - _cvcAt > TimeSpan.FromMinutes(30) : _cvc.Stale(ctx.Utc));

    private async Task ReadCvcAsync(PlayContext ctx, CancellationToken ct)
    {
        _cvcAt = ctx.Utc;
        _cvc = await game.ReadCvcAsync(ct);
        if (_cvc is not null) ctx.Event(AreaId, "cvc", _cvc.StartUtc is { } s ? $"Clan vs Clan starts in {CvcKeyHold.Hours(s - ctx.Utc)}" : $"Clan vs Clan: {_cvc.Phase}");
    }

    /// <summary>The keys to leave in this crypt: Clan vs Clan about to start holds them (<see cref="CvcKeyHold"/>).</summary>
    private int KeepFor(CryptPlan c, FactionWarsSettings s)
    {
        var keep = CvcKeyHold.Keep(_cvc, _now, c.Keys, s.KeepKeys, _questKeys, _tagQuest, NextUtcDay(_now), out _);
        return keep;
    }

    /// <summary>Nothing to play because the keys are held for Clan vs Clan: wait for its start, saying so (logged at most every 2 hours).</summary>
    private Decision? HoldDecision(PlayContext ctx)
    {
        if (_day is null || _cvc is not { Phase: CvcPhase.NotStarted, StartUtc: { } start } || start <= ctx.Utc) return null;
        var s = ctx.Settings.FactionWars;
        if (!_day.Open.Any(c => CvcKeyHold.Keep(_cvc, ctx.Utc, c.Keys, s.KeepKeys, _questKeys, _tagQuest, NextUtcDay(ctx.Utc), out _) > s.KeepKeys)) return null;
        var why = $"keeping crypt keys for Clan vs Clan, starts in {CvcKeyHold.Hours(start - ctx.Utc)}";
        if (ctx.Utc - _holdLogged > TimeSpan.FromHours(2)) { _holdLogged = ctx.Utc; ctx.Event(AreaId, "hold-cvc", why); }
        var until = start + TimeSpan.FromMinutes(1);
        return Decision.Wait(until < ctx.Utc + TimeSpan.FromMinutes(31) ? until : ctx.Utc + TimeSpan.FromMinutes(31), why);
    }

    private static DateTime NextUtcDay(DateTime utc) => utc.Date.AddDays(1).AddMinutes(3);
}
