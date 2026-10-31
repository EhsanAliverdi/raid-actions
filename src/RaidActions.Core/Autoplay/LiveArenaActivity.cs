using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <param name="Active">The arena is open now (it has hours).</param>
/// <param name="CanPlay">The game offers Find Opponent: a token is there.</param>
public sealed record LiveArenaState(bool Active, bool CanPlay);

/// <summary>What Live Arena asks of the game.</summary>
public interface ILiveArenaGame
{
    Task<LiveArenaState> ReadLiveArenaAsync(CancellationToken ct);

    /// <summary>One match: Find Opponent, the draft turn by turn, Start, auto, the result. A dry run stops at Find Opponent.</summary>
    Task<BattleOutcome> PlayLiveArenaAsync(LiveArenaSettings s, bool dryRun, CancellationToken ct);
}

/// <summary>Live Arena (Auto Play, plan/live-arena.md): while it is open and a token is there, one match a turn; checked again every half hour.</summary>
public sealed class LiveArenaActivity(ILiveArenaGame game) : IActivity
{
    public const string AreaId = "live-arena";
    public string Id => AreaId;
    public string Name => "Live Arena";

    private DateTime _checkAt = DateTime.MinValue;
    private bool _dryShown;
    private int _battles, _wins, _losses;
    private string _last = "";

    public bool IsEnabled(AutoplaySettings settings) => settings.LiveArena.Enabled;

    public Decision Decide(PlayContext ctx)
    {
        if (_dryShown) return Decision.Off("shown in this dry run");
        return ctx.Utc >= _checkAt ? Decision.Now("looking at Live Arena") : Decision.Wait(_checkAt, _last.Length > 0 ? _last : "waiting for tokens or its hours");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings.LiveArena;
        var wait = TimeSpan.FromMinutes(Math.Max(5, s.CheckEveryMinutes));
        try
        {
            var state = await game.ReadLiveArenaAsync(ct);
            if (!state.Active || !state.CanPlay)
            {
                _checkAt = ctx.Utc + wait;
                _last = state.Active ? "open, no token" : "closed now";
                return "";
            }
            await ctx.ThinkAsync(ct);
            var outcome = await game.PlayLiveArenaAsync(s, ctx.DryRun, ct);
            if (outcome.Result is BattleResult.NotStarted or BattleResult.Retry)
            {
                _dryShown = ctx.DryRun;
                _checkAt = ctx.Utc + wait;
                ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", outcome.Text);
                return "";
            }
            _battles++;
            if (outcome.Result == BattleResult.Victory) _wins++;
            else if (outcome.Result == BattleResult.Defeat) _losses++;
            _last = $"{outcome.Result}. {outcome.Text}";
            ctx.Event(AreaId, "battle", _last);
            return _last;
        }
        catch (PlayStopException)
        {
            _checkAt = ctx.Utc + wait; // Live Arena waits; the rest of the game goes on
            throw;
        }
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.LiveArena;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Battles"] = _battles, ["Wins"] = _wins, ["Losses"] = _losses };
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next check"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, decision.Why, decision.UntilUtc, counters, timers);
    }
}
