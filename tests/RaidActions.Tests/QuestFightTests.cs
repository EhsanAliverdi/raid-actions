using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Quests the areas play their own way (plan/quests.md, Ehsan 2026-10-31): the 4-affinity arena fight, Cursed City's cleared stages.</summary>
public class QuestFightTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private static QuestNeed Need(QuestNeedKind kind, int left) => new(kind, left, kind.ToString(), "Advanced", true);

    [Fact]
    public async Task The_affinity_quest_takes_one_arena_fight_then_the_usual_team_fights()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true } } };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync };
        var ctx = new PlayContext(host.Settings, game, host, false, host.Events.Add) { QuestNeeds = [Need(QuestNeedKind.ArenaAffinities, 1)] };
        var arena = new ClassicArenaActivity();
        await arena.RunTurnAsync(ctx, CancellationToken.None);
        await arena.RunTurnAsync(ctx, CancellationToken.None);
        Assert.Equal(1, game.AffinityFights);
        Assert.Equal(2, game.Calls.Count(c => c.StartsWith("fight ", StringComparison.Ordinal)));
        Assert.Equal(0, ctx.QuestLeft(QuestNeedKind.ArenaAffinities));
    }

    [Fact]
    public async Task A_cleared_stages_quest_replays_a_beaten_level_before_opening_new_ones()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, CursedCity = new CursedCityPlaySettings { Enabled = true, GearUnused = false } } };
        var game = new FakeCursedCityGame { Keys = 4, WinsUpTo = 10 };
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 5, Team = [(1, false)], Wins = 1 });
        game.Levels.Add(new FakeCursedCityGame.Level { Number = 6, Team = [(1, false)], Open = true });
        var ctx = new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add) { QuestNeeds = [Need(QuestNeedKind.CursedCity, 2)] };
        var cc = new CursedCityActivity(game);
        for (var i = 0; i < 3; i++) await cc.RunTurnAsync(ctx, CancellationToken.None);
        Assert.Equal(["fight 5", "fight 5", "fight 6"], game.Calls);
        Assert.Equal(0, ctx.QuestLeft(QuestNeedKind.CursedCity));
    }
}
