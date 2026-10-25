using OpenCvSharp;
using RaidActions.Execution;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;
using RaidActions.Plans;

namespace RaidActions.Game.Dry;

/// <summary>
/// The dry run against the live game, sending NO input: for every action it waits until the game shows the screen the action
/// needs (and says in one plain line what to open when it does not), resolves the click target from memory, saves an overlay with
/// the target marked, and records "would click … at …". Selections are confirmed from memory. Equip is never clicked: a dry run
/// highlights the popup's Equip button instead. Nothing is brought to the front.
/// </summary>
public sealed class ObservingDriver(LiveGame game, UnityUi ui, Plan plan, string folder, Func<GameWindowInfo, string?> sizeCheck,
    Action<string> say, TimeSpan? waitLimit = null) : IGameDriver
{
    private readonly TimeSpan _wait = waitLimit ?? TimeSpan.FromMinutes(20);
    private int _shot;

    public string Name => "observe (no input)";
    public bool SendsInput => false;

    /// <summary>Every "would click" so far: step, action, target, rectangle.</summary>
    public List<(int Step, ActionKind Kind, string Target, ClientRect Rect, string Overlay)> WouldClick { get; } = [];

    public async Task<DriverResult> PerformAsync(UiAction a, CancellationToken ct)
    {
        var step = a.Step > 0 ? plan.Steps.Single(s => s.Number == a.Step) : null;
        string Piece() => $"{step!.Slot} #{step.ArtifactId} ({step.Piece})";
        switch (a.Kind)
        {
            case ActionKind.CheckGameIdle:
            {
                var (s0, why) = game.ReadUi();
                if (s0 is null) return DriverResult.Failure("UI state unavailable: " + why);
                if (s0.InBattle) return DriverResult.Failure("A battle (or a battle's result screen) is open: close it first.");
                var until = DateTime.Now + _wait;
                var said = "";
                while (DateTime.Now < until)
                {
                    ct.ThrowIfCancellationRequested();
                    var (s, _) = game.ReadUi();
                    if (s?.InBattle == true) return DriverResult.Failure("A battle started: the dry run stops.");
                    var (w, problem) = GameWindow.Find();
                    var wait = w is null ? problem : (w.NotUsable is not null ? "Restore the Raid window (or run  RaidActions window fit)." : sizeCheck(w)) ?? (s?.UiBlocked == true ? "Wait: the game's UI is busy." : null);
                    if (wait is null) return DriverResult.Success($"idle: top {s?.Top?.Name}, no battle, window {w!.Client.Width}x{w.Client.Height}");
                    if (wait != said) say($"Before starting: {wait}");
                    said = wait;
                    await Task.Delay(700, ct);
                }
                return DriverResult.Failure("Timed out waiting for a usable game window.");
            }
            case ActionKind.FocusGame:
                return DriverResult.Success("observe mode: the game is not brought to the front");
            case ActionKind.OpenChampion:
            {
                var (ok, target, s) = await WaitFor(a, $"Open Champions and scroll the list until {step!.ToName} is visible.", ct, (s, t) =>
                    s.ChampionScreen?.Champion == a.ChampionId || UiTargets.Resolve(t, "champion-tile", a.ChampionId).Target is not null,
                    t => UiTargets.Resolve(t, "champion-tile", a.ChampionId).Target);
                if (!ok) return DriverResult.Failure($"Timed out waiting for {step.ToName}'s tile.");
                if (target is null) return DriverResult.Success($"{step.ToName} (#{a.ChampionId}) is already shown: no click needed");
                return Would(a, target);
            }
            case ActionKind.OpenSlot:
            {
                var (ok, target, _) = await WaitFor(a, $"Show {step!.ToName} on the first gear tab (click their tile).", ct, (s, t) =>
                    s.ChampionScreen?.Champion == a.ChampionId && s.ChampionScreen.DetailsTab == 0 && UiTargets.Resolve(t, "gear-slot", slot: a.Slot).Target is not null,
                    t => UiTargets.Resolve(t, "gear-slot", slot: a.Slot).Target);
                return ok ? Would(a, target!) : DriverResult.Failure($"Timed out waiting for {step.ToName}'s {UiTargets.DisplayName(a.Slot!)} slot.");
            }
            case ActionKind.FindPiece:
            case ActionKind.SelectPiece:
            {
                var (ok, target, _) = await WaitFor(a,
                    $"Click {step!.ToName}'s {UiTargets.DisplayName(a.Slot!)} slot and scroll the picker until {Piece()} is visible.", ct, (s, t) =>
                    s.ChampionScreen?.Champion == a.ChampionId && UiTargets.Resolve(t, "inventory-item", a.ArtifactId).Target is not null,
                    t => UiTargets.Resolve(t, "inventory-item", a.ArtifactId).Target);
                if (!ok) return DriverResult.Failure($"Timed out waiting for {Piece()} in the picker.");
                return a.Kind == ActionKind.FindPiece ? Saw(a, target!, "found in the picker") : Would(a, target!);
            }
            case ActionKind.VerifySelection:
            {
                var (ok, _, s) = await WaitFor(a, $"Click {Piece()} in the picker so its popup shows.", ct, (s, _) =>
                    s.ChampionScreen?.Champion == a.ChampionId && s.ChampionScreen.InventoryArtifactId == a.ArtifactId, _ => null);
                return ok
                    ? DriverResult.Success($"memory: champion #{s!.ChampionScreen!.Champion} shown, artifact #{s.ChampionScreen.InventoryArtifactId} selected")
                    : DriverResult.Failure("Timed out waiting for the selection.");
            }
            case ActionKind.Highlight:
            {
                var (ok, target, _) = await WaitFor(a, $"Keep {Piece()}'s popup open.", ct, (s, t) =>
                    s.ChampionScreen?.InventoryArtifactId == a.ArtifactId && UiTargets.Resolve(t, "popup-equip").Target is not null,
                    t => UiTargets.Resolve(t, "popup-equip").Target);
                if (!ok) return DriverResult.Failure("Timed out waiting for the popup's Equip button.");
                var r = Would(a, target!, "Equip (dry run: NOT clicked)");
                say(step!.FromChampionId is null
                    ? $"Step {a.Step} done in the dry run. Close the popup."
                    : $"Step {a.Step} done in the dry run (a real run would confirm taking it from {step.FromName}). Close the popup.");
                return r;
            }
            default:
                return DriverResult.Failure($"{a.Kind} is never done by the observing dry run.");
        }
    }

    private DriverResult Would(UiAction a, UiTarget t, string? label = null)
    {
        var png = Overlay(a, t, $"WOULD CLICK {label ?? t.Name}");
        WouldClick.Add((a.Step, a.Kind, label ?? t.Name, t.Rect!.Value, png ?? "-"));
        return DriverResult.Success($"would click {label ?? t.Name} at {t.Rect} (center {t.Rect.Value.Center.X:0},{t.Rect.Value.Center.Y:0}) → {png}");
    }

    private DriverResult Saw(UiAction a, UiTarget t, string what)
    {
        var png = Overlay(a, t, $"{t.Name}: {what}");
        return DriverResult.Success($"{t.Name} {what} at {t.Rect} → {png}");
    }

    /// <summary>Polls the UI until <paramref name="ready"/> holds, saying <paramref name="instruction"/> once while it does not.</summary>
    private async Task<(bool Ok, UiTarget? Target, UiState? State)> WaitFor(UiAction a, string instruction, CancellationToken ct,
        Func<UiState, IReadOnlyList<UiTarget>, bool> ready, Func<IReadOnlyList<UiTarget>, UiTarget?> pick)
    {
        var until = DateTime.Now + _wait;
        var said = false;
        string? lastCovered = null;
        while (DateTime.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            var (s, _) = game.ReadUi();
            string? covered = null;
            if (s is not null)
            {
                // A popup over the champion screen would take the click: wait until it is closed.
                covered = s.ChampionScreenOpen && s.Top?.Name != UiState.ChampionScreenView ? s.Top?.Name : null;
                var targets = Targets(s);
                if (covered is null && ready(s, targets)) return (true, pick(targets), s);
            }
            if (covered is not null && covered != lastCovered) say($"Step {a.Step} {a.Kind}: close the {covered} popup first (it covers the champion screen).");
            lastCovered = covered;
            if (!said)
            {
                say($"Step {a.Step} {a.Kind}: {instruction}");
                said = true;
            }
            await Task.Delay(700, ct);
        }
        return (false, null, null);
    }

    /// <summary>The targets of the visible views (the champion screen and any dialog over it), for the current client size.</summary>
    private List<UiTarget> Targets(UiState s)
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

    private string? Overlay(UiAction a, UiTarget t, string label)
    {
        var (w, _) = GameWindow.Find();
        if (w is null || sizeCheck(w) is not null) return null;
        var (cap, _) = WindowCapture.Take(w);
        if (cap is null) return null;
        using (cap)
        {
            var img = cap.Image;
            var r = t.Rect!.Value;
            var box = new Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
            img.Rectangle(new Rect(box.X - 3, box.Y - 3, box.Width + 6, box.Height + 6), Scalar.Yellow, 4);
            var (cx, cy) = r.Center;
            img.DrawMarker(new Point((int)cx, (int)cy), Scalar.Red, MarkerTypes.Cross, 24, 3);
            img.Rectangle(new Rect(0, 0, img.Width, 34), Scalar.Black, -1);
            img.PutText($"step {a.Step} {a.Kind}: {label} at {r}", new Point(8, 24), HersheyFonts.HersheySimplex, 0.7, Scalar.Yellow, 2);
            Directory.CreateDirectory(folder);
            var png = Path.Combine(folder, $"{++_shot:00}-step{a.Step}-{a.Kind}.png");
            img.SaveImage(png);
            return png;
        }
    }

    public string? SaveScreenshot(string folder, string name)
    {
        var (w, _) = GameWindow.Find();
        if (w is null) return null;
        var (cap, _) = WindowCapture.Take(w);
        if (cap is null) return null;
        using (cap)
        {
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, name + ".png");
            cap.Image.SaveImage(file);
            return file;
        }
    }
}
