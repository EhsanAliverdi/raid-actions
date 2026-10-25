using OpenCvSharp;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;

namespace RaidActions.Game.Dry;

/// <summary>What the drivers read from the screen: the targets of the visible views, and overlays of a target on a capture.</summary>
public static class Screen
{
    /// <summary>The targets of the visible views (the champion screen and any dialog over it), for the current client size.</summary>
    public static List<UiTarget> Targets(LiveGame game, UnityUi ui, UiState s)
    {
        var (w, _) = GameWindow.Find();
        if (w is null || w.NotUsable is not null) return [];
        var visible = s.Active.Where(v => v.Visible).Select(v => v.Name).ToHashSet();
        var result = new List<UiTarget>();
        foreach (var (name, view) in game.Views().Where(v => visible.Contains(v.Name) && !v.Name.EndsWith("NotificationsView", StringComparison.Ordinal)))
        {
            try { result.AddRange(UiTargets.Find(ui.Read(view, name, w.Client.Width, w.Client.Height), game.Objects)); }
            catch (Exception e) when (e is UiReadException or RaidExtractor.ReadCheckException) { }
        }
        return result;
    }

    /// <summary>The popup covering the champion screen (it would take a click), or null.</summary>
    public static string? Covering(UiState s) => s.ChampionScreenOpen && s.Top?.Name != UiState.ChampionScreenView ? s.Top?.Name : null;

    /// <summary>A capture with <paramref name="r"/> marked and a caption; saved as &lt;folder&gt;/&lt;file&gt;. Null when no capture.</summary>
    public static string? Overlay(string folder, string file, string caption, ClientRect r, Func<GameWindowInfo, string?> sizeCheck)
    {
        var (w, _) = GameWindow.Find();
        if (w is null || sizeCheck(w) is not null) return null;
        var (cap, _) = WindowCapture.Take(w);
        if (cap is null) return null;
        using (cap)
        {
            var img = cap.Image;
            var box = new Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
            img.Rectangle(new Rect(box.X - 3, box.Y - 3, box.Width + 6, box.Height + 6), Scalar.Yellow, 4);
            var (cx, cy) = r.Center;
            img.DrawMarker(new Point((int)cx, (int)cy), Scalar.Red, MarkerTypes.Cross, 24, 3);
            img.Rectangle(new Rect(0, 0, img.Width, 34), Scalar.Black, -1);
            img.PutText(caption, new Point(8, 24), HersheyFonts.HersheySimplex, 0.7, Scalar.Yellow, 2);
            Directory.CreateDirectory(folder);
            var png = Path.Combine(folder, file);
            img.SaveImage(png);
            return png;
        }
    }
}
