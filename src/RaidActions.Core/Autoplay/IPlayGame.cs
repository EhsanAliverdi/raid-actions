using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>
/// What an activity may ask of the game, in game terms. The live game does it with clicks (raid-actions App/Play: every click on an
/// allow-list, checked before and after); tests use <see cref="Fake.FakeArenaGame"/>. A method that cannot do what it says throws
/// <see cref="PlayStopException"/> (the engine sends the game home and backs off); a dry run never presses anything that spends a
/// resource or changes the account (it shows the click instead).
/// </summary>
public interface IPlayGame
{
    /// <summary>The amounts that regenerate (and gems, never spent): read from the account, timers from the HUD when it shows them.</summary>
    Task<GameResources> ReadResourcesAsync(CancellationToken ct);

    /// <summary>Back to the village with back/close elements only (waits out a running battle).</summary>
    Task GoHomeAsync(CancellationToken ct);

    /// <summary>The Classic Arena's battle tab, opened from wherever the game is (village → Battle → Arena → Classic), and its board.</summary>
    Task<ArenaBoard> OpenClassicArenaAsync(CancellationToken ct);

    /// <summary>
    /// Fights <paramref name="userId"/> from the open board: its Battle button, the team selection's Start (the team the game has set),
    /// auto on, the battle watched to its end, the result read and left (back on the board). A dry run stops before Start (shown, not
    /// pressed) and returns <see cref="BattleResult.NotStarted"/>.
    /// </summary>
    Task<BattleOutcome> FightArenaOpponentAsync(long userId, bool dryRun, CancellationToken ct);

    /// <summary>The board's free refresh (never a paid one): false when the game does not offer it free now. Returns the new board.</summary>
    Task<ArenaBoard?> RefreshArenaFreeAsync(bool dryRun, CancellationToken ct);

    /// <summary>Collects the arena tokens waiting in the inbox; returns how many tokens came (0: none there). Leaves the inbox closed.</summary>
    Task<int> CollectInboxTokensAsync(bool dryRun, CancellationToken ct);
}

/// <param name="TokenTimer">Time to the next arena token, when the game shows it.</param>
/// <param name="TokenCap">The regeneration cap, when the game shows it ("7/10").</param>
public sealed record GameResources(DateTime Utc, double Tokens, double Energy, double Gems, TimeSpan? TokenTimer = null, TimeSpan? EnergyTimer = null,
    int? TokenCap = null, int? EnergyCap = null);

/// <param name="FreeRefreshIn">Time to the next free refresh, when the game shows it.</param>
public sealed record ArenaBoard(DateTime Utc, List<ArenaOpponentInfo> Opponents, bool FreeRefreshAvailable, TimeSpan? FreeRefreshIn);

public enum BattleResult { Victory, Defeat, Draw, Unknown, NotStarted }

/// <param name="Text">What happened, for the event log (opponent, power, result, how long).</param>
public sealed record BattleOutcome(BattleResult Result, string Text);

/// <summary>The game is not where it should be, or a check failed: nothing more is clicked in this turn.</summary>
public sealed class PlayStopException(string message) : Exception(message);
