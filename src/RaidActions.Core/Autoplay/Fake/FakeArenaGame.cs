using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay.Fake;

/// <summary>
/// A made-up Classic Arena for tests and for trying the Auto Play page without the game (RaidActions autoplay --fake): tokens that
/// regenerate, a board of 10 opponents, a free refresh every 15 minutes, inbox tokens, battles won against weaker teams. Time is the
/// host's (virtual in tests). Records every call so tests can check what was done.
/// </summary>
public sealed class FakeArenaGame(Func<DateTime> now, int seed = 7) : IPlayGame
{
    private readonly Random _random = new(seed);
    private DateTime _tokenTick = DateTime.MinValue;
    private DateTime _lastRefresh = DateTime.MinValue;
    private List<ArenaOpponentInfo> _board = [];

    public double Tokens { get; set; } = 10;
    public int TokenCap { get; set; } = 10;
    public TimeSpan TokenPeriod { get; set; } = TimeSpan.FromMinutes(12);
    public TimeSpan FreeRefreshEvery { get; set; } = TimeSpan.FromMinutes(15);
    public int InboxTokens { get; set; }
    public double Gems { get; set; } = 500;
    public double Energy { get; set; } = 100;

    /// <summary>Our team's power: opponents below it are beaten.</summary>
    public int OurPower { get; set; } = 120_000;

    /// <summary>Opponents' power range on a new board.</summary>
    public (int Min, int Max) Powers { get; set; } = (40_000, 200_000);

    /// <summary>How long a battle takes (time passes on the host while the fake "fights").</summary>
    public TimeSpan BattleTime { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>Makes the next call throw (to test the engine's error handling); cleared after it fires.</summary>
    public string? FailNext { get; set; }

    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    public List<string> Calls { get; } = [];
    public bool AtArena { get; private set; }

    public IReadOnlyList<ArenaOpponentInfo> Board => _board;

    public Task<GameResources> ReadResourcesAsync(CancellationToken ct)
    {
        Regen();
        var left = Tokens >= TokenCap ? (TimeSpan?)null : _tokenTick + TokenPeriod - now();
        return Task.FromResult(new GameResources(now(), Tokens, Energy, Gems, left, null, TokenCap));
    }

    public Task GoHomeAsync(CancellationToken ct)
    {
        Calls.Add("home");
        AtArena = false;
        return Task.CompletedTask;
    }

    public Task<ArenaBoard> OpenClassicArenaAsync(CancellationToken ct)
    {
        Fail();
        Calls.Add("open-arena");
        AtArena = true;
        if (_board.Count == 0) NewBoard();
        return Task.FromResult(Read());
    }

    public async Task<BattleOutcome> FightArenaOpponentAsync(long userId, bool dryRun, CancellationToken ct)
    {
        Fail();
        if (!AtArena) throw new PlayStopException("not on the arena board");
        var o = _board.FirstOrDefault(x => x.UserId == userId) ?? throw new PlayStopException($"opponent {userId} not on the board");
        if (o.Status != OpponentStatus.None) throw new PlayStopException($"opponent {userId} was fought already");
        Regen();
        if (Tokens < 1) throw new PlayStopException("no token (the game would offer to buy one: refused)");
        if (dryRun)
        {
            Calls.Add($"show-fight {userId}");
            return new BattleOutcome(BattleResult.NotStarted, "dry run: Start shown, not pressed");
        }
        Calls.Add($"fight {userId}");
        if (Tokens >= TokenCap) _tokenTick = now(); // regeneration starts when below the cap
        Tokens -= 1;
        if (Delay is { } d) await d(BattleTime, ct);
        var won = o.Power < OurPower;
        _board[_board.IndexOf(o)] = o with { Status = won ? OpponentStatus.Defeated : OpponentStatus.Won };
        return new BattleOutcome(won ? BattleResult.Victory : BattleResult.Defeat, $"{BattleTime.TotalSeconds:0} s on auto");
    }

    public Task<ArenaBoard?> RefreshArenaFreeAsync(bool dryRun, CancellationToken ct)
    {
        Fail();
        if (!FreeNow) throw new PlayStopException("the free refresh is not available (a paid refresh is never used)");
        if (dryRun)
        {
            Calls.Add("show-refresh");
            return Task.FromResult<ArenaBoard?>(null);
        }
        Calls.Add("refresh");
        NewBoard();
        return Task.FromResult<ArenaBoard?>(Read());
    }

    public Task<int> CollectInboxTokensAsync(bool dryRun, CancellationToken ct)
    {
        Fail();
        Calls.Add(dryRun ? "show-inbox" : "inbox");
        AtArena = false;
        var got = InboxTokens;
        if (dryRun) return Task.FromResult(0);
        if (Tokens + got >= TokenCap && Tokens < TokenCap) _tokenTick = now();
        Tokens += got;
        InboxTokens = 0;
        return Task.FromResult(got);
    }

    private bool FreeNow => now() - _lastRefresh >= FreeRefreshEvery;

    private ArenaBoard Read() => new(now(), [.. _board], FreeNow, FreeNow ? null : _lastRefresh + FreeRefreshEvery - now());

    private void NewBoard()
    {
        _lastRefresh = now();
        _board = [.. Enumerable.Range(0, 10).Select(i => new ArenaOpponentInfo(_random.NextInt64(1_000_000, 9_999_999), $"Player{i}",
            _random.Next(Powers.Min, Powers.Max), OpponentStatus.None, true))];
    }

    private void Regen()
    {
        if (Tokens >= TokenCap) return;
        if (_tokenTick == DateTime.MinValue) _tokenTick = now();
        while (Tokens < TokenCap && now() >= _tokenTick + TokenPeriod)
        {
            _tokenTick += TokenPeriod;
            Tokens++;
        }
    }

    private void Fail()
    {
        if (FailNext is not { } why) return;
        FailNext = null;
        throw new PlayStopException(why);
    }
}

/// <summary>A host on virtual time (tests): delays move the clock forward at once; settings, stop and the person are set by the test.</summary>
public sealed class FakeHost(DateTime start) : IAutoplayHost
{
    public DateTime UtcNow { get; set; } = start;
    public Random Random { get; } = new(3);
    public int ProcessId => Environment.ProcessId;
    public AutoplaySettings Settings { get; set; } = new();
    public bool Stop { get; set; }
    public DateTime? Person { get; set; }
    public List<AutoplayEvent> Events { get; } = [];
    public AutoplayStatus? Last { get; private set; }
    public int StatusWrites { get; private set; }

    /// <summary>Runs on every delay (a test changing settings at a given time, stopping after N hours, ...).</summary>
    public Action<FakeHost>? OnDelay { get; set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UtcNow += delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        OnDelay?.Invoke(this);
        return Task.CompletedTask;
    }

    public AutoplaySettings LoadSettings() => Settings;
    public bool StopRequested() => Stop;
    public DateTime? PersonLastInputUtc() => Person;

    public void WriteStatus(AutoplayStatus status)
    {
        Last = status;
        StatusWrites++;
    }

    public void Event(AutoplayEvent e) => Events.Add(e);
}
