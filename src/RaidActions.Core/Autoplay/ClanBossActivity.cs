using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Clan Boss needs from the account: keys now, the best damage per difficulty, the highest Quick Battle difficulty.</summary>
public sealed record ClanBossAccount(double Keys, IReadOnlyDictionary<string, long> BestDamage, string? MaxQuick);

/// <summary>A difficulty's standing today, as the game shows it.</summary>
/// <param name="Damage">My damage on it today.</param>
/// <param name="KeysSpent">My keys on it today.</param>
/// <param name="TopChest">The damage of its top chest; null when it could not be read.</param>
public sealed record ClanStanding(long Damage, int KeysSpent, long? TopChest)
{
    public bool Full => TopChest is { } t && Damage > t;
}

/// <summary>What Clan Boss asks of the game (the live game: village → Battle → Clan Boss → Demon Lord → difficulty → team → Start).</summary>
public interface IClanBossGame
{
    Task<ClanBossAccount> ReadClanBossAsync(CancellationToken ct);

    /// <summary>One key spent on the difficulty with the team the game shows (Quick Battle when asked); a dry run shows Start only.</summary>
    Task<BattleOutcome> FightClanBossAsync(string difficulty, bool quick, bool dryRun, CancellationToken ct);

    /// <summary>The difficulty's standing today (its list and chest list opened, nothing spent); null when it cannot be read.</summary>
    Task<ClanStanding?> ReadClanStandingAsync(string difficulty, CancellationToken ct) => Task.FromResult<ClanStanding?>(null);

    /// <summary>The Hydra's keys and best damage (the same alliance map).</summary>
    Task<ClanBossAccount> ReadHydraAsync(CancellationToken ct);

    /// <summary>One Hydra key spent on the difficulty with the team the game shows; a dry run shows Start only.</summary>
    Task<BattleOutcome> FightHydraAsync(string difficulty, bool dryRun, CancellationToken ct);
}

/// <summary>
/// Hydra (Auto Play, plan/clan-boss.md): every Hydra key above the ones kept goes to one difficulty (the setting, else the best damage
/// so far); off unless switched on. Keys come weekly, so it checks again every half hour; a turn is one battle.
/// </summary>
public sealed class HydraActivity(IClanBossGame game) : IActivity
{
    public const string AreaId = "hydra";
    public string Id => AreaId;
    public string Name => "Hydra";

    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(30);
    private ClanBossAccount? _account;
    private DateTime _readAt = DateTime.MinValue;
    private bool _dryShown;
    private int _battles;
    private string _last = "";

    public bool IsEnabled(AutoplaySettings settings) => settings.Hydra.Enabled;

    private bool HasKey(PlayContext ctx) => _account is { } a && Math.Floor(a.Keys) >= 1 + Math.Max(0, ctx.Settings.Hydra.KeepKeys);

    /// <summary>A battle that could not start (no team saved, ...): not tried again before this (2026-10-31: it looped and held the areas after it).</summary>
    private DateTime _retryAt = DateTime.MinValue;

    public Decision Decide(PlayContext ctx)
    {
        if (_dryShown) return Decision.Off("shown in this dry run");
        if (ctx.Utc < _retryAt) return Decision.Wait(_retryAt, _last.Length > 0 ? _last : "could not start: tried again later");
        if (_account is null || ctx.Utc - _readAt >= Recheck) return Decision.Now("reading the keys");
        if (HasKey(ctx)) return Decision.Now($"{Math.Floor(_account.Keys)} key(s): {HydraRules.Pick(ctx.Settings.Hydra, _account.BestDamage)}");
        return Decision.Wait(_readAt + Recheck, $"no key to spend ({_account.Keys:0.##}, keeping {ctx.Settings.Hydra.KeepKeys})");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        _account = await game.ReadHydraAsync(ct);
        _readAt = ctx.Utc;
        if (!HasKey(ctx)) return "";
        var difficulty = HydraRules.Pick(ctx.Settings.Hydra, _account.BestDamage);
        await ctx.ThinkAsync(ct);
        var outcome = await game.FightHydraAsync(difficulty, ctx.DryRun, ct);
        if (outcome.Result is BattleResult.NotStarted)
        {
            _dryShown = ctx.DryRun;
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{difficulty}: {outcome.Text}");
            _retryAt = ctx.Utc + TimeSpan.FromHours(6);
            _last = $"{difficulty}: {outcome.Text}";
            return "";
        }
        _battles++;
        _last = $"{difficulty}: {outcome.Text}";
        ctx.Event(AreaId, "battle", _last);
        _account = await game.ReadHydraAsync(ct);
        _readAt = ctx.Utc;
        return _last;
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.Hydra;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles };
        if (_account is { } a) counters["Keys"] = (int)Math.Floor(a.Keys);
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next check"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, _last.Length > 0 ? $"{decision.Why} · last: {_last}" : decision.Why, decision.UntilUtc, counters, timers);
    }
}

/// <summary>
/// Clan Boss (Auto Play, plan/clan-boss.md): every key above the ones kept goes to one difficulty (the setting, else the best damage so
/// far). Keys regenerate slowly, so it checks again every half hour; a turn is one battle.
/// </summary>
public sealed class ClanBossActivity(IClanBossGame game) : IActivity
{
    public const string AreaId = "clan-boss";
    /// <summary>Today's battles per difficulty (the key plan's counts), restored from today's events.</summary>
    private readonly Dictionary<string, int> _today = [];
    private readonly HashSet<string> _topChest = [];
    private DateOnly? _day;

    private void NewDay(PlayContext ctx)
    {
        var d = ClanBossRules.Day(ctx.Utc);
        if (_day == d) return;
        if (_day is not null)
        {
            _today.Clear();
            _topChest.Clear();
        }
        _day = d;
    }
    public string Id => AreaId;
    public string Name => "Clan Boss";

    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(30);
    private ClanBossAccount? _account;
    private DateTime _readAt = DateTime.MinValue;
    private DateTime _retryAt = DateTime.MinValue;
    private bool _dryShown;
    private int _battles;
    private string _last = "";

    public bool IsEnabled(AutoplaySettings settings) => settings.ClanBoss.Enabled;

    public void Restore(IEnumerable<AutoplayEvent> events, DateTime utc)
    {
        var day = ClanBossRules.Day(utc);
        _day = day;
        foreach (var e in events.Where(e => e.Area == AreaId && e.Kind == "battle" && ClanBossRules.Day(e.Utc) == day))
        {
            _battles++;
            var d = e.Text.Split(':', ' ')[0];
            _today[d] = _today.GetValueOrDefault(d) + 1;
        }
    }

    private bool HasKey(PlayContext ctx) => _account is { } a && Math.Floor(a.Keys) >= 1 + Math.Max(0, ctx.Settings.ClanBoss.KeepKeys);

    public Decision Decide(PlayContext ctx)
    {
        NewDay(ctx);
        if (_dryShown) return Decision.Off("shown in this dry run");
        if (ctx.Utc < _retryAt) return Decision.Wait(_retryAt, "could not start: tried again later");
        if (_account is not null && ClanBossRules.Next(ctx.Settings.ClanBoss, _account.BestDamage, _today, _topChest) is null)
            return Decision.Wait(ClanBossRules.NextReset(ctx.Utc), "the key plan is done for today");
        if (_account is null || ctx.Utc - _readAt >= Recheck) return Decision.Now("reading the keys");
        if (HasKey(ctx)) return Decision.Now($"{Math.Floor(_account.Keys)} key(s): {ClanBossRules.Next(ctx.Settings.ClanBoss, _account.BestDamage, _today, _topChest)}");
        return Decision.Wait(_readAt + Recheck, $"no key to spend ({_account.Keys:0.##}, keeping {ctx.Settings.ClanBoss.KeepKeys})");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        _account = await game.ReadClanBossAsync(ct);
        _readAt = ctx.Utc;
        NewDay(ctx);
        if (!HasKey(ctx)) return "";
        var s = ctx.Settings.ClanBoss;
        if (ClanBossRules.Next(s, _account.BestDamage, _today, _topChest) is not { } difficulty) return "";
        // The game's own numbers before any key (Ehsan 2026-11-01: a key past the top chest is a key thrown away): its keys spent replace the bot's
        // count, and a difficulty past its top chest gets no more.
        if (await game.ReadClanStandingAsync(difficulty, ct) is { } standing)
        {
            _today[difficulty] = Math.Max(_today.GetValueOrDefault(difficulty), standing.KeysSpent);
            ctx.Event(AreaId, "standing", $"{difficulty}: {standing.Damage:N0} damage today, top chest {standing.TopChest?.ToString("N0") ?? "not read"}, {standing.KeysSpent} key(s)");
            if (standing.Full)
            {
                _topChest.Add(difficulty);
                return $"{difficulty}: past its top chest, no more keys there today";
            }
            if (ClanBossRules.Next(s, _account.BestDamage, _today, _topChest) != difficulty) return "";
        }
        var quick = ClanBossRules.Quick(s, difficulty, _account.MaxQuick);
        await ctx.ThinkAsync(ct);
        var keys = Math.Floor(_account.Keys);
        BattleOutcome outcome;
        try { outcome = await game.FightClanBossAsync(difficulty, quick, ctx.DryRun, ct); }
        catch (PlayStopException e) when (!ctx.DryRun)
        {
            // Something went wrong after Start can still have spent the key (2026-11-01: a result screen the bot did not know, the key went
            // uncounted and Brutal got a second one). Counted when the key is gone, and when the keys cannot be read: a row may under-spend, never over-spend.
            ClanBossAccount? now = null;
            try { now = await game.ReadClanBossAsync(ct); }
            catch (PlayStopException) { }
            if (now is null || Math.Floor(now.Keys) < keys)
            {
                _battles++;
                _today[difficulty] = _today.GetValueOrDefault(difficulty) + 1;
                ctx.Event(AreaId, "battle", $"{difficulty}: the key is gone, the battle's end was not read: {e.Message}");
            }
            if (now is not null) _account = now;
            throw;
        }
        if (outcome.Result is BattleResult.NotStarted)
        {
            _dryShown = ctx.DryRun;
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{difficulty}: {outcome.Text}");
            _retryAt = ctx.Utc + TimeSpan.FromHours(1); // not at once: it would hold the areas after it
            return "";
        }
        _battles++;
        _today[difficulty] = _today.GetValueOrDefault(difficulty) + 1;
        _last = $"{difficulty}{(quick ? " (Quick Battle)" : "")}: {outcome.Text}";
        ctx.Event(AreaId, "battle", _last);
        _account = await game.ReadClanBossAsync(ct);
        _readAt = ctx.Utc;
        return _last;
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.ClanBoss;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles };
        if (_account is { } a) counters["Keys"] = (int)Math.Floor(a.Keys);
        foreach (var (d, n) in _today) counters[$"{d} today"] = n;
        foreach (var d in _topChest) counters[$"{d} top chest"] = 1;
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next check"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, _last.Length > 0 ? $"{decision.Why} · last: {_last}" : decision.Why, decision.UntilUtc, counters, timers);
    }
}
