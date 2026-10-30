using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What the energy places need from the account: resources now, each stage's cost, the beaten stages, the Super Raid multiplier.</summary>
/// <param name="Champions">The roster (food rows: who is food, who is maxed); null: not read.</param>
/// <param name="MysteryShards">Mystery shards owned (food rows open them when food runs out).</param>
public sealed record EnergyAccount(IReadOnlyDictionary<string, double> Resources, IReadOnlyList<StageCostOut> Costs, IReadOnlyList<AreaProgressOut> Progress,
    int SuperRaidMultiplier = 2, IReadOnlyList<ChampionOut>? Champions = null, int? MysteryShards = null);

/// <summary>A gem refill: bought or not (why), its price, what it gave.</summary>
public sealed record RefillResult(bool Bought, int Gems, double Got, string Why);

/// <summary>What Energy asks of the game (the live game: a fresh account read, the dungeon screens, the inbox, the refill window).</summary>
public interface IEnergyGame
{
    Task<EnergyAccount> ReadAccountAsync(CancellationToken ct);

    /// <summary>One run of the stage with the team the game shows there; Super Raid set as the row says. A dry run shows Start only.</summary>
    Task<BattleOutcome> RunStageAsync(EnergyRun run, bool dryRun, CancellationToken ct);

    /// <summary>Collects this resource from the inbox; what was collected (0: none there).</summary>
    Task<double> CollectInboxAsync(string resource, bool dryRun, CancellationToken ct);

    /// <summary>One refill bought with gems, only at <paramref name="maxGems"/> or less (the price is read first; above it: closed).</summary>
    Task<RefillResult> BuyRefillAsync(string resource, int maxGems, bool dryRun, CancellationToken ct);

    /// <summary>
    /// Food rank-ups (plan/campaign.md) in the game's Fast Rank Up: only champions and food in <paramref name="pool"/> (anything else the
    /// game lists is switched off, or nothing is pressed). How many champions were ranked up (a dry run: 0, the button shown).
    /// </summary>
    Task<int> RankUpFoodAsync(IReadOnlyList<RankUpPlan> rankUps, IReadOnlySet<int> pool, bool dryRun, CancellationToken ct);

    /// <summary>Opens up to <paramref name="count"/> Mystery shards, one at a time (never another shard kind); how many were opened.</summary>
    Task<int> SummonMysteryAsync(int count, bool dryRun, CancellationToken ct);

    /// <summary>
    /// Sells the gear Campaign dropped since the farm started (<see cref="GearSale"/>), once at least <see cref="GearSellSettings.Every"/>
    /// pieces wait, through the sell command's checks; how many were sold (a dry run: 0, the sale shown).
    /// </summary>
    Task<int> SellCampaignGearAsync(GearSellSettings settings, bool dryRun, CancellationToken ct);
}

/// <summary>
/// Energy (Auto Play, plan/energy.md, Ehsan 2026-10-30): the priority list of places that spend energy. Each turn: a fresh account read,
/// <see cref="EnergyPlanner"/>, then one step (a run, the inbox, a gem refill within the limits); otherwise it waits until the energy
/// clock covers the first waiting row (the other areas play meanwhile). Runs per row are counted per local day and restored from
/// today's events; the cost a run really took is learnt (and used when the export has none).
/// </summary>
public sealed class EnergyActivity(IEnergyGame game) : IActivity
{
    public const string AreaId = "energy";
    public string Id => AreaId;
    public string Name => "Energy";

    private static readonly TimeSpan InboxRecheck = TimeSpan.FromHours(1);
    private static readonly TimeSpan OtherResourceRecheck = TimeSpan.FromMinutes(30);
    /// <summary>Energy's regeneration when the clock has not learnt it yet (1 per 3 minutes).</summary>
    private static readonly TimeSpan EnergyPeriod = TimeSpan.FromMinutes(3);

    private readonly Dictionary<string, int> _runsToday = [];
    private readonly Dictionary<string, double> _learnt = [];
    private readonly HashSet<string> _dryShown = [];
    private int _refillsToday, _wins, _losses;
    private DateOnly? _day;
    private DateTime _inboxAt = DateTime.MinValue;
    private EnergyAccount? _account;
    private DateTime _accountAt;
    private EnergyPlan? _plan;
    /// <summary>A food step that did nothing (or a dry run that showed it): not tried again until then.</summary>
    private DateTime _rankUpBlockedUntil = DateTime.MinValue, _summonBlockedUntil = DateTime.MinValue;
    private int _rankedUp, _summoned, _sold;

    public bool IsEnabled(AutoplaySettings settings) => settings.Energy.Enabled;

    /// <summary>Today's runs, refills and learnt costs from the events (a restarted bot keeps counting).</summary>
    public void Restore(IEnumerable<AutoplayEvent> events, DateOnly localDay)
    {
        _day = localDay;
        foreach (var e in events.Where(e => e.Area == AreaId && DateOnly.FromDateTime(e.Utc.ToLocalTime()) == localDay))
        {
            if (e.Kind == "battle" && System.Text.RegularExpressions.Regex.Match(e.Text, @"\[row (\w+)\]") is { Success: true } m)
            {
                _runsToday[m.Groups[1].Value] = _runsToday.GetValueOrDefault(m.Groups[1].Value) + 1;
                if (e.Text.StartsWith("Victory", StringComparison.Ordinal)) _wins++; else _losses++;
            }
            if (e.Kind == "refill" && e.Text.StartsWith("bought", StringComparison.Ordinal)) _refillsToday++;
            // Today's food rank-ups and summons ("ranked up 3 food champion(s)", "opened 2 Mystery shard(s)").
            if (e.Kind == "rankup" && System.Text.RegularExpressions.Regex.Match(e.Text, @"^ranked up (\d+)") is { Success: true } r) _rankedUp += int.Parse(r.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (e.Kind == "summon" && System.Text.RegularExpressions.Regex.Match(e.Text, @"^opened (\d+)") is { Success: true } o) _summoned += int.Parse(o.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (e.Kind == "cost" && System.Text.RegularExpressions.Regex.Match(e.Text, @"^(\S+) costs ([\d.]+)") is { Success: true } c
                && double.TryParse(c.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var v))
                _learnt[c.Groups[1].Value] = v;
        }
    }

    private void NewDay(PlayContext ctx)
    {
        var today = DateOnly.FromDateTime(ctx.Local);
        if (_day == today) return;
        _day = today;
        _runsToday.Clear();
        _dryShown.Clear();
        _refillsToday = 0;
    }

    private EnergyWorld World(PlayContext ctx, EnergyAccount a, bool expected)
    {
        var resources = new Dictionary<string, double>(a.Resources);
        // Between reads the energy clock says how much there is now (regenerated since the read).
        if (expected && ctx.Expected("Energy") is { } e && e > resources.GetValueOrDefault("Energy")) resources["Energy"] = e;
        else if (expected && resources.TryGetValue("Energy", out var read))
            resources["Energy"] = read + Math.Floor((ctx.Utc - _accountAt) / EnergyPeriod);
        return new EnergyWorld(resources, a.Costs, a.Progress, _runsToday, _learnt, a.SuperRaidMultiplier, _refillsToday,
            ctx.Utc - _inboxAt < InboxRecheck, a.Champions, a.MysteryShards);
    }

    /// <summary>The settings the planner sees: rows shown in a dry run left out, a food step that did nothing switched off for a while.</summary>
    private EnergyPlaySettings Effective(PlayContext ctx)
    {
        var s = ctx.Settings.Energy;
        return s with
        {
            Targets = [.. s.Targets.Where(t => !_dryShown.Contains(t.Id))],
            Food = s.Food with
            {
                RankUp = s.Food.RankUp && ctx.Utc >= _rankUpBlockedUntil,
                SummonMystery = s.Food.SummonMystery && ctx.Utc >= _summonBlockedUntil,
            },
        };
    }

    public Decision Decide(PlayContext ctx)
    {
        NewDay(ctx);
        var s = ctx.Settings.Energy;
        if (!s.Targets.Any(t => t.Enabled)) return Decision.Off("no place in the list is switched on");
        if (_account is null) return Decision.Now("reading the account");
        var plan = EnergyPlanner.Plan(Effective(ctx), World(ctx, _account, expected: true));
        _plan = plan;
        switch (plan.Step)
        {
            case EnergyStep.Run or EnergyStep.Inbox or EnergyStep.BuyRefill or EnergyStep.RankUp or EnergyStep.Summon:
                return Decision.Now(plan.Why);
            case EnergyStep.Nothing:
                return Decision.Wait(ctx.Local.Date.AddDays(1).ToUniversalTime(), plan.Why);
            default:
                var have = World(ctx, _account, expected: true).Resources.GetValueOrDefault(plan.Resource!);
                var at = plan.Resource == "Energy"
                    ? ctx.Clocks["Energy"].Out()?.WhenAt(plan.Needed!.Value) ?? ctx.Utc + EnergyPeriod * Math.Max(1, Math.Ceiling(plan.Needed!.Value - have))
                    : ctx.Utc + OtherResourceRecheck;
                if (at <= ctx.Utc) at = ctx.Utc + TimeSpan.FromMinutes(1);
                return Decision.Wait(at, plan.Why);
        }
    }

    public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
    {
        NewDay(ctx);
        var s = ctx.Settings.Energy;
        _account = await game.ReadAccountAsync(ct);
        _accountAt = ctx.Utc;
        var plan = EnergyPlanner.Plan(Effective(ctx), World(ctx, _account, expected: false));
        _plan = plan;
        switch (plan.Step)
        {
            case EnergyStep.RankUp:
            {
                var row = plan.Rows[^1].Target;
                var pool = (_account.Champions ?? []).Where(c => FoodPlanner.IsFood(c, s.Food, row.Team)).Select(c => c.Id).ToHashSet();
                await ctx.ThinkAsync(ct);
                int done;
                try { done = await game.RankUpFoodAsync(plan.Food!.RankUps, pool, ctx.DryRun, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A rank-up that cannot be done (a screen not as expected) pauses rank-ups, never the farm or the other areas.
                    _rankUpBlockedUntil = ctx.Utc + TimeSpan.FromHours(1);
                    ctx.Event(AreaId, "error", $"food rank-ups paused for an hour: {e.Message}");
                    await game.ReadAccountAsync(ct);
                    return "";
                }
                _rankedUp += done;
                var due = string.Join(", ", plan.Food.RankUps.Select(r => $"#{r.HeroId} {r.Rank}*"));
                ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "rankup", done > 0
                    ? $"ranked up {done} food champion(s) ({due})"
                    : $"no rank-up done ({plan.Food.RankUps.Count} due: {due}){(ctx.DryRun ? ": dry run" : "")}");
                if (done == 0) _rankUpBlockedUntil = ctx.Utc + TimeSpan.FromHours(1);
                _account = await game.ReadAccountAsync(ct);
                _accountAt = ctx.Utc;
                return done > 0 ? $"rank-up: {done} food" : "";
            }
            case EnergyStep.Summon:
            {
                var count = Math.Max(0, (_account.MysteryShards ?? 0) - s.Food.KeepMysteryShards);
                await ctx.ThinkAsync(ct);
                int opened;
                try { opened = await game.SummonMysteryAsync(Math.Min(count, 10), ctx.DryRun, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _summonBlockedUntil = ctx.Local.Date.AddDays(1).ToUniversalTime();
                    ctx.Event(AreaId, "error", $"Mystery shards paused until tomorrow: {e.Message}");
                    return "";
                }
                _summoned += opened;
                ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "summon",
                    opened > 0 ? $"opened {opened} Mystery shard(s) for food" : $"no Mystery shard opened{(ctx.DryRun ? ": dry run" : "")}");
                if (opened == 0) _summonBlockedUntil = ctx.Local.Date.AddDays(1).ToUniversalTime();
                _account = await game.ReadAccountAsync(ct);
                _accountAt = ctx.Utc;
                return opened > 0 ? $"summon: {opened} Mystery shard(s)" : "";
            }
            case EnergyStep.Run:
            {
                var run = plan.Run!;
                var before = _account.Resources.GetValueOrDefault(run.Resource);
                await ctx.ThinkAsync(ct);
                var outcome = await game.RunStageAsync(run, ctx.DryRun, ct);
                var where = EnergyPlanner.Describe(run);
                if (outcome.Result is BattleResult.Retry)
                {
                    // Nothing fought; the game showed something the plan takes in first (a team member it will not take).
                    ctx.Event(AreaId, "skip", $"{where}: {outcome.Text}");
                    return "";
                }
                if (outcome.Result is BattleResult.NotStarted)
                {
                    _dryShown.Add(run.Target.Id);
                    ctx.Event(AreaId, ctx.DryRun ? "dry-run" : "skip", $"{where}: {outcome.Text}");
                    return "";
                }
                _runsToday[run.Target.Id] = _runsToday.GetValueOrDefault(run.Target.Id) + 1;
                if (outcome.Result == BattleResult.Victory) _wins++; else _losses++;
                ctx.Event(AreaId, "battle", $"{outcome.Result} {where} [row {run.Target.Id}]. {outcome.Text}");
                // Campaign drops sold when enough have come (the option; a failed sale stops only the sale, not the farm).
                if (run.Target.Area == "Story" && s.SellGear.Enabled && outcome.Result == BattleResult.Victory)
                {
                    try
                    {
                        var sold = await game.SellCampaignGearAsync(s.SellGear, ctx.DryRun, ct);
                        _sold += sold;
                        if (sold > 0) ctx.Event(AreaId, "sell", $"sold {sold} Campaign drop(s)");
                    }
                    catch (PlayStopException e)
                    {
                        ctx.Event(AreaId, "sell", $"the Campaign gear sale stopped: {e.Message}");
                    }
                }
                // What the run really took (the regeneration during it is under one energy for a short battle): learnt when not exported.
                _account = await game.ReadAccountAsync(ct);
                _accountAt = ctx.Utc;
                var spent = before - _account.Resources.GetValueOrDefault(run.Resource);
                var key = EnergyPlanner.LearntKey(run.Target, run.Number);
                if (spent > 0 && Math.Abs(spent - run.Cost) >= 1 && (!run.CostKnown || _learnt.ContainsKey(key)))
                {
                    _learnt[key] = Math.Round(spent);
                    ctx.Event(AreaId, "cost", $"{key} costs {Math.Round(spent)} {run.Resource} (seen on a run)");
                }
                return $"{outcome.Result}: {where}";
            }
            case EnergyStep.Inbox:
            {
                _inboxAt = ctx.Utc;
                var got = await game.CollectInboxAsync(plan.Resource!, ctx.DryRun, ct);
                ctx.Event(AreaId, "inbox", got > 0 ? $"collected {got} {plan.Resource} from the inbox" : $"no {plan.Resource} in the inbox");
                if (got > 0) { _account = await game.ReadAccountAsync(ct); _accountAt = ctx.Utc; }
                return got > 0 ? $"inbox: {got} {plan.Resource}" : "";
            }
            case EnergyStep.BuyRefill:
            {
                var source = s.SourceFor(plan.Resource!);
                await ctx.ThinkAsync(ct);
                var r = await game.BuyRefillAsync(plan.Resource!, source.MaxGemsPerRefill, ctx.DryRun, ct);
                if (r.Bought && !ctx.DryRun) _refillsToday++;
                ctx.Event(AreaId, "refill", r.Bought
                    ? $"bought {r.Got} {plan.Resource} for {r.Gems} gems ({_refillsToday} of {source.MaxRefillsPerDay} today)"
                    : $"not bought: {r.Why}");
                // Not bought (the price above the limit, no refill offered): no more tries today.
                if (!r.Bought) _refillsToday = Math.Max(_refillsToday, source.MaxRefillsPerDay);
                if (r.Bought) { _account = await game.ReadAccountAsync(ct); _accountAt = ctx.Utc; }
                return r.Bought ? $"refill: {r.Got} {plan.Resource} for {r.Gems} gems" : "";
            }
            default:
                return "";
        }
    }

    public AreaStatus Status(PlayContext ctx, Decision decision)
    {
        var s = ctx.Settings.Energy;
        var state = !s.Enabled ? AreaState.Off : decision.Act ? AreaState.Playing : decision.UntilUtc is null ? AreaState.Done : AreaState.Waiting;
        var counters = new Dictionary<string, int> { ["Wins"] = _wins, ["Losses"] = _losses, ["Refills"] = _refillsToday };
        if (s.Targets.Any(t => t.Food)) { counters["Food ranked up"] = _rankedUp; counters["Mystery opened"] = _summoned; }
        if (s.SellGear.Enabled) counters["Gear sold"] = _sold;
        foreach (var t in s.Targets) counters[$"{EnergyPlanner.Where(t)} runs"] = _runsToday.GetValueOrDefault(t.Id);
        var timers = new Dictionary<string, DateTime>();
        if (decision.UntilUtc is { } u) timers["Next try"] = u;
        if (ctx.Clocks["Energy"].Out() is { NextUtc: { } nt }) timers["Next energy"] = nt;
        var detail = decision.Why;
        return new AreaStatus(Id, Name, s.Enabled, state, detail, decision.UntilUtc, counters, timers);
    }

    /// <summary>The plan the last decision or turn made (rows and their states, for tests and the page).</summary>
    public EnergyPlan? LastPlan => _plan;
}
