using System.Text.Json.Nodes;
using OpenCvSharp;

namespace RaidActions.Game.Vision;

/// <summary>
/// What the screen shows. <see cref="Screen"/>: "champion-screen" (the champion's gear panel is visible) or "unknown".
/// <see cref="GearSlots"/>: the nine slot boxes, when the gear panel is found.
/// </summary>
public sealed record Recognition(string Screen, IReadOnlyList<Match> Anchors, IReadOnlyDictionary<string, Rect> GearSlots, IReadOnlyList<string> Notes)
{
    public bool Has(string anchor) => Anchors.Any(a => a.Name == anchor);
    public bool ChampionListVisible => Has("champion-list.sort");
    public bool ArtifactPickerOpen => Has("picker.auto-equip");
    public bool PiecePopupOpen => Has("popup.equip");
}

/// <summary>Screen geometry relative to anchors (config/ui-layout.json).</summary>
public sealed record UiLayout(string GearAnchor, double GearAnchorWidth, string GearStatus, IReadOnlyDictionary<string, Rect> GearSlots)
{
    public static UiLayout Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var g = root["gearSlots"]!.AsObject();
        var slots = g["slots"]!.AsObject().ToDictionary(kv => kv.Key, kv =>
        {
            var a = kv.Value!.AsArray().Select(n => (int)n!).ToArray();
            return new Rect(a[0], a[1], a[2], a[3]);
        });
        return new UiLayout((string)g["anchor"]!, (double)g["anchorWidth"]!, (string)g["status"]!, slots);
    }

    /// <summary>The slot boxes for a gear-panel anchor found at <paramref name="anchor"/>.</summary>
    public Dictionary<string, Rect> SlotsAt(Match anchor)
    {
        var k = anchor.Box.Width / GearAnchorWidth;
        return GearSlots.ToDictionary(kv => kv.Key, kv => new Rect(
            anchor.Box.X + (int)Math.Round(kv.Value.X * k), anchor.Box.Y + (int)Math.Round(kv.Value.Y * k),
            (int)Math.Round(kv.Value.Width * k), (int)Math.Round(kv.Value.Height * k)));
    }
}

/// <summary>Finds the anchors on a capture and works out the screen and the gear slots. Read-only: it only looks.</summary>
public sealed class ScreenRecognizer(TemplateLibrary templates, UiLayout layout)
{
    public Recognition Recognize(Mat capture)
    {
        var notes = new List<string>();
        // Templates are independent: match them in parallel (images loaded first; the library's cache is not thread-safe).
        var jobs = templates.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (Name: e.Key, Entry: e.Value, Image: templates.Image(e.Key))).ToList();
        var results = new Match?[jobs.Count];
        Parallel.For(0, jobs.Count, i =>
        {
            var (name, entry, image) = jobs[i];
            var o = entry.RefClientHeight is { } h
                ? new TemplateMatcher.Options(capture.Height / (double)h * 0.9, capture.Height / (double)h * 1.1, 1.02, entry.Threshold)
                : new TemplateMatcher.Options(Threshold: entry.Threshold);
            results[i] = TemplateMatcher.Find(capture, image, name, o);
        });
        var found = results.OfType<Match>().ToList();
        var gear = found.FirstOrDefault(m => m.Name == layout.GearAnchor);
        var slots = gear is null ? new Dictionary<string, Rect>() : layout.SlotsAt(gear);
        if (gear is not null && layout.GearStatus != "verified") notes.Add($"Gear slot geometry is {layout.GearStatus}: check the boxes on the annotated image.");
        if (gear is not null && slots.Values.Any(r => r.Right > capture.Width || r.Bottom > capture.Height)) notes.Add("Some gear slots fall outside the capture.");
        return new Recognition(gear is null ? "unknown" : "champion-screen", found, slots, notes);
    }

    /// <summary>A copy of the capture with the anchors (green) and the gear slots (yellow) drawn on it.</summary>
    public static Mat Annotate(Mat capture, Recognition r)
    {
        var img = capture.Clone();
        foreach (var a in r.Anchors)
        {
            img.Rectangle(a.Box, Scalar.LimeGreen, 2);
            img.PutText($"{a.Name} {a.Score:0.00}", new Point(a.Box.X, Math.Max(12, a.Box.Y - 4)), HersheyFonts.HersheySimplex, 0.45, Scalar.LimeGreen, 1);
        }
        foreach (var (slot, box) in r.GearSlots)
        {
            img.Rectangle(box, Scalar.Yellow, 2);
            img.PutText(slot, new Point(box.X + 2, box.Y + 12), HersheyFonts.HersheySimplex, 0.35, Scalar.Yellow, 1);
        }
        img.PutText(r.Screen, new Point(8, 20), HersheyFonts.HersheySimplex, 0.6, Scalar.White, 2);
        return img;
    }
}
