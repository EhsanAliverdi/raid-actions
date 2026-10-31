using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Free rewards (plan/free-rewards.md): playtime gifts checked every so often, a failure retried at the next check.</summary>
public class FreeRewardsActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private sealed class FakeGifts : IFreeRewardsGame
    {
        public int Ready { get; set; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }

        public Task<int> CollectPlaytimeGiftsAsync(bool dryRun, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new PlayStopException("the gifts button is not recognised");
            if (dryRun) return Task.FromResult(0);
            var n = Ready;
            Ready = 0;
            return Task.FromResult(n);
        }

        public int QuestCalls { get; private set; }

        public List<QuestRead> Quests { get; set; } = [];

        public Task<QuestPass> QuestPassAsync(bool claim, bool dryRun, CancellationToken ct)
        {
            QuestCalls++;
            if (Fail) throw new PlayStopException("the quests button is not recognised");
            return Task.FromResult(new QuestPass(0, Quests));
        }
    }

    private static (FakeHost Host, FakeGifts Game, AutoplayEngine Engine) Make(TimeSpan runFor, bool dryRun = false)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, FreeRewards = new FreeRewardsSettings { Enabled = true } },
        };
        var game = new FakeGifts();
        host.OnDelay = h => { if (h.UtcNow - Start >= runFor) h.Stop = true; };
        return (host, game, new AutoplayEngine([new FreeRewardsActivity(game)], new FakeArenaGame(() => host.UtcNow), host, dryRun));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task The_gifts_are_checked_every_twenty_minutes_and_the_ready_ones_collected()
    {
        var (host, game, engine) = Make(TimeSpan.FromMinutes(65));
        game.Ready = 2;
        await engine.RunAsync(once: false, Limit());
        Assert.InRange(game.Calls, 3, 4);                              // 0, 20, 40, 60 minutes
        Assert.Single(host.Events, e => e.Kind == "collect" && e.Text.Contains("collected 2"));
    }

    [Fact]
    public async Task A_check_that_fails_waits_for_the_next_one_and_never_stops_the_bot()
    {
        var (host, game, engine) = Make(TimeSpan.FromMinutes(45));
        game.Fail = true;
        await engine.RunAsync(once: false, Limit());
        Assert.InRange(game.Calls, 2, 3);
        Assert.All(host.Events.Where(e => e.Area == FreeRewardsActivity.AreaId), e => Assert.Equal("error", e.Kind));
    }

    [Fact]
    public async Task The_quest_pass_turns_the_open_quests_into_needs_for_the_other_areas()
    {
        var (host, game, engine) = Make(TimeSpan.FromMinutes(5));
        game.Quests =
        [
            new(1, "Daily", "Win 5 Classic Arena battles", "WinArenaBattles", "2/5", false, false),
            new(2, "Advanced", "Defeat a Faction Wars boss", "FractionWarsMapTransition", "0/1", false, false),
            new(3, "Weekly", "Ascend a Champion", "AscendHero", "0/1", false, false),
            new(4, "Daily", "Summon a champion", "SummonHero", "1/1", true, true),
        ];
        await engine.RunAsync(once: false, Limit());
        var ev = host.Events.Single(e => e.Kind == "quest-plan");
        Assert.Contains("ArenaWin ×3", ev.Text);
        Assert.Contains("FactionWarsBoss ×1", ev.Text);
        Assert.Contains("given up: 1", ev.Text);
    }
}
