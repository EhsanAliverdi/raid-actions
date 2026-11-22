using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>
/// 2026-11-23 (Ehsan: "many rewards are missed in last several days"; "energy was over loaded in all accounts"): Chores' reward jobs get a window even
/// while energy is full, the chain break agrees with the pick, and energy that would pass the cap while the account is away counts as full.
/// </summary>
public class ChoresWindowTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>Energy at its cap that can always play (the food farm): every turn takes 3 minutes, and each run asks whether its chain should end.</summary>
    private sealed class FullEnergy(FakeHost host) : IActivity
    {
        public string Id => EnergyActivity.AreaId;
        public string Name => "Energy";
        public int Runs { get; private set; }
        public List<string> Breaks { get; } = [];
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("energy full");
        public bool Full(PlayContext ctx) => true;
        public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
        {
            // A chain of up to 5 runs, ended by what the engine says.
            for (var i = 0; i < 5; i++)
            {
                Runs++;
                await host.DelayAsync(TimeSpan.FromMinutes(3), ct);
                if (ctx.ChainBreak?.Invoke(Id) is { } why) { Breaks.Add(why); break; }
            }
            return "ran";
        }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }

    private sealed class Jobs : IChoresGame
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<(string Button, string Dot)>> VillageRedDotsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>([]);
        public Task<IReadOnlyList<string>> BuyAtMarketAsync(ChoresSettings s, bool quest, bool dryRun, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> LevelUpFoodAsync(FoodSettings food, IReadOnlyCollection<int> farmers, bool dryRun, CancellationToken ct) => Task.FromResult(false);
        public Task<int> CollectExpiringTokensAsync(bool dryRun, CancellationToken ct) { Calls.Add("inbox"); return Task.FromResult(0); }
        public bool MasteryRequested() => false;
        public Task<string> ApplyMasteriesAsync(CancellationToken ct) => Task.FromResult("");
        public Task<string?> FillSparringPitAsync(bool dryRun, CancellationToken ct) { Calls.Add("pit"); return Task.FromResult<string?>(null); }
        public Task<int> ClaimPassRewardsAsync(bool dryRun, CancellationToken ct) { Calls.Add("pass"); return Task.FromResult(1); }
        public Task<int> CollectFreeAsync(bool dryRun, CancellationToken ct) { Calls.Add("free"); return Task.FromResult(1); }
        public Task<ArtifactOutcome?> UpgradeArtifactAsync(int? minLevel, bool attempts, long keepSilver, bool dryRun, CancellationToken ct) => Task.FromResult<ArtifactOutcome?>(null);
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Reward_jobs_are_claimed_while_energy_is_full_and_energy_keeps_its_chains()
    {
        var settings = new AutoplaySettings
        {
            Enabled = true,
            Pacing = new PacingSettings { SessionMinutes = 0 },
            Chores = new ChoresSettings { Enabled = true, RedDots = false, ArtifactUpgrades = false, TavernLevelUps = false, Cleanup = new GearCleanupSettings { Enabled = false } },
        };
        var host = new FakeHost(Start) { Settings = settings, OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(90)) h.Stop = true; } };
        var energy = new FullEnergy(host);
        var jobs = new Jobs();
        var engine = new AutoplayEngine([new ChoresActivity(jobs), energy], new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync }, host, false);
        await engine.RunAsync(once: false, Limit());

        // The reward jobs came (before the fix: none while energy was full).
        Assert.True(jobs.Calls.Contains("pass"), $"no Pass claim; calls [{string.Join(", ", jobs.Calls)}]; energy runs {energy.Runs}; events: "
            + string.Join(" | ", host.Events.Take(12).Select(e => $"{e.Area} {e.Kind} {e.Text}"))
            + "; areas: " + string.Join(" | ", host.Last?.Areas.Select(a => $"{a.Id} {a.Enabled} {a.State} {a.Detail}") ?? []));
        Assert.Contains("free", jobs.Calls);
        Assert.Contains("inbox", jobs.Calls);
        Assert.Contains("pit", jobs.Calls);
        // Energy still played most of the time, in chains: far fewer breaks than runs (before: a break after every run, for Chores that never came).
        Assert.True(energy.Runs >= 15, $"energy runs {energy.Runs}");
        Assert.True(energy.Breaks.Count * 2 < energy.Runs, $"breaks {energy.Breaks.Count} of {energy.Runs} runs: {string.Join(" | ", energy.Breaks)}");
    }

    /// <summary>An area that is urgent all the time (an arena's free refresh with nobody to fight).</summary>
    private sealed class UrgentArena(FakeHost host) : IActivity
    {
        public string Id => "classic-arena";
        public string Name => "Classic Arena";
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("free refresh");
        public bool Urgent(PlayContext ctx) => true;
        public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct) { await host.DelayAsync(TimeSpan.FromMinutes(1), ct); return "refresh"; }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }

    /// <summary>A crypt with 16 keys: one battle per key, asking after each whether the chain ends.</summary>
    private sealed class Crypt(FakeHost host) : IActivity
    {
        public string Id => FactionWarsActivity.AreaId;
        public string Name => "Faction Wars";
        public int Keys { get; private set; } = 16;
        public List<int> Chains { get; } = [];
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Keys > 0 ? Decision.Now("crypt") : Decision.Off("no keys");
        public async Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
        {
            var runs = 0;
            while (Keys > 0)
            {
                Keys--; runs++;
                await host.DelayAsync(TimeSpan.FromMinutes(2), ct);
                if (ctx.ChainBreak?.Invoke(Id) is not null) break;
            }
            Chains.Add(runs);
            return "crypt";
        }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }

    [Fact]
    public async Task A_crypt_is_finished_in_one_chain_even_with_an_urgent_arena()
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 } },
            OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(60)) h.Stop = true; },
        };
        var crypt = new Crypt(host);
        var engine = new AutoplayEngine([crypt, new UrgentArena(host)], new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync }, host, false);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(0, crypt.Keys);
        Assert.Equal([16], crypt.Chains);
    }

    private static PlayContext Ctx(FakeHost host) => new(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add);

    private static GameResources Energy(double e) => new(Start, 0, e, 0, EnergyCap: 130);

    [Fact]
    public void Energy_that_would_pass_the_cap_while_the_account_is_away_counts_as_full()
    {
        var host = new FakeHost(Start) { Turn = new TurnView("A", "B", Start.AddMinutes(30)), Away = TimeSpan.FromMinutes(135) };
        var ctx = Ctx(host);
        // 165 minutes until the next turn: about 56 energy comes. 70 + 56 > 130? no. 80 + 56: yes.
        Assert.False(EnergyActivity.OverflowsAway(ctx, Energy(70)));
        Assert.True(EnergyActivity.OverflowsAway(ctx, Energy(80)));
        // No rotation: only the margin rule (cap - 40) counts.
        var alone = Ctx(new FakeHost(Start));
        Assert.False(EnergyActivity.OverflowsAway(alone, Energy(120)));
    }
}
