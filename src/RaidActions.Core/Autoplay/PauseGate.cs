namespace RaidActions.Autoplay;

/// <summary>
/// Pause (Ehsan 2026-11-20: "pause and resume buttons; useful when I need the game for a moment"): the app writes <c>autoplay/pause.request</c>; the
/// bot sends no more input after the click step it is on (every input call asks <see cref="Check"/> first), holds, and on resume re-reads the game.
/// </summary>
public static class PauseGate
{
    /// <summary>The message of an input refused because the bot is paused (nothing was sent); the engine recognises it in the step's error.</summary>
    public const string Refusal = "Paused from the app: nothing sent.";

    /// <summary>The ends of a run for the account changes of a pause (the command restarts the bot or switches the account).</summary>
    public const string NowOn = "pause: the game is on another account of the pool; it plays on that one";
    public const string SwitchBack = "pause: the game is on an account the bot does not play; switching back to its own";
    public const string GameClosed = "pause: the game was closed during the pause; starting it again";

    public static string PathIn(string accountDir) => Path.Combine(accountDir, "autoplay", "pause.request");

    /// <summary>Set by the live host: whether a pause is asked now.</summary>
    public static Func<bool>? IsPaused { get; set; }

    /// <summary>Null when input may go to the game, else why not (the caller sends nothing).</summary>
    public static string? Check() => IsPaused?.Invoke() == true ? Refusal : null;
}
