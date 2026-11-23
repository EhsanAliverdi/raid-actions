using RaidActions.App.Input;
using RaidActions.Game.Window;

namespace RaidActions.Tests;

public class BackgroundInputTests
{
    private class FakeNative : IBackgroundNative
    {
        public long Tick = 100_000;
        public long LastInputTick = 0;
        public GameWindowInfo? Window;
        public bool CanSetCursor = true;
        public readonly List<string> Log = [];
        public (int X, int Y) Cursor = (5, 6);
        public bool MinimizedStaysMinimized;
        public int ShowCalls;
        public GameWindowInfo? ReadWindow(IntPtr handle) => Window;
        public virtual long IdleMs() => Tick - LastInputTick;
        public long TickMs() => Tick;
        public void Sleep(int ms) { Tick += ms; Log.Add("sleep"); }
        public bool GetCursor(out int x, out int y) { (x, y) = Cursor; return true; }
        public bool SetCursor(int x, int y) { if (!CanSetCursor) return false; Cursor = (x, y); Log.Add($"cursor {x},{y}"); return true; }
        public void PostMouse(IntPtr h, uint msg, uint w, int x, int y) => Log.Add($"mouse {msg:X} {w} {x},{y}");
        public void PostWheel(IntPtr h, int d, int x, int y) => Log.Add($"wheel {d} {x},{y}");
        public void PostKey(IntPtr h, int vk, bool up) => Log.Add($"key {vk} {(up ? "up" : "down")}");
        public void ShowNoActivate(IntPtr h)
        {
            ShowCalls++;
            if (!MinimizedStaysMinimized && Window is { } w) Window = w with { Minimized = false };
        }
    }

    /// <summary>A person whose hand is always on the mouse.</summary>
    private sealed class BusyNative : FakeNative
    {
        public override long IdleMs() => 10;
    }

    private static GameWindowInfo Win(bool minimized = false) =>
        new(new IntPtr(7), 1, "Raid", "UnityWndClass", new PixelRect(90, 90, 1310, 760), new PixelRect(100, 100, 1298, 732), 96, true, minimized, false, false);

    [Fact]
    public void WaitStill_returns_at_once_when_idle()
    {
        var n = new FakeNative();
        Assert.True(new BackgroundInput(n).WaitStill());
        Assert.Empty(n.Log);
    }

    [Fact]
    public void WaitStill_waits_until_the_mouse_has_been_still()
    {
        var n = new FakeNative { LastInputTick = 99_800 }; // idle 200 ms
        Assert.True(new BackgroundInput(n).WaitStill());
        Assert.True(n.Tick - n.LastInputTick >= 1000);
    }

    [Fact]
    public void WaitStill_gives_up_when_the_person_keeps_moving()
    {
        var n = new BusyNative();
        Assert.False(new BackgroundInput(n).WaitStill(1000, 2000));
        Assert.InRange(n.Tick - 100_000, 2000, 2600);
    }

    [Fact]
    public void Click_flicks_posts_and_puts_the_cursor_back()
    {
        var n = new FakeNative { Window = Win() };
        Assert.Null(new BackgroundInput(n).Click(Win(), 10, 20, new Random(1)));
        var posts = n.Log.Where(l => l.StartsWith("mouse")).ToList();
        Assert.Equal(["mouse 200 0 10,20", "mouse 201 1 10,20", "mouse 202 0 10,20"], posts);
        Assert.Equal("cursor 110,120", n.Log.First(l => l.StartsWith("cursor")));
        Assert.Equal((5, 6), n.Cursor);
    }

    [Fact]
    public void Click_outside_the_client_sends_nothing()
    {
        var n = new FakeNative { Window = Win() };
        Assert.Contains("outside", new BackgroundInput(n).Click(Win(), 5000, 20, new Random(1)));
        Assert.Empty(n.Log);
    }

    [Fact]
    public void Click_uses_the_window_read_right_before_the_flick()
    {
        var moved = Win() with { Client = new PixelRect(300, 200, 1298, 732) };
        var n = new FakeNative { Window = moved };
        new BackgroundInput(n).Click(Win(), 10, 20, new Random(1));
        Assert.Equal("cursor 310,220", n.Log.First(l => l.StartsWith("cursor")));
    }

    [Fact]
    public void Click_restores_a_minimized_window_without_activating()
    {
        var n = new FakeNative { Window = Win(minimized: true) };
        Assert.Null(new BackgroundInput(n).Click(Win(), 10, 20, new Random(1)));
        Assert.True(n.ShowCalls >= 1);
    }

    [Fact]
    public void A_window_that_stays_minimized_falls_back()
    {
        var n = new FakeNative { Window = Win(minimized: true), MinimizedStaysMinimized = true };
        Assert.True(BackgroundMode.IsFallback(new BackgroundInput(n).Click(Win(), 10, 20, new Random(1))));
    }

    [Fact]
    public void A_cursor_that_cannot_move_falls_back()
    {
        var n = new FakeNative { Window = Win(), CanSetCursor = false };
        Assert.True(BackgroundMode.IsFallback(new BackgroundInput(n).Click(Win(), 10, 20, new Random(1))));
        Assert.DoesNotContain(n.Log, l => l.StartsWith("mouse"));
    }

    [Fact]
    public void No_click_while_the_person_keeps_using_the_mouse()
    {
        var n = new BusyNative { Window = Win() };
        var why = new BackgroundInput(n).Click(Win(), 10, 20, new Random(1));
        Assert.Contains("mouse", why);
        Assert.DoesNotContain(n.Log, l => l.StartsWith("mouse"));
    }

    [Fact]
    public void Drag_presses_moves_releases_and_restores_the_cursor()
    {
        var n = new FakeNative { Window = Win() };
        Assert.Null(new BackgroundInput(n).Drag(Win(), 10, 10, 10, 100, steps: 3));
        var posts = n.Log.Where(l => l.StartsWith("mouse")).ToList();
        Assert.Equal("mouse 201 1 10,10", posts[1]);
        Assert.Equal("mouse 202 0 10,100", posts[^1]);
        Assert.Equal((5, 6), n.Cursor);
    }

    [Fact]
    public void Wheel_posts_one_message_per_notch()
    {
        var n = new FakeNative { Window = Win() };
        Assert.Null(new BackgroundInput(n).Wheel(Win(), 50, 60, -3));
        Assert.Equal(3, n.Log.Count(l => l == "wheel -120 150,160"));
    }

    [Fact]
    public void Escape_posts_key_down_and_up_without_moving_the_cursor()
    {
        var n = new FakeNative { Window = Win() };
        Assert.Null(new BackgroundInput(n).Escape(Win()));
        Assert.Equal(["key 27 down", "sleep", "key 27 up"], n.Log);
    }

    [Theory]
    [InlineData(false, null, Gesture.Click, false)]
    [InlineData(true, null, Gesture.Click, true)]
    [InlineData(true, "wheel,drag", Gesture.Wheel, false)]
    [InlineData(true, "wheel, Drag", Gesture.Drag, false)]
    [InlineData(true, "wheel,drag", Gesture.Click, true)]
    public void Routing_decision(bool enabled, string? fallback, Gesture g, bool background) =>
        Assert.Equal(background, BackgroundMode.UseBackground(g, enabled, fallback));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("ON", true)]
    public void Switch_from_env_value(string? v, bool on) => Assert.Equal(on, BackgroundMode.ParseEnabled(v));

    [Fact]
    public void Switch_off_by_default_and_flag_turns_it_on()
    {
        var saved = Environment.GetEnvironmentVariable(BackgroundMode.EnvOn);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundMode.EnvOn, null);
            BackgroundMode.ForTest(null);
            Assert.False(BackgroundMode.Enabled);
            BackgroundMode.Configure(["autoplay"]);
            Assert.False(BackgroundMode.Enabled);
            BackgroundMode.Configure(["autoplay", "--background-clicks"]);
            Assert.True(BackgroundMode.Enabled);
            Assert.Equal("1", Environment.GetEnvironmentVariable(BackgroundMode.EnvOn));
        }
        finally { BackgroundMode.ForTest(null); Environment.SetEnvironmentVariable(BackgroundMode.EnvOn, saved); }
    }
}
