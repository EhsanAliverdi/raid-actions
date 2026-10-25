using OpenCvSharp;

namespace RaidActions.Game.Vision;

/// <summary>Where a template was found: its box in the scene (pixels), the score (normalized correlation, 1 = identical) and the scale.</summary>
public sealed record Match(string Name, double Score, Rect Box, double Scale)
{
    public Point Center => new(Box.X + Box.Width / 2, Box.Y + Box.Height / 2);
    public override string ToString() => $"{Name} {Score:0.000} at {Box.X},{Box.Y} {Box.Width}x{Box.Height} (scale {Scale:0.000})";
}

/// <summary>
/// Multi-scale template matching (normalized cross-correlation on color): a coarse sweep on a half-size scene, then a refinement
/// at full size around the best hit. Deterministic; no learning.
/// </summary>
public static class TemplateMatcher
{
    public sealed record Options(double MinScale = 0.4, double MaxScale = 4.0, double Step = 1.04, double Threshold = 0.8, Rect? SearchArea = null);

    public static Match? Find(Mat scene, Mat template, string name, Options o)
    {
        var area = Clip(o.SearchArea ?? new Rect(0, 0, scene.Width, scene.Height), scene);
        if (area.Width < 8 || area.Height < 8) return null;
        using var roi = new Mat(scene, area);

        // Coarse: half size when the scene is large.
        var d = roi.Width > 900 ? 0.5 : 1.0;
        using var small = d < 1 ? roi.Resize(new Size(), d, d, InterpolationFlags.Area) : roi.Clone();
        (double Score, double Scale, Point Loc) best = (-1, 0, default);
        for (var s = o.MinScale; s <= o.MaxScale + 1e-9; s *= o.Step)
        {
            var hit = MatchAt(small, template, s * d);
            if (hit is { } h && h.Score > best.Score) best = (h.Score, s, h.Loc);
        }
        if (best.Score < 0) return null;

        // Refine at full size, near the coarse hit, within ±5% of its scale.
        var guess = new Point((int)(best.Loc.X / d), (int)(best.Loc.Y / d));
        var tw = (int)Math.Round(template.Width * best.Scale);
        var th = (int)Math.Round(template.Height * best.Scale);
        var margin = (int)Math.Max(8, 0.15 * Math.Max(tw, th));
        var near = Clip(new Rect(guess.X - margin, guess.Y - margin, tw + 2 * margin, th + 2 * margin), roi);
        using var nearMat = new Mat(roi, near);
        (double Score, double Scale, Point Loc) fine = (-1, 0, default);
        for (var s = best.Scale * 0.95; s <= best.Scale * 1.05 + 1e-9; s *= 1.01)
        {
            var hit = MatchAt(nearMat, template, s);
            if (hit is { } h && h.Score > fine.Score) fine = (h.Score, s, h.Loc);
        }
        if (fine.Score < 0) fine = (best.Score, best.Scale, new Point(guess.X - near.X, guess.Y - near.Y));
        if (fine.Score < o.Threshold) return null;
        var w = (int)Math.Round(template.Width * fine.Scale);
        var hgt = (int)Math.Round(template.Height * fine.Scale);
        return new Match(name, fine.Score, new Rect(area.X + near.X + fine.Loc.X, area.Y + near.Y + fine.Loc.Y, w, hgt), fine.Scale);
    }

    /// <summary>The best match of the template scaled by <paramref name="scale"/>, or null when it does not fit.</summary>
    private static (double Score, Point Loc)? MatchAt(Mat scene, Mat template, double scale)
    {
        var w = (int)Math.Round(template.Width * scale);
        var h = (int)Math.Round(template.Height * scale);
        if (w < 10 || h < 6 || w > scene.Width || h > scene.Height) return null;
        using var t = template.Resize(new Size(w, h), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        using var result = new Mat();
        Cv2.MatchTemplate(scene, t, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var max, out _, out var loc);
        return double.IsFinite(max) ? (max, loc) : null;
    }

    private static Rect Clip(Rect r, Mat m)
    {
        var x = Math.Clamp(r.X, 0, m.Width);
        var y = Math.Clamp(r.Y, 0, m.Height);
        return new Rect(x, y, Math.Clamp(r.Right, x, m.Width) - x, Math.Clamp(r.Bottom, y, m.Height) - y);
    }
}
