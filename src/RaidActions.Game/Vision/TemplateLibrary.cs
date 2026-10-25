using System.Text.Json;
using OpenCvSharp;

namespace RaidActions.Game.Vision;

/// <param name="RefClientHeight">The game window's client height the template was cut at (live templates), or null (cut from a
/// reference screenshot of unknown scale: matched over a wide range of scales).</param>
/// <param name="Source">Where it was cut from: "file.png@x,y,w,h".</param>
public sealed record TemplateEntry(string File, double Threshold, int? RefClientHeight, string Source);

/// <summary>
/// The anchor templates, cut from the game's own UI and cached locally in output/templates (gitignored: they are Plarium's
/// images and are never committed). Seeded from raid-extractor's reference screenshots; re-cut from a live capture once the
/// anchors are found there (<see cref="Recut"/>), which pins their scale to the real window.
/// </summary>
public sealed class TemplateLibrary : IDisposable
{
    public const string IndexFile = "templates.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Dictionary<string, Mat> _images = [];

    public string Folder { get; }
    public Dictionary<string, TemplateEntry> Entries { get; }

    private TemplateLibrary(string folder, Dictionary<string, TemplateEntry> entries)
    {
        Folder = folder;
        Entries = entries;
    }

    /// <summary>An anchor cut from a reference screenshot.</summary>
    public sealed record Seed(string Name, string Screenshot, Rect Region, double Threshold);

    /// <summary>
    /// The anchors, from raid-extractor's output/ui-reference screenshots. The champion-list anchor is the "By Rank" sort box: it
    /// matches only with that sort (which raid-extractor's roster order also assumes).
    /// </summary>
    public static readonly Seed[] Seeds =
    [
        new("champion-list.sort", "01-roster.png", new Rect(0, 0, 145, 26), 0.75),
        new("gear.tabs", "02-gear-panel-dorothy.png", new Rect(10, 11, 170, 31), 0.75),
        new("gear.total-stats", "02-gear-panel-dorothy.png", new Rect(64, 216, 66, 18), 0.75),
        new("picker.auto-equip", "05-artifact-inventory-curing.png", new Rect(386, 78, 88, 25), 0.8),
        new("popup.equip", "05-artifact-inventory-curing.png", new Rect(88, 389, 78, 26), 0.8),
        new("popup.upgrade", "05-artifact-inventory-curing.png", new Rect(4, 389, 78, 26), 0.8),
    ];

    public static TemplateLibrary Load(string folder)
    {
        var index = Path.Combine(folder, IndexFile);
        var entries = File.Exists(index)
            ? JsonSerializer.Deserialize<Dictionary<string, TemplateEntry>>(File.ReadAllText(index), Json) ?? []
            : [];
        return new TemplateLibrary(folder, entries);
    }

    /// <summary>Cuts the seed templates from the reference screenshots into <paramref name="folder"/>. Returns what was cut and what is missing.</summary>
    public static (TemplateLibrary Library, List<string> Missing) SeedFrom(string referenceFolder, string folder)
    {
        Directory.CreateDirectory(folder);
        var lib = Load(folder);
        var missing = new List<string>();
        foreach (var s in Seeds)
        {
            var shot = Path.Combine(referenceFolder, s.Screenshot);
            if (!File.Exists(shot)) { missing.Add($"{s.Name}: {shot} not found"); continue; }
            using var img = Cv2.ImRead(shot, ImreadModes.Color);
            if (img.Empty() || s.Region.Right > img.Width || s.Region.Bottom > img.Height) { missing.Add($"{s.Name}: {shot} is not as expected"); continue; }
            using var cut = new Mat(img, s.Region);
            lib.Save(s.Name, cut, new TemplateEntry($"{s.Name}.png", s.Threshold, null, $"{s.Screenshot}@{s.Region.X},{s.Region.Y},{s.Region.Width},{s.Region.Height}"));
        }
        lib.WriteIndex();
        return (lib, missing);
    }

    /// <summary>Replaces a template with the same region cut from a live capture, and pins it to that window height.</summary>
    public void Recut(string name, Mat capture, Rect box, int clientHeight)
    {
        var old = Entries[name];
        using var cut = new Mat(capture, box);
        Save(name, cut, old with { RefClientHeight = clientHeight, Source = $"live {capture.Width}x{capture.Height}@{box.X},{box.Y},{box.Width},{box.Height}" });
        WriteIndex();
    }

    public Mat Image(string name)
    {
        if (_images.TryGetValue(name, out var m)) return m;
        var e = Entries[name];
        m = Cv2.ImRead(Path.Combine(Folder, e.File), ImreadModes.Color);
        if (m.Empty()) throw new FileNotFoundException($"Template {name}: {e.File} is missing or unreadable in {Folder}.");
        return _images[name] = m;
    }

    private void Save(string name, Mat image, TemplateEntry entry)
    {
        Directory.CreateDirectory(Folder);
        Cv2.ImWrite(Path.Combine(Folder, entry.File), image);
        if (_images.Remove(name, out var old)) old.Dispose();
        Entries[name] = entry;
    }

    private void WriteIndex() => File.WriteAllText(Path.Combine(Folder, IndexFile), JsonSerializer.Serialize(Entries, Json));

    public void Dispose()
    {
        foreach (var m in _images.Values) m.Dispose();
        _images.Clear();
    }
}
