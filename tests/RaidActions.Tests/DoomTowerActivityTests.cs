using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's Doom Tower on a fake tower: every silver and gold key spent on beaten stages, kept keys kept, a quest first.</summary>
public class DoomTowerActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private static DtStage S(int id, int status) => new(id, DoomTowerRules.KindOf(id, false), status, DoomTowerRules.FloorOf(id), DoomTowerRules.HardOf(id));

    private static (FakeHost Host, FakeDoomTowerGame Game, AutoplayEngine Engine) Make(DoomTowerSettings dt)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, DoomTower = dt } };
        var game = new FakeDoomTowerGame();
        game.Stages.AddRange([S(7021801, 2), S(7021802, 1), S(7021040, 2), S(7021050, 2), S(7021060, 1)]);
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromHours(3)) h.Stop = true; };
        return (host, game, new AutoplayEngine([new DoomTowerActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Every_key_goes_to_a_beaten_room_or_the_highest_boss_beaten()
    {
        var (_, game, engine) = Make(new DoomTowerSettings { Enabled = true });
        (game.Silver, game.Gold) = (2, 2);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight 7021801", "fight 7021801", "fight 7021050", "fight 7021050"], game.Calls);
        Assert.Equal((0d, 0d), (game.Silver, game.Gold));
    }

    [Fact]
    public async Task Kept_keys_stay_and_a_lost_boss_is_not_tried_again()
    {
        var (_, game, engine) = Make(new DoomTowerSettings { Enabled = true, KeepSilver = 2, UseGold = true });
        (game.Silver, game.Gold) = (2, 3);
        game.Loses.Add(7021050);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight 7021050", "fight 7021040", "fight 7021040", "fight 7021040"], game.Calls);
        Assert.Equal(2, game.Silver);
    }
}
