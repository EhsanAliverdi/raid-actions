using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>The banner's Pause and Resume (Ehsan 2026-11-20), on the fakes and virtual time.</summary>
public class PauseTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static (FakeHost Host, FakeArenaGame Game, AutoplayEngine Engine) Make()
    {
        var settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true }, Pacing = new PacingSettings { SessionMinutes = 0 } };
        var host = new FakeHost(Start) { Settings = settings };
        var game = new FakeArenaGame(() => host.UtcNow) { Delay = host.DelayAsync };
        return (host, game, new AutoplayEngine([new ClassicArenaActivity()], game, host, false));
    }

    private static CancellationToken Limit() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Paused_the_bot_holds_with_a_paused_status_and_sends_nothing_then_resumes_and_goes_home()
    {
        var (host, game, engine) = Make();
        var pausedAt = Start + TimeSpan.FromMinutes(5);
        var statuses = new List<(string Engine, string Detail)>();
        var callsWhilePaused = -1;
        host.OnDelay = h =>
        {
            if (h.UtcNow - Start >= TimeSpan.FromMinutes(30)) h.Stop = true;
            if (h.Paused is null && h.UtcNow >= pausedAt && h.Resumes.Count == 0) { h.Paused = h.UtcNow; callsWhilePaused = game.Calls.Count; return; }
            if (h.Paused is { } since)
            {
                if (h.Last is { Engine: "paused" } l) statuses.Add((l.Engine, l.Detail));
                if (h.UtcNow - since >= TimeSpan.FromMinutes(10)) { Assert.Equal(callsWhilePaused, game.Calls.Count); h.Paused = null; game.Calls.Add("(resume marker)"); }
            }
        };
        await engine.RunAsync(false, Limit());
        Assert.Equal(1, host.PauseStarts);
        Assert.Single(host.Resumes);
        Assert.InRange(host.Resumes[0], TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11));
        Assert.NotEmpty(statuses);
        Assert.All(statuses, s => Assert.StartsWith("Paused since ", s.Detail));
        // After the resume the game goes home first.
        var i = game.Calls.IndexOf("(resume marker)");
        Assert.True(i >= 0 && game.Calls.Skip(i).Any(c => c == "home"), string.Join(",", game.Calls.Skip(i).Take(5)));
        Assert.Contains(host.Events, e => e.Kind == "pause");
        Assert.Contains(host.Events, e => e.Kind == "resume");
    }

    [Fact]
    public async Task An_input_refused_by_the_pause_is_not_an_error_and_the_turn_goes_on_after_the_resume()
    {
        var (host, game, engine) = Make();
        game.FailNext = PauseGate.Refusal; // the step's click found the pause
        host.Paused = Start;
        host.OnDelay = h =>
        {
            if (h.UtcNow - Start >= TimeSpan.FromMinutes(20)) h.Stop = true;
            if (h.Paused is { } p && h.UtcNow - p >= TimeSpan.FromMinutes(3)) h.Paused = null;
        };
        await engine.RunAsync(false, Limit());
        Assert.DoesNotContain(host.Events, e => e.Kind == "error");
        Assert.Contains(game.Calls, c => c.StartsWith("fight ", StringComparison.Ordinal)); // played after the resume
    }

    [Fact]
    public async Task The_slice_clock_is_given_back_by_the_pause_length()
    {
        var (host, game, engine) = Make();
        host.Paused = Start;
        host.OnDelay = h =>
        {
            if (h.UtcNow - Start >= TimeSpan.FromMinutes(25)) h.Stop = true;
            if (h.Paused is { } p && h.UtcNow - p >= TimeSpan.FromMinutes(7)) h.Paused = null;
        };
        await engine.RunAsync(false, Limit());
        Assert.Single(host.Resumes);
        Assert.Equal(TimeSpan.FromMinutes(7), host.Resumes[0]); // what the host shifts the turn's start by
    }

    [Theory]
    [InlineData("pause: the game is on another account of the pool; it plays on that one")]
    [InlineData("pause: the game is on an account the bot does not play; switching back to its own")]
    [InlineData("pause: the game was closed during the pause; starting it again")]
    public async Task After_the_pause_the_run_ends_when_the_game_is_on_another_account_or_closed(string reason)
    {
        var (host, game, engine) = Make();
        host.Paused = Start;
        host.PauseEndedFor = reason;
        host.OnDelay = h => { if (h.Paused is { } p && h.UtcNow - p >= TimeSpan.FromMinutes(1)) h.Paused = null; };
        var why = await engine.RunAsync(false, Limit());
        Assert.Equal(reason, why);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("fight ", StringComparison.Ordinal));
        Assert.Equal("stopped", host.Last!.Engine);
    }

    [Fact]
    public void Input_is_refused_while_the_gate_says_paused()
    {
        var paused = true;
        PauseGate.IsPaused = () => paused;
        try
        {
            Assert.Equal(PauseGate.Refusal, PauseGate.Check());
            paused = false;
            Assert.Null(PauseGate.Check());
        }
        finally { PauseGate.IsPaused = null; }
    }
}
