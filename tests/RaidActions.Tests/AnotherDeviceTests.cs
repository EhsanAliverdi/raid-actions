using RaidActions.Autoplay;
using RaidActions.Autoplay.Fake;
using RaidExtractor;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

/// <summary>Ehsan 2026-11-09: an account open on another device ends its turn (other accounts play); a paused account is never switched to.</summary>
public class AnotherDeviceTests
{
    private static readonly DateTime Start = new(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc);

    private sealed class BlockedHost(DateTime start, string blocked, bool others) : IAutoplayHost
    {
        private readonly FakeHost _inner = new(start) { Settings = new AutoplaySettings { Enabled = true, ClassicArena = new ClassicArenaSettings { Enabled = true } } };
        public DateTime UtcNow => _inner.UtcNow;
        public Random Random => _inner.Random;
        public int ProcessId => _inner.ProcessId;
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) { _inner.UtcNow += delay; if (_inner.UtcNow - start > TimeSpan.FromHours(1)) _inner.Stop = true; return Task.CompletedTask; }
        public AutoplaySettings LoadSettings() => _inner.Settings;
        public bool StopRequested() => _inner.Stop;
        public DateTime? PersonLastInputUtc() => null;
        public string? InputBlocked() => blocked;
        public void WriteStatus(AutoplayStatus status) { }
        public void Event(AutoplayEvent e) { }
        public string? RotateDue(DateTime? nextUsefulUtc) => null; // a turn held (a task in progress): the other device still moves it on
        public bool CanRotate() => others;
    }

    [Fact]
    public void Open_on_another_device_ends_the_turn_when_other_accounts_wait_and_waits_when_none_do()
    {
        var blocked = AutoplayEngine.AnotherDevice + "; waiting";
        var engine = new AutoplayEngine([new ClassicArenaActivity()], new FakeArenaGame(() => Start), new BlockedHost(Start, blocked, others: true), false);
        Assert.StartsWith("rotation: " + AutoplayEngine.AnotherDevice, engine.RunAsync(once: false, CancellationToken.None).Result);
        var alone = new AutoplayEngine([new ClassicArenaActivity()], new FakeArenaGame(() => Start), new BlockedHost(Start, blocked, others: false), false);
        Assert.DoesNotContain("another device", alone.RunAsync(once: false, CancellationToken.None).Result);
    }

    [Fact]
    public void A_game_that_reloaded_mid_turn_is_taken_for_another_device()
    {
        Assert.True(AutoplayEngine.GameReloaded("AppModel static field: implausible pointer 0x0"));
        Assert.False(AutoplayEngine.GameReloaded("team-start: not on screen"));
    }

    [Fact]
    public void A_paused_account_is_skipped_and_its_own_turn_ends()
    {
        var pool = new AccountPool { Rotate = true, Accounts = [new() { Id = 1, PausedUntilUtc = Start.AddHours(1) }, new() { Id = 2 }, new() { Id = 3 }] };
        Assert.Equal(3, pool.Next(2, Start));
        Assert.Equal(2, pool.Next(3, Start));
        Assert.Equal(2, pool.Next(1, Start));
        Assert.Equal(1, pool.Next(3, Start.AddHours(2)));
        var turn = new AccountTurn { Id = 1, StartedUtc = Start };
        Assert.StartsWith("this account is paused", pool.RotateDue(1, turn, Start, null));
    }
}
