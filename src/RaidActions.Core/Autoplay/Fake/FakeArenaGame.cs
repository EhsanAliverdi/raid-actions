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
    /// <summary>Fights with the 4-affinity team (the quest).</summary>
    public int AffinityFights { get; private set; }

    public Task<BattleOutcome> FightArenaVariantAsync(long userId, TeamVariant variant, bool dryRun, CancellationToken ct)
    {
        if (!dryRun) AffinityFights++;
        return FightArenaOpponentAsync(userId, dryRun, ct);
    }
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

    /// <summary>The forecast by the enemy's type id (opponent i of the board has one hero, type 1000 + i); null: the fake cannot forecast and the board gives no teams.</summary>
    public Func<int, ArenaForecast>? ForecastOf { get; set; }

    public Task<ArenaForecast> ForecastArenaAsync(IReadOnlyList<ArenaEnemyHero> enemy, int runs, CancellationToken ct)
    {
        Calls.Add($"forecast {enemy[0].TypeId}");
        return Task.FromResult(ForecastOf?.Invoke(enemy[0].TypeId) ?? new ArenaForecast(null, 0, "this game cannot forecast"));
    }

    public IReadOnlyList<ArenaOpponentInfo> Board => _board;

    /// <summary>The account's other resources (an area's keys), as the live game reads them; null: not read.</summary>
    public Dictionary<string, double>? Resources { get; set; }

    public Task<GameResources> ReadResourcesAsync(CancellationToken ct)
    {
        Regen();
        var left = Tokens >= TokenCap ? (TimeSpan?)null : _tokenTick + TokenPeriod - now();
        return Task.FromResult(new GameResources(now(), Tokens, Energy, Gems, left, null, TokenCap, All: Resources));
    }

    /// <summary>The game's background battle in this fake (null: none). Collecting it ends it, like the game.</summary>
    public BackgroundBattleInfo? Background { get; set; }

    /// <summary>True: collecting leaves the report as it is (the game's last auto-climb report, 2026-11-22).</summary>
    public bool BackgroundSticks { get; set; }

    public Task<IReadOnlyList<BackgroundBattleInfo>> ReadBackgroundBattlesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BackgroundBattleInfo>>(Background is null ? [] : [Background]);

    public Task<string> CollectBackgroundBattleAsync(BackgroundBattleInfo battle, CancellationToken ct)
    {
        Calls.Add($"collect-background {battle.StageId}");
        if (BackgroundSticks) return Task.FromResult($"stage {battle.StageId}: its results are still waiting after DoomTower was opened");
        Background = null;
        return Task.FromResult($"stage {battle.StageId} collected");
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

    private ArenaBoard Read() => new(now(), [.. _board], FreeNow, FreeNow ? null : _lastRefresh + FreeRefreshEvery - now(),
        Teams: ForecastOf is null ? null : _board.Select((o, i) => (o, i)).ToDictionary(x => x.o.UserId, x => new List<ArenaEnemyHero> { new(1, 1000 + x.i, 50, 5) }));

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

    /// <summary>The banner's goal and switch (a test sets it); null: no rotation.</summary>
    public TurnView? Turn { get; set; }
    public TurnView? BannerTurn() => Turn;

    /// <summary>The time away after the turn (a test sets it); null: no rotation.</summary>
    public TimeSpan? Away { get; set; }
    public TimeSpan? AwayAfterTurn() => Away;

    /// <summary>Every status written while playing (the banner's fields over time).</summary>
    public List<AutoplayStatus> Playing { get; } = [];

    /// <summary>The account's tasks (a test sets a <see cref="MemoryTaskBook"/>); null: no store, as on a dry run host.</summary>
    public ITaskBook? Tasks { get; set; }

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

    /// <summary>The pause a test asks for (UTC); null: none. <see cref="PauseEndedFor"/> is what the resume re-read says.</summary>
    public DateTime? Paused { get; set; }
    public string? PauseEndedFor { get; set; }
    public List<TimeSpan> Resumes { get; } = [];
    public int PauseStarts { get; private set; }
    public DateTime? PausedSince() => Paused;
    public void PauseBegan() => PauseStarts++;
    public string? PauseEnded(TimeSpan pausedFor) { Resumes.Add(pausedFor); return PauseEndedFor; }
    public DateTime? PersonLastInputUtc() => Person;

    public void WriteStatus(AutoplayStatus status)
    {
        Last = status;
        StatusWrites++;
        if (status.Engine == "playing") Playing.Add(status);
    }

    public void Event(AutoplayEvent e) => Events.Add(e);

    /// <summary>The areas' saved state, kept between engines on this host (a rotation's restart).</summary>
    public Dictionary<string, string> States { get; } = [];
    public IReadOnlyDictionary<string, string> LoadActivityState() => new Dictionary<string, string>(States);
    public void SaveActivityState(IReadOnlyDictionary<string, string> states) { States.Clear(); foreach (var (k, v) in states) States[k] = v; }
}

/// <summary>Tasks in memory, for tests (the live bot uses raid-extractor's SQLite TaskStore).</summary>
public sealed class MemoryTaskBook : ITaskBook
{
    public Dictionary<string, TaskRow> All { get; } = [];
    public TaskRow Put(TaskRow task)
    {
        var old = All.GetValueOrDefault(task.Key);
        return All[task.Key] = old is null ? task : task with { Checkpoint = task.Checkpoint ?? old.Checkpoint, Why = task.Why ?? old.Why, Parent = task.Parent ?? old.Parent };
    }
    public TaskRow? Get(string key) => All.GetValueOrDefault(key);
    public bool Set(string key, TodoState state, string? why = null, string? checkpoint = null)
    {
        if (!All.TryGetValue(key, out var t)) return false;
        All[key] = t with { State = state, Why = why ?? t.Why, Checkpoint = checkpoint ?? t.Checkpoint };
        return true;
    }
    public IReadOnlyList<TaskRow> OpenTasks() => All.Values.Where(t => t.Open).ToList();
}
