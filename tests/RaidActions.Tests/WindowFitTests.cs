using RaidActions.Game.Window;

namespace RaidActions.Tests;

/// <summary>The window size the actioner owns (no game window needed).</summary>
public class WindowFitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static GameWindowInfo Window(int w, int h) =>
        new(IntPtr.Zero, 1, "Raid", "UnityWndClass", new PixelRect(0, 0, w + 16, h + 39), new PixelRect(8, 31, w, h), 96, true, false, false, false);

    private WindowStandard Standard()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "window.json");
        File.Copy(Path.Combine(AppPathsForTests.Config, "window.json"), path);
        return WindowStandard.Load(path);
    }

    [Fact]
    public void The_committed_standard_is_1600x900()
    {
        var s = WindowStandard.Load(Path.Combine(AppPathsForTests.Config, "window.json"));
        Assert.Equal((1600, 900), (s.Width, s.Height));
        Assert.Equal((1600, 900), (s.RequestedWidth, s.RequestedHeight));
    }

    [Fact]
    public void Another_size_is_refused_with_the_command_to_run()
    {
        var s = Standard();
        Assert.Null(s.Mismatch(Window(1600, 900)));
        Assert.Contains("run  RaidActions window fit", s.Mismatch(Window(906, 495)));
        Assert.Contains("906x495", s.Mismatch(Window(906, 495)));
        Assert.NotNull(s.Mismatch(Window(1600, 901)));
    }

    [Fact]
    public void The_size_the_game_takes_becomes_the_standard()
    {
        var s = Standard();
        var applied = s.Save(1600, 900);
        Assert.Equal("applied", applied.Status);
        var snapped = s.Save(1600, 880);
        Assert.Equal("snapped", snapped.Status);
        var reloaded = WindowStandard.Load(snapped.Path);
        Assert.Equal((1600, 880, 1600, 900, "snapped"), (reloaded.Width, reloaded.Height, reloaded.RequestedWidth, reloaded.RequestedHeight, reloaded.Status));
        Assert.DoesNotContain("\\" + "u0027", File.ReadAllText(snapped.Path)); // the comment's apostrophes stay readable
    }

    [Fact]
    public void The_window_is_centred_in_the_work_area()
    {
        var p = WindowFit.Center(new PixelRect(0, 0, 1920, 1040), 1616, 939, 1600, 900, restore: false);
        Assert.Equal(new PixelRect(152, 50, 1616, 939), p.Window);
        var off = WindowFit.Center(new PixelRect(1920, 0, 1280, 680), 1616, 939, 1600, 900, restore: true);
        Assert.Equal((1920, 0), (off.Window.X, off.Window.Y)); // too big: top left of the work area, never above or left of it
        Assert.True(off.Restore);
    }
}
