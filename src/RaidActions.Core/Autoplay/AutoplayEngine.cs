using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>Where the engine runs: time, waiting, the settings and stop files, the person's input, the status and the event log.</summary>
public interface IAutoplayHost
{
    DateTime UtcNow { get; }
    Random Random { get; }
    int ProcessId { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
    AutoplaySettings LoadSettings();
    bool StopRequested();

    /// <summary>The last time the person (not the bot) used the mouse or keyboard; null when unknown.</summary>
    DateTime? PersonLastInputUtc();

    /// <summary>Why no input can reach the game now (a disconnected or locked Windows session); null when it can.</summary>
    string? InputBlocked() => null;

    void WriteStatus(AutoplayStatus status);
    void Event(AutoplayEvent e);

    /// <summary>Yesterday's and today's events, for what earlier runs learned (holds).</summary>
    IReadOnlyList<AutoplayEvent> RecentEvents() => [];

    /// <summary>Why this account's turn ends now, to play another one (raid-extractor's account pool); null: it goes on. Asked between turns only.</summary>
    string? RotateDue(DateTime? nextUsefulUtc) => null;

    /// <summary>What each area knew when the last bot on this account stopped (<see cref="IActivity.SaveState"/>), by area id; empty: nothing saved.</summary>
    IReadOnlyDictionary<string, string> LoadActivityState() => new Dictionary<string, string>();

    /// <summary>Keeps what each area knows for the next bot on this account (rotation restarts the bot every turn).</summary>
    void SaveActivityState(IReadOnlyDictionary<string, string> states) { }

    /// <summary>This account's tasks (raid-extractor plan/todo-list.md step 2: follow-ups, checkpoints); null without a store.</summary>
    ITaskBook? Tasks => null;

    /// <summary>The dungeons (Energy regions: "SpiritKeep") known to be closed on this UTC day from the schedule (seen closed on this weekday); empty when unknown.</summary>
    IReadOnlySet<string> ClosedBySchedule(DateTime utc) => new HashSet<string>();

    /// <summary>Keeps the last quest read for the app's To-do page (every open quest and what the bot does about it).</summary>
    void SaveQuests(QuestSnapshot snapshot) { }
}

/// <summary>
/// The scheduler: before every turn it re-reads the settings (master switch, area switches) and the stop request, keeps to the
/// active hours and breaks, keeps its hands off while the person uses the PC, asks each enabled activity what it wants, runs one
/// turn of the first that wants to act now (activities are in priority order), else waits until the earliest time one wants.
/// An error sends the game home and backs off (30 s, 60 s, ...); too many in a row stop the bot. The status file is written on
/// every pass (at least every 5 s).
/// </summary>
public sealed class AutoplayEngine(IReadOnlyList<IActivity> activities, IPlayGame game, IAutoplayHost host, bool dryRun)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private readonly List<AutoplayEvent> _recent = [];
    private const int MaxTurnsInARow = 3;
    private string? _lastArea;
    private int _sameArea;
    private readonly Dictionary<string, int> _playedAt = [];
    private int _turnNumber;
    private DateTime _resourcesAt = DateTime.MinValue;

    /// <summary>Quests as if read (autoplay --test-quest): to try an area's quest on the live game when no such quest is open.</summary>
    public IReadOnlyList<QuestNeed> StartNeeds { get; init; } = [];
    private string _engine = "starting", _detail = "";
    private DateTime? _next;
    private PlayContext? _ctx;
    private Dictionary<string, Decision> _decisions = [];

    /// <summary>Turns run (one battle, one refresh, ...).</summary>
    public int Turns { get; private set; }

    /// <param name="once">Stop when no activity wants to act now (instead of waiting): a dry run, a test, a single session.</param>
    /// <returns>Why the engine stopped.</returns>
    /// <summary>A locked area is tried again after this.</summary>
    public static readonly TimeSpan LockedHold = TimeSpan.FromHours(6);

    /// <summary>A read failing because the game's model is gone (it reloaded: logged out, e.g. the account opened on another device).</summary>
    public static bool GameReloaded(string error) => error.Contains("AppModel", StringComparison.Ordinal) && error.Contains("implausible pointer 0x0", StringComparison.Ordinal);

    /// <summary>How the host's block reason starts when the account is open on another device (LivePlayGame.SessionTaken).</summary>
    public const string AnotherDevice = "the account is open on another device";

    /// <summary>An area that failed this many turns in a row waits <see cref="AreaErrorHold"/>; the others go on.</summary>
    public const int AreaErrorsToHold = 2;

    public static readonly TimeSpan AreaErrorHold = TimeSpan.FromHours(1);

    private readonly Dictionary<string, int> _areaErrors = [];

    /// <summary>The area the plan puts first (an open follow-up, then a daily quest due soon), among those that want to act; null: none.</summary>
    private IActivity? PlanFirst(PlannerSettings p, List<IActivity> enabled, DateTime now)
    {
        if (host.Tasks is not { } book || !(p.FollowUpsFirst || p.QuestTasks)) return null;
        IReadOnlyList<TaskRow> open;
        try { open = book.OpenTasks(); }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException) { return null; }
        var wanting = enabled.Where(a => _decisions[a.Id].Act).ToList();
        if (p.FollowUpsFirst && wanting.FirstOrDefault(a => open.Any(t => t.Area == a.Id && t.FollowUpDue(now))) is { } followUp) return followUp;
        if (p.QuestTasks && p.QuestDeadlineHours > 0 && wanting.FirstOrDefault(a => QuestTasks.DueSoon(open, a.Id, now, p.QuestDeadlineHours) is not null) is { } quest) return quest;
        return null;
    }

    public async Task<string> RunAsync(bool once, CancellationToken ct)
    {
        var settings = host.LoadSettings();
        // The quests read first in every run (Ehsan 2026-11-09: the player may finish a mission on another device while the bot is away, e.g. the
        // Spirit Keep potions on Monday at 11:10; the bot coming at 14:00 must not act on the old read): the read closes the tasks of quests gone.
        var ctx = _ctx = new PlayContext(settings, game, host, dryRun, Remember) { QuestNeeds = [.. StartNeeds], QuestsStale = StartNeeds.Count == 0 };
        ctx.RestoreHolds(host.RecentEvents());
        // What the areas knew when the last bot here stopped (Ehsan 2026-11-06: "track stuff, don't just check them": the arena's board and refresh, the
        // reward checks' times), so a rotation's new bot does not go and look again.
        var saved = host.LoadActivityState();
        foreach (var a in activities)
            if (saved.TryGetValue(a.Id, out var state))
                try { a.LoadState(state, host.UtcNow); }
                catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { }
        var errors = 0;
        var sessionEnd = host.UtcNow + Jitter(settings.Pacing.SessionMinutes);
        try
        {
            Remember(new AutoplayEvent(host.UtcNow, "engine", "start", dryRun ? "Auto Play started (dry run: nothing is spent or pressed for real)" : "Auto Play started"));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                settings = ctx.Settings = host.LoadSettings();
                if (host.StopRequested()) return Stop("stopped from the app");
                if (!settings.Enabled) return Stop("Auto Play is switched off");
                var now = host.UtcNow;
                var p = settings.Pacing;

                if (p.NextActive(now.ToLocalTime()) is { } wake)
                {
                    await Idle("resting", $"outside the active hours ({p.ActiveFrom}–{p.ActiveTo})", wake.ToUniversalTime(), ct);
                    if (once) return Stop("outside the active hours");
                    continue;
                }
                if (p.SessionMinutes > 0 && p.BreakMinutes > 0 && now >= sessionEnd)
                {
                    var until = now + Jitter(p.BreakMinutes);
                    Remember(new AutoplayEvent(now, "engine", "break", $"a break until {until.ToLocalTime():HH:mm}"));
                    while (host.UtcNow < until && !host.StopRequested() && host.LoadSettings().Enabled) await Idle("resting", "taking a break, like a person", until, ct);
                    sessionEnd = host.UtcNow + Jitter(p.SessionMinutes);
                    continue;
                }
                if (host.InputBlocked() is { } blocked)
                {
                    // The account is open on another device (Ehsan 2026-11-09: "if it is logged in from another device you switch account to the next
                    // and come back after the next account"): with other accounts to play, this account's turn ends now instead of waiting.
                    if (!once && blocked.StartsWith(AnotherDevice, StringComparison.Ordinal) && host.RotateDue(now.AddDays(1)) is not null)
                        return Stop("rotation: " + blocked);
                    // Nothing can be clicked (Remote Desktop disconnected, PC locked): wait, look again every minute, carry on after.
                    await Idle("waiting", "no input reaches the game: " + blocked, now + TimeSpan.FromMinutes(1), ct, untilOnly: true);
                    continue;
                }
                if (host.PersonLastInputUtc() is { } person && now - person < TimeSpan.FromMinutes(Math.Max(0, p.YieldToPersonMinutes)))
                {
                    await Idle("yielding", "you are using the PC: hands off", person.AddMinutes(p.YieldToPersonMinutes), ct);
                    continue;
                }

                // The order set on the page; an area not in it keeps its place after those that are.
                var order = settings.AreaOrder;
                if (now >= _resourcesAt && activities.Any(a => a.IsEnabled(settings)))
                {
                    _resourcesAt = now + TimeSpan.FromMinutes(2);
                    // A game still logging in cannot be read (2026-11-01: this ended the bot): the next read tries again.
                    try { await ctx.ReadResourcesAsync(ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { }
                }
                var enabled = activities.Where(a => a.IsEnabled(settings)).OrderBy(a => order.IndexOf(a.Id) is >= 0 and var at ? at : int.MaxValue).ToList();
                ctx.ChainBreak = self => ChainBreak(ctx, enabled, self, once);
                _decisions = activities.ToDictionary(a => a.Id, a => !a.IsEnabled(settings) ? Decision.Off("switched off")
                    : ctx.Held(a.Id) is { } held ? Decision.Wait(held.Until, held.Why) : a.Decide(ctx));
                if (enabled.Count == 0)
                {
                    if (once) return Stop("no area is switched on");
                    await Idle("waiting", "no area is switched on", null, ct);
                    continue;
                }
                // Priority decides who goes first, but an area with many turns in hand (Faction Wars, 24 keys: two hours) steps aside after three in a row,
                // so full energy and short-lived chances (Live Arena's hours) do not wait for it (2026-11-01).
                var act = enabled.FirstOrDefault(a => _decisions[a.Id].Act);
                var stepAside = act is not null && _sameArea >= MaxTurnsInARow;
                if (stepAside)
                    // The one waiting longest, not the next in line: that one and the leader would take turns and the rest never play (2026-11-02: Chores waited 25 minutes).
                    act = enabled.Where(a => _decisions[a.Id].Act && a.Id != _lastArea).OrderBy(a => _playedAt.GetValueOrDefault(a.Id)).FirstOrDefault() ?? act;
                // The plan (raid-extractor plan/todo-list.md): an area with an open follow-up (champions to put back in a vault) first, then one with a
                // daily quest close to the reset; a full resource and an urgent chance still come before them.
                if (!stepAside && PlanFirst(settings.Planner, enabled, now) is { } planned) act = planned;
                // A full resource stops regenerating: its area first, whatever the order; but after three turns in a row another area gets one, or long runs with
                // energy near its cap starve the rest (2026-11-02: only Energy played for 30 minutes).
                if (!stepAside && enabled.FirstOrDefault(a => _decisions[a.Id].Act && a.Full(ctx)) is { } full) act = full;
                // Every fourth turn the area that has waited longest (never played in this run first, in the order): each account's turn is a new bot of
                // 30 minutes, and the areas at the end of the order never came (2026-11-10: BlackLord65's Doom Tower, 10 gold and 15 silver keys, wanted
                // to play all day behind the arenas, Clan Boss, Faction Wars and full energy).
                if (!stepAside && _turnNumber % 4 == 3 && enabled.Where(a => _decisions[a.Id].Act).OrderBy(a => _playedAt.GetValueOrDefault(a.Id)).FirstOrDefault() is { } waited)
                    act = waited;
                // An urgent one (a free arena refresh due with nobody to fight) before anything: what a chain of runs was broken for.
                if (!stepAside && enabled.FirstOrDefault(a => _decisions[a.Id].Act && a.Urgent(ctx)) is { } urgent) act = urgent;
                if (act is not null)
                {
                    _sameArea = act.Id == _lastArea ? _sameArea + 1 : 1;
                    _playedAt[act.Id] = ++_turnNumber;
                    _lastArea = act.Id;
                }
                // Another account's turn (its slice used, or nothing due here for a while): this run ends, the next account's starts.
                var nextUseful = act is null ? enabled.Select(a => _decisions[a.Id].UntilUtc).Where(t => t is not null).Min() : null;
                if (!once && host.RotateDue(nextUseful) is { } rotate) return Stop("rotation: " + rotate);
                if (act is null)
                {
                    var wait = enabled.Select(a => _decisions[a.Id].UntilUtc).Where(t => t is not null).Min();
                    if (once) return Stop("nothing to do now" + (wait is { } w ? $" (next at {w.ToLocalTime():HH:mm:ss})" : ""));
                    var first = enabled.FirstOrDefault(a => _decisions[a.Id].UntilUtc == wait) ?? enabled[0];
                    await Idle("waiting", $"{first.Name}: {_decisions[first.Id].Why}", wait, ct);
                    continue;
                }

                (_engine, _detail, _next) = ("playing", $"{act.Name}: {_decisions[act.Id].Why}", null);
                WriteStatus();
                try
                {
                    await ctx.ThinkAsync(ct);
                    var before = await Holdings(ct);
                    var done = await act.RunTurnAsync(ctx, ct);
                    // What the turn brought in, and from where (the rewards log; nothing in a dry run).
                    if (!dryRun && before is not null && await Holdings(ct) is { } after && Gains.Describe(before, after) is { } got)
                        Remember(new AutoplayEvent(host.UtcNow, act.Id, "reward", $"{act.Name}{(done.Length > 0 ? $" ({(done.Length > 80 ? done[..80] + "…" : done)})" : "")}: {got}"));
                    Turns++;
                    errors = 0;
                    _areaErrors.Remove(act.Id);
                    if (done.Length > 0) Remember(new AutoplayEvent(host.UtcNow, act.Id, "turn", done));
                    SaveStates();
                }
                catch (AreaLockedException e)
                {
                    // Not open on this account yet: tried again later, never counted as an error.
                    ctx.Hold(act.Id, host.UtcNow + LockedHold, "not open yet: " + e.Message);
                    try { await game.GoHomeAsync(ct); }
                    catch (Exception home) when (home is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Remember(new AutoplayEvent(host.UtcNow, "engine", "error", "going home failed: " + home.Message));
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    errors++;
                    Remember(new AutoplayEvent(host.UtcNow, act.Id, "error", e.Message));
                    // The game dropped its data mid-turn (its AppModel gone): it was logged out and came back by itself, with no "another device" box
                    // (2026-11-10 15:38: Ehsan logged in on another device, the game here reloaded and the bot played on and took the account back). With
                    // other accounts to play, this one's turn ends and the next account is played (Ehsan: "if I logged in from another device you should
                    // move to the next account in rotation").
                    if (!once && GameReloaded(e.Message) && host.RotateDue(host.UtcNow.AddDays(1)) is not null)
                        return Stop($"rotation: {AnotherDevice} (the game reloaded mid-turn: {e.Message})");
                    // The same area failing again: it alone waits an hour, so one area that cannot be played never stops the others (2026-11-04).
                    _areaErrors[act.Id] = _areaErrors.GetValueOrDefault(act.Id) + 1;
                    if (_areaErrors[act.Id] >= AreaErrorsToHold && enabled.Count > 1)
                    {
                        ctx.Hold(act.Id, host.UtcNow + AreaErrorHold, $"paused after {_areaErrors[act.Id]} errors in a row: {e.Message}");
                        _areaErrors.Remove(act.Id);
                    }
                    if (errors >= Math.Max(1, p.MaxErrorsInARow)) return Stop($"{errors} errors in a row; last: {e.Message}", error: true);
                    try { await game.GoHomeAsync(ct); }
                    catch (Exception home) when (home is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Remember(new AutoplayEvent(host.UtcNow, "engine", "error", "going home failed: " + home.Message));
                    }
                    var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, errors - 1));
                    await Idle("waiting", $"after an error ({errors} in a row), trying again at {(host.UtcNow + backoff).ToLocalTime():HH:mm:ss}", host.UtcNow + backoff, ct, untilOnly: true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Stop("aborted (Ctrl+Alt+Esc or Ctrl+C)");
        }
    }

    /// <summary>Waits (in short slices, re-checking stop and settings) until <paramref name="until"/>, at most one tick unless untilOnly.</summary>
    private async Task Idle(string engine, string detail, DateTime? until, CancellationToken ct, bool untilOnly = false)
    {
        (_engine, _detail, _next) = (engine, detail, until);
        WriteStatus();
        var end = until ?? host.UtcNow + Tick;
        if (!untilOnly && end - host.UtcNow > Tick) end = host.UtcNow + Tick;
        // A time already past (a clock's estimate): still a short wait, never a busy loop.
        if (end - host.UtcNow < TimeSpan.FromSeconds(1)) end = host.UtcNow + TimeSpan.FromSeconds(1);
        while (host.UtcNow < end)
        {
            var slice = end - host.UtcNow < Tick ? end - host.UtcNow : Tick;
            await host.DelayAsync(slice, ct);
            if (!untilOnly) break;
            if (host.StopRequested() || !host.LoadSettings().Enabled) break;
            WriteStatus();
        }
    }

    /// <summary>The account's holdings, or null (not readable now: the rewards log skips this turn).</summary>
    private async Task<Holdings?> Holdings(CancellationToken ct)
    {
        try { return await game.ReadHoldingsAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private void SaveStates()
    {
        var states = new Dictionary<string, string>();
        foreach (var a in activities)
            if (a.SaveState() is { } s) states[a.Id] = s;
        try { host.SaveActivityState(states); }
        catch (IOException) { }
    }

    private string Stop(string why, bool error = false)
    {
        SaveStates();
        Remember(new AutoplayEvent(host.UtcNow, "engine", error ? "error" : "stop", "Auto Play stopped: " + why));
        (_engine, _detail, _next) = (error ? "error" : "stopped", why, null);
        WriteStatus();
        return why;
    }

    private void Remember(AutoplayEvent e)
    {
        _recent.Add(e);
        if (_recent.Count > 50) _recent.RemoveAt(0);
        host.Event(e);
    }

    private void WriteStatus()
    {
        var ctx = _ctx;
        var areas = ctx is null ? [] : activities.Select(a => a.Status(ctx, _decisions.GetValueOrDefault(a.Id) ?? Decision.Off(a.IsEnabled(ctx.Settings) ? "starting" : "switched off"))).ToList();
        var clocks = ctx is null ? [] : ctx.Clocks.Values.Select(c => c.Out()).OfType<ClockOut>().ToList();
        host.WriteStatus(new AutoplayStatus(host.UtcNow, host.ProcessId, dryRun, _engine, _detail, _next, clocks, areas, [.. _recent.TakeLast(20)]));
    }

    private TimeSpan Jitter(int minutes) => TimeSpan.FromMinutes(Math.Max(0, minutes) * (0.75 + 0.5 * host.Random.NextDouble()));

    /// <summary>
    /// What ends a chain of runs (Ehsan 2026-11-06: "the only time you go back to base is when something more critical is due, e.g. the Arena refresh"):
    /// an area that wants to act and is urgent (<see cref="IActivity.Urgent"/>) or at its cap (<see cref="IActivity.Full"/>), or the account's turn is over.
    /// </summary>
    private string? ChainBreak(PlayContext ctx, IReadOnlyList<IActivity> enabled, string self, bool once)
    {
        if (!once && host.RotateDue(null) is { } rotate) return "rotation: " + rotate;
        // Only an area the next turn would really give to (2026-11-06: arena tokens full broke every Campaign chain, and the next turn went to Energy again,
        // full too and first in the order): an urgent one, or a full one while this area is not full or comes after it.
        var me = enabled.FirstOrDefault(a => a.Id == self);
        var meFull = me?.Full(ctx) == true;
        var myIndex = me is null ? int.MaxValue : enabled.ToList().IndexOf(me);
        foreach (var (a, i) in enabled.Select((a, i) => (a, i)).Where(x => x.a.Id != self))
        {
            if (ctx.Held(a.Id) is not null) continue;
            var d = a.Decide(ctx);
            if (d.Act && (a.Urgent(ctx) || a.Full(ctx) && (!meFull || i < myIndex))) return $"{a.Name}: {d.Why}";
        }
        return null;
    }
}
