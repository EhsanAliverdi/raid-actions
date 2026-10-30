using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Tag Team Arena asks of the game (the live game: Battle → Arena → Tag Team Arena, its battle tab).</summary>
public interface ITagArenaGame
{
    /// <summary>Tag Team Arena tokens now (Arena3x3Token).</summary>
    Task<double> ReadTagTokensAsync(CancellationToken ct);

    /// <summary>The battle tab and its board (opponents, power = the three teams', status, free refresh).</summary>
    Task<ArenaBoard> OpenTagArenaAsync(CancellationToken ct);

    /// <summary>One fight (three rounds) with the teams the game has set; a dry run shows Start only.</summary>
    Task<BattleOutcome> FightTagOpponentAsync(long userId, bool dryRun, CancellationToken ct);

    /// <summary>The free refresh (never a paid one); the new board, or null when not free now.</summary>
    Task<ArenaBoard?> RefreshTagFreeAsync(bool dryRun, CancellationToken ct);
}

/// <summary>
/// Tag Team Arena (Auto Play, plan/tag-arena.md): the Classic Arena's rules on the 3-team arena. A token above the ones kept: the weakest
/// opponent under the power limit not fought yet; nobody left: the free refresh when the game offers it (whatever the tokens; Ehsan
/// 2026-10-31: "do the refresh even with no token; the only time you don't is when there is an opponent you have not played"); else a
/// look again later. Off unless switched on.
/// </summary>
public sealed class TagArenaActivity(ITagArenaGame game) : IActivity
{
    public const string AreaId = "tag-arena";
    public string Id => AreaId;
    public string Name => "Tag Team Arena";

    private static readonly TimeSpan Recheck = TimeSpan.FromMinutes(20);
    private ArenaBoard? _board;
    private double? _tokens;
    private DateTime _checkedAt = DateTime.MinValue;
    private readonly HashSet<long> _skip = [];
    private bool _dryShown;
    private int _battles, _wins, _losses, _refreshes;

    public bool IsEnabled(AutoplaySettings settings) => settings.TagArena.Enabled;

    private List<ArenaOpponentInfo> Eligible(ClassicArenaSettings s) =>
        _board is null ? [] : [.. ArenaRules.Eligible(_board.Opponents, s).Where(o => !_skip.Contains(o.UserId))];

    public Decision Decide(PlayContext ctx)
    {
        if (_dryShown) return Decision.Off("shown in this dry run");
        if (_board is null || ctx.Utc - _checkedAt >= Recheck) return Decision.Now("looking at the Tag Team Arena");
        var s = ctx.Settings.TagArena;
        if (Eligible(s).Count > 0 && ArenaRules.SpendableTokens(_tokens ?? 0, s) > 0) return Decision.Now("an opponent to fight");
        if (Eligible(s).Count == 0 && s.UseFreeRefresh && _board.FreeRefreshAvailable) return Decision.Now("nobody left to fight: free refresh");
        return Decision.Wait(_checkedAt + Recheck, Eligible(s).Count > 0 ? "waiting for a token" : "nobody left to fight: waiting for the free refresh");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings.TagArena;
        _tokens = await game.ReadTagTokensAsync(ct);
        _board = await game.OpenTagArenaAsync(ct);
        _checkedAt = ctx.Utc;
        var pick = Eligible(s);
        if (pick.Count > 0 && ArenaRules.SpendableTokens(_tokens.Value, s) > 0)
        {
            var o = s.Order == ArenaOrder.StrongestFirst ? pick.MaxBy(x => x.Power)! : s.Order == ArenaOrder.BoardOrder ? pick[0] : pick.MinBy(x => x.Power)!;
            await ctx.ThinkAsync(ct);
            var outcome = await game.FightTagOpponentAsync(o.UserId, ctx.DryRun, ct);
            _skip.Add(o.UserId);
            if (outcome.Result is BattleResult.NotStarted)
            {
                _dryShown = ctx.DryRun;
                ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{o.Name} ({o.Power:N0}): {outcome.Text}");
                return "";
            }
            _battles++;
            if (outcome.Result == BattleResult.Victory) _wins++; else _losses++;
            ctx.Event(AreaId, "battle", $"{outcome.Result} vs {o.Name} ({o.Power:N0}). {outcome.Text}");
            _checkedAt = DateTime.MinValue; // the board again next turn
            return $"{outcome.Result} vs {o.Name}";
        }
        if (pick.Count == 0 && s.UseFreeRefresh && _board.FreeRefreshAvailable)
        {
            var fresh = await game.RefreshTagFreeAsync(ctx.DryRun, ct);
            if (ctx.DryRun) { _dryShown = true; ctx.Event(AreaId, "dry-run", "would use the free refresh (not pressed)"); return ""; }
            if (fresh is null) return "";
            _board = fresh;
            _skip.Clear();
            _refreshes++;
            ctx.Event(AreaId, "refresh", $"free refresh: {Eligible(s).Count} opponent(s) under {s.MaxOpponentPower:N0} now");
            return "free refresh";
        }
        return "";
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.TagArena;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles, ["Wins"] = _wins, ["Losses"] = _losses, ["Refreshes"] = _refreshes };
        if (_tokens is { } t) counters["Tokens"] = (int)Math.Floor(t);
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next look"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, decision.Why, decision.UntilUtc, counters, timers);
    }
}
