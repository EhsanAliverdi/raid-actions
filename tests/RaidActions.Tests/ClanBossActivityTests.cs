using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Clan Boss on a fake account (plan/clan-boss.md): keys spent on the best-damage difficulty, kept keys, Quick Battle, dry run.</summary>
public class ClanBossActivityTests
{
    private static readonly DateTime Start = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeClanBossGame Game, AutoplayEngine Engine) Make(ClanBossSettings cb, TimeSpan runFor, bool dryRun = false)
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, ClanBoss = cb } };
        var game = new FakeClanBossGame(() => host.UtcNow);
        host.OnDelay = h => { if (h.UtcNow - Start >= runFor) h.Stop = true; };
        return (host, game, new AutoplayEngine([new ClanBossActivity(game)], new FakeArenaGame(() => host.UtcNow), host, dryRun));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Every_key_goes_to_the_best_damage_difficulty_and_a_new_key_is_spent_when_it_comes()
    {
        var (host, game, engine) = Make(new ClanBossSettings { Enabled = true }, TimeSpan.FromHours(13));
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight Nightmare", "fight Nightmare", "fight Nightmare"], game.Calls);   // 2 keys, then the one that came after 12 h
        Assert.Equal(3, host.Events.Count(e => e.Area == ClanBossActivity.AreaId && e.Kind == "battle"));
    }

    [Fact]
    public async Task Kept_keys_are_never_spent_and_quick_battle_only_where_the_game_allows()
    {
        var (_, game, engine) = Make(new ClanBossSettings { Enabled = true, KeepKeys = 1, Difficulty = "Brutal", QuickBattle = true }, TimeSpan.FromHours(1));
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight Brutal quick"], game.Calls);
        Assert.Equal(1, game.Keys);
    }

    [Fact]
    public async Task A_dry_run_shows_the_battle_once_and_spends_nothing()
    {
        var (_, game, engine) = Make(new ClanBossSettings { Enabled = true }, TimeSpan.FromHours(1), dryRun: true);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["show Nightmare"], game.Calls);
        Assert.Equal(2, game.Keys);
    }

    [Fact]
    public async Task Hydra_keys_go_to_its_best_damage_difficulty_only_when_switched_on()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Hydra = new HydraSettings { Enabled = true, KeepKeys = 1 } } };
        var game = new FakeClanBossGame(() => host.UtcNow);
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromHours(1)) h.Stop = true; };
        await new AutoplayEngine([new HydraActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        Assert.Equal(["hydra Normal", "hydra Normal"], game.Calls);
        Assert.Equal(1, game.HydraKeys);
        Assert.False(new HydraActivity(game).IsEnabled(new AutoplaySettings()));      // off by default
    }
}
