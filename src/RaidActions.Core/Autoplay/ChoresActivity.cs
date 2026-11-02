using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What Chores ask of the game.</summary>
public interface IChoresGame
{
    /// <summary>The Market opened, what <see cref="ChoreRules.Buy"/> picks bought; returns what was bought, by name.</summary>
    Task<IReadOnlyList<string>> BuyAtMarketAsync(ChoresSettings s, bool quest, bool dryRun, CancellationToken ct);

    /// <summary>One Tavern level-up with level-1 food; false when there is no food for it.</summary>
    Task<bool> LevelUpFoodAsync(FoodSettings food, IReadOnlyCollection<int> farmers, bool dryRun, CancellationToken ct);

    /// <summary>Arena and Tag Team tokens in the inbox expiring within a day claimed; how many items.</summary>
    Task<int> CollectExpiringTokensAsync(bool dryRun, CancellationToken ct);

    /// <summary>The Forge Pass's and the Challenges' rewards claimed; how many claims.</summary>
    Task<int> ClaimPassRewardsAsync(bool dryRun, CancellationToken ct);

    /// <summary>True when the app has asked for masteries to be learned.</summary>
    bool MasteryRequested();

    /// <summary>The request carried out; what was done.</summary>
    Task<string> ApplyMasteriesAsync(CancellationToken ct);

    /// <summary>The Sparring Pit's free slot kept filled; who was put in, or null when it was already training.</summary>
    Task<string?> FillSparringPitAsync(bool dryRun, CancellationToken ct);

    /// <summary>The gem Mine's bubble and the Shop's offers the game marks free; returns how many offers came.</summary>
    Task<int> CollectFreeAsync(bool dryRun, CancellationToken ct);

    /// <summary>One piece upgraded (the choice from the gear advice); null when nothing is worth upgrading.</summary>
    /// <param name="minLevel">A quest's level.</param>
    /// <param name="attempts">A quest counting attempts: the next level only.</param>
    /// <param name="keepSilver">No upgrade below this much silver (0 for a quest).</param>
    Task<ArtifactOutcome?> UpgradeArtifactAsync(int? minLevel, bool attempts, long keepSilver, bool dryRun, CancellationToken ct);
}

/// <param name="Attempts">Upgrade attempts made.</param>
public sealed record ArtifactOutcome(string Text, int Attempts, int Level);

/// <summary>
/// Chores (Auto Play, plan/chores.md): the Market every half hour; what a quest asks in the Tavern and on artifacts; a piece the gear
/// advice says to upgrade while the silver is above the floor. One job a turn; a job that fails waits an hour, the others go on.
/// </summary>
public sealed class ChoresActivity(IChoresGame game) : IActivity
{
    public const string AreaId = "chores";
    public string Id => AreaId;
    public string Name => "Chores";

    private static readonly TimeSpan Pause = TimeSpan.FromHours(1);
    private DateTime _marketAt = DateTime.MinValue, _upgradeAt = DateTime.MinValue, _freeAt = DateTime.MinValue, _inboxAt = DateTime.MinValue, _passAt = DateTime.MinValue, _pitAt = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _paused = [];
    private int _bought, _levelUps, _upgrades, _free;
    private string _last = "";

    public bool IsEnabled(AutoplaySettings settings) => settings.Chores.Enabled;

    /// <summary>A mastery request from the app goes first: someone is waiting for it.</summary>
    public bool Full(PlayContext ctx) => game.MasteryRequested();

    private bool Open(PlayContext ctx, string job) => !_paused.TryGetValue(job, out var until) || ctx.Utc >= until;

    /// <summary>The job due now, or null; the quests' first.</summary>
    private string? Next(PlayContext ctx)
    {
        var s = ctx.Settings.Chores;
        if (game.MasteryRequested()) return "masteries";
        if (ctx.QuestLeft(QuestNeedKind.Market) > 0 && Open(ctx, "market")) return "market";
        if (s.TavernLevelUps && ctx.QuestLeft(QuestNeedKind.LevelUp) > 0 && Open(ctx, "level")) return "level";
        if (s.ArtifactUpgrades && ctx.QuestLeft(QuestNeedKind.ArtifactAttempts) > 0 && Open(ctx, "attempts")) return "attempts";
        if (s.ArtifactUpgrades && ctx.QuestLeft(QuestNeedKind.ArtifactLevel) > 0 && Open(ctx, "piece")) return "piece";
        if (s.InboxExpiringTokens && ctx.Utc >= _inboxAt && Open(ctx, "inbox")) return "inbox";
        if (s.PassRewards && ctx.Utc >= _passAt && Open(ctx, "pass")) return "pass";
        if (s.SparringPit && ctx.Utc >= _pitAt && Open(ctx, "pit")) return "pit";
        if (s.FreeItems && ctx.Utc >= _freeAt && Open(ctx, "free")) return "free";
        if (s.MarketShards && ctx.Utc >= _marketAt && Open(ctx, "market")) return "market";
        if (s.ArtifactUpgrades && ctx.Utc >= _upgradeAt && Open(ctx, "advised")) return "advised";
        return null;
    }

    public Decision Decide(PlayContext ctx)
    {
        if (Next(ctx) is { } job) return Decision.Now(job);
        var at = new[] { _marketAt, _upgradeAt, _freeAt, _inboxAt, _passAt, _pitAt }.Where(t => t > ctx.Utc).DefaultIfEmpty(ctx.Utc + TimeSpan.FromMinutes(10)).Min();
        return Decision.Wait(at, "nothing due");
    }

    private static void Spend(PlayContext ctx, QuestNeedKind kind, int n) =>
        ctx.QuestNeeds = [.. ctx.QuestNeeds.Select(q => q.Kind == kind ? q with { Remaining = q.Remaining - n } : q).Where(q => q.Remaining > 0)];

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings.Chores;
        var job = Next(ctx);
        try
        {
            switch (job)
            {
                case "inbox":
                    _inboxAt = ctx.Utc + TimeSpan.FromMinutes(Math.Max(30, s.InboxEveryMinutes));
                    var tokens = await game.CollectExpiringTokensAsync(ctx.DryRun, ct);
                    return tokens > 0 ? Report(ctx, "inbox", $"claimed {tokens} expiring token item(s)") : "";
                case "masteries":
                    return Report(ctx, "masteries", await game.ApplyMasteriesAsync(ct));
                case "pit":
                    _pitAt = ctx.Utc + TimeSpan.FromMinutes(Math.Max(15, s.PitEveryMinutes));
                    return await game.FillSparringPitAsync(ctx.DryRun, ct) is { } trained ? Report(ctx, "pit", $"Sparring Pit: {trained} training") : "";
                case "pass":
                    _passAt = ctx.Utc + TimeSpan.FromMinutes(Math.Max(15, s.PassEveryMinutes));
                    var claims = await game.ClaimPassRewardsAsync(ctx.DryRun, ct);
                    return claims > 0 ? Report(ctx, "pass", $"claimed {claims} Forge Pass or Challenges reward(s)") : "";
                case "free":
                    _freeAt = ctx.Utc + TimeSpan.FromMinutes(Math.Max(10, s.FreeEveryMinutes));
                    var free = await game.CollectFreeAsync(ctx.DryRun, ct);
                    _free += free;
                    return free > 0 ? Report(ctx, "free", $"claimed {free} free Shop offer(s)") : "";
                case "market":
                    var quest = ctx.QuestLeft(QuestNeedKind.Market) > 0;
                    var bought = await game.BuyAtMarketAsync(s, quest, ctx.DryRun, ct);
                    _marketAt = ctx.Utc + TimeSpan.FromMinutes(Math.Max(5, s.MarketEveryMinutes));
                    if (bought.Count > 0) Spend(ctx, QuestNeedKind.Market, bought.Count);
                    else if (quest) _paused["market"] = ctx.Utc + Pause;
                    _bought += bought.Count;
                    return Report(ctx, "market", bought.Count > 0 ? $"bought {string.Join(", ", bought)}" : "nothing to buy");
                case "level":
                    var farmers = ctx.Settings.Energy.Targets.SelectMany(t => t.Team).ToList();
                    if (!await game.LevelUpFoodAsync(ctx.Settings.Energy.Food, farmers, ctx.DryRun, ct))
                    {
                        _paused["level"] = ctx.Utc + Pause;
                        return Report(ctx, "level", "no food for a level-up");
                    }
                    _levelUps++;
                    if (ctx.DryRun) _paused["level"] = ctx.Utc + Pause;
                    else Spend(ctx, QuestNeedKind.LevelUp, 1);
                    return Report(ctx, "level", "a 1-star levelled with food");
                case "attempts" or "piece" or "advised":
                    var minLevel = job == "piece" ? ctx.QuestNeeds.FirstOrDefault(q => q.Kind == QuestNeedKind.ArtifactLevel)?.MinStage : null;
                    if (job == "advised") _upgradeAt = ctx.Utc + TimeSpan.FromMinutes(Math.Max(5, s.UpgradeEveryMinutes));
                    var outcome = await game.UpgradeArtifactAsync(minLevel, job == "attempts", job == "advised" ? s.UpgradeKeepSilver : 0, ctx.DryRun, ct);
                    if (outcome is null || ctx.DryRun)
                    {
                        _paused[job] = ctx.Utc + Pause;
                        return Report(ctx, "upgrade", outcome?.Text ?? "no piece to upgrade now");
                    }
                    _upgrades++;
                    if (job == "attempts") Spend(ctx, QuestNeedKind.ArtifactAttempts, Math.Max(1, outcome.Attempts));
                    if (job == "piece" && outcome.Level >= (minLevel ?? 0)) Spend(ctx, QuestNeedKind.ArtifactLevel, 1);
                    return Report(ctx, "upgrade", outcome.Text);
            }
            return "";
        }
        catch (PlayStopException)
        {
            // This job waits; the rest of the game goes on.
            if (job is not null) _paused[job] = ctx.Utc + Pause;
            if (job == "market") _marketAt = ctx.Utc + Pause;
            throw;
        }
    }

    private string Report(PlayContext ctx, string kind, string text)
    {
        _last = text;
        ctx.Event(AreaId, kind, text);
        return text;
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.Chores;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Bought"] = _bought, ["Level-ups"] = _levelUps, ["Upgrades"] = _upgrades, ["Free offers"] = _free };
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next"] = u;
        return new AreaStatus(Id, Name, s.Enabled, state, _last.Length > 0 ? $"{decision.Why} · last: {_last}" : decision.Why, decision.UntilUtc, counters, timers);
    }
}
