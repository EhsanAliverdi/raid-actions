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
        if (!watchPerson) return null;
        if (MouseInput.SessionProblem() is { } session) return session;
        var (w, why) = Game.Window.GameWindow.Find();
        if (w is null) return why;
        if (w.Minimized || MouseInput.OffScreen(w) is null) return null;
        var (after, problem) = Game.Window.WindowFit.Apply(w, Game.Window.WindowFit.Plan(w, w.Client.Width, w.Client.Height));
        if (after is null) return "the game window is off the screen and could not be moved: " + problem;
        return MouseInput.OffScreen(after) is { } still ? still + " (moved it as far as it goes)" : null;
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
    public static List<AutoplayEvent> Today(string dir)
    {
        var path = AutoplayStatus.EventsPathIn(dir, DateTime.Now);
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
