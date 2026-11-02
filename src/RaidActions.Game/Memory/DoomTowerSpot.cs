using OpenCvSharp;

namespace RaidActions.Game.Memory;

/// <summary>
/// The tower's floors are a 3D scene with no screen rectangles, but the floor to play is a solid gold banner (seen 2026-11-02). A finished floor has a gold
/// edge and a blue middle, so the middle decides. Thresholds from captures at 1298 wide, scaled with the image.
/// </summary>
public static class DoomTowerSpot
{
    /// <summary>The middle of the gold banner in client pixels, or null when there is none on screen.</summary>
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
