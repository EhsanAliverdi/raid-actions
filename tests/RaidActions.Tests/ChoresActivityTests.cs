using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Chores on a fake game: the quests' jobs first, one job a turn, a failed job waits and the others go on.</summary>
public class ChoresActivityTests
{
    private sealed class Game : IChoresGame
    {
        public List<string> Calls { get; } = [];
        public bool FailMarket { get; set; }

        public Task<IReadOnlyList<string>> BuyAtMarketAsync(ChoresSettings s, bool quest, bool dryRun, CancellationToken ct)
        {
            Calls.Add($"market quest={quest}");
            if (FailMarket) throw new PlayStopException("no market");
            return Task.FromResult<IReadOnlyList<string>>(["Bandit"]);
        }

        public Task<bool> LevelUpFoodAsync(FoodSettings food, IReadOnlyCollection<int> farmers, bool dryRun, CancellationToken ct)
        {
            Calls.Add("level");
            return Task.FromResult(true);
        }

        public Task<string> JoinClanAsync(bool dryRun, CancellationToken ct)
        {
            Calls.Add("clan");
            return Task.FromResult("joined the clan Test [TST] with Quick Join");
        }

        public Task<int> CollectExpiringTokensAsync(bool dryRun, CancellationToken ct)
        {
            Calls.Add("inbox");
            return Task.FromResult(0);
        }

        public bool MasteryRequested() => false;

        public Task<string> ApplyMasteriesAsync(CancellationToken ct) => Task.FromResult("");

        public Task<string?> FillSparringPitAsync(bool dryRun, CancellationToken ct)
        {
            Calls.Add("pit");
            return Task.FromResult<string?>(null);
        }

        public Task<int> ClaimPassRewardsAsync(bool dryRun, CancellationToken ct)
        {
            Calls.Add("pass");
            return Task.FromResult(0);
        }

        public Task<int> CollectFreeAsync(bool dryRun, CancellationToken ct)
        {
            Calls.Add("free");
            return Task.FromResult(1);
        }

        public Task<ArtifactOutcome?> UpgradeArtifactAsync(int? minLevel, bool attempts, long keepSilver, bool dryRun, CancellationToken ct)
        {
            Calls.Add($"upgrade {minLevel} {attempts} {keepSilver}");
            return Task.FromResult<ArtifactOutcome?>(new ArtifactOutcome("done", 2, 12));
        }
    }

    private static readonly DateTime Start = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private static QuestNeed Need(QuestNeedKind kind, int left, int? min = null) => new(kind, left, kind.ToString(), "Daily", true, MinStage: min);

    private static (PlayContext Ctx, Game Game, ChoresActivity Chores) Make(params QuestNeed[] needs)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Chores = new ChoresSettings { Enabled = true, Cleanup = new GearCleanupSettings { Enabled = false } } } };
        var game = new Game();
        return (new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add) { QuestNeeds = [.. needs] }, game, new ChoresActivity(game));
    }

    [Fact]
    public async Task Auto_join_clan_runs_when_switched_on_and_not_again_for_six_hours()
    {
        var (off, _, offChores) = Make();
        Assert.NotEqual("clan", offChores.Decide(off).Why);     // off by default
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Chores = new ChoresSettings { Enabled = true, AutoJoinClan = true, Cleanup = new GearCleanupSettings { Enabled = false } } } };
        var game = new Game();
        var ctx = new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add);
        var chores = new ChoresActivity(game);
        Assert.Equal("clan", chores.Decide(ctx).Why);
        Assert.Contains("joined", await chores.RunTurnAsync(ctx, CancellationToken.None));
        Assert.NotEqual("clan", chores.Decide(ctx).Why);
        Assert.Single(game.Calls, "clan");
    }

    [Fact]
    public async Task The_quests_jobs_come_first_then_the_market_and_the_advised_upgrades()
    {
        var (ctx, game, chores) = Make(Need(QuestNeedKind.Market, 1), Need(QuestNeedKind.LevelUp, 1), Need(QuestNeedKind.ArtifactAttempts, 4), Need(QuestNeedKind.ArtifactLevel, 1, 12));
        for (var i = 0; i < 6; i++) await chores.RunTurnAsync(ctx, CancellationToken.None);
        Assert.Equal(["market quest=True", "level", "upgrade  True 0", "upgrade  True 0", "upgrade 12 False 0", "inbox"], game.Calls);
    }

    [Fact]
    public async Task A_failed_job_waits_and_the_next_one_goes_on()
    {
        var (ctx, game, chores) = Make(Need(QuestNeedKind.Market, 1), Need(QuestNeedKind.LevelUp, 1));
        game.FailMarket = true;
        await Assert.ThrowsAsync<PlayStopException>(() => chores.RunTurnAsync(ctx, CancellationToken.None));
        await chores.RunTurnAsync(ctx, CancellationToken.None);
        Assert.Equal(["market quest=True", "level"], game.Calls);
    }

    [Fact]
    public async Task The_gear_clean_up_comes_first_once_a_day()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Chores = new ChoresSettings { Enabled = true } } };
        var game = new Game();
        var chores = new ChoresActivity(game);
        var ctx = new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add);
        Assert.True(chores.Full(ctx));
        Assert.Equal("cleanup", chores.Decide(ctx).Why);
        await chores.RunTurnAsync(ctx, CancellationToken.None);
        Assert.NotEqual("cleanup", chores.Decide(ctx).Why);
        Assert.False(chores.Full(ctx));
    }
}
