using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Faction Wars after the daily Tag Team quest (its reward is the crypt keys), and all of a crypt's battles in one turn.</summary>
public class FwTagOrderTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);

    private sealed class FakeTag(Func<DateTime> now, List<string> log) : ITagArenaGame
    {
        public double Tokens { get; set; } = 5;
        private List<ArenaOpponentInfo> _board = [.. Enumerable.Range(1, 8).Select(i => new ArenaOpponentInfo(i, $"o{i}", 100_000 + i, OpponentStatus.None, true))];
        public Task<double> ReadTagTokensAsync(CancellationToken ct) => Task.FromResult(Tokens);
        public Task<ArenaBoard> OpenTagArenaAsync(CancellationToken ct) => Task.FromResult(new ArenaBoard(now(), [.. _board], false, null));
        public Task<BattleOutcome> FightTagOpponentAsync(long userId, bool dryRun, CancellationToken ct)
        {
            log.Add("tag");
            Tokens--;
            _board = [.. _board.Select(o => o.UserId == userId ? o with { Status = OpponentStatus.Defeated } : o)];
            return Task.FromResult(new BattleOutcome(BattleResult.Victory, "3 rounds"));
        }
        public Task<ArenaBoard?> RefreshTagFreeAsync(bool dryRun, CancellationToken ct) => Task.FromResult<ArenaBoard?>(null);
    }

    private static (FakeHost Host, FakeFactionWarsGame Fw, FakeTag Tag, List<string> Log, AutoplayEngine Engine) Make(double tokens, bool needTag = true, bool gear = false)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Quests = new QuestSettings { Enabled = true },
                FactionWars = new FactionWarsSettings { Enabled = true, GearTeam = false, RemoveGearAfter = false, StripYesterdaysTeams = false },
                Teams = gear ? new() { ["faction-wars"] = new AreaTeams { Default = new TeamChoice { Mode = TeamMode.BotChooses, GearUp = true } } } : [],
                TagArena = new ClassicArenaSettings { Enabled = true, MaxOpponentPower = 300_000 },
            },
        };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(90)) h.Stop = true; };
        var log = new List<string>();
        var fw = new FakeFactionWarsGame(() => host.UtcNow) { Delay = host.DelayAsync };
        var tag = new FakeTag(() => host.UtcNow, log) { Tokens = tokens };
        fw.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 4, HighestPassed = 10, Team = [1, 2, 3] });
        // Faction Wars first in the order, so only the rule puts Tag Team before it.
        var need = needTag ? [new QuestNeed(QuestNeedKind.TagArena, 5, "Fight 5 Tag Team Arena battles", "Daily", true)] : new List<QuestNeed>();
        var engine = new AutoplayEngine([new FactionWarsActivity(fw), new TagArenaActivity(tag)], new FakeArenaGame(() => host.UtcNow), host, false) { StartNeeds = need };
        return (host, fw, tag, log, engine);
    }

    private static string Dump(FakeHost host) => string.Join(" / ", host.Events.Select(e => $"{e.Utc:HH:mm} {e.Area} {e.Kind} {e.Text}"));

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task The_Tag_quest_battles_come_before_Faction_Wars_starts()
    {
        var (host, _, _, log, engine) = Make(tokens: 5);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(5, log.Count(x => x == "tag"));
        var order = host.Events.Where(e => e.Kind == "battle").Select(e => e.Area).ToList();
        Assert.True(order.Take(5).All(a => a == "tag-arena"), Dump(host));
        Assert.Contains("faction-wars", order.Skip(5));
        Assert.Contains(host.Events, e => e.Area == "faction-wars" && e.Kind == "battle");
    }

    [Fact]
    public async Task Without_Tag_tokens_Faction_Wars_plays_anyway()
    {
        var (host, fw, _, log, engine) = Make(tokens: 0);
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain("tag", log);
        Assert.Contains(fw.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_open_Tag_quest_the_order_is_unchanged()
    {
        var (_, fw, _, log, engine) = Make(tokens: 5, needTag: false);
        await engine.RunAsync(once: false, Limit());
        Assert.Contains(fw.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_the_keys_of_a_crypt_are_played_in_one_turn_after_one_gear()
    {
        var (host, fw, _, _, engine) = Make(tokens: 0, needTag: false, gear: true);
        fw.Open[0].Keys = 5;
        await engine.RunAsync(once: false, Limit());
        Assert.Single(fw.Calls, c => c.StartsWith("gear", StringComparison.Ordinal));
        Assert.Equal(5, fw.Calls.Count(c => c.StartsWith("fight", StringComparison.Ordinal)));
        Assert.True(host.Events.Count(e => e.Area == "faction-wars" && e.Kind == "turn") == 1, Dump(host)); // the gear and the five battles were one turn
    }

    private static (FakeHost Host, FakeFactionWarsGame Fw, AutoplayEngine Engine) MakeBoss(bool bossQuest, Func<string, int, double?> rates)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Quests = new QuestSettings { Enabled = true },
                FactionWars = new FactionWarsSettings { Enabled = true, GearTeam = false, RemoveGearAfter = false, StripYesterdaysTeams = false },
            },
        };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(30)) h.Stop = true; };
        var fw = new FakeFactionWarsGame(() => host.UtcNow) { Delay = host.DelayAsync, ForecastFor = rates };
        fw.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 3, HighestPassed = 15, Team = [1, 2, 3] });
        fw.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "OgrynTribes", Keys = 3, HighestPassed = 21, Team = [4, 5, 6] });
        var need = bossQuest ? [new QuestNeed(QuestNeedKind.FactionWarsBoss, 1, "Defeat 1 Faction Wars Boss", "Daily", true)] : new List<QuestNeed>();
        return (host, fw, new AutoplayEngine([new FactionWarsActivity(fw)], new FakeArenaGame(() => host.UtcNow), host, false) { StartNeeds = need });
    }

    [Fact]
    public async Task A_boss_quest_plays_the_highest_boss_stage_beatable_across_the_crypts()
    {
        // OgrynTribes 21 is too hard; 14 is beatable in both: the better rate wins the tie.
        var (_, fw, engine) = MakeBoss(true, (f, st) => st == 21 ? 0.3 : st == 14 ? (f == "SacredOrder" ? 0.9 : 0.8) : 0.95);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal("fight SacredOrder 14", fw.Calls.First(c => c.StartsWith("fight", StringComparison.Ordinal)));
        Assert.Single(fw.Forecasts, f => f == "OgrynTribes 21"); // once a day
    }

    [Fact]
    public async Task A_boss_quest_with_no_beatable_boss_is_given_up_and_logged_and_normal_play_goes_on()
    {
        var (host, fw, engine) = MakeBoss(true, (f, st) => st % 7 == 0 ? 0.3 : 0.9);
        await engine.RunAsync(once: false, Limit());
        Assert.Contains(host.Events, e => e.Kind == "quest" && e.Text.StartsWith("no Faction Wars boss beatable (best: OgrynTribes stage 21, 30%)", StringComparison.Ordinal));
        Assert.Equal(1, host.Events.Count(e => e.Text.StartsWith("no Faction Wars boss beatable", StringComparison.Ordinal)));
        Assert.Contains(fw.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));
        Assert.DoesNotContain(fw.Calls, c => c is "fight SacredOrder 14" or "fight OgrynTribes 14");
    }

    [Fact]
    public async Task Without_a_boss_quest_the_normal_choice_is_played_and_no_boss_is_simulated()
    {
        var (_, fw, engine) = MakeBoss(false, (f, st) => 0.9);
        await engine.RunAsync(once: false, Limit());
        Assert.Contains(fw.Calls, c => c == "fight SacredOrder 16");
        Assert.DoesNotContain(fw.Forecasts, f => f.EndsWith(" 7", StringComparison.Ordinal) || f.EndsWith(" 14", StringComparison.Ordinal));
    }
}
