using System.Runtime.InteropServices;
using System.Text.Json;
using RaidActions.App.Input;
using RaidActions.Autoplay;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Auto Play on this PC: real time, the files the app shares with the bot (raid-extractor's output/autoplay: settings.json read
/// before every decision, status.json written, events-&lt;day&gt;.jsonl appended, stop.request), and the person's own input
/// (GetLastInputInfo, minus the input this process sent).
/// </summary>
public sealed class LiveAutoplayHost(string dir, Action<string> print, bool watchPerson, Func<AutoplaySettings, AutoplaySettings>? overrideSettings = null) : IAutoplayHost
{
    /// <summary>A check of the game itself (LivePlayGame.SessionTaken): why it cannot be played now, or null.</summary>
    public Func<string?>? GameBlocked { get; set; }

    private static readonly JsonSerializerOptions OneLine = new(AutoplayJson.Options) { WriteIndented = false };

    public DateTime UtcNow => DateTime.UtcNow;
    public Random Random { get; } = new();
    public int ProcessId => Environment.ProcessId;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);

    public AutoplaySettings LoadSettings()
    {
        var s = AutoplaySettings.Load(dir);
        return overrideSettings is null ? s : overrideSettings(s);
    }

    public bool StopRequested() => File.Exists(AutoplayStatus.StopPathIn(dir));

    public DateTime? PersonLastInputUtc()
    {
        if (!watchPerson) return null;
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        var ago = TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        var at = DateTime.UtcNow - ago;
        // Input within 2 s of the bot's own is the bot's (a click, the keystroke that brings the game to the front).
        return at > MouseInput.LastSentUtc + TimeSpan.FromSeconds(2) ? at : null;
    }

    /// <summary>
    /// Why no click can reach the game now: the session is disconnected or locked, or the game window does not fit the screen (the
    /// window is then moved onto the screen, same size, and checked again: a phone in portrait is wide enough for the minimum window).
    /// </summary>
    public string? InputBlocked()
    {
        // The session first, with or without --no-yield (2026-11-04: with it this whole check was skipped, so a closed Remote Desktop was never handed to
        // the console and every click failed): no click can reach a disconnected or locked session, whoever uses the PC.
        if (MouseInput.SessionProblem() is { } session)
        {
            // Ehsan (2026-10-29): "always hand the session to the console; a remote connection should not stop Auto Play". Only for a
            // session that is already disconnected (never while he is connected): the on-demand task moves it to the PC's monitor.
            // The session guard hands it over (it also watches every 3 s, Ehsan 2026-11-06: "remote connection should not break the bot").
            if (SessionGuard.TryHandoff())
            {
                Thread.Sleep(5000);
                if (MouseInput.SessionProblem() is null) return null;
            }
            return session;
        }
        // The game drawing and its window on the screen are checked too: --no-yield only stops the bot waiting for a person (PersonLastInputUtc).
        if (_drawCheckAt is { } check && DateTime.UtcNow >= check)
        {
            // Every 10 minutes: the game can stop drawing later too (2026-11-02: black an hour after the handoff, and the bot played on by memory).
            _drawCheckAt = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            RestartIfBlank();
        }
        var (w, why) = Game.Window.GameWindow.Find();
        if (w is null) return why;
        if (GameBlocked?.Invoke() is { } game)
        {
            if (game == LivePlayGame.ConnectionLostText) RetryConnection();
            return game;
        }
        // After a Retry the game loads again and cannot be read for a while (2026-11-05 05:33: the box closed, the areas found nothing readable and the
        // bot restarted the game as frozen, a minute into the load): up to 5 minutes for it to load.
        if (_connectionTries > 0 && DateTime.UtcNow - _lastRetry < TimeSpan.FromMinutes(5) && GameReadable?.Invoke() == false)
            return "the game is loading again after Retry";
        _connectionTries = 0;
        if (w.Minimized || MouseInput.OffScreen(w) is null) return null;
        var (after, problem) = Game.Window.WindowFit.Apply(w, Game.Window.WindowFit.Plan(w, w.Client.Width, w.Client.Height));
        if (after is null) return "the game window is off the screen and could not be moved: " + problem;
        return MouseInput.OffScreen(after) is { } still ? still + " (moved it as far as it goes)" : null;
    }

    /// <summary>Presses the game's Retry on its connection error box (LivePlayGame.RetryConnection): null when pressed.</summary>
    public Func<string?>? RetryGame { get; set; }

    /// <summary>The game's UI can be read (LivePlayGame.UiReadable).</summary>
    public Func<bool>? GameReadable { get; set; }

    private DateTime _lastRetry = DateTime.MinValue;
    private int _connectionTries;

    /// <summary>
    /// The game cannot reach its server: Retry every 2 minutes, and after 6 tries (12 minutes) a restart of the game (2026-11-05: the box came back after
    /// every Retry; nothing was counted as an area's error, so no area is held for it).
    /// </summary>
    private void RetryConnection()
    {
        if (DateTime.UtcNow - _lastRetry < TimeSpan.FromMinutes(2)) return;
        _lastRetry = DateTime.UtcNow;
        if (++_connectionTries > 6)
        {
            Event(new AutoplayEvent(DateTime.UtcNow, "engine", "restart", "the game could not reach its server for 12 minutes: restarting it"));
            Relaunch(dir, "autoplay --restart-first" + (watchPerson ? "" : " --no-yield"));
        }
        var why = RetryGame?.Invoke();
        Event(new AutoplayEvent(DateTime.UtcNow, "engine", "connection", why is null
            ? $"the game cannot reach its server: Retry pressed (try {_connectionTries} of 6 before a restart)"
            : "the game cannot reach its server; Retry was not pressed: " + why));
    }

    /// <summary>
    /// The session guard for this bot (B-1/B-2, Ehsan 2026-11-06): the console handoff as soon as the Remote Desktop is disconnected (checked every 3 s),
    /// and inputs that wait for the session instead of failing. Each handoff is an event, and the game's drawing is checked 20 s later (it can go black).
    /// </summary>
    public void GuardSession()
    {
        SessionGuard.Handoff = () =>
        {
            var ran = RunHandoff();
            Event(new AutoplayEvent(DateTime.UtcNow, "engine", "handoff", ran is null
                ? "Remote Desktop disconnected: handed the session to the console so Auto Play keeps playing"
                : "Remote Desktop disconnected; the console handoff did not run: " + ran));
            if (ran is null) _drawCheckAt = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            return ran;
        };
        SessionGuard.Log = text => print($"{DateTime.Now:HH:mm:ss} session: {text}");
        // An account read is reused until the next input (a full read takes seconds; Auto Play asked for many with nothing sent between).
        RaidActions.Game.Memory.LiveGame.LastInputUtc = () => MouseInput.LastSentUtc;
        RaidActions.Game.Memory.LiveGame.SlowReadLog = text => print($"{DateTime.Now:HH:mm:ss} autoplay: {text}");
        SessionGuard.Watch(TimeSpan.FromSeconds(3));
    }

    /// <summary>When to look at the game after a handoff.</summary>
    private DateTime? _drawCheckAt = DateTime.UtcNow + TimeSpan.FromMinutes(2);

    /// <summary>
    /// The game can stop drawing when the session moves (2026-11-02: a black window, the memory still fine, so everything looked healthy). The bot is tied to the
    /// game's process, so a new bot restarts the game and plays on, and this one ends.
    /// </summary>
    private void RestartIfBlank()
    {
        // Black means blank with the game in front: behind another window (the app, 2026-11-02) a capture is blank too, and that is nothing.
        bool Black()
        {
            var (w, _) = Game.Window.GameWindow.Find();
            if (w is null || MouseInput.EnsureForeground(w) is not null) return false;
            Thread.Sleep(1500);
            (w, _) = Game.Window.GameWindow.Find();
            if (w is null) return false;
            var (capture, problem) = Game.Window.WindowCapture.Take(w);
            capture?.Dispose();
            return capture is null && problem == "The capture is blank.";
        }
        if (!Black()) return;
        Thread.Sleep(15000);
        if (!Black()) return;
        Event(new AutoplayEvent(DateTime.UtcNow, "engine", "restart", "the game went black after the session moved: restarting it and playing on"));
        Relaunch(dir);
    }

    /// <summary>
    /// Restarts the game and plays on: the bot is tied to the game's process, so a new bot (autoplay --restart-first) does it and this one ends.
    /// </summary>
    public static void Relaunch(string dir, string args = "autoplay --restart-first")
    {
        var exe = Environment.ProcessPath!;
        var log = Environment.GetEnvironmentVariable("AUTOPLAY_LOG") ?? Path.Combine(dir, AutoplaySettings.Folder, "bot.log");
        // Through "start", in its own process tree: a plain child died with this process (2026-11-02: the old bot left and no new one came).
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c start \"\" /b cmd /c \"ping -n 8 127.0.0.1 >nul & \"{exe}\" {args} >> \"{log}\" 2>&1\"")
            { UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
        Environment.Exit(0);
    }

    /// <summary>Runs the on-demand "Raid console handoff" task (raid-extractor scripts/disconnect-to-console.ps1); null when it ran.</summary>
    private static string? RunHandoff()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/run", "/tn", "Raid console handoff" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? null : $"schtasks exit {p.ExitCode}: {err.Trim()}";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return e.Message; }
    }

    public void WriteStatus(AutoplayStatus status)
    {
        try { status.Save(dir); }
        catch (IOException) { } // the app is reading it: the next write (a few seconds later) gets through
        catch (UnauthorizedAccessException) { }
    }

    public void Event(AutoplayEvent e)
    {
        print($"{e.Utc.ToLocalTime():HH:mm:ss} [{e.Area}] {e.Kind}: {e.Text}");
        try
        {
            var path = AutoplayStatus.EventsPathIn(dir, e.Utc.ToLocalTime());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(e, OneLine) + Environment.NewLine);
        }
        catch (IOException) { }
    }

    /// <summary>Today's events (to carry today's counters over a restart).</summary>
    public IReadOnlyList<AutoplayEvent> RecentEvents() => Recent(dir);

    private RaidExtractor.AccountTurn? _turn;

    /// <summary>
    /// The turn on the account in the game ends now? It starts once per account (turn.json, which the app shows); the app's "+20 min" is read back each
    /// time.
    /// </summary>
    private string StatePath => Path.Combine(dir, AutoplaySettings.Folder, "state.json");

    public IReadOnlyDictionary<string, string> LoadActivityState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatePath), AutoplayJson.Options) ?? [] : new Dictionary<string, string>();
        }
        catch (Exception e) when (e is IOException or JsonException) { return new Dictionary<string, string>(); }
    }

    public void SaveActivityState(IReadOnlyDictionary<string, string> states) => AutoplayJson.WriteAtomic(StatePath, states);

    public string? RotateDue(DateTime? nextUsefulUtc)
    {
        if (RaidExtractor.OutputLayout.GameId(dir) is not { } current) return null;
        var pool = RaidExtractor.AccountPool.Load(dir);
        if (!pool.Rotate) return null;
        var now = DateTime.UtcNow;
        // A new bot on the account whose turn ran out in the last two hours goes on with that turn, so it rotates at once (2026-11-05: a restart after the
        // game went black gave BlackLord65 a fresh 30 minutes after it had played an hour; Ehsan: "you keep going to the same account"). An older turn
        // (the bot was off) starts a new one.
        var kept = RaidExtractor.AccountTurn.Load(dir);
        _turn = _turn?.Id == current
            ? (kept is { } saved && saved.Id == current ? saved : _turn)
            : kept is { } last && last.Id == current && now - last.EndsUtc(pool.SliceOf(current)) is var over && over >= TimeSpan.Zero && over < TimeSpan.FromHours(2)
                ? last
                : RaidExtractor.AccountTurn.Begin(dir, current, pool.SliceOf(current), now);
        return pool.RotateDue(current, _turn, now, nextUsefulUtc);
    }

    public static List<AutoplayEvent> Today(string dir) => Day(dir, DateTime.Now);

    /// <summary>Yesterday's and today's events: for a day that does not start at local midnight (the Clan Boss's).</summary>
    public static List<AutoplayEvent> Recent(string dir) => [.. Day(dir, DateTime.Now.AddDays(-1)), .. Day(dir, DateTime.Now)];

    private static List<AutoplayEvent> Day(string dir, DateTime day)
    {
        var path = AutoplayStatus.EventsPathIn(dir, day);
        if (!File.Exists(path)) return [];
        var list = new List<AutoplayEvent>();
        foreach (var line in File.ReadLines(path))
        {
            try { if (JsonSerializer.Deserialize<AutoplayEvent>(line, AutoplayJson.Options) is { } e) list.Add(e); }
            catch (JsonException) { }
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>One bot at a time: Auto Play holds this while it runs; a second Auto Play and the commands that click refuse while it is held.</summary>
public static class BotLock
{
    private const string AutoplayName = @"Local\RaidActions.Autoplay";

    /// <summary>Auto Play's lock, or null when another Auto Play holds it.</summary>
    public static Mutex? TakeAutoplay()
    {
        var m = new Mutex(true, AutoplayName, out var created);
        if (created) return m;
        m.Dispose();
        return null;
    }

    public static bool AutoplayRunning()
    {
        if (!Mutex.TryOpenExisting(AutoplayName, out var m)) return false;
        m.Dispose();
        return true;
    }
}
