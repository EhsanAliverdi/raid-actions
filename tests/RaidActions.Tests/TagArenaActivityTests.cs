using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Tag Team Arena on a fake board (plan/tag-arena.md): the weakest under the limit, the free refresh when nobody is left.</summary>
public class TagArenaActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private sealed class FakeTag(Func<DateTime> now) : ITagArenaGame
    {
        public double Tokens { get; set; } = 3;
        public List<ArenaOpponentInfo> Board { get; set; } = [];
        public bool FreeRefresh { get; set; } = true;
        public List<string> Calls { get; } = [];

        public Task<double> ReadTagTokensAsync(CancellationToken ct) => Task.FromResult(Tokens);
        public Task<ArenaBoard> OpenTagArenaAsync(CancellationToken ct) => Task.FromResult(new ArenaBoard(now(), [.. Board], FreeRefresh, null));

        public Task<BattleOutcome> FightTagOpponentAsync(long userId, bool dryRun, CancellationToken ct)
        {
            Calls.Add($"fight {userId}");
            Tokens--;
            Board = [.. Board.Select(o => o.UserId == userId ? o with { Status = OpponentStatus.Defeated } : o)];
            return Task.FromResult(new BattleOutcome(BattleResult.Victory, "3 rounds"));
        }

        public Task<ArenaBoard?> RefreshTagFreeAsync(bool dryRun, CancellationToken ct)
        {
            Calls.Add("refresh");
            FreeRefresh = false;
            Board = [new(9, "new", 90_000, OpponentStatus.None, true)];
            return Task.FromResult<ArenaBoard?>(new ArenaBoard(now(), [.. Board], false, TimeSpan.FromMinutes(15)));
        }
    }

    [Fact]
    public async Task The_weakest_under_the_limit_first_then_the_free_refresh_even_without_tokens()
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, TagArena = new ClassicArenaSettings { Enabled = true, MaxOpponentPower = 300_000 } },
        };
        var game = new FakeTag(() => host.UtcNow)
        {
            Tokens = 2,
            Board = [new(1, "big", 500_000, OpponentStatus.None, true), new(2, "small", 120_000, OpponentStatus.None, true), new(3, "mid", 200_000, OpponentStatus.None, true)],
        };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromHours(1)) h.Stop = true; };
        await new AutoplayEngine([new TagArenaActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Equal(["fight 2", "fight 3", "refresh"], game.Calls.Take(3));   // 120K, 200K; 500K is over the limit; no token left: refresh anyway
        Assert.Contains(host.Events, e => e.Area == TagArenaActivity.AreaId && e.Kind == "refresh");
    }
}
