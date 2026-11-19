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

    private static (FakeHost Host, FakeFactionWarsGame Game, AutoplayEngine Engine) Make(TimeSpan stopAfter)
    {
        var host = new FakeHost(Start)
        {
            Settings = new AutoplaySettings
            {
                Enabled = true, Pacing = new PacingSettings { SessionMinutes = 0 },
                FactionWars = new FactionWarsSettings { Enabled = true, GearTeam = false, RemoveGearAfter = false, StripYesterdaysTeams = false },
            },
        };
        var game = new FakeFactionWarsGame(() => host.UtcNow) { Delay = host.DelayAsync };
        host.OnDelay = h => { if (h.UtcNow - Start >= stopAfter) h.Stop = true; };
        return (host, game, new AutoplayEngine([new FactionWarsActivity(game)], new FakeArenaGame(() => host.UtcNow), host, false));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Keys_are_kept_when_Clan_vs_Clan_starts_before_they_would_cap_and_played_once_it_started()
    {
        CvcKeyHold.KeyCap = 10; CvcKeyHold.KeyRegen = TimeSpan.FromHours(2);
        var (host, game, engine) = Make(TimeSpan.FromHours(5));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 6, HighestPassed = 10, Team = [1, 2, 3] });
        game.CvcPhase = CvcPhase.NotStarted;
        game.CvcStart = Start.AddHours(2); // 4 keys from the cap = 8h: it starts first
        await engine.RunAsync(once: false, Limit());
        Assert.Contains(host.Events, e => e.Kind == "hold-cvc" && e.Text.StartsWith("keeping crypt keys for Clan vs Clan, starts in", StringComparison.Ordinal));
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("fight", StringComparison.Ordinal) && host.UtcNow < Start.AddHours(2));
        Assert.True(game.CvcReads < 15, "the state is cached, not read every turn");
    }

    [Fact]
    public async Task Keys_at_the_cap_are_spent_down_to_one_below_it_while_held()
    {
        CvcKeyHold.KeyCap = 10; CvcKeyHold.KeyRegen = TimeSpan.FromHours(2);
        var (_, game, engine) = Make(TimeSpan.FromMinutes(40));
        game.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 10, HighestPassed = 10, Team = [1, 2, 3] });
        game.CvcPhase = CvcPhase.NotStarted;
        game.CvcStart = Start.AddHours(1);
        await engine.RunAsync(once: false, Limit());
        Assert.InRange(game.Open[0].Keys, 7, 9); // a stage can cost several keys
        Assert.Single(game.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keys_are_played_normally_when_Clan_vs_Clan_starts_after_they_would_cap_or_is_running()
    {
        CvcKeyHold.KeyCap = 10; CvcKeyHold.KeyRegen = TimeSpan.FromHours(2);
        var (_, late, e1) = Make(TimeSpan.FromHours(1));
        late.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 6, HighestPassed = 10, Team = [1, 2, 3] });
        late.CvcPhase = CvcPhase.NotStarted;
        late.CvcStart = Start.AddHours(20);
        await e1.RunAsync(once: false, Limit());
        Assert.Contains(late.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));

        var (_, running, e2) = Make(TimeSpan.FromHours(1));
        running.Open.Add(new FakeFactionWarsGame.Crypt { Faction = "SacredOrder", Keys = 6, HighestPassed = 10, Team = [1, 2, 3] });
        running.CvcPhase = CvcPhase.Started;
        await e2.RunAsync(once: false, Limit());
        Assert.Contains(running.Calls, c => c.StartsWith("fight", StringComparison.Ordinal));
    }

    [Fact]
    public void A_quest_that_expires_before_the_start_lifts_the_hold()
    {
        CvcKeyHold.KeyCap = 10; CvcKeyHold.KeyRegen = TimeSpan.FromHours(4); // 4 keys: 24h to the cap
        var cvc = new CvcStatus(CvcPhase.NotStarted, Start, Start.AddHours(20));
        var reset = Start.AddHours(18);
        Assert.Equal(9, CvcKeyHold.Keep(cvc, Start, 4, 0, false, reset, out _));
        Assert.Equal(0, CvcKeyHold.Keep(cvc, Start, 4, 0, true, reset, out _)); // the quest resets at the day's end, before the start
        Assert.Equal(9, CvcKeyHold.Keep(cvc with { StartUtc = Start.AddHours(10) }, Start, 4, 0, true, reset, out _)); // starts before the quest ends
    }
}


