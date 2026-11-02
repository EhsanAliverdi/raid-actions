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
    public async Task A_key_spent_on_a_battle_that_went_wrong_counts_so_the_row_gets_no_second_key()
    {
        var (_, game, engine) = Make(new ClanBossSettings { Enabled = true, Plan = [new("Brutal", 1), new("Nightmare", 2)] }, TimeSpan.FromHours(2));
        game.FailAfterSpend = true;
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight Brutal", "fight Nightmare"], game.Calls);
    }

    [Fact]
    public async Task A_difficulty_past_its_top_chest_gets_no_key_and_the_next_row_does()
    {
        var (host, game, engine) = Make(new ClanBossSettings { Enabled = true, Plan = [new("Brutal", 2), new("Nightmare", 1)] }, TimeSpan.FromHours(2));
        game.Standing["Brutal"] = new ClanStanding(23_030_000, 1, 21_700_000);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight Nightmare"], game.Calls);
    }

    [Fact]
    public async Task An_uncapped_difficulty_with_a_key_spent_and_its_top_chest_unread_gets_no_more()
    {
        var (_, game, engine) = Make(new ClanBossSettings { Enabled = true, Plan = [new("UltraNightmare", null)] }, TimeSpan.FromHours(2));
        game.Standing["UltraNightmare"] = new ClanStanding(5_000_000, 1, null);
        await engine.RunAsync(once: false, Limit());
        Assert.Empty(game.Calls);
    }

    [Fact]
    public async Task A_restart_keeps_the_top_chest_when_the_game_cannot_be_read()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, ClanBoss = new ClanBossSettings { Enabled = true, Plan = [new("Nightmare", 2)] } } };
        var game = new FakeClanBossGame(() => host.UtcNow);
        game.Standing["Nightmare"] = new ClanStanding(44_000_000, 1, null);
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromHours(2)) h.Stop = true; };
        var clan = new ClanBossActivity(game);
        clan.Restore([new AutoplayEvent(Start.AddDays(-1), ClanBossActivity.AreaId, "standing", "Nightmare: 0 damage today, top chest 39,170,000, 0 key(s)")], Start);
        await new AutoplayEngine([clan], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        Assert.Empty(game.Calls);
    }

    [Fact]
    public async Task The_Chimera_spends_its_keys_on_its_best_difficulty()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Chimera = new HydraSettings { Enabled = true } } };
        var game = new FakeClanBossGame(() => host.UtcNow);
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromHours(2)) h.Stop = true; };
        await new AutoplayEngine([new ChimeraActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        Assert.Equal(["chimera Normal", "chimera Normal"], game.Calls);
    }

    [Fact]
    public void A_building_label_is_found_wherever_the_camera_is()
    {
        using var image = new OpenCvSharp.Mat(732, 1298, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(40, 40, 40));
        OpenCvSharp.Cv2.PutText(image, "Market", new OpenCvSharp.Point(400, 300), OpenCvSharp.HersheyFonts.HersheySimplex, 1, new OpenCvSharp.Scalar(255, 255, 255), 2);
        using var label = new OpenCvSharp.Mat(image, new OpenCvSharp.Rect(395, 270, 120, 40)).Clone();
        var at = RaidActions.Game.Memory.VillageSpot.Find(image, label);
        Assert.NotNull(at);
        Assert.InRange(at.Value.X, 450, 460);
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(395, 270, 120, 40), new OpenCvSharp.Scalar(40, 40, 40), -1);
        Assert.Null(RaidActions.Game.Memory.VillageSpot.Find(image, label));
    }

    [Fact]
    public void The_gold_floor_is_found_and_a_finished_floor_is_not()
    {
        using var image = new OpenCvSharp.Mat(732, 1298, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(20, 30, 90));
        var gold = new OpenCvSharp.Scalar(60, 150, 190);
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(868, 423, 97, 134), gold, 12);
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(31, 652, 266, 50), gold, -1);
        Assert.Null(RaidActions.Game.Memory.DoomTowerSpot.Find(image));
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(675, 383, 112, 145), gold, -1);
        var at = RaidActions.Game.Memory.DoomTowerSpot.Find(image);
        Assert.NotNull(at);
        Assert.InRange(at.Value.X, 725, 737);
        Assert.InRange(at.Value.Y, 450, 460);
    }

    [Fact]
    public async Task The_tower_climbs_and_rests_a_day_after_a_loss()
    {
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, DoomTower = new DoomTowerSettings { Enabled = true, Climb = true } } };
        var tower = new FakeDoomTowerGame { ClimbResult = BattleResult.Defeat };
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromHours(3)) h.Stop = true; };
        await new AutoplayEngine([new DoomTowerActivity(tower)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        Assert.Equal(1, tower.Climbs);
    }

    private sealed class FullArea(string id, List<string> turns) : IActivity
    {
        public string Id => id;
        public string Name => id;
        public bool IsEnabled(AutoplaySettings settings) => true;
        public bool Full(PlayContext ctx) => id == "full";
        public Decision Decide(PlayContext ctx) => Decision.Now("go");
        public Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct) { turns.Add(id); return Task.FromResult("turn"); }
        public AreaStatus Status(PlayContext ctx, Decision decision) => new(Id, Name, true, AreaState.Playing, "", null, [], []);
    }

    [Fact]
    public async Task A_full_area_goes_first_but_another_gets_a_turn_after_three()
    {
        var turns = new List<string>();
        var host = new FakeHost(Start) { Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 } } };
        host.OnDelay = h => { if (turns.Count >= 8) h.Stop = true; };
        await new AutoplayEngine([new FullArea("other", turns), new FullArea("full", turns)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        Assert.Equal(["full", "full", "full", "other", "full", "full", "full", "other"], turns.Take(8));
    }

    [Fact]
    public async Task The_games_count_of_keys_replaces_the_bots()
    {
        var (_, game, engine) = Make(new ClanBossSettings { Enabled = true, Plan = [new("Brutal", 1), new("Nightmare", 1)] }, TimeSpan.FromHours(2));
        game.Standing["Brutal"] = new ClanStanding(5_000_000, 1, 21_700_000);
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight Nightmare"], game.Calls);
    }

    [Fact]
    public async Task An_area_with_many_turns_steps_aside_after_three()
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings { Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, ClanBoss = new ClanBossSettings { Enabled = true }, DoomTower = new DoomTowerSettings { Enabled = true } },
        };
        var clan = new FakeClanBossGame(() => host.UtcNow) { Keys = 8 };
        var tower = new FakeDoomTowerGame { Silver = 2 };
        tower.Stages.Add(new DtStage(120, DtKind.Boss, 2, 120, false));
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(5)) h.Stop = true; };
        await new AutoplayEngine([new ClanBossActivity(clan), new DoomTowerActivity(tower)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        var battles = host.Events.Where(e => e.Kind == "battle").Select(e => e.Area).ToList();
        Assert.Equal(DoomTowerActivity.AreaId, battles[3]);
    }

    [Fact]
    public async Task The_page_s_order_decides_which_area_plays_first()
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, ClanBoss = new ClanBossSettings { Enabled = true }, DoomTower = new DoomTowerSettings { Enabled = true },
                AreaOrder = [DoomTowerActivity.AreaId, ClanBossActivity.AreaId],
            },
        };
        var order = new List<string>();
        var clan = new FakeClanBossGame(() => host.UtcNow);
        var tower = new FakeDoomTowerGame { Silver = 1 };
        tower.Stages.Add(new DtStage(120, DtKind.Boss, 2, 120, false));
        host.OnDelay = h => { if (h.UtcNow - Start >= TimeSpan.FromMinutes(5)) h.Stop = true; };
        await new AutoplayEngine([new ClanBossActivity(clan), new DoomTowerActivity(tower)], new FakeArenaGame(() => host.UtcNow), host, false).RunAsync(once: false, Limit());
        Assert.Single(tower.Calls);
        Assert.True(host.Events.FindIndex(e => e.Area == DoomTowerActivity.AreaId && e.Kind == "battle") < host.Events.FindIndex(e => e.Area == ClanBossActivity.AreaId && e.Kind == "battle"));
    }

    [Fact]
    public void The_clan_boss_day_starts_at_its_reset_not_at_local_midnight()
    {
        var before = new DateTime(2026, 11, 1, 9, 59, 0, DateTimeKind.Utc);
        var after = new DateTime(2026, 11, 1, 10, 1, 0, DateTimeKind.Utc);
        Assert.NotEqual(ClanBossRules.Day(before), ClanBossRules.Day(after));
        Assert.Equal(ClanBossRules.Day(after), ClanBossRules.Day(after.AddHours(23)));
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), ClanBossRules.NextReset(after));
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), ClanBossRules.NextReset(before));
    }

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

    [Fact]
    public async Task A_key_plan_plays_its_keys_per_difficulty_then_waits_for_tomorrow()
    {
        var (host, game, engine) = Make(new ClanBossSettings { Enabled = true, Plan = [new("Nightmare", 1), new("Brutal", 1)] }, TimeSpan.FromHours(1));
        game.Keys = 4;
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(["fight Nightmare", "fight Brutal"], game.Calls);
        Assert.Equal(2, game.Keys);                                                     // the plan is done: the rest kept for tomorrow
    }
}
