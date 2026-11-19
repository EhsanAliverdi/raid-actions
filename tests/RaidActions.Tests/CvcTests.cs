using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Clan vs Clan: the button's countdown text, and Faction Wars holding its crypt keys for the start.</summary>
public class CvcTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("05:12:33", 5 * 3600 + 12 * 60 + 33)]
    [InlineData("2d 04:10:00", 2 * 86400 + 4 * 3600 + 10 * 60)]
    [InlineData("1d 5h", 86400 + 5 * 3600)]
    [InlineData("5h 12m", 5 * 3600 + 12 * 60)]
    [InlineData("12m 30s", 12 * 60 + 30)]
    public void The_countdown_text_is_read(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), CvcStatus.ParseSpan(text));

    [Fact]
    public void The_button_note_gives_the_phase_and_the_start()
    {
        var s = CvcStatus.FromNote("1|03:00:00|Starts in", Start)!;
        Assert.Equal(CvcPhase.NotStarted, s.Phase);
        Assert.Equal(Start.AddHours(3), s.StartUtc);
        Assert.Equal(CvcPhase.Started, CvcStatus.FromNote("2|1d 2h|Ends in", Start)!.Phase);
        Assert.Equal(CvcPhase.NotStarted, CvcStatus.FromNote("|04:00:00|Starts in", Start)!.Phase); // state not read: the title says
        Assert.Null(CvcStatus.FromNote("||", Start));
    }

    private static (FakeHost Host, FakeFactionWarsGame Game, AutoplayEngine Engine) Make(TimeSpan stopAfter, bool tagQuest = false)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 }, Quests = new QuestSettings { Enabled = true },
                FactionWars = new FactionWarsSettings { Enabled = true, GearTeam = false, RemoveGearAfter = false, StripYesterdaysTeams = false },
            },
        };
        var game = new FakeFactionWarsGame(() => host.UtcNow) { Delay = host.DelayAsync };
        host.OnDelay = h => { if (h.UtcNow - Start >= stopAfter) h.Stop = true; };
        return (host, game, new AutoplayEngine([new FactionWarsActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false)
            { StartNeeds = tagQuest ? [new QuestNeed(QuestNeedKind.TagArena, 5, "Fight 5 Tag Team Arena battles", "Daily", true)] : [] });
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Keys_are_all_kept_while_Clan_vs_Clan_has_not_started_and_played_once_it_started()
    {
        CvcKeyHold.KeyCap = 18; CvcKeyHold.TagQuestKeys = 6;
        var (host, game, engine) = Make(TimeSpan.FromHours(5));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 6, HighestPassed = 10, Team = [1, 2, 3] });
        game.CvcPhase = CvcPhase.NotStarted;
        game.CvcStart = Start.AddHours(2); // no regeneration: nothing is lost by holding
        await engine.RunAsync(once: false, Limit());
        Assert.Contains(host.Events, e => e.Kind == "hold-cvc" && e.Text.StartsWith("keeping crypt keys for Clan vs Clan, starts in", StringComparison.Ordinal));
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("fight", StringComparison.Ordinal) && host.UtcNow < Start.AddHours(2));
        Assert.True(game.CvcReads < 15, "the state is cached, not read every turn");
    }

    [Fact]
    public async Task A_crypt_above_the_cap_minus_the_Tag_quest_reward_is_spent_down_to_it_while_the_quest_is_open()
    {
        CvcKeyHold.KeyCap = 18; CvcKeyHold.TagQuestKeys = 6;
        var (_, game, engine) = Make(TimeSpan.FromMinutes(40), tagQuest: true);
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 15, HighestPassed = 10, Team = [1, 2, 3] });
        game.CvcPhase = CvcPhase.NotStarted;
        game.CvcStart = Start.AddHours(1);
        await engine.RunAsync(once: false, Limit());
        Assert.InRange(game.Open[0].Keys, 9, 12); // down to 12 (a stage can cost several keys), never below
        Assert.Contains(game.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));
    }

    [Fact]
    public void Keep_holds_everything_without_regeneration_and_makes_room_for_the_Tag_reward()
    {
        CvcKeyHold.KeyCap = 18; CvcKeyHold.TagQuestKeys = 6;
        var cvc = new CvcStatus(CvcPhase.NotStarted, Start, Start.AddHours(20));
        var reset = Start.AddHours(30); // after the start: no second reward before it
        Assert.Equal(4, CvcKeyHold.Keep(cvc, Start, 4, 0, false, false, reset, out _)); // all held, whatever the time to the start
        Assert.Equal(18, CvcKeyHold.Keep(cvc, Start, 18, 0, false, false, reset, out _)); // no quest coming: all held, even at the cap
        Assert.Equal(12, CvcKeyHold.Keep(cvc, Start, 15, 0, false, true, reset, out _)); // the quest's +6 would overflow: down to 12
        Assert.Equal(12, CvcKeyHold.Keep(cvc, Start, 4, 0, false, true, reset, out _)); // below it: nothing to spend (Keys > keep false)
        CvcKeyHold.KeyCap = 20;
        Assert.Equal(14, CvcKeyHold.Keep(cvc, Start, 15, 0, false, true, reset, out _)); // the cap is configurable
        CvcKeyHold.KeyCap = 18;
    }

    [Fact]
    public void A_reset_before_the_start_pays_the_Tag_reward_again_and_a_Faction_Wars_quest_lifts_the_hold()
    {
        CvcKeyHold.KeyCap = 18; CvcKeyHold.TagQuestKeys = 6;
        var cvc = new CvcStatus(CvcPhase.NotStarted, Start, Start.AddHours(20));
        var reset = Start.AddHours(18);
        Assert.Equal(12, CvcKeyHold.Keep(cvc, Start, 15, 0, false, false, reset, out _)); // the next quest's +6 comes before the start
        Assert.Equal(0, CvcKeyHold.Keep(cvc, Start, 15, 0, true, false, reset, out _)); // a Faction Wars quest expires at the reset, before the start
        Assert.Equal(15, CvcKeyHold.Keep(cvc with { StartUtc = Start.AddHours(10) }, Start, 15, 0, true, false, reset, out _)); // starts first: held
    }
}
