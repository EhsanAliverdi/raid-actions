using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>The idle-time fixes of 2026-11-20: turn holds, Tag Team yielding, a refused Doom Tower team, chores once per run.</summary>
public class IdleFixesTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

    private static TaskRow Return(string area) => new(0, "fw-return:x", "return", area, "Back to the Reserve Vault without gear: Brute", "follow-up", TodoState.ToDo,
        Parent: "fw-gear:x", CreatedUtc: Now.AddMinutes(-30), UpdatedUtc: Now.AddMinutes(-30));

    [Fact]
    public void A_vault_put_back_of_an_area_that_does_not_act_does_not_hold_the_turn()
    {
        var open = new List<TaskRow> { Return("faction-wars") };
        Assert.Null(TurnHold.Pick(open, Now, new HashSet<string> { "classic-arena" }));
        Assert.NotNull(TurnHold.Pick(open, Now, new HashSet<string> { "faction-wars" }));
        Assert.Null(TurnHold.Pick(open, Now, new HashSet<string>()));
    }

    [Fact]
    public void An_in_progress_task_of_a_waiting_area_does_not_hold_the_turn()
    {
        var task = new TaskRow(0, "k", "gear", "faction-wars", "Gear", "faction-wars", TodoState.InProgress, UpdatedUtc: Now.AddMinutes(-5));
        Assert.Null(TurnHold.Pick([task], Now, new HashSet<string>()));
        Assert.NotNull(TurnHold.Pick([task], Now, new HashSet<string> { "faction-wars" }));
    }

    private sealed class FakeTagReady : ITagArenaGame
    {
        public double Tokens { get; set; } = 3;
        public Task<double> ReadTagTokensAsync(CancellationToken ct) => Task.FromResult(Tokens);
        public Task<ArenaBoard> OpenTagArenaAsync(CancellationToken ct) => Task.FromResult(new ArenaBoard(Now, [.. Enumerable.Range(1, 8).Select(i => new ArenaOpponentInfo(i, $"o{i}", 100_000 + i, OpponentStatus.None, true))], false, null));
        public Task<BattleOutcome> FightTagOpponentAsync(long userId, bool dryRun, CancellationToken ct) { Tokens--; return Task.FromResult(new BattleOutcome(BattleResult.Victory, "")); }
        public Task<ArenaBoard?> RefreshTagFreeAsync(bool dryRun, CancellationToken ct) => Task.FromResult<ArenaBoard?>(null);
    }

    [Fact]
    public async Task Tag_Team_with_too_few_tokens_for_the_quest_cannot_finish_it()
    {
        var host = new FakeHost(Now) { Settings = new AutoplaySettings { Enabled = true, TagArena = new ClassicArenaSettings { Enabled = true, MaxOpponentPower = 300_000 } } };
        var tag = new TagArenaActivity(new FakeTagReady { Tokens = 3 });
        var ctx = new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add);
        await tag.RunTurnAsync(ctx, CancellationToken.None); // reads the board and tokens (a battle may follow)
        var ok3 = tag.CanFinishQuest(ctx, 5, out var why);
        Assert.False(ok3);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public async Task A_refused_Normal_climb_rests_minutes_not_hours()
    {
        var host = new FakeHost(Now) { Settings = new AutoplaySettings { Enabled = true, DoomTower = new DoomTowerSettings { Enabled = true } } };
        var game = new FakeDoomTowerGame { NormalDone = false, NormalRefused = true, Gold = 10, Silver = 15 };
        var ctx = new PlayContext(host.Settings, new FakeArenaGame(() => host.UtcNow), host, false, host.Events.Add);
        var dt = new DoomTowerActivity(game);
        await dt.RunTurnAsync(ctx, CancellationToken.None);
        var held = ctx.Held("doom-tower:climb-normal");
        Assert.True(held.HasValue);
        Assert.True(held!.Value.Until - Now <= TimeSpan.FromMinutes(16), held.Value.Until.ToString());
    }
}
