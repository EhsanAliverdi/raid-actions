using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>The game's background battle: the state reader on a fake object graph, and the engine's collect / hold at each turn start.</summary>
public class BackgroundBattleTests
{
    private sealed class Graph : IGameObjects
    {
        private readonly Dictionary<ulong, (string Class, Dictionary<string, object?> F)> _o = [];
        private ulong _next = 0x100;
        public ulong Add(string cls, Dictionary<string, object?> f) { _o[_next += 0x10] = (cls, f); return _next; }
        public ulong App;
        public ulong Root(string name, string expectedType) => App;
        public string ClassName(ulong obj) => _o[obj].Class;
        public ulong Ref(ulong obj, string type, string member) => _o[obj].F.TryGetValue(member, out var v) && v is ulong p ? p : 0;
        public int Int(ulong obj, string type, string member) => (int)_o[obj].F[member]!;
        public bool Bool(ulong obj, string type, string member) => (bool)_o[obj].F[member]!;
        public string? String(ulong obj, string type, string member) => null;
        public IReadOnlyList<string> ClassChain(ulong obj) => [_o[obj].Class];
        public int? NullableInt(ulong obj, string type, string member) => null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => [];
        public string? EnumName(string enumType, int value) => enumType == BackgroundBattleReader.StateEnum
            ? new[] { "UnStarted", "Started", "Finished", "EndedOnCurrentBattle", "StoppedMultiBattle", "EndedAfterStoppedBattle" }.ElementAtOrDefault(value) : null;
    }

    private static Graph Make(int state, int stage = 5206012, int done = 7, bool climb = false, bool noWrapper = false)
    {
        var g = new Graph();
        var stats = g.Add("SharedModel.Meta.Battle.AutoBattle.UserBackgroundBattleStats", new() { ["LastStageId"] = stage, ["FirstStageId"] = stage - 2, ["CompletedBattles"] = done, ["WonBattles"] = done - 1, ["IsAutoClimb"] = climb });
        var data = g.Add("SharedModel.Meta.Battle.UpdatableUserBattleData", new() { ["BackgroundBattleStats"] = stats });
        var report = g.Add("Client.Model.Gameplay.AutoBattle.AutoBattleReport", new() { ["StageId"] = stage, ["FirstStageId"] = stage - 2, ["BattleCount"] = done, ["VictoryBattlesCount"] = done - 1, ["IsAutoClimb"] = climb });
        var wrapper = g.Add("Client.Model.Gameplay.AutoBattle.AutoBattleWrapper", new() { ["State"] = state, ["BattleData"] = data, ["_report"] = report });
        var user = g.Add("Client.Model.Guard.UserWrapper", new() { ["BackgroundAutoBattle"] = noWrapper ? null : wrapper });
        g.App = g.Add("Client.Model.AppModel", new() { ["_userWrapper"] = user });
        return g;
    }

    [Fact]
    public void A_running_run_is_read_with_its_stage_and_progress()
    {
        var b = Assert.Single(new BackgroundBattleReader(Make(1)).Read());
        Assert.True(b.Running);
        Assert.False(b.Finished);
        Assert.Equal((5206012, 7, 6), (b.StageId, b.Completed, b.Won));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void An_ended_run_has_its_results_waiting(int state) =>
        Assert.True(Assert.Single(new BackgroundBattleReader(Make(state)).Read()).Finished);

    [Fact]
    public void No_run_reads_as_nothing_and_an_auto_climb_as_the_Doom_Tower()
    {
        Assert.Empty(new BackgroundBattleReader(Make(0)).Read());
        Assert.Empty(new BackgroundBattleReader(Make(1, noWrapper: true)).Read());
        var climb = Assert.Single(new BackgroundBattleReader(Make(1, climb: true)).Read());
        Assert.Equal("DoomTower", climb.Area);
    }

    private static readonly DateTime Start = new(2026, 11, 21, 18, 0, 0, DateTimeKind.Utc);

    private static async Task<(FakeHost Host, FakeArenaGame Game)> Run(BackgroundBattleInfo? bg, bool dryRun = false, bool sticks = false)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true }, Pacing = new PacingSettings() with { SessionMinutes = 0 } } };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync, Background = bg, BackgroundSticks = sticks };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(10)) h.Stop = true; };
        await new AutoplayEngine([new ClassicArenaActivity()], game, host, dryRun).RunAsync(once: false, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        return (host, game);
    }

    [Fact]
    public async Task A_finished_run_is_collected_before_any_battle_and_logged()
    {
        var (host, game) = await Run(new BackgroundBattleInfo(5206012, 5206010, "Dungeon", "Finished", 7, 6, false));
        var collected = game.Calls.IndexOf("collect-background 5206012");
        Assert.True(collected >= 0);
        Assert.DoesNotContain(game.Calls.Take(collected), c => c.StartsWith("fight ", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Kind == "reward" && e.Text.Contains("background battle collected"));
    }

    [Fact]
    public async Task A_running_run_holds_the_battle_areas_and_is_logged_not_collected()
    {
        var (host, game) = await Run(new BackgroundBattleInfo(5206012, 5206010, "Dungeon", "Started", 3, 3, false));
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("collect-background", StringComparison.Ordinal) || c.StartsWith("fight ", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Text.Contains("background battle running"));
    }

    [Fact]
    public async Task A_finished_auto_climb_report_that_stays_after_one_collect_no_longer_holds_battles()
    {
        // 2026-11-22: the game kept the 08:10 climb's report "Finished"; every battle area was held at 16:07.
        var (host, game) = await Run(new BackgroundBattleInfo(7031002, 7031002, "DoomTower", "Finished", 1, 0, true), sticks: true);
        Assert.Single(game.Calls, c => c.StartsWith("collect-background", StringComparison.Ordinal));
        Assert.Contains(game.Calls, c => c.StartsWith("fight ", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Text.Contains("ignored from now on"));
    }

    [Fact]
    public async Task A_dry_run_only_says_it_would_collect()
    {
        var (host, game) = await Run(new BackgroundBattleInfo(5206012, 5206010, "Dungeon", "Finished", 7, 6, false), dryRun: true);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("collect-background", StringComparison.Ordinal));
        Assert.Contains(host.Events, e => e.Text.Contains("results waiting"));
    }

    [Fact]
    public void Chores_and_free_rewards_are_not_battle_areas() =>
        Assert.Equal(["chores", "free-rewards"], AutoplayEngine.NonBattleAreas.Order().ToArray());
}
