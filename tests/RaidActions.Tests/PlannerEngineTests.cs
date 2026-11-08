using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>The engine follows the plan (raid-extractor plan/todo-list.md): a due follow-up first, then a daily quest close to the reset.</summary>
public class PlannerEngineTests
{
    private static readonly DateTime Start = new(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private sealed class Area(string id, List<string> turns) : IActivity
    {
        public string Id => id;
        public string Name => id;
        public bool IsEnabled(AutoplaySettings settings) => true;
        public Decision Decide(PlayContext ctx) => Decision.Now("go");
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct) { turns.Add(id); return Task.FromResult("turn"); }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }

    private static async Task<List<string>> Run(MemoryTaskBook book, Func<PlannerSettings, PlannerSettings>? planner = null)
    {
        var turns = new List<string>();
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Planner = (planner ?? (p => p))(new PlannerSettings()) },
            Tasks = book,
        };
        host.OnDelay = h => { if (turns.Count >= 2) h.Stop = true; };
        await new AutoplayEngine([new Area("first", turns), new Area("faction-wars", turns), new Area("classic-arena", turns)],
            new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        return turns;
    }

    [Fact]
    public async Task A_due_follow_up_puts_its_area_first_and_one_waiting_for_its_trigger_does_not()
    {
        var book = new MemoryTaskBook();
        book.Put(new TaskRow(0, "fw-return:x", "return", "faction-wars", "back to the vault", "follow-up", TodoState.Blocked, DeadlineUtc: Start.AddHours(2), Parent: "fw-gear:x"));
        Assert.Equal("first", (await Run(book))[0]);
        book.Set("fw-return:x", TodoState.ToDo);
        Assert.Equal("faction-wars", (await Run(book))[0]);
        Assert.Equal("first", (await Run(book, p => p with { FollowUpsFirst = false }))[0]);
    }

    [Fact]
    public async Task A_daily_quest_close_to_the_reset_puts_its_area_first()
    {
        var book = new MemoryTaskBook();
        book.Put(new TaskRow(0, "quest:Daily:abc", "quest", "classic-arena", "Daily: Win 5 Classic Arena battles (2/5)", "quests", DeadlineUtc: Start.Date.AddDays(1)));
        Assert.Equal("classic-arena", (await Run(book))[0]);
        Assert.Equal("first", (await Run(book, p => p with { QuestDeadlineHours = 1 }))[0]);
    }
}
