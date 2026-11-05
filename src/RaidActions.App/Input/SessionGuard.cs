namespace RaidActions.App.Input;

/// <summary>
/// A Remote Desktop that comes and goes does not break what the bot is doing (Ehsan 2026-11-06: "remote connection to this pc should not break the
/// behaviour of the bot"). Under Auto Play (<see cref="Handoff"/> set): the session is watched every few seconds and, the moment it is disconnected, handed
/// to the console (never while someone is connected); and an input that finds the session away waits for it to come back instead of failing, so a
/// battle, a switch or a rank-up goes on where it was. Without Auto Play (the gear commands) nothing waits: the reason is returned at once, as before.
/// </summary>
public static class SessionGuard
{
    /// <summary>Hands a disconnected session to the console; null when it ran, else why not. Set by Auto Play.</summary>
    public static Func<string?>? Handoff { get; set; }

    /// <summary>Logs what the guard did (the handoff, a wait). Set by Auto Play.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>How many times an input waited for the session (a caller tells a wait happened by it).</summary>
    public static int Waits { get; private set; }

    private static readonly object Gate = new();
    private static DateTime _lastHandoff = DateTime.MinValue;
    private static Timer? _watch;

    /// <summary>The session is checked every <paramref name="every"/> and handed to the console as soon as it is disconnected.</summary>
    public static void Watch(TimeSpan every) => _watch ??= new Timer(_ => { try { TryHandoff(); } catch (Exception e) { Log?.Invoke("session watch: " + e.Message); } }, null, every, every);

    /// <summary>The session is disconnected (Windows' WTSDisconnected): runs the handoff, at most once every 30 seconds. True when it ran now.</summary>
    public static bool TryHandoff()
    {
        if (Handoff is not { } handoff || MouseInput.SessionState() != 4) return false;
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastHandoff < TimeSpan.FromSeconds(30)) return false;
            _lastHandoff = DateTime.UtcNow;
        }
        var why = handoff();
        Log?.Invoke(why is null ? "the session was disconnected: handed to the console" : "the session was disconnected; the console handoff did not run: " + why);
        return why is null;
    }

    /// <summary>
    /// Null when the session can take input now or comes back within <paramref name="max"/> (handed to the console meanwhile when it is disconnected);
    /// else why not. Without Auto Play: the reason at once.
    /// </summary>
    public static string? WaitActive(TimeSpan max)
    {
        if (MouseInput.SessionProblem() is not { } problem) return null;
        if (Handoff is null) return problem;
        Waits++;
        Log?.Invoke("the session is away (" + Short(problem) + "): waiting for it before the next input");
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            TryHandoff();
            Thread.Sleep(1000);
            if (MouseInput.SessionProblem() is not { } still)
            {
                Thread.Sleep(3000); // the desktop settles after the move
                Log?.Invoke("the session is back: going on");
                return null;
            }
            problem = still;
        }
        return problem;
    }

    private static string Short(string problem) => problem.Length > 60 ? problem[..60] + "…" : problem;
}
