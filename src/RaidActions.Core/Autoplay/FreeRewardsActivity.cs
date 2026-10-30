using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Free rewards asks of the game: the playtime gifts collected (how many; a dry run: 0, the ready ones shown).</summary>
public interface IFreeRewardsGame
{
    Task<int> CollectPlaytimeGiftsAsync(bool dryRun, CancellationToken ct);

    /// <summary>
    /// The quests window: every tab read (the quests, their progress) and, when <paramref name="claim"/>, its Claim all pressed where the
    /// game says a reward waits. How many tabs were claimed, and the quests read.
    /// </summary>
    Task<QuestPass> QuestPassAsync(bool claim, bool dryRun, CancellationToken ct);
}

/// <summary>
/// Free rewards (Auto Play, plan/free-rewards.md): every <see cref="FreeRewardsSettings.CheckEveryMinutes"/> the playtime gifts that are
/// ready are collected. Nothing is spent. A check that fails is tried again at the next check, never in a loop.
/// </summary>
/// <summary>A quests window visit: tabs claimed, the quests read.</summary>
public sealed record QuestPass(int Claimed, List<QuestRead> Quests);

public sealed class FreeRewardsActivity(IFreeRewardsGame game) : IActivity
{
    public const string AreaId = "free-rewards";
    public string Id => AreaId;
    public string Name => "Free rewards";

    private DateTime _checkedAt = DateTime.MinValue;
    private string? _lastQuestText;
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
        if (!ctx.Settings.FreeRewards.PlaytimeGifts && !ctx.Settings.FreeRewards.Quests && !ctx.Settings.Quests.Enabled) return Decision.Off("nothing switched on");
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
        if (s.Quests || ctx.Settings.Quests.Enabled)
        {
            try
            {
                var claim = s.Quests && ctx.Settings.Quests.ClaimRewards;
                var pass = await game.QuestPassAsync(claim, ctx.DryRun, ct);
                if (pass.Claimed > 0 && !ctx.DryRun)
                {
                    ctx.Event(AreaId, "quests", $"claimed the quest rewards of {pass.Claimed} tab(s)");
                    done.Add($"quests: {pass.Claimed} tab(s)");
                }
                if (pass.Quests.Count > 0)
                {
                    ctx.QuestNeeds = QuestPlanner.Needs(pass.Quests, ctx.Settings.Quests);
                    var open = ctx.QuestNeeds.Where(n => n.Playable).ToList();
                    var text = open.Count == 0 ? "no open quest the bot can play"
                        : string.Join("; ", open.Select(n => $"{n.Tab} {n.Kind} ×{n.Remaining}"));
                    var given = ctx.QuestNeeds.Count(n => !n.Playable);
                    if (text != _lastQuestText)
                    {
                        _lastQuestText = text;
                        ctx.Event(AreaId, "quest-plan", text + (given > 0 ? $" (given up: {given} the bot cannot do)" : ""));
                    }
                }
            }
            catch (PlayStopException e) { ctx.Event(AreaId, "error", $"quests not read (tried again at the next check): {e.Message}"); }
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
