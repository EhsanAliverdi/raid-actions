using OpenCvSharp;
using RaidActions.Game.Memory;
using RaidActions.Game.Vision;
using RaidActions.Game.Window;

namespace RaidActions.App;

/// <summary>
/// Read-only commands against the running game: the window, a capture, the UI state from memory, screen recognition, templates.
/// None of them sends input or brings the game to the front.
/// </summary>
public static class GameCommands
{
    public static readonly string[] Names = ["window", "capture", "ui-state", "recognize", "templates"];

    public static LiveGamePaths LivePaths(string output) => new(AppPaths.RaidExtractorRoot(),
        Path.Combine(AppPaths.Config(), "ui-offsets.json"), Path.Combine(AppPaths.Config(), "ui-offsets.manual.json"), output);

    public static string Templates(string output) => Path.Combine(output, "templates");

    public static async Task<int> RunAsync(string command, string[] args, string output, TextWriter o)
    {
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        switch (command)
        {
            case "window":
            {
                var (w, problem) = GameWindow.Find();
                o.WriteLine(w?.ToString() ?? problem);
                if (w?.NotUsable is { } why) o.WriteLine("Not usable: " + why);
                return w is null ? Cli.Failed : Cli.Ok;
            }
            case "capture":
            {
                using var cap = TakeCapture(o);
                if (cap is null) return Cli.Failed;
                var file = Opt("--out") ?? Path.Combine(output, "captures", $"{cap.Time:yyyyMMdd-HHmmss}-{cap.Image.Width}x{cap.Image.Height}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                cap.Image.SaveImage(file);
                o.WriteLine($"Captured {cap.Image.Width}x{cap.Image.Height} ({cap.Source}) → {file}");
                return Cli.Ok;
            }
            case "ui-state":
            {
                var (game, problem) = LiveGame.Open(LivePaths(output));
                if (game is null)
                {
                    o.WriteLine(problem);
                    return Cli.Failed;
                }
                using (game)
                {
                    var watch = args.Contains("--watch");
                    string? last = null;
                    do
                    {
                        var (s, why) = game.ReadUi();
                        var text = s is null ? $"UI state unavailable: {why}" : Describe(s);
                        if (text != last) o.WriteLine($"{DateTime.Now:HH:mm:ss}\n{text}");
                        last = text;
                        if (watch) await Task.Delay(500);
                    } while (watch);
                }
                return Cli.Ok;
            }
            case "recognize":
            {
                using var lib = TemplateLibrary.Load(Templates(output));
                if (lib.Entries.Count == 0)
                {
                    o.WriteLine("No templates: run  RaidActions templates seed  first.");
                    return Cli.Failed;
                }
                var image = Opt("--image");
                using var scene = image is not null ? Cv2.ImRead(image, ImreadModes.Color) : TakeCapture(o)?.Image;
                if (scene is null || scene.Empty())
                {
                    o.WriteLine(image is null ? "No capture." : $"Cannot read {image}.");
                    return Cli.Failed;
                }
                var recognizer = new ScreenRecognizer(lib, UiLayout.Load(Path.Combine(AppPaths.Config(), "ui-layout.json")));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = recognizer.Recognize(scene);
                o.WriteLine($"Screen: {r.Screen} ({watch.ElapsedMilliseconds} ms, {scene.Width}x{scene.Height})");
                foreach (var a in r.Anchors) o.WriteLine("  anchor " + a);
                foreach (var (slot, box) in r.GearSlots) o.WriteLine($"  slot {slot,-7} {box.Width}x{box.Height} at {box.X},{box.Y}");
                foreach (var n in r.Notes) o.WriteLine("  note: " + n);
                var file = Opt("--out") ?? Path.Combine(output, "captures", $"recognized-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                using var annotated = ScreenRecognizer.Annotate(scene, r);
                annotated.SaveImage(file);
                o.WriteLine($"Annotated: {file}");
                return Cli.Ok;
            }
            case "templates":
            {
                var sub = args.Length > 1 ? args[1] : "";
                if (sub == "seed")
                {
                    var reference = Opt("--reference") ?? Path.Combine(AppPaths.RaidExtractorRoot(), "output", "ui-reference");
                    var (lib, missing) = TemplateLibrary.SeedFrom(reference, Templates(output));
                    using (lib)
                    {
                        o.WriteLine($"{lib.Entries.Count} template(s) in {lib.Folder} (local only, never committed).");
                        foreach (var m in missing) o.WriteLine("  missing: " + m);
                    }
                    return missing.Count == 0 ? Cli.Ok : Cli.Failed;
                }
                if (sub == "recut")
                {
                    using var lib = TemplateLibrary.Load(Templates(output));
                    using var cap = TakeCapture(o);
                    if (cap is null) return Cli.Failed;
                    var r = new ScreenRecognizer(lib, UiLayout.Load(Path.Combine(AppPaths.Config(), "ui-layout.json"))).Recognize(cap.Image);
                    foreach (var a in r.Anchors)
                    {
                        lib.Recut(a.Name, cap.Image, a.Box, cap.Image.Height);
                        o.WriteLine($"  recut {a.Name} from the live window ({a.Score:0.000})");
                    }
                    o.WriteLine(r.Anchors.Count == 0 ? "No anchor found; nothing recut." : $"{r.Anchors.Count} template(s) now pinned to a {cap.Image.Height} px high window.");
                    return Cli.Ok;
                }
                o.WriteLine("templates seed [--reference <folder>] | templates recut");
                return Cli.Usage;
            }
        }
        return Cli.Usage;
    }

    private static Capture? TakeCapture(TextWriter o)
    {
        var (w, problem) = GameWindow.Find();
        if (w is null)
        {
            o.WriteLine(problem);
            return null;
        }
        o.WriteLine(w);
        var (cap, why) = WindowCapture.Take(w);
        if (cap is null) o.WriteLine("Capture failed: " + why);
        return cap;
    }

    public static string Describe(UiState s)
    {
        var lines = new List<string>
        {
            $"  top: {s.Top?.ToString() ?? "none"}   battle: {(s.InBattle ? "YES" : "no")}   UI blocked: {(s.UiBlocked ? $"yes ({s.BlockingEvents})" : "no")}",
            "  views: " + string.Join(", ", s.Views.Select(v => v.ToString())),
        };
        if (s.ChampionScreen is { } c)
            lines.Add($"  champion screen: hero {c.HeroId?.ToString() ?? "-"} (selected {c.SelectedHeroId?.ToString() ?? "-"}, details {c.DetailsHeroId?.ToString() ?? "-"}),"
                + $" inventory artifact {c.InventoryArtifactId?.ToString() ?? "-"}, details tab {c.DetailsTab?.ToString() ?? "-"}, view state {c.ViewState?.ToString() ?? "-"}");
        else if (s.ChampionScreenOpen) lines.Add("  champion screen: open, context not bound yet");
        return string.Join("\n", lines);
    }
}
