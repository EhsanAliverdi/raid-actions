using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Clan Boss needs from the account: keys now, the best damage per difficulty, the highest Quick Battle difficulty.</summary>
public sealed record ClanBossAccount(double Keys, IReadOnlyDictionary<string, long> BestDamage, string? MaxQuick);

/// <summary>What Clan Boss asks of the game (the live game: village → Battle → Clan Boss → Demon Lord → difficulty → team → Start).</summary>
public interface IClanBossGame
{
    Task<ClanBossAccount> ReadClanBossAsync(CancellationToken ct);

    /// <summary>One key spent on the difficulty with the team the game shows (Quick Battle when asked); a dry run shows Start only.</summary>
    Task<BattleOutcome> FightClanBossAsync(string difficulty, bool quick, bool dryRun, CancellationToken ct);

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

    public Decision Decide(PlayContext ctx)
    {
        if (_dryShown) return Decision.Off("shown in this dry run");
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
    private DateOnly? _day;

    private void NewDay(PlayContext ctx)
    {
        var d = DateOnly.FromDateTime(ctx.Local);
        if (_day == d) return;
        if (_day is not null) _today.Clear();
        _day = d;
    }
    public string Id => AreaId;
    public string Name => "Clan Boss";

    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(30);
    private ClanBossAccount? _account;
    private DateTime _readAt = DateTime.MinValue;
    private bool _dryShown;
    private int _battles;
    private string _last = "";

    public bool IsEnabled(AutoplaySettings settings) => settings.ClanBoss.Enabled;

    public void Restore(IEnumerable<AutoplayEvent> events, DateOnly localDay)
    {
        _day = localDay;
        foreach (var e in events.Where(e => e.Area == AreaId && e.Kind == "battle" && DateOnly.FromDateTime(e.Utc.ToLocalTime()) == localDay))
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
        if (_account is not null && ClanBossRules.Next(ctx.Settings.ClanBoss, _account.BestDamage, _today) is null)
            return Decision.Wait(ctx.Local.Date.AddDays(1).ToUniversalTime(), "the key plan is done for today");
        if (_account is null || ctx.Utc - _readAt >= Recheck) return Decision.Now("reading the keys");
        if (HasKey(ctx)) return Decision.Now($"{Math.Floor(_account.Keys)} key(s): {ClanBossRules.Next(ctx.Settings.ClanBoss, _account.BestDamage, _today)}");
        return Decision.Wait(_readAt + Recheck, $"no key to spend ({_account.Keys:0.##}, keeping {ctx.Settings.ClanBoss.KeepKeys})");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        _account = await game.ReadClanBossAsync(ct);
        _readAt = ctx.Utc;
        NewDay(ctx);
        if (!HasKey(ctx)) return "";
        var s = ctx.Settings.ClanBoss;
        if (ClanBossRules.Next(s, _account.BestDamage, _today) is not { } difficulty) return "";
        var quick = ClanBossRules.Quick(s, difficulty, _account.MaxQuick);
        await ctx.ThinkAsync(ct);
        var outcome = await game.FightClanBossAsync(difficulty, quick, ctx.DryRun, ct);
        if (outcome.Result is BattleResult.NotStarted)
        {
            _dryShown = ctx.DryRun;
            ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{difficulty}: {outcome.Text}");
            if (!ctx.DryRun) _readAt = ctx.Utc; // tried again after the recheck
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
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next check"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, _last.Length > 0 ? $"{decision.Why} · last: {_last}" : decision.Why, decision.UntilUtc, counters, timers);
    }
}
