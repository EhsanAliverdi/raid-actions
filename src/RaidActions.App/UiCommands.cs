using System.Text;
using OpenCvSharp;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;

namespace RaidActions.App;

/// <summary>
/// Unity UI from memory: element trees, clickable rectangles, and the overlay check (memory rectangles drawn on a capture).
/// Read-only: nothing here sends input to the game.
/// </summary>
public static class UiCommands
{
    public static readonly string[] Names = ["ui-tree", "ui-rects", "overlay"];

    public static string NativeLayoutPath => Path.Combine(AppPaths.Config(), "ui-native.json");

    public static async Task<int> RunAsync(string command, string[] args, string output, TextWriter o)
    {
        string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var viewArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && args[Array.IndexOf(args, a) - 1] is not ("--for" or "--out"));
        var (game, problem) = LiveGame.Open(GameCommands.LivePaths(output));
        if (game is null)
        {
            o.WriteLine(problem);
            return Cli.Failed;
        }
        using (game)
        {
            var (w, why) = GameWindow.Find();
            if (w is null)
            {
                o.WriteLine(why);
                return Cli.Failed;
            }
            UnityUi ui;
            try { ui = game.Ui(NativeLayoutPath); }
            catch (UiReadException e)
            {
                o.WriteLine(e.Message);
                return Cli.Failed;
            }

            if (command == "ui-tree" && args.Contains("--watch"))
            {
                var until = double.TryParse(Opt("--for"), System.Globalization.CultureInfo.InvariantCulture, out var min) ? DateTime.Now.AddMinutes(min) : DateTime.MaxValue;
                string? last = null;
                string? lastProblem = null;
                var lastBeat = DateTime.Now;
                o.WriteLine($"Watching until {(until == DateTime.MaxValue ? "stopped" : until.ToString("HH:mm:ss"))}; a snapshot (tree + overlay) on every UI change.");
                while (DateTime.Now < until)
                {
                    var (s, readProblem) = game.ReadUi();
                    // A failed read is logged (once per distinct problem), never mistaken for "nothing changed".
                    if (s is null && readProblem != lastProblem) o.WriteLine($"{DateTime.Now:HH:mm:ss} UI read failed: {readProblem}");
                    lastProblem = s is null ? readProblem : null;
                    if (DateTime.Now - lastBeat > TimeSpan.FromMinutes(1))
                    {
                        lastBeat = DateTime.Now;
                        var (w2, _) = GameWindow.Find();
                        o.WriteLine($"{DateTime.Now:HH:mm:ss} still watching: {(s is null ? "no UI state" : s.Top?.Name ?? "no top view")}, window {w2?.Client.Width}x{w2?.Client.Height}");
                    }
                    var key = s is null ? null : string.Join("|", s.Active.Where(v => v.Visible).Select(v => v.Name)) + s.ChampionScreen;
                    if (key is not null && key != last)
                    {
                        last = key;
                        o.WriteLine($"{DateTime.Now:HH:mm:ss} {GameCommands.Describe(s!).Split('\n')[0].Trim()}");
                        foreach (var (name, view) in TopViews(game, s!)) Snapshot(game, ui, name, view, output, o, capture: true);
                    }
                    await Task.Delay(700);
                }
                return Cli.Ok;
            }

            var (state, stateProblem) = game.ReadUi();
            if (state is null)
            {
                o.WriteLine("UI state unavailable: " + stateProblem);
                return Cli.Failed;
            }
            var views = viewArg is null ? TopViews(game, state) : AllViews(game).Where(v => v.Name == viewArg).ToList();
            if (views.Count == 0)
            {
                o.WriteLine(viewArg is null ? "No open view." : $"View {viewArg} is not open.");
                return Cli.Failed;
            }
            switch (command)
            {
                case "ui-tree":
                    foreach (var (name, view) in views) Snapshot(game, ui, name, view, output, o, capture: false);
                    return Cli.Ok;
                case "ui-rects":
                    foreach (var (name, view) in views)
                    {
                        var tree = ui.Read(view, name, w.Client.Width, w.Client.Height);
                        o.WriteLine($"== {name} ({tree.Nodes.Count} elements){(tree.ScreenProblem is { } sp ? "  " + sp : $"  canvas scale {tree.CanvasScale:0.###}")}");
                        foreach (var n in Clickable(tree)) o.WriteLine("  " + Line(n));
                    }
                    return Cli.Ok;
                case "overlay":
                    foreach (var (name, view) in views) Snapshot(game, ui, name, view, output, o, capture: true, anySize: args.Contains("--any-size"));
                    return Cli.Ok;
            }
        }
        return Cli.Usage;
    }

    /// <summary>Elements a click could target: active in the hierarchy, with a Selectable, on screen.</summary>
    public static IEnumerable<UiNode> Clickable(UiTree t) =>
        t.Nodes.Where(n => n.ActiveInHierarchy && n.Interactable is not null && n.Screen is { Width: > 0, Height: > 0 });

    public static string Line(UiNode n) =>
        $"{n.Screen?.ToString() ?? "no rect",-26} {(n.Interactable == true ? "interactable" : n.Interactable == false ? "NOT interactable" : ""),-16} {n.Path}"
        + (n.Context is { } c ? $"  ctx {Short(c.Class)}" : "");

    private static string Short(string cls) => cls[(cls.LastIndexOf('.') + 1)..];

    /// <summary>Every open view with its managed View object.</summary>
    public static List<(string Name, ulong View)> AllViews(LiveGame game)
    {
        var g = game.Objects;
        var app = g.Root("AppViewModel", "Client.ViewModel.AppViewModel");
        var master = g.Ref(g.Ref(app, "Client.ViewModel.AppViewModel", "_overlayManager"), "Client.ViewModel.OverlayManagerBase", "_viewMaster");
        var result = new List<(string, ulong)>();
        foreach (var meta in g.RefList(g.Ref(master, "Client.RaidApp.RaidViewMaster", "_views"), 256))
        {
            var key = g.Int(meta, "Client.RaidApp.ViewMeta", "Key");
            var view = g.Ref(meta, "Client.RaidApp.ViewMeta", "View");
            if (view != 0) result.Add((g.EnumName(UiStateReader.ViewKeyEnum, key) ?? $"#{key}", view));
        }
        return result;
    }

    /// <summary>The visible active views, without the notification layers.</summary>
    public static List<(string Name, ulong View)> TopViews(LiveGame game, UiState s)
    {
        var visible = s.Active.Where(v => v.Visible && !v.Name.EndsWith("NotificationsView", StringComparison.Ordinal) && v.Name != "BlockingView").Select(v => v.Name).ToHashSet();
        return AllViews(game).Where(v => visible.Contains(v.Name)).ToList();
    }

    private static void Snapshot(LiveGame game, UnityUi ui, string name, ulong view, string output, TextWriter o, bool capture, bool anySize = false)
    {
        var (w, _) = GameWindow.Find();
        if (w is null) return;
        UiTree tree;
        try { tree = ui.Read(view, name, w.NotUsable is null ? w.Client.Width : 0, w.NotUsable is null ? w.Client.Height : 0); }
        catch (Exception e) when (e is UiReadException or RaidExtractor.ReadCheckException)
        {
            o.WriteLine($"  {name}: {e.Message}");
            return;
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(output, "ui-trees");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{stamp}-{name}.txt");
        File.WriteAllText(file, Text(tree));
        var active = tree.Nodes.Count(n => n.ActiveInHierarchy);
        var contexts = tree.Nodes.Where(n => n.ActiveInHierarchy && n.Context is not null).GroupBy(n => Short(n.Context!.Class)).OrderByDescending(x => x.Count()).Take(8);
        var screen = w.NotUsable is { } unusable ? "; no screen rectangles: " + unusable : tree.ScreenProblem is { } p ? "; " + p : "";
        var broken = tree.Nodes.Count(n => n.Name.StartsWith("<unreadable", StringComparison.Ordinal));
        o.WriteLine($"  {name}: {tree.Nodes.Count} elements, {active} active, {Clickable(tree).Count()} clickable{(broken > 0 ? $", {broken} unreadable" : "")}{screen} → {file}");
        o.WriteLine("    contexts: " + string.Join(", ", contexts.Select(x => $"{x.Key} x{x.Count()}")));
        if (!capture) return;
        if (!anySize && WindowStandard.Load(GameCommands.WindowConfig).Mismatch(w) is { } mismatch)
        {
            o.WriteLine("  overlay refused: " + mismatch);
            return;
        }
        var (cap, why) = WindowCapture.Take(w);
        if (cap is null)
        {
            o.WriteLine("  overlay: no capture: " + why);
            return;
        }
        using (cap)
        {
            using var img = Overlay(cap.Image, tree);
            var odir = Path.Combine(output, "overlays");
            Directory.CreateDirectory(odir);
            var png = Path.Combine(odir, $"{stamp}-{name}.png");
            img.SaveImage(png);
            o.WriteLine($"    overlay → {png}");
        }
    }

    /// <summary>The memory rectangles on a capture: clickable (green, red when not interactable), context-bound (cyan).</summary>
    public static Mat Overlay(Mat capture, UiTree tree)
    {
        var img = capture.Clone();
        foreach (var n in tree.Nodes.Where(n => n.ActiveInHierarchy && n.Screen is not null))
        {
            var r = n.Screen!.Value;
            var box = new Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
            if (n.Interactable is { } i)
            {
                img.Rectangle(box, i ? Scalar.LimeGreen : Scalar.Red, 2);
                img.PutText(n.Name, new Point(box.X + 2, box.Y + 12), HersheyFonts.HersheySimplex, 0.35, i ? Scalar.LimeGreen : Scalar.Red, 1);
            }
            else if (n.Context is not null) img.Rectangle(box, Scalar.Cyan, 1);
        }
        img.PutText($"{tree.View}: memory rectangles (green clickable, red not interactable, cyan bound context)", new Point(8, img.Height - 10),
            HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
        return img;
    }

    public static string Text(UiTree tree)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{tree.View}: {tree.Nodes.Count} elements, client {tree.ClientWidth}x{tree.ClientHeight}, canvas scale {tree.CanvasScale:0.####}{(tree.ScreenProblem is { } p ? ", " + p : "")}");
        foreach (var n in tree.Nodes)
        {
            sb.Append(' ', n.Depth * 2).Append(n.Name);
            if (!n.ActiveInHierarchy) sb.Append(n.ActiveSelf ? "  [inactive parent]" : "  [inactive]");
            if (n.Screen is { } r) sb.Append("  ").Append(r);
            if (n.Interactable is { } i) sb.Append(i ? "  interactable" : "  NOT interactable");
            if (n.Context is { } c) sb.Append("  ctx ").Append(c.Class).Append($" @0x{c.Address:X}");
            var scripts = n.Components.Where(x => x.ManagedClass is not null).Select(x => Short(x.ManagedClass!)).ToList();
            if (scripts.Count > 0) sb.Append("  [").Append(string.Join(", ", scripts)).Append(']');
            sb.AppendLine($"  #{n.Index}");
        }
        return sb.ToString();
    }
}
