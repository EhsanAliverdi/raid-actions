using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Auto Play's engine and Classic Arena on the fake arena, on virtual time (hours of play in milliseconds).</summary>
public class AutoplayEngineTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc);

    private static AutoplaySettings ArenaOn(Func<ClassicArenaSettings, ClassicArenaSettings>? arena = null, Func<PacingSettings, PacingSettings>? pacing = null) =>
        new() { Enabled = true, ClassicArena = (arena ?? (a => a))(new ClassicArenaSettings { Enabled = true }), Pacing = (pacing ?? (p => p with { SessionMinutes = 0 }))(new PacingSettings()) };

    private static (FakeHost Host, FakeArenaGame Game, AutoplayEngine Engine) Make(AutoplaySettings settings, bool dryRun = false, TimeSpan? stopAfter = null)
    {
        var host = new FakeHost(Start) { Settings = settings };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync };
        if (stopAfter is { } after) host.OnDelay = h => { if (h.UtcNow - Start >= after) h.Stop = true; };
        return (host, game, new AutoplayEngine([new ClassicArenaActivity()], game, host, dryRun));
    }

    /// <summary>A real-time limit: a loop that stops advancing virtual time fails the test instead of hanging the run.</summary>
    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static IEnumerable<long> Fought(FakeArenaGame g) => g.Calls.Where(c => c.StartsWith("fight ", StringComparison.Ordinal)).Select(c => long.Parse(c[6..]));

    [Fact]
    public async Task Switched_off_it_stops_at_once_and_touches_nothing()
    {
        var (host, game, engine) = Make(new AutoplaySettings());
        Assert.Equal("Auto Play is switched off", await engine.RunAsync(once: false, Limit()));
        Assert.Empty(game.Calls);
        Assert.Equal("stopped", host.Last!.Engine);
    }

    [Fact]
    public async Task Three_hours_of_arena_fight_only_under_the_limit_with_free_refreshes_and_regenerated_tokens()
    {
        var (host, game, engine) = Make(ArenaOn(), stopAfter: TimeSpan.FromHours(3));
        var seen = new Dictionary<long, int>();
        game.Calls.Clear();
        Assert.Equal("stopped from the app", await engine.RunAsync(once: false, Limit()));

        var fought = Fought(game).ToList();
        // 10 tokens at the start + about one every 12 minutes for 3 hours: every token spent, none bought.
        Assert.InRange(fought.Count, 20, 26);
        Assert.Contains("refresh", game.Calls);
        Assert.InRange(game.Tokens, 0, 1);
        var battles = host.Events.Where(e => e.Kind == "battle").ToList();
        Assert.Equal(fought.Count, battles.Count);
        Assert.All(battles, e => Assert.Matches(@"^(Victory|Defeat) vs Player\d \((\d{1,2},\d{3})\)", e.Text));
        // Every opponent fought was at most 100,000 (the fake throws for a token it does not have or a paid refresh).
        Assert.All(battles, e => Assert.True(int.Parse(System.Text.RegularExpressions.Regex.Match(e.Text, @"\(([\d,]+)\)").Groups[1].Value.Replace(",", "")) <= 100_000));
        var arena = host.Last!.Areas.Single();
        Assert.Equal(fought.Count, arena.Counters["Battles"]);
        Assert.Contains(host.Last.Resources, r => r.Resource == "Token");
        Assert.DoesNotContain(host.Events, e => e.Kind == "error");
    }

    [Fact]
    public async Task Out_of_tokens_it_takes_the_inbox_tokens_once_then_waits_for_regeneration()
    {
        var (host, game, engine) = Make(ArenaOn(), stopAfter: TimeSpan.FromMinutes(30));
        game.Tokens = 0;
        game.InboxTokens = 5;
        await engine.RunAsync(once: false, Limit());
        Assert.Single(game.Calls, "inbox"); // collected once; not checked again within the hour
        Assert.Contains(host.Events, e => e.Kind == "inbox" && e.Text == "collected 5 arena tokens from the inbox");
        Assert.True(Fought(game).Count() >= 5);
    }

    [Fact]
    public async Task Inbox_tokens_are_left_alone_when_switched_off()
    {
        var (_, game, engine) = Make(ArenaOn(a => a with { CollectInboxTokens = false }), stopAfter: TimeSpan.FromMinutes(20));
        game.Tokens = 0;
        game.InboxTokens = 5;
        await engine.RunAsync(once: false, Limit());
        Assert.DoesNotContain("inbox", game.Calls);
        Assert.Equal(5, game.InboxTokens);
    }

    [Fact]
    public async Task A_dry_run_shows_each_battle_and_the_refresh_and_presses_none()
    {
        var (host, game, engine) = Make(ArenaOn(), dryRun: true);
        game.FreeRefreshEvery = TimeSpan.Zero; // the free refresh is on offer
        var why = await engine.RunAsync(once: true, Limit());
        Assert.StartsWith("nothing to do now", why);
        Assert.Empty(Fought(game));
        Assert.DoesNotContain("refresh", game.Calls);
        var eligible = game.Board.Count(o => o.Power <= 100_000);
        Assert.Equal(eligible, game.Calls.Count(c => c.StartsWith("show-fight", StringComparison.Ordinal)));
        Assert.Contains("show-refresh", game.Calls);
        Assert.Equal(10, game.Tokens);
        Assert.True(host.Last!.DryRun);
    }

    [Fact]
    public async Task The_person_using_the_pc_keeps_the_bot_s_hands_off()
    {
        var (host, game, engine) = Make(ArenaOn(), stopAfter: TimeSpan.FromMinutes(10));
        host.OnDelay += h => h.Person = h.UtcNow; // the person keeps moving the mouse
        host.Person = Start;
        await engine.RunAsync(once: false, Limit());
        Assert.Empty(game.Calls);
        Assert.Contains(host.Events, e => e.Text.StartsWith("Auto Play stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Errors_send_the_game_home_and_back_off_and_too_many_in_a_row_stop_the_bot()
    {
        var (host, game, engine) = Make(ArenaOn(), stopAfter: TimeSpan.FromHours(1));
        game.FailNext = "unexpected popup";
        await engine.RunAsync(once: false, Limit());
        Assert.Contains(host.Events, e => e.Kind == "error" && e.Text == "unexpected popup");
        Assert.Contains("home", game.Calls);
        Assert.NotEmpty(Fought(game)); // it carried on after the back-off

        var (host2, game2, engine2) = Make(ArenaOn(), stopAfter: TimeSpan.FromHours(1));
        host2.OnDelay += _ => game2.FailNext = "the screen did not change";
        game2.FailNext = "the screen did not change";
        var why = await engine2.RunAsync(once: false, Limit());
        Assert.StartsWith("3 errors in a row", why);
        Assert.Equal("error", host2.Last!.Engine);
    }

    [Fact]
    public async Task Switching_the_area_off_in_the_app_stops_the_battles_at_once()
    {
        var (host, game, engine) = Make(ArenaOn(), stopAfter: TimeSpan.FromHours(1));
        host.OnDelay += h => { if (Fought(game).Count() >= 3) h.Settings = h.Settings with { ClassicArena = h.Settings.ClassicArena with { Enabled = false } }; };
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(3, Fought(game).Count());
        Assert.Equal(AreaState.Off, host.Last!.Areas.Single().State);
    }

    [Fact]
    public async Task The_daily_battle_limit_and_kept_tokens_are_respected()
    {
        var (_, game, engine) = Make(ArenaOn(a => a with { MaxBattlesPerDay = 4 }), stopAfter: TimeSpan.FromHours(2));
        await engine.RunAsync(once: false, Limit());
        Assert.Equal(4, Fought(game).Count());

        var (_, game2, engine2) = Make(ArenaOn(a => a with { KeepTokens = 7 }), stopAfter: TimeSpan.FromMinutes(5));
        await engine2.RunAsync(once: false, Limit());
        Assert.Equal(3, Fought(game2).Count());
        Assert.Equal(7, game2.Tokens);
    }

    [Fact]
    public async Task Outside_the_active_hours_it_rests()
    {
        var local = Start.ToLocalTime();
        var from = local.AddHours(2);
        var (host, game, engine) = Make(ArenaOn(pacing: p => p with { SessionMinutes = 0, ActiveFrom = from.ToString("HH:mm"), ActiveTo = from.AddHours(1).ToString("HH:mm") }),
            stopAfter: TimeSpan.FromMinutes(90));
        await engine.RunAsync(once: false, Limit());
        Assert.Empty(game.Calls);
        Assert.Equal("stopped", host.Last!.Engine);
    }
}
