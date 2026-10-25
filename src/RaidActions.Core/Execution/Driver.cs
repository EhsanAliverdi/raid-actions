using RaidExtractor;

namespace RaidActions.Execution;

/// <summary>The outcome of one action. A failed action stops the run; it is never retried.</summary>
public sealed record DriverResult(bool Ok, string? Message = null)
{
    public static DriverResult Success(string? message = null) => new(true, message);
    public static DriverResult Failure(string message) => new(false, message);
}

/// <summary>
/// Performs the UI actions of a run. The game driver (RaidActions.exe only) sends mouse/keyboard input; the fake driver sends none.
/// Each action is done once: on anything unexpected the driver returns a failure instead of trying again.
/// </summary>
public interface IGameDriver
{
    /// <summary>"fake" or "game".</summary>
    string Name { get; }

    /// <summary>Whether this driver sends input to the game.</summary>
    bool SendsInput { get; }

    Task<DriverResult> PerformAsync(UiAction action, CancellationToken ct);

    /// <summary>Saves what the game shows now (on a failure). Returns the file, or null when there is nothing to capture.</summary>
    string? SaveScreenshot(string folder, string name);
}

/// <summary>A fresh read of the account: RaidExtractor.Core against the game, or the fake game.</summary>
public interface IAccountSource
{
    Task<SavedExport> ReadAsync(CancellationToken ct);
}

/// <summary>Human-like pacing between inputs.</summary>
public interface IPacer
{
    Task DelayAsync(UiAction next, CancellationToken ct);
}

public sealed class NoPacer : IPacer
{
    public static NoPacer Instance { get; } = new();
    public Task DelayAsync(UiAction next, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A random pause in [min, max] before every input action (not before reads or the snapshot).</summary>
public sealed class RandomPacer(TimeSpan min, TimeSpan max, Random? random = null) : IPacer
{
    private readonly Random _random = random ?? Random.Shared;

    public Task DelayAsync(UiAction next, CancellationToken ct)
    {
        if (next.Kind is ActionKind.SaveSnapshot or ActionKind.VerifyStep or ActionKind.VerifyEnd) return Task.CompletedTask;
        var ms = min.TotalMilliseconds + _random.NextDouble() * (max - min).TotalMilliseconds;
        return Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
    }
}

/// <summary>Pause between actions: <see cref="WaitAsync"/> holds while paused. Abort (the token) always wins.</summary>
public sealed class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _open = Open();

    private static TaskCompletionSource Open()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public bool IsPaused { get { lock (_lock) return !_open.Task.IsCompleted; } }

    public void Pause()
    {
        lock (_lock) if (_open.Task.IsCompleted) _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        lock (_lock) _open.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task t;
        lock (_lock) t = _open.Task;
        return t.WaitAsync(ct);
    }
}

/// <summary>A driver for checks only: it refuses every action (used when nothing may run, e.g. validate against the live game).</summary>
public sealed class NoInputDriver : IGameDriver
{
    public string Name => "none";
    public bool SendsInput => false;
    public Task<DriverResult> PerformAsync(UiAction action, CancellationToken ct) => Task.FromResult(DriverResult.Failure("No executor: this session only checks."));
    public string? SaveScreenshot(string folder, string name) => null;
}
