using OpenCvSharp;

namespace RaidActions.Game.Memory;

/// <summary>
/// The tower's floors are a 3D scene with no screen rectangles, but the floor to play is a solid gold banner (seen 2026-11-02). A finished floor has a gold
/// edge and a blue middle, so the middle decides. Thresholds from captures at 1298 wide, scaled with the image.
/// </summary>
public static class DoomTowerSpot
{
    /// <summary>The middle of the gold banner in client pixels, or null when there is none on screen.</summary>
    /// <summary>
    /// Every spot that may be the floor to play, the likeliest first: a boss portrait whose frame glows red (an open boss floor: 2026-11-13, the Dark Fae
    /// on floor 40; a beaten boss has no glow), then the solid gold banners, the highest on the screen first. The bot checks in memory which stage a tap
    /// opened, so a wrong candidate costs only a back.
    /// </summary>
    public static List<(float X, float Y)> Candidates(Mat bgr)
    {
        var scale = bgr.Width / 1298f;
        var list = new List<(float X, float Y, double Score)>();
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        // The red glow: windows the size of a portrait, scored by the red in their outer band.
        using var red = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 120, 150), new Scalar(12, 255, 255), red);
        int w = (int)(100 * scale), h = (int)(150 * scale), band = (int)(14 * scale), step = (int)(12 * scale);
        for (var y = (int)(60 * scale); y + h <= bgr.Height; y += step)
            for (var x = 0; x + w <= bgr.Width; x += step)
            {
                using var outer = new Mat(red, new Rect(x, y, w, h));
                using var inner = new Mat(red, new Rect(x + band, y + band, w - 2 * band, h - 2 * band));
                var ring = Cv2.CountNonZero(outer) - Cv2.CountNonZero(inner);
                if (ring > 250 * scale * scale) list.Add((x + w / 2f, y + h / 2f, 10_000 + ring));
            }
        // Keep the best of overlapping windows.
        var glows = new List<(float X, float Y, double Score)>();
        foreach (var c in list.OrderByDescending(c => c.Score))
            if (glows.All(g => Math.Abs(g.X - c.X) > w / 2f || Math.Abs(g.Y - c.Y) > h / 2f)) glows.Add(c);
        using var gold = new Mat();
        Cv2.InRange(hsv, new Scalar(14, 70, 90), new Scalar(32, 255, 255), gold);
        Cv2.FindContours(gold, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var banners = new List<(float X, float Y, double Score)>();
        foreach (var c in contours)
        {
            var box = Cv2.BoundingRect(c);
            if (box.Y < 60 * scale || box.Width < 75 * scale || box.Width > 170 * scale || box.Height < 95 * scale || box.Height > 215 * scale) continue;
            using var middle = new Mat(gold, new Rect(box.X + box.Width / 3, box.Y + box.Height / 3, box.Width / 3, box.Height / 3));
            if (Cv2.CountNonZero(middle) < 0.5 * middle.Total()) continue;
            banners.Add((box.X + box.Width / 2f, box.Y + box.Height / 2f, -box.Y));
        }
        return [.. glows.Take(3).Select(g => (g.X, g.Y)), .. banners.OrderByDescending(b => b.Score).Select(b => (b.X, b.Y))];
    }

    public static (float X, float Y)? Find(Mat bgr)
    {
        var scale = bgr.Width / 1298f;
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var gold = new Mat();
        Cv2.InRange(hsv, new Scalar(14, 70, 90), new Scalar(32, 255, 255), gold);
        Cv2.FindContours(gold, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        (float X, float Y)? best = null;
        var bestArea = 0.0;
        foreach (var c in contours)
        {
            var box = Cv2.BoundingRect(c);
            if (box.Y < 60 * scale || box.Width < 75 * scale || box.Width > 170 * scale || box.Height < 95 * scale || box.Height > 215 * scale) continue;
            using var middle = new Mat(gold, new Rect(box.X + box.Width / 3, box.Y + box.Height / 3, box.Width / 3, box.Height / 3));
            if (Cv2.CountNonZero(middle) < 0.5 * middle.Total()) continue;
            var area = Cv2.ContourArea(c);
            if (area > bestArea) (best, bestArea) = ((box.X + box.Width / 2f, box.Y + box.Height / 2f), area);
        }
        return best;
    }
}
