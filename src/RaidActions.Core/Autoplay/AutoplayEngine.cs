using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>The rotation as the banner shows it: this account's name, the next account, when this turn ends (UTC).</summary>
public sealed record TurnView(string Who, string? To, DateTime? EndsUtc);

/// <summary>Where the engine runs: time, waiting, the settings and stop files, the person's input, the status and the event log.</summary>
public interface IAutoplayHost
{
    DateTime UtcNow { get; }
    Random Random { get; }
    int ProcessId { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
    AutoplaySettings LoadSettings();
    bool StopRequested();

    /// <summary>When the app asked for a pause (UTC); null: not paused. While it is set the bot sends no input.</summary>
    DateTime? PausedSince() => null;

    /// <summary>The pause begins: the host lets go of any held input.</summary>
    void PauseBegan() { }

    /// <summary>
    /// The pause ended after <paramref name="pausedFor"/>: the host moves the rotation slice's clock on by it and re-reads the game. Null: go on with the
    /// account the bot plays; else why this run ends (<see cref="PauseGate.NowOn"/>, <see cref="PauseGate.SwitchBack"/>, <see cref="PauseGate.GameClosed"/>).
    /// </summary>
    string? PauseEnded(TimeSpan pausedFor) => null;

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

    /// <summary>The areas that want to act now (this loop): only their open tasks can hold the turn past its time (2026-11-20: a vault put-back of an area that was waiting held the turn for nothing).</summary>
    void Acting(IReadOnlySet<string> areaIds) { }

    /// <summary>For the banner: this account's name, the account the rotation goes to next, and when this turn ends (UTC); null: no rotation.</summary>
    TurnView? BannerTurn() => null;
    /// <summary>Another account of the pool can be played now, whatever this turn's time or hold (the account taken by another device).</summary>
    bool CanRotate() => false;

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

    /// <summary>Every quest of the read, done ones too (the app's quest table).</summary>
    void SaveQuestBoard(QuestBoard board) { }
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
    private bool _fairOwed;
    /// <summary>When each area last had a turn on this account, kept across bots (each account's 30 minutes is a new bot, so the turn numbers start over).</summary>
    private Dictionary<string, DateTime> _lastPlayedUtc = [];
    private const string PlayedKey = "engine:played";
    private DateTime _resourcesAt = DateTime.MinValue;

    /// <summary>Quests as if read (autoplay --test-quest): to try an area's quest on the live game when no such quest is open.</summary>
    public IReadOnlyList<QuestNeed> StartNeeds { get; init; } = [];
    private string _engine = "starting", _detail = "";
    private string? _now;
    private DateTime _nowWritten;
    private DateTime? _next;
    private PlayContext? _ctx;
    private List<IActivity> _enabledOrder = [];
    private IActivity? _current;
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
    /// <summary>The input layer's message when Windows keeps the game behind another window (MouseInput.EnsureForeground).</summary>
    public const string NoFront = "Could not bring the game window to the front.";

    /// <summary>An area that failed this many turns in a row waits <see cref="AreaErrorHold"/>; the others go on.</summary>
    public const int AreaErrorsToHold = 2;

    public static readonly TimeSpan AreaErrorHold = TimeSpan.FromHours(1);

    private readonly Dictionary<string, int> _areaErrors = [];

    private string? _blockedSaid;

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
        ctx.Reported = () => { if (host.UtcNow - _nowWritten >= TimeSpan.FromSeconds(2) || _now is null) WriteStatus(); };
        ctx.RestoreHolds(host.RecentEvents());
        // What the areas knew when the last bot here stopped (Ehsan 2026-11-06: "track stuff, don't just check them": the arena's board and refresh, the
        // reward checks' times), so a rotation's new bot does not go and look again.
        var saved = host.LoadActivityState();
        foreach (var a in activities)
            if (saved.TryGetValue(a.Id, out var state))
                try { a.LoadState(state, host.UtcNow); }
                catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { }
        if (saved.TryGetValue(PlayedKey, out var played))
            try { _lastPlayedUtc = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(played) ?? []; }
            catch (System.Text.Json.JsonException) { }
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
                if (host.PausedSince() is not null)
                {
                    var held = await HoldWhilePaused(false, ct);
                    sessionEnd += held.Span;
                    if (held.Reason is not null) return held.Reason;
                    continue;
                }
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
                    while (host.UtcNow < until && !host.StopRequested() && host.LoadSettings().Enabled && host.PausedSince() is null) await Idle("resting", "taking a break, like a person", until, ct);
                    sessionEnd = host.UtcNow + Jitter(p.SessionMinutes);
                    continue;
                }
                if (host.InputBlocked() is { } blocked)
                {
                    // The account is open on another device (Ehsan 2026-11-09: "if it is logged in from another device you switch account to the next
                    // and come back after the next account"): with other accounts to play, this account's turn ends now instead of waiting.
                    if (!once && blocked.StartsWith(AnotherDevice, StringComparison.Ordinal) && host.CanRotate())
                        return Stop("rotation: " + blocked);
                    // Nothing can be clicked (Remote Desktop disconnected, PC locked): wait, look again every minute, carry on after. Logged once per reason
                    // (2026-11-10 16:05-16:53: the bot waited 48 minutes and the log said nothing).
                    if (blocked != _blockedSaid) Remember(new AutoplayEvent(now, "engine", "wait", "no input reaches the game: " + blocked));
                    _blockedSaid = blocked;
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
                _enabledOrder = enabled;
                ctx.ChainBreak = self => ChainBreak(ctx, enabled, self, once);
                ctx.YieldTo = self => YieldTo(ctx, enabled, self);
                _decisions = activities.ToDictionary(a => a.Id, a => !a.IsEnabled(settings) ? Decision.Off("switched off")
                    : ctx.Held(a.Id) is { } held ? Decision.Wait(held.Until, held.Why) : a.Decide(ctx));
                host.Acting(enabled.Where(a => _decisions[a.Id].Act).Select(a => a.Id).ToHashSet());
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
                // A fair turn taken by an urgent one is owed, not lost (2026-11-10: an arena refresh or full energy fell on every fourth turn and Doom
                // Tower still never came).
                if (_turnNumber % 4 == 3) _fairOwed = true;
                // Longest by the clock, across this account's bots (2026-11-10: with the turn numbers of one bot, the fair turns went to Clan Boss, Faction
                // Wars and Cursed City in every 30 minutes, and Doom Tower, after them in the order, never came).
                var waited = !stepAside && _fairOwed ? enabled.Where(a => _decisions[a.Id].Act).OrderBy(a => _lastPlayedUtc.GetValueOrDefault(a.Id)).FirstOrDefault() : null;
                if (waited is not null) act = waited;
                // An urgent one (a free arena refresh due with nobody to fight) before anything: what a chain of runs was broken for.
                if (!stepAside && enabled.FirstOrDefault(a => _decisions[a.Id].Act && a.Urgent(ctx)) is { } urgent) act = urgent;
                if (waited is not null && act == waited) _fairOwed = false;
                // Chores never hold back resources that sit (2026-11-20: 149 energy and a 28-energy run waiting 11 minutes while chores and arenas took every turn): an
                // area that wants to act with its resource full or urgent goes first, and Chores play at most once per run while another area has something to do.
                if (act?.Id == ChoresActivity.AreaId && !enabled.OfType<ChoresActivity>().Any(c => c.MasteryAsked))
                {
                    var spender = enabled.FirstOrDefault(a => a.Id != ChoresActivity.AreaId && _decisions[a.Id].Act && (a.Urgent(ctx) || a.Full(ctx)))
                        ?? (_choresPlayed ? enabled.FirstOrDefault(a => a.Id != ChoresActivity.AreaId && _decisions[a.Id].Act) : null);
                    if (spender is not null) act = spender;
                }
                if (act?.Id == ChoresActivity.AreaId) _choresPlayed = true;
                if (act is not null)
                {
                    _sameArea = act.Id == _lastArea ? _sameArea + 1 : 1;
                    _playedAt[act.Id] = ++_turnNumber;
                    _lastPlayedUtc[act.Id] = now;
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
                _now = null;
                _current = act;
                ctx.ClearTask(new BotTask(act.Name, _decisions[act.Id].Why, 1, 1, ""));
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
                    // Paused from the app: the click step finished, the next input refused; held here, then on with the plan (not an error).
                    if (host.PausedSince() is not null || e.Message.Contains(PauseGate.Refusal, StringComparison.Ordinal))
                    {
                        var held = await HoldWhilePaused(true, ct);
                        sessionEnd += held.Span;
                        if (held.Reason is not null) return held.Reason;
                        continue;
                    }
                    // Windows would not let the game come to the front (someone working at the PC, 2026-11-11 17:22: three of these stopped Auto Play
                    // for good): nothing is wrong with the area or the game, so it is not counted; the bot waits a minute and tries again.
                    // A battle running in the background (the game's background battles, e.g. Doom Tower's auto-climb, 2026-11-12 22:56): a Start shows
                    // its status window instead of a battle. Not an error: the area waits 15 minutes; the background battle is never stopped by the bot.
                    // Its rewards' list when it is over (BackgroundBattleRewardListOverlay, 2026-11-13 01:14): closed on the way home, the area again in 2 minutes.
                    if (e.Message.Contains("BackgroundBattle", StringComparison.Ordinal))
                    {
                        var over = e.Message.Contains("BackgroundBattleRewardListOverlay", StringComparison.Ordinal);
                        var wait = over ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15);
                        ctx.Hold(act.Id, host.UtcNow + wait, over ? "a background battle's rewards came up: closed, tried again in 2 minutes"
                            : "a background battle is running (the game's status window came up): tried again in 15 minutes");
                        Remember(new AutoplayEvent(host.UtcNow, act.Id, "wait", over ? "a background battle's rewards came up: tried again in 2 minutes"
                            : "a background battle is running: tried again in 15 minutes"));
                        try { await game.GoHomeAsync(ct); }
                        catch (Exception home) when (home is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            Remember(new AutoplayEvent(host.UtcNow, "engine", "error", "going home failed: " + home.Message));
                        }
                        continue;
                    }
                    if (e.Message.Contains(NoFront, StringComparison.Ordinal))
                    {
                        Remember(new AutoplayEvent(host.UtcNow, "engine", "wait", "the game could not come to the front: trying again in a minute"));
                        await Idle("waiting", "the game could not come to the front", host.UtcNow + TimeSpan.FromMinutes(1), ct, untilOnly: true);
                        continue;
                    }
                    errors++;
                    Remember(new AutoplayEvent(host.UtcNow, act.Id, "error", e.Message));
                    // The game dropped its data mid-turn (its AppModel gone): it was logged out and came back by itself, with no "another device" box
                    // (2026-11-10 15:38: Ehsan logged in on another device, the game here reloaded and the bot played on and took the account back). With
                    // other accounts to play, this one's turn ends and the next account is played (Ehsan: "if I logged in from another device you should
                    // move to the next account in rotation").
                    if (!once && GameReloaded(e.Message) && host.CanRotate())
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
            if (host.StopRequested() || !host.LoadSettings().Enabled || host.PausedSince() is not null) break;
            WriteStatus();
        }
    }

    /// <summary>
    /// Holds while the app's pause stands: no input, status "paused", every state kept. After it the host moves the slice clock and re-reads the game
    /// (<see cref="IAutoplayHost.PauseEnded"/>), the game goes home (the person may have left a screen open) and the turn goes on from its plan.
    /// </summary>
    private async Task<(string? Reason, TimeSpan Span)> HoldWhilePaused(bool refused, CancellationToken ct)
    {
        var began = host.UtcNow;
        var since = host.PausedSince() ?? began;
        if (host.PausedSince() is not null)
        {
            host.PauseBegan();
            Remember(new AutoplayEvent(began, "engine", "pause", $"Paused since {since.ToLocalTime():HH:mm}"));
        }
        while (host.PausedSince() is not null && !host.StopRequested() && host.LoadSettings().Enabled)
        {
            (_engine, _detail, _next) = ("paused", $"Paused since {since.ToLocalTime():HH:mm}", null);
            WriteStatus();
            await host.DelayAsync(TimeSpan.FromSeconds(1), ct);
        }
        var span = host.UtcNow - began;
        if (host.StopRequested() || !host.LoadSettings().Enabled) return (null, span); // the loop's top stops it
        Remember(new AutoplayEvent(host.UtcNow, "engine", "resume", $"Resumed after {span.TotalMinutes:0.#} min"));
        if (host.PauseEnded(span) is { } end) return (Stop(end), span);
        try { await game.GoHomeAsync(ct); }
        catch (Exception home) when (home is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Remember(new AutoplayEvent(host.UtcNow, "engine", "error", "going home after the pause failed: " + home.Message));
        }
        return (null, span);
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
        states[PlayedKey] = System.Text.Json.JsonSerializer.Serialize(_lastPlayedUtc);
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

    /// <summary>
    /// The step the bot is on, in its own words (each "STEP …" it logs; Ehsan 2026-11-18): shown as "now" on the app's page; written at most every
    /// 2 seconds so a burst of steps does not flood the status file.
    /// </summary>
    public void Now(string step)
    {
        var first = _now is null; // the turn's first step is always shown
        _now = step.Length > 220 ? step[..217] + "…" : step;
        if (!first && host.UtcNow - _nowWritten < TimeSpan.FromSeconds(2)) return;
        WriteStatus();
    }

    /// <summary>The area due next (the earliest waiting decision of another area) and when.</summary>
    private string? NextText()
    {
        if (_ctx is not { } ctx) return null;
        var current = _detail;
        var waiting = activities.Where(a => a.IsEnabled(ctx.Settings) && _decisions.TryGetValue(a.Id, out var d) && d.UntilUtc is not null && !current.StartsWith(a.Name + ":", StringComparison.Ordinal))
            .OrderBy(a => _decisions[a.Id].UntilUtc).FirstOrDefault();
        if (waiting is null) return null;
        var d0 = _decisions[waiting.Id];
        return $"{waiting.Name} at {d0.UntilUtc!.Value.ToLocalTime():HH:mm}: {d0.Why}";
    }

    /// <summary>The ordered plan after the current task: the areas due now in priority order, then the ones waiting by their time (the banner shows two).</summary>
    internal List<string>? NextTasks()
    {
        if (_ctx is null || _enabledOrder.Count == 0) return null;
        var rest = _enabledOrder.Where(a => a != (_engine == "playing" ? _current : null) && _decisions.ContainsKey(a.Id)).ToList();
        var due = rest.Where(a => _decisions[a.Id].Act).Select(a => $"{a.Name}: {_decisions[a.Id].Why}");
        var later = rest.Where(a => !_decisions[a.Id].Act && _decisions[a.Id].UntilUtc is not null).OrderBy(a => _decisions[a.Id].UntilUtc)
            .Select(a => $"{a.Name} at {_decisions[a.Id].UntilUtc!.Value.ToLocalTime():HH:mm}");
        return [.. due.Concat(later).Take(3)];
    }

    /// <summary>Why this turn exists and how far it is: tasks done this run, and about as many more as areas are due now.</summary>
    private TurnGoal? GoalNow()
    {
        if (host.BannerTurn() is not { } t) return null;
        var dueNow = _decisions.Values.Count(d => d.Act);
        return new TurnGoal(t.Who, Turns, Turns + dueNow);
    }

    private SwitchInfo? SwitchNow() => host.BannerTurn() is { To: { } to, EndsUtc: { } ends } ? new SwitchInfo(to, ends, true) : null;

    private void WriteStatus()
    {
        _nowWritten = host.UtcNow;
        var ctx = _ctx;
        var areas = ctx is null ? [] : activities.Select(a => a.Status(ctx, _decisions.GetValueOrDefault(a.Id) ?? Decision.Off(a.IsEnabled(ctx.Settings) ? "starting" : "switched off"))).ToList();
        var clocks = ctx is null ? [] : ctx.Clocks.Values.Select(c => c.Out()).OfType<ClockOut>().ToList();
        host.WriteStatus(new AutoplayStatus(host.UtcNow, host.ProcessId, dryRun, _engine, _detail, _next, clocks, areas, [.. _recent.TakeLast(20)])
            { Now = _engine == "playing" ? _now : null, Next = NextText(), Task = _engine == "playing" ? ctx?.Task : null, NextTasks = NextTasks(), Goal = GoalNow(), Switch = SwitchNow() });
    }

    private TimeSpan Jitter(int minutes) => TimeSpan.FromMinutes(Math.Max(0, minutes) * (0.75 + 0.5 * host.Random.NextDouble()));


    /// <summary>
    /// Faction Wars after Tag Team (Ehsan 2026-11-20: the daily "5 Tag Team Arena battles" quest pays the +6 crypt keys, so it comes first): while that quest is
    /// open and Tag Team can play now (tokens), Faction Wars waits. No tokens, or Tag Team off or held: Faction Wars plays.
    /// </summary>
    private static string? _yieldSaid;
    private bool _choresPlayed;
    private static string? YieldTo(PlayContext ctx, IReadOnlyList<IActivity> enabled, string self)
    {
        if (self != FactionWarsActivity.AreaId || ctx.QuestLeft(QuestNeedKind.TagArena) <= 0) return null;
        if (enabled.FirstOrDefault(a => a.Id == TagArenaActivity.AreaId) is not { } tag || ctx.Held(tag.Id) is not null) return null;
        if (tag.Decide(ctx) is not { Act: true } d) return null;
        // Wait only while Tag Team really fights this turn and has the tokens to finish the quest (2026-11-20: 3 tokens of 5, only a free refresh).
        if (tag is TagArenaActivity ta && !ta.CanFinishQuest(ctx, ctx.QuestLeft(QuestNeedKind.TagArena), out var why))
        {
            if (why != _yieldSaid) { ctx.Event(self, "order", $"Faction Wars plays now: {why}"); _yieldSaid = why; }
            return null;
        }
        return $"the Tag Team quest first (its reward is the crypt keys): {d.Why}";
    }

    /// <summary>
    /// What ends a chain of runs (Ehsan 2026-11-06: "the only time you go back to base is when something more critical is due, e.g. the Arena refresh"):
    /// an area that wants to act and is urgent (<see cref="IActivity.Urgent"/>) or at its cap (<see cref="IActivity.Full"/>), or the account's turn is over.
    /// </summary>
    private string? ChainBreak(PlayContext ctx, IReadOnlyList<IActivity> enabled, string self, bool once)
    {
        // Stop asked (2026-11-14 07:41: a Dragon's Lair chain started run after run while the app asked it to stop).
        if (host.StopRequested()) return "stop asked";
        if (host.PausedSince() is not null) return "paused";
        if (!once && host.RotateDue(null) is { } rotate) return "rotation: " + rotate;
        // Only an area the next turn would really give to (2026-11-06: arena tokens full broke every Campaign chain, and the next turn went to Energy again,
        // full too and first in the order): an urgent one, or a full one while this area is not full or comes after it.
        var me = enabled.FirstOrDefault(a => a.Id == self);
        var meFull = me?.Full(ctx) == true;
        // An urgent chain (energy piled far over its cap) is broken only by another urgent area, not by a full one.
        var meUrgent = me?.Urgent(ctx) == true;
        var myIndex = me is null ? int.MaxValue : enabled.ToList().IndexOf(me);
        foreach (var (a, i) in enabled.Select((a, i) => (a, i)).Where(x => x.a.Id != self))
        {
            if (ctx.Held(a.Id) is not null) continue;
            // Chores that played already in this run do not break a chain again (once per run; masteries asked from the app still do).
            if (a is ChoresActivity { MasteryAsked: false } && _choresPlayed && !a.Urgent(ctx)) continue;
            var d = a.Decide(ctx);
            if (d.Act && (a.Urgent(ctx) || !meUrgent && a.Full(ctx) && (!meFull || i < myIndex))) return $"{a.Name}: {d.Why}";
        }
        return null;
    }
}
