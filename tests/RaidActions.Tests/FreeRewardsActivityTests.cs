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

        public DateTime FirstQuestAt { get; private set; } = DateTime.MaxValue;

        public Task<QuestPass> QuestPassAsync(bool claim, bool dryRun, CancellationToken ct)
        {
            if (FirstQuestAt == DateTime.MaxValue) FirstQuestAt = DateTime.UtcNow;
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
    public async Task A_new_bot_on_the_account_does_not_check_again_before_the_interval()
    {
        // 2026-11-06: each rotation restarted the bot, and each one opened the reward pages at once.
        var (host, game, engine) = Make(TimeSpan.FromMinutes(5));
        await engine.RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Equal(1, game.Calls);
        host.Stop = false;
        var start = host.UtcNow;
        host.OnDelay = h => { if (h.UtcNow - start >= TimeSpan.FromMinutes(5)) h.Stop = true; };
        await new AutoplayEngine([new FreeRewardsActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.Equal(1, game.Calls);   // the second bot knew the last check (20 minutes apart)
    }

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

    /// <summary>Ehsan 2026-11-09: a mission done on another device while the bot was away; the bot's first act on the account is a quest read, which closes its task.</summary>
    [Fact]
    public async Task A_new_run_reads_the_quests_before_any_other_area_and_closes_tasks_of_quests_gone()
    {
        var turns = new List<string>();
        // The mission was finished on another device: the read shows only another quest.
        var game = new FakeGifts { Quests = [new QuestRead(2, "Daily", "Win 5 Classic Arena battles", "WinArenaBattles", "1/5", false, false)] };
        var book = new MemoryTaskBook();
        book.Put(new TaskRow(0, "quest:Mission:abc", "quest", "energy", "Mission: Win 5 Superior Spirit Potions", "quests"));
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, FreeRewards = new FreeRewardsSettings { Enabled = true, Quests = true } },
            Tasks = book,
        };
        host.OnDelay = h => { if (h.UtcNow - Start > TimeSpan.FromMinutes(5)) h.Stop = true; };
        var first = new Recorder("first", turns);
        await new AutoplayEngine([first, new FreeRewardsActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        Assert.True(game.QuestCalls >= 1);
        Assert.Equal("free-rewards", turns.Count == 0 || first.FirstAt > game.FirstQuestAt ? "free-rewards" : "first");
        Assert.Equal(TodoState.Done, book.Get("quest:Mission:abc")!.State);
    }

    private sealed class Recorder(string id, List<string> turns) : IActivity
    {
        public DateTime FirstAt { get; private set; } = DateTime.MaxValue;
        public string Id => id;
        public string Name => id;
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("go");
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct)
        {
            if (FirstAt == DateTime.MaxValue) FirstAt = DateTime.UtcNow;
            turns.Add(id);
            return Task.FromResult("turn");
        }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }
}
