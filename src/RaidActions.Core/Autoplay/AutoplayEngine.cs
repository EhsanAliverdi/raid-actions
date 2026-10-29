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
    private string _engine = "starting", _detail = "";
    private DateTime? _next;
    private PlayContext? _ctx;
    private Dictionary<string, Decision> _decisions = [];

    /// <summary>Turns run (one battle, one refresh, ...).</summary>
    public int Turns { get; private set; }

    /// <param name="once">Stop when no activity wants to act now (instead of waiting): a dry run, a test, a single session.</param>
    /// <returns>Why the engine stopped.</returns>
    public async Task<string> RunAsync(bool once, CancellationToken ct)
    {
        var settings = host.LoadSettings();
        var ctx = _ctx = new PlayContext(settings, game, host, dryRun, Remember);
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
                    // Nothing can be clicked (Remote Desktop disconnected, PC locked): wait, look again every minute, carry on after.
                    await Idle("waiting", "no input reaches the game: " + blocked, now + TimeSpan.FromMinutes(1), ct, untilOnly: true);
                    continue;
                }
                if (host.PersonLastInputUtc() is { } person && now - person < TimeSpan.FromMinutes(Math.Max(0, p.YieldToPersonMinutes)))
                {
                    await Idle("yielding", "you are using the PC: hands off", person.AddMinutes(p.YieldToPersonMinutes), ct);
                    continue;
                }

                var enabled = activities.Where(a => a.IsEnabled(settings)).ToList();
                _decisions = activities.ToDictionary(a => a.Id, a => a.IsEnabled(settings) ? a.Decide(ctx) : Decision.Off("switched off"));
                if (enabled.Count == 0)
                {
                    if (once) return Stop("no area is switched on");
                    await Idle("waiting", "no area is switched on", null, ct);
                    continue;
                }
                var act = enabled.FirstOrDefault(a => _decisions[a.Id].Act);
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
                    var done = await act.RunTurnAsync(ctx, ct);
                    Turns++;
                    errors = 0;
                    if (done.Length > 0) Remember(new AutoplayEvent(host.UtcNow, act.Id, "turn", done));
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    errors++;
                    Remember(new AutoplayEvent(host.UtcNow, act.Id, "error", e.Message));
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

    private string Stop(string why, bool error = false)
    {
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
}
