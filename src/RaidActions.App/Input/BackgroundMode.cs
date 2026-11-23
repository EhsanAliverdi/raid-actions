namespace RaidActions.App.Input;

/// <summary>The kinds of input Auto Play sends, for the background-mode routing.</summary>
public enum Gesture { Click, Drag, Wheel, Escape }

/// <summary>
/// Background clicks (plan/background-play.md): the switch and the routing decision. OFF by default. On: clicks, drags, wheel turns and
/// Esc go to the game window as posted messages (see <see cref="BackgroundInput"/>), the game is never brought to the front, and Auto Play
/// does not yield to the person (only the short mouse-still wait before each flick of the cursor).
/// </summary>
public static class BackgroundMode
{
    public const string Flag = "--background-clicks";
    public const string EnvOn = "RAID_BACKGROUND_CLICKS";
    /// <summary>Comma list of gestures (click, drag, wheel, escape) to keep on today's foreground input even in background mode (a gesture that does not work live).</summary>
    public const string EnvFallback = "RAID_BACKGROUND_FALLBACK";
    /// <summary>What a background gesture returns when it cannot be done safely there: the caller then uses today's input for it.</summary>
    public const string FallbackSignal = "background-fallback";

    private static bool? _forced;

    /// <summary>Where messages about the mode go (the bot's log); null: nowhere.</summary>
    public static Action<string>? Log { get; set; }

    public static bool Enabled => _forced ?? ParseEnabled(Environment.GetEnvironmentVariable(EnvOn));

    /// <summary>Reads the CLI flag; when set it also sets the env var, so a relaunched bot (restart, account switch) keeps the mode.</summary>
    public static void Configure(IEnumerable<string> args)
    {
        if (!args.Contains(Flag, StringComparer.OrdinalIgnoreCase)) return;
        _forced = true;
        Environment.SetEnvironmentVariable(EnvOn, "1");
    }

    public static void ForTest(bool? enabled) => _forced = enabled;

    public static bool ParseEnabled(string? value)
    {
        var v = value?.Trim();
        return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether this gesture goes the background way: the mode is on and the gesture is not in the fallback list.</summary>
    public static bool UseBackground(Gesture g, bool enabled, string? fallbackList)
    {
        if (!enabled) return false;
        if (string.IsNullOrWhiteSpace(fallbackList)) return true;
        var off = fallbackList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return !off.Contains(g.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool UseBackground(Gesture g) => UseBackground(g, Enabled, Environment.GetEnvironmentVariable(EnvFallback));

    /// <summary>The result of a background gesture means "do it the foreground way".</summary>
    public static bool IsFallback(string? result) => result == FallbackSignal;

    private static readonly HashSet<Gesture> _said = [];

    /// <summary>Logs, once per gesture kind, that it used the normal input.</summary>
    public static void SayFallback(Gesture g, string why)
    {
        lock (_said) { if (!_said.Add(g)) return; }
        Log?.Invoke($"background clicks: {g} uses the normal (foreground) input: {why}");
    }
}
