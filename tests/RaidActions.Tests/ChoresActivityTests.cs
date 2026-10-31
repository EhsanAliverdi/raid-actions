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
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Chores = new ChoresSettings { Enabled = true } } };
        var game = new Game();
        return (new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add) { QuestNeeds = [.. needs] }, game, new ChoresActivity(game));
    }

    [Fact]
    public async Task The_quests_jobs_come_first_then_the_market_and_the_advised_upgrades()
    {
        var (ctx, game, chores) = Make(Need(QuestNeedKind.Market, 1), Need(QuestNeedKind.LevelUp, 1), Need(QuestNeedKind.ArtifactAttempts, 4), Need(QuestNeedKind.ArtifactLevel, 1, 12));
        for (var i = 0; i < 6; i++) await chores.RunTurnAsync(ctx, CancellationToken.None);
        Assert.Equal(["market quest=True", "level", "upgrade  True 0", "upgrade  True 0", "upgrade 12 False 0", "free"], game.Calls);
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
}
