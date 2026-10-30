using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>
/// Classic Arena (Auto Play phase 1, Ehsan 2026-10-29): fight every opponent on the board whose power is under the limit (default
/// 100,000), on auto, one battle per turn; out of tokens, collect arena tokens from the inbox if any, else wait for the next token;
/// nobody left to fight, use the free refresh when the game offers it, else wait for it. Never spends gems.
/// </summary>
public sealed class ClassicArenaActivity : IActivity
{
    public const string AreaId = "classic-arena";
    public string Id => AreaId;
    public string Name => "Classic Arena";

    private static readonly TimeSpan BoardStale = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InboxRecheck = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan RefreshUnknown = TimeSpan.FromMinutes(15);

    private ArenaBoard? _board;
    private DateTime _inboxCheckAfter = DateTime.MinValue;
    private DateTime? _freeRefreshAt;
    private DateTime? _dryRefreshShown;
    private readonly HashSet<long> _skip = []; // opponents that failed to start on this board
    private DateOnly _day;
    private int _battles, _wins, _losses, _draws, _refreshes, _inboxTokens;

    public bool IsEnabled(AutoplaySettings settings) => settings.ClassicArena.Enabled;

    /// <summary>Today's counters from the events already logged today (a restarted bot keeps counting the day).</summary>
    public void Restore(IEnumerable<AutoplayEvent> today, DateOnly day)
    {
        _day = day;
        foreach (var e in today.Where(e => e.Area == AreaId && e.Kind == "battle"))
        {
            _battles++;
            if (e.Text.StartsWith("Victory", StringComparison.Ordinal)) _wins++;
            else if (e.Text.StartsWith("Defeat", StringComparison.Ordinal)) _losses++;
            else if (e.Text.StartsWith("Draw", StringComparison.Ordinal)) _draws++;
        }
    }

    public Decision Decide(PlayContext ctx)
    {
        NewDay(ctx);
        var s = ctx.Settings.ClassicArena;
        if (s.MaxBattlesPerDay > 0 && _battles >= s.MaxBattlesPerDay)
            return Decision.Wait(ctx.Local.Date.AddDays(1).ToUniversalTime(), $"{_battles} battles today: the daily limit");
        var tokens = ctx.Expected("Token");
        if (tokens is null || _board is null || ctx.Utc - _board.Utc > BoardStale) return Decision.Now("looking at the arena");
        if (ArenaRules.SpendableTokens(tokens.Value, s) == 0)
        {
            if (s.CollectInboxTokens && ctx.Utc >= _inboxCheckAfter) return Decision.Now("out of tokens: checking the inbox");
            var at = ctx.Clocks["Token"].Out()?.WhenAt(s.KeepTokens + 1) ?? ctx.Utc + RefreshUnknown;
            // Ehsan (2026-10-30): with no token and nobody to fight under the limit, refresh anyway (free) so an opponent is there when the
            // next token comes; never refresh while someone is there to fight.
            if (s.UseFreeRefresh && Eligible(s).Count == 0 && !ctx.DryRun)
            {
                if (_board.FreeRefreshAvailable || _freeRefreshAt is { } due && due <= ctx.Utc)
                    return Decision.Now("out of tokens, nobody to fight: free refresh for the next token");
                if (_freeRefreshAt is { } fr && fr < at) return Decision.Wait(fr, "out of tokens, nobody to fight: waiting for the free refresh");
            }
            return Decision.Wait(at, "out of tokens: waiting for the next one");
        }
        var eligible = Eligible(s);
        if (eligible.Count > 0) return Decision.Now($"{eligible.Count} opponent{(eligible.Count == 1 ? "" : "s")} under {Limit(s):N0}");
        if (!s.UseFreeRefresh) return Decision.Wait(_board.Utc + BoardStale, "nobody to fight under the power limit (free refresh is off)");
        if (_board.FreeRefreshAvailable && ctx.DryRun && _dryRefreshShown == _board.Utc)
            return Decision.Wait(_board.Utc + BoardStale, "dry run: the free refresh was shown, not pressed");
        if (_board.FreeRefreshAvailable) return Decision.Now("nobody left to fight: free refresh");
        return Decision.Wait(_freeRefreshAt ?? _board.Utc + RefreshUnknown, "nobody left to fight: waiting for the free refresh");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        NewDay(ctx);
        var s = ctx.Settings.ClassicArena;
        var r = await ctx.ReadResourcesAsync(ct);
        if (ArenaRules.SpendableTokens(r.Tokens, s) == 0)
        {
            if (!s.CollectInboxTokens || ctx.Utc < _inboxCheckAfter)
            {
                // Read the board anyway when it is unknown (timers for the page); nobody to fight on it: the free refresh, so an opponent
                // waits for the next token.
                var seen = _board is null || ctx.Utc - _board.Utc > BoardStale || _freeRefreshAt is { } due && due <= ctx.Utc
                    ? Seen(ctx, await ctx.Game.OpenClassicArenaAsync(ct)) : _board;
                if (s.UseFreeRefresh && !ctx.DryRun && seen.FreeRefreshAvailable && Eligible(s).Count == 0) return await FreeRefresh(ctx, seen, s, ct);
                return "";
            }
            var got = await ctx.Game.CollectInboxTokensAsync(ctx.DryRun, ct);
            _inboxCheckAfter = ctx.Utc + InboxRecheck;
            if (got > 0)
            {
                _inboxTokens += got;
                await ctx.ReadResourcesAsync(ct);
                ctx.Event(AreaId, "inbox", $"collected {got} arena token{(got == 1 ? "" : "s")} from the inbox");
                return $"collected {got} token{(got == 1 ? "" : "s")} from the inbox";
            }
            ctx.Event(AreaId, "inbox", "no arena tokens in the inbox");
            return "";
        }

        var board = Seen(ctx, await ctx.Game.OpenClassicArenaAsync(ct));
        var eligible = Eligible(s);
        if (eligible.Count == 0)
        {
            if (!s.UseFreeRefresh || !board.FreeRefreshAvailable) return "";
            return await FreeRefresh(ctx, board, s, ct);
        }

        var target = eligible[0];
        await ctx.ThinkAsync(ct);
        BattleOutcome outcome;
        try { outcome = await ctx.Game.FightArenaOpponentAsync(target.UserId, ctx.DryRun, ct); }
        catch (PlayStopException)
        {
            _skip.Add(target.UserId); // do not try the same one again on this board
            throw;
        }
        var who = $"{target.Name ?? "?"} ({target.Power:N0})";
        if (outcome.Result == BattleResult.NotStarted)
        {
            _skip.Add(target.UserId);
            ctx.Event(AreaId, "dry-run", $"would fight {who}: {outcome.Text}");
            return "";
        }
        _battles++;
        switch (outcome.Result)
        {
            case BattleResult.Victory: _wins++; break;
            case BattleResult.Defeat: _losses++; break;
            case BattleResult.Draw: _draws++; break;
        }
        _board = _board is null ? null : _board with { Utc = DateTime.MinValue }; // the board changed: read it again next turn
        ctx.Event(AreaId, "battle", $"{outcome.Result} vs {who}. {outcome.Text}");
        return $"{outcome.Result} vs {who}";
    }

    /// <summary>The arena's free refresh (nobody to fight under the power limit): the new board read, the skips cleared.</summary>
    private async Task<string> FreeRefresh(PlayContext ctx, ArenaBoard board, ClassicArenaSettings s, CancellationToken ct)
    {
        await ctx.ThinkAsync(ct);
        var fresh = await ctx.Game.RefreshArenaFreeAsync(ctx.DryRun, ct);
        if (ctx.DryRun) _dryRefreshShown = board.Utc;
        if (fresh is null)
        {
            if (ctx.DryRun) ctx.Event(AreaId, "dry-run", "would use the free refresh (not pressed)");
            return "";
        }
        _refreshes++;
        _skip.Clear();
        Seen(ctx, fresh);
        ctx.Event(AreaId, "refresh", ctx.DryRun ? "would use the free refresh (dry run: not pressed)" : $"free refresh: {Eligible(s).Count} opponent(s) under {Limit(s):N0} now{Skipped(s)}");
        return ctx.DryRun ? "" : "free refresh";
    }

    /// <summary>Why the rest of the board is skipped ("; skipped: 6 already beaten, 4 the game does not offer the battle").</summary>
    private string Skipped(ClassicArenaSettings s)
    {
        if (_board is null) return "";
        var why = _board.Opponents.Select(o => _skip.Contains(o.UserId) ? "skipped this turn" : ArenaRules.Why(o, s, _board.MyPower)).OfType<string>()
            .Select(w => w.StartsWith("power ", StringComparison.Ordinal) ? $"above {Limit(s):N0}" : w)
            .GroupBy(w => w).Select(g => $"{g.Count()} {g.Key}").ToList();
        return why.Count == 0 ? "" : "; skipped: " + string.Join(", ", why) + $" (powers {string.Join(", ", _board.Opponents.Select(o => o.Power.ToString("N0")))})";
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.ClassicArena;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var timers = new Dictionary<string, DateTime>();
        if (_freeRefreshAt is { } fr && fr > ctx.Utc) timers["Free refresh"] = fr;
        if (ctx.Clocks["Token"].Out() is { NextUtc: { } nt }) timers["Next token"] = nt;
        if (decision.UntilUtc is { } u) timers["Next try"] = u;
        var counters = new Dictionary<string, int>
        {
            ["Battles"] = _battles, ["Wins"] = _wins, ["Losses"] = _losses, ["Draws"] = _draws, ["Refreshes"] = _refreshes, ["Inbox tokens"] = _inboxTokens,
        };
        if (_board is { } b)
        {
            counters["On the board"] = b.Opponents.Count;
            counters["To fight"] = Eligible(s).Count;
        }
        return new AreaStatus(Id, Name, s.Enabled, state, decision.Why, decision.UntilUtc, counters, timers);
    }

    private List<ArenaOpponentInfo> Eligible(ClassicArenaSettings s) =>
        _board is null ? [] : ArenaRules.Eligible(_board.Opponents.Where(o => !_skip.Contains(o.UserId)), s, _board.MyPower);

    /// <summary>The power limit now: my defense team's power when asked for and known, else the setting's number.</summary>
    private int Limit(ClassicArenaSettings s) => ArenaRules.Limit(s, _board?.MyPower);

    private ArenaBoard Seen(PlayContext ctx, ArenaBoard b)
    {
        if (_board is null || !_board.Opponents.Select(o => o.UserId).OrderBy(x => x).SequenceEqual(b.Opponents.Select(o => o.UserId).OrderBy(x => x))) _skip.Clear();
        _board = b;
        _freeRefreshAt = b.FreeRefreshAvailable ? null : b.FreeRefreshIn is { } left ? b.Utc + left : _freeRefreshAt;
        return b;
    }

    private void NewDay(PlayContext ctx)
    {
        var today = DateOnly.FromDateTime(ctx.Local);
        if (today == _day) return;
        _day = today;
        _battles = _wins = _losses = _draws = _refreshes = _inboxTokens = 0;
    }
}
