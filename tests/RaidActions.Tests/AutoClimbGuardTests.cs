using RaidActions.Game.Memory;

namespace RaidActions.Tests;

/// <summary>The tower's Start never launches an auto-climb: the switch is read, turned off by one tap, or Start is not pressed.</summary>
public class AutoClimbGuardTests
{
    /// <summary>A fake game: the stored switch, what a tap does to it, and the log.</summary>
    private sealed class Fake(bool? state, bool tapWorks = true, bool readThrows = false)
    {
        public bool? State = state;
        public int Taps;
        public readonly List<string> Log = [];
        public bool? Read() => readThrows ? throw new InvalidOperationException("no memory") : State;
        public Task Tap() { Taps++; if (tapWorks && State == true) State = false; return Task.CompletedTask; }
        public Task<AutoClimbGuard.Result> Run() => AutoClimbGuard.EnsureOffAsync(Read, Tap, _ => Task.CompletedTask, Log.Add);
    }

    [Fact]
    public async Task On_is_tapped_once_then_start_is_allowed()
    {
        var f = new Fake(true);
        var r = await f.Run();
        Assert.True(r.CanStart);
        Assert.Equal(1, f.Taps);
        Assert.Contains(f.Log, l => l.Contains("was on: turned off"));
    }

    [Fact]
    public async Task On_and_staying_on_does_not_start_and_taps_only_once()
    {
        var f = new Fake(true, tapWorks: false);
        var r = await f.Run();
        Assert.False(r.CanStart);
        Assert.Equal(1, f.Taps);
        Assert.Contains("still on", r.Message);
    }

    [Fact]
    public async Task Unknown_does_not_start_and_is_never_tapped()
    {
        var f = new Fake(null);
        var r = await f.Run();
        Assert.False(r.CanStart);
        Assert.Equal(0, f.Taps);
        var t = new Fake(true, readThrows: true);
        Assert.False((await t.Run()).CanStart);
        Assert.Equal(0, t.Taps);
    }

    [Fact]
    public async Task Off_starts_directly_without_a_tap()
    {
        var f = new Fake(false);
        Assert.True((await f.Run()).CanStart);
        Assert.Equal(0, f.Taps);
        Assert.Contains(f.Log, l => l.Contains("auto-climb off"));
    }

    private sealed class Graph : IGameObjects
    {
        public readonly Dictionary<ulong, Dictionary<string, object>> F = [];
        public ulong Root(string name, string expectedType) => 1;
        public string ClassName(ulong obj) => "";
        public ulong Ref(ulong obj, string type, string member) => F.TryGetValue(obj, out var f) && f.TryGetValue(member, out var v) ? (ulong)v : 0;
        public int Int(ulong obj, string type, string member) => 0;
        public bool Bool(ulong obj, string type, string member) => (bool)F[obj][member];
        public string? String(ulong obj, string type, string member) => null;
        public IReadOnlyList<string> ClassChain(ulong obj) => [];
        public int? NullableInt(ulong obj, string type, string member) => null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => [];
        public string? EnumName(string enumType, int value) => null;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_reader_follows_the_account_to_AutoClimbMode(bool on)
    {
        var g = new Graph();
        g.F[1] = new() { ["_userWrapper"] = 2UL };
        g.F[2] = new() { ["User"] = 3UL };
        g.F[3] = new() { ["GameData"] = 4UL };
        g.F[4] = new() { ["StageData"] = 5UL };
        g.F[5] = new() { ["DoomTowerData"] = 6UL };
        g.F[6] = new() { ["AutoClimbMode"] = on };
        Assert.Equal(on, new AutoClimbReader(g).Read());
        g.F[5].Remove("DoomTowerData");
        Assert.Null(new AutoClimbReader(g).Read());
    }
}
