using OpenCvSharp;
using RaidActions.Game.Vision;
using RaidActions.Game.Window;

namespace RaidActions.Tests;

/// <summary>Template matching and recognition on synthetic images (no game images, no game).</summary>
public class VisionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A textured template that cannot match noise by accident.</summary>
    internal static Mat Pattern(int w, int h, int seed)
    {
        var m = new Mat(h, w, MatType.CV_8UC3, Scalar.Black);
        var r = new Random(seed);
        for (var i = 0; i < 14; i++)
            m.Rectangle(new Rect(r.Next(w - 8), r.Next(h - 6), r.Next(4, w / 3), r.Next(3, h / 2)), new Scalar(r.Next(256), r.Next(256), r.Next(256)), -1);
        m.PutText("RA", new Point(4, h - 6), HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
        return m;
    }

    internal static Mat Scene(int w, int h, int seed)
    {
        var s = new Mat(h, w, MatType.CV_8UC3);
        Cv2.Randu(s, Scalar.All(20), Scalar.All(90));
        using var blur = s.GaussianBlur(new Size(5, 5), 2);
        blur.CopyTo(s);
        return s;
    }

    internal static void Paste(Mat scene, Mat template, double scale, Point at)
    {
        using var t = template.Resize(new Size((int)Math.Round(template.Width * scale), (int)Math.Round(template.Height * scale)), 0, 0, InterpolationFlags.Area);
        t.CopyTo(new Mat(scene, new Rect(at.X, at.Y, t.Width, t.Height)));
    }

    [Theory]
    [InlineData(1.0, 300, 200)]
    [InlineData(1.5, 900, 420)]
    [InlineData(0.62, 40, 600)]
    [InlineData(2.4, 1200, 100)]
    public void Finds_a_template_at_any_scale(double scale, int x, int y)
    {
        using var tpl = Pattern(170, 31, 1);
        using var scene = Scene(1920, 1080, 2);
        Paste(scene, tpl, scale, new Point(x, y));
        var m = TemplateMatcher.Find(scene, tpl, "t", new TemplateMatcher.Options());
        Assert.NotNull(m);
        Assert.InRange(m.Box.X, x - 3, x + 3);
        Assert.InRange(m.Box.Y, y - 3, y + 3);
        Assert.InRange(m.Scale, scale * 0.97, scale * 1.03);
        Assert.True(m.Score > 0.9, m.ToString());
    }

    [Fact]
    public void Nothing_is_found_where_the_template_is_not()
    {
        using var tpl = Pattern(170, 31, 1);
        using var other = Pattern(170, 31, 99);
        using var scene = Scene(1280, 720, 3);
        Paste(scene, other, 1.2, new Point(400, 300));
        Assert.Null(TemplateMatcher.Find(scene, tpl, "t", new TemplateMatcher.Options(Threshold: 0.8)));
    }

    [Fact]
    public void The_search_area_limits_where_it_looks()
    {
        using var tpl = Pattern(120, 30, 5);
        using var scene = Scene(1280, 720, 4);
        Paste(scene, tpl, 1.0, new Point(900, 500));
        Assert.Null(TemplateMatcher.Find(scene, tpl, "t", new TemplateMatcher.Options(SearchArea: new Rect(0, 0, 600, 400))));
        Assert.NotNull(TemplateMatcher.Find(scene, tpl, "t", new TemplateMatcher.Options(SearchArea: new Rect(700, 400, 500, 300))));
    }

    [Fact]
    public void Gear_slots_scale_with_the_anchor()
    {
        var layout = UiLayout.Load(Path.Combine(AppPathsForTests.Config, "ui-layout.json"));
        var anchor = new Match(layout.GearAnchor, 0.95, new Rect(1000, 100, 255, 46), 1.5); // 170 wide at 1.5
        var slots = layout.SlotsAt(anchor);
        Assert.Equal(9, slots.Count);
        Assert.Equal(new Rect(1000 + 21, 100 + 72, 66, 66), slots["Weapon"]);
        Assert.Equal(new Rect(1000 + 171, 100 + 147, 66, 66), slots["Boots"]);
        Assert.Equal(["Weapon", "Helmet", "Shield", "Gloves", "Chest", "Boots", "Ring", "Cloak", "Banner"], layout.GearSlots.Keys);
    }

    [Fact]
    public void Recognizes_the_champion_screen_from_its_gear_anchor()
    {
        // A library with a synthetic gear anchor, a scene that shows it at 1.3x.
        Directory.CreateDirectory(_dir);
        using (var gear = Pattern(170, 31, 7)) gear.SaveImage(Path.Combine(_dir, "gear.tabs.png"));
        File.WriteAllText(Path.Combine(_dir, TemplateLibrary.IndexFile),
            """{ "gear.tabs": { "file": "gear.tabs.png", "threshold": 0.8, "refClientHeight": null, "source": "test" } }""");
        using var lib = TemplateLibrary.Load(_dir);
        var layout = UiLayout.Load(Path.Combine(AppPathsForTests.Config, "ui-layout.json"));
        using var scene = Scene(1600, 900, 8);
        Paste(scene, lib.Image("gear.tabs"), 1.3, new Point(1180, 60));

        var r = new ScreenRecognizer(lib, layout).Recognize(scene);
        Assert.Equal("champion-screen", r.Screen);
        Assert.False(r.ArtifactPickerOpen);
        var weapon = r.GearSlots["Weapon"];
        Assert.InRange(weapon.X, 1180 + 18 - 3, 1180 + 18 + 3);
        Assert.InRange(weapon.Width, 55, 59);
        using var annotated = ScreenRecognizer.Annotate(scene, r);
        Assert.Equal(scene.Size(), annotated.Size());

        using var blank = Scene(1600, 900, 9);
        Assert.Equal("unknown", new ScreenRecognizer(lib, layout).Recognize(blank).Screen);
    }

    [Fact]
    public void A_live_template_is_searched_near_its_window_height()
    {
        Directory.CreateDirectory(_dir);
        using (var t = Pattern(170, 31, 11)) t.SaveImage(Path.Combine(_dir, "gear.tabs.png"));
        File.WriteAllText(Path.Combine(_dir, TemplateLibrary.IndexFile),
            """{ "gear.tabs": { "file": "gear.tabs.png", "threshold": 0.8, "refClientHeight": 720, "source": "test" } }""");
        using var lib = TemplateLibrary.Load(_dir);
        var layout = UiLayout.Load(Path.Combine(AppPathsForTests.Config, "ui-layout.json"));
        using var scene = Scene(1920, 1080, 12); // 1080 / 720 = 1.5
        Paste(scene, lib.Image("gear.tabs"), 1.5, new Point(1500, 40));
        var m = Assert.Single(new ScreenRecognizer(lib, layout).Recognize(scene).Anchors);
        Assert.InRange(m.Scale, 1.45, 1.55);
    }

    [Fact]
    public void Blank_captures_are_recognized()
    {
        using var black = new Mat(200, 300, MatType.CV_8UC3, Scalar.Black);
        Assert.True(WindowCapture.IsBlank(black));
        using var scene = Scene(300, 200, 1);
        Assert.False(WindowCapture.IsBlank(scene));
    }

    [Fact]
    public void A_window_is_usable_only_when_shown_and_big_enough()
    {
        GameWindowInfo W(bool min = false, bool vis = true, bool cloaked = false, int w = 1280, int h = 720) =>
            new(IntPtr.Zero, 1, "Raid", "UnityWndClass", new PixelRect(0, 0, w + 16, h + 39), new PixelRect(8, 31, w, h), 144, vis, min, false, cloaked);
        Assert.Null(W().NotUsable);
        Assert.Contains("minimized", W(min: true).NotUsable);
        Assert.Contains("hidden", W(vis: false).NotUsable);
        Assert.Contains("cloaked", W(cloaked: true).NotUsable);
        Assert.Contains("too small", W(w: 0, h: 0).NotUsable);
        Assert.Equal(1.5, W().Scale);
    }
}

/// <summary>The repo's config folder, from the test's output folder.</summary>
internal static class AppPathsForTests
{
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "RaidActions.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("repo root");
        }
    }

    public static string Config => Path.Combine(Root, "config");
    public static string RaidExtractor => Path.GetFullPath(Path.Combine(Root, "..", "raid-extractor"));
}
