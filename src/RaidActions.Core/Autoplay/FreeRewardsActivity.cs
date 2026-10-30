using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Free rewards asks of the game: the playtime gifts collected (how many; a dry run: 0, the ready ones shown).</summary>
public interface IFreeRewardsGame
{
    Task<int> CollectPlaytimeGiftsAsync(bool dryRun, CancellationToken ct);

    /// <summary>Every quest tab's Claim all pressed where the game says a reward waits; how many tabs were claimed.</summary>
    Task<int> ClaimQuestRewardsAsync(bool dryRun, CancellationToken ct);
}

/// <summary>
/// Free rewards (Auto Play, plan/free-rewards.md): every <see cref="FreeRewardsSettings.CheckEveryMinutes"/> the playtime gifts that are
/// ready are collected. Nothing is spent. A check that fails is tried again at the next check, never in a loop.
/// </summary>
public sealed class FreeRewardsActivity(IFreeRewardsGame game) : IActivity
{
    public const string AreaId = "free-rewards";
    public string Id => AreaId;
    public string Name => "Free rewards";

    private DateTime _checkedAt = DateTime.MinValue;
    private int _collected;
    private bool _dryShown;

    public bool IsEnabled(AutoplaySettings settings) => settings.FreeRewards.Enabled;

    public void Restore(IEnumerable<AutoplayEvent> events, DateOnly localDay) =>
        _collected = events.Where(e => e.Area == AreaId && e.Kind == "collect" && DateOnly.FromDateTime(e.Utc.ToLocalTime()) == localDay)
            .Sum(e => int.TryParse(e.Text.Split(' ').ElementAtOrDefault(1), out var n) ? n : 0);

    private TimeSpan Every(PlayContext ctx) => TimeSpan.FromMinutes(Math.Max(5, ctx.Settings.FreeRewards.CheckEveryMinutes));

    public Decision Decide(PlayContext ctx)
    {
        if (_dryShown) return Decision.Off("shown in this dry run");
        if (!ctx.Settings.FreeRewards.PlaytimeGifts && !ctx.Settings.FreeRewards.Quests) return Decision.Off("nothing switched on");
        var next = _checkedAt + Every(ctx);
        return ctx.Utc >= next ? Decision.Now("checking the free rewards") : Decision.Wait(next, "next check of the free rewards");
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        _checkedAt = ctx.Utc;
        var s = ctx.Settings.FreeRewards;
        var done = new List<string>();
        if (s.PlaytimeGifts)
        {
            try
            {
                var got = await game.CollectPlaytimeGiftsAsync(ctx.DryRun, ct);
                if (got > 0 && !ctx.DryRun)
                {
                    _collected += got;
                    ctx.Event(AreaId, "collect", $"collected {got} playtime gift(s)");
                    done.Add($"playtime gifts: {got}");
                }
            }
            catch (PlayStopException e) { ctx.Event(AreaId, "error", $"playtime gifts not collected (tried again at the next check): {e.Message}"); }
        }
        if (s.Quests)
        {
            try
            {
                var tabs = await game.ClaimQuestRewardsAsync(ctx.DryRun, ct);
                if (tabs > 0 && !ctx.DryRun)
                {
                    ctx.Event(AreaId, "quests", $"claimed the quest rewards of {tabs} tab(s)");
                    done.Add($"quests: {tabs} tab(s)");
                }
            }
            catch (PlayStopException e) { ctx.Event(AreaId, "error", $"quest rewards not claimed (tried again at the next check): {e.Message}"); }
        }
        if (ctx.DryRun) { _dryShown = true; ctx.Event(AreaId, "dry-run", "free rewards shown"); return ""; }
        return string.Join("; ", done);
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.FreeRewards;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next check"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, decision.Why, decision.UntilUtc, new() { ["Collected today"] = _collected }, timers);
    }
}
