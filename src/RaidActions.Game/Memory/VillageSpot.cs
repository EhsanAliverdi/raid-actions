using OpenCvSharp;

namespace RaidActions.Game.Memory;

/// <summary>
/// A village building found by its name label: the village's camera is not always in the same place (2026-11-02: fixed spots missed the Portal by 130 pixels and
/// the Market at times), and the 3D buildings have no screen rectangles. Labels are in config/village, cut at 1298 wide.
/// </summary>
public static class VillageSpot
{
    public const double MinScore = 0.7;

    /// <summary>The middle of the label in the image's pixels, or null when it is not on screen.</summary>
    public static (float X, float Y)? Find(Mat image, Mat label)
    {
        var scale = image.Width / 1298.0;
        using var sized = new Mat();
        if (Math.Abs(scale - 1) > 0.01) Cv2.Resize(image, sized, new Size(1298, (int)Math.Round(image.Height / scale)));
        else image.CopyTo(sized);
        // The village's camera has two zooms (2026-11-02: labels 8% apart): the label is tried at several sizes.
        var (best, at, size) = (0.0, new Point(), label.Size());
        for (var f = 0.85; f <= 1.25; f += 0.03)
        {
            using var sizedLabel = new Mat();
            Cv2.Resize(label, sizedLabel, new Size(0, 0), f, f, f < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
            using var scores = new Mat();
            Cv2.MatchTemplate(sized, sizedLabel, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out double score, out _, out Point place);
            if (score > best) (best, at, size) = (score, place, sizedLabel.Size());
        }
        if (best < MinScore) return null;
        return ((float)((at.X + size.Width / 2f) * scale), (float)((at.Y + size.Height / 2f) * scale));
    }
}
