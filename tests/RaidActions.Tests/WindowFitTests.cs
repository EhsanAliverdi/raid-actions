using RaidActions.Game.Window;

namespace RaidActions.Tests;

/// <summary>Window sizes: any size works above the minimum; the minimum from the smallest click target (no game window needed).</summary>
public class WindowFitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static GameWindowInfo Window(int w, int h, bool minimized = false) =>
        new(IntPtr.Zero, 1, "Raid", "UnityWndClass", new PixelRect(0, 0, w + 16, h + 39), new PixelRect(8, 31, w, h), 96, true, minimized, false, false);

    private WindowPolicy Policy()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "window.json");
        File.Copy(Path.Combine(AppPathsForTests.Config, "window.json"), path);
        return WindowPolicy.Load(path);
    }

    [Fact]
    public void The_committed_policy_has_a_floor_of_864x486_and_40_px_targets()
    {
        var p = WindowPolicy.Load(Path.Combine(AppPathsForTests.Config, "window.json"));
        Assert.Equal((864, 486), (p.FloorWidth, p.FloorHeight));
        Assert.Equal(40, p.TargetPx);
        Assert.True(p.MinWidth >= p.FloorWidth && p.MinHeight >= p.FloorHeight);
    }

    [Theory]
    [InlineData(864, 486)]
    [InlineData(1280, 720)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1017)]
    [InlineData(2560, 1440)]
    public void Any_size_at_or_above_the_minimum_is_used_as_it_is(int w, int h) => Assert.Null(Policy().Problem(Window(w, h)));

    [Fact]
    public void Minimized_or_too_small_windows_are_refused()
    {
        var p = Policy();
        Assert.Contains("minimized", p.Problem(Window(1600, 900, minimized: true)));
        Assert.Contains("below the minimum", p.Problem(Window(863, 486)));
        Assert.Contains("below the minimum", p.Problem(Window(648, 334)));
        Assert.Null(WindowPolicy.CaptureProblem(Window(648, 334))); // captures work at any size
    }

    [Fact]
    public void The_minimum_keeps_the_smallest_target_at_40_px_but_never_below_the_floor()
    {
        // Measured: champion tiles 73 px wide at 1600x900. 40 px needs 877x494, below the floor: the floor wins.
        Assert.Equal((1280, 720), WindowPolicy.ComputeMinimum(1600, 900, 73, 40, 1280, 720));
        Assert.Equal((877, 494), WindowPolicy.ComputeMinimum(1600, 900, 73, 40, 0, 0));
        // Small targets need a bigger window, at the same aspect ratio.
        Assert.Equal((2560, 1440), WindowPolicy.ComputeMinimum(1280, 720, 20, 40, 1280, 720));
        // A 4:3 window keeps 4:3 when the floor raises it (the height decides: 720 → 960 wide).
        Assert.Equal((960, 720), WindowPolicy.ComputeMinimum(800, 600, 80, 40, 0, 720));
    }

    [Fact]
    public void The_minimum_is_stored_with_how_it_was_worked_out()
    {
        var p = Policy();
        var saved = p.SaveMinimum(1280, 720, "test: smallest target 73 px at 1600x900");
        var reloaded = WindowPolicy.Load(saved.Path);
        Assert.Equal((1280, 720), (reloaded.MinWidth, reloaded.MinHeight));
        Assert.Equal("test: smallest target 73 px at 1600x900", reloaded.How);
        Assert.DoesNotContain("\\" + "u0027", File.ReadAllText(saved.Path));
    }

    [Fact]
    public void The_window_is_centred_in_the_work_area()
    {
        var p = WindowFit.Center(new PixelRect(0, 0, 1920, 1040), 1296, 759, 1280, 720, restore: false);
        Assert.Equal(new PixelRect(312, 140, 1296, 759), p.Window);
        var off = WindowFit.Center(new PixelRect(1920, 0, 1280, 680), 1616, 939, 1600, 900, restore: true);
        Assert.Equal((1920, 0), (off.Window.X, off.Window.Y)); // too big: top left of the work area, never above or left of it
        Assert.True(off.Restore);
    }
}
