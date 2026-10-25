using RaidActions.Execution;
using RaidActions.Game.Dry;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;
using RaidActions.Plans;

namespace RaidActions.App.Input;

/// <summary>
/// The dry run that navigates by itself: it opens screens, picks the champion, the gear tab and slot, scrolls lists, selects the
/// piece and closes popups, all with clicks from memory targets, and shows the Equip click it would make as an overlay without
/// pressing it. Every click is on the allow-list, resolved fresh from memory with an id check right before it, sent only to Raid's
/// window, and followed by a read that must show the expected change; anything unexpected stops the run. No click during a battle.
/// </summary>
public sealed class NavigatingDriver(LiveGame game, UnityUi ui, Plan plan, string folder, WindowStandard standard, Action<string> log) : IGameDriver
{
    /// <summary>The only kinds this driver may click. Equip, Remove, Sell, PowerUp, Auto-Equip and everything else are refused.</summary>
    public static readonly IReadOnlySet<string> AllowedClicks = new HashSet<string>
    {
        "open-champions", "screen-close", "champion-tile", "gear-tab", "gear-slot", "inventory-item",
        "picker-close", "popup-close-selected", "popup-close-worn", "manage-close",
    };

    private static readonly TimeSpan BattleWait = TimeSpan.FromMinutes(30);
    private readonly Random _random = new();
    private int _shot;

    public string Name => "navigate (dry run: Equip never pressed)";
    public bool SendsInput => true;

    /// <summary>Every click sent, and every click only shown (Equip): step, what, rectangle, sent or not, overlay.</summary>
    public List<(int Step, string What, ClientRect Rect, bool Sent, string Overlay)> Clicks { get; } = [];

    public async Task<DriverResult> PerformAsync(UiAction a, CancellationToken ct)
    {
        try { return await Do(a, ct); }
        catch (StopException e) { return DriverResult.Failure(e.Message); }
    }

    private async Task<DriverResult> Do(UiAction a, CancellationToken ct)
    {
        var step = a.Step > 0 ? plan.Steps.Single(s => s.Number == a.Step) : null;
        switch (a.Kind)
        {
            case ActionKind.CheckGameIdle:
                await NoBattle(ct);
                FitWindow();
                return DriverResult.Success("no battle, window at the standard size");
            case ActionKind.FocusGame:
                return DriverResult.Success("the game is brought to the front only when a click needs it");
            case ActionKind.OpenChampion:
            {
                var (s, _) = await Read(ct);
                if (!s.ChampionScreenOpen)
                {
                    if (Screen.Covering(s) is null && s.Top?.Name is "VillageHUD" or "Village")
                        await Click(a, "open-champions", null, null, (s2, _) => s2.ChampionScreenOpen, "the champion screen opens", ct);
                    else throw new StopException($"Unexpected screen {s.Top?.Name}: not the village or the champion screen.");
                    (s, _) = await Read(ct);
                }
                if (s.ChampionScreen?.Champion == a.ChampionId) return DriverResult.Success($"{step!.ToName} (#{a.ChampionId}) is already shown: no click");
                await Scrolled(a, "champion-tile", a.ChampionId, "scroll-champions", ct);
                await Click(a, "champion-tile", a.ChampionId, null, (s2, _) => s2.ChampionScreen?.Champion == a.ChampionId, $"{step!.ToName} is shown", ct);
                return DriverResult.Success($"{step.ToName} (#{a.ChampionId}) shown");
            }
            case ActionKind.OpenSlot:
            {
                var (s, _) = await Read(ct);
                if (s.ChampionScreen?.Champion != a.ChampionId) throw new StopException($"Expected champion #{a.ChampionId}, the screen shows #{s.ChampionScreen?.Champion}.");
                if (s.ChampionScreen.InventoryArtifactId is not null)
                    await Click(a, "popup-close-selected", null, null, (s2, _) => s2.ChampionScreen?.InventoryArtifactId is null, "the piece's popup closes", ct);
                (s, _) = await Read(ct);
                if (s.ChampionScreen?.DetailsTab != 0)
                    await Click(a, "gear-tab", 1, null, (s2, _) => s2.ChampionScreen?.DetailsTab == 0, "the gear tab is on", ct);
                await Click(a, "gear-slot", null, a.Slot, (_, t) => t.Any(x => x.Kind == "inventory-item" && x.Active), "the picker shows pieces", ct);
                return DriverResult.Success($"{UiTargets.DisplayName(a.Slot!)} slot open");
            }
            case ActionKind.FindPiece:
            {
                var t = await Scrolled(a, "inventory-item", a.ArtifactId, "scroll-picker", ct);
                var png = Shot(a, $"step {a.Step} FindPiece: {t.Name} in the picker at {t.Rect}", t.Rect!.Value);
                return DriverResult.Success($"{t.Name} in the picker at {t.Rect} → {png}");
            }
            case ActionKind.SelectPiece:
                await Click(a, "inventory-item", a.ArtifactId, null, (s, _) => s.ChampionScreen?.InventoryArtifactId == a.ArtifactId, $"artifact #{a.ArtifactId} is selected", ct);
                return DriverResult.Success($"artifact #{a.ArtifactId} selected");
            case ActionKind.VerifySelection:
            {
                var (s, _) = await Read(ct);
                return s.ChampionScreen?.Champion == a.ChampionId && s.ChampionScreen.InventoryArtifactId == a.ArtifactId
                    ? DriverResult.Success($"memory: champion #{a.ChampionId} shown, artifact #{a.ArtifactId} selected")
                    : DriverResult.Failure($"memory: champion #{s.ChampionScreen?.Champion}, artifact #{s.ChampionScreen?.InventoryArtifactId}; expected #{a.ChampionId}, #{a.ArtifactId}.");
            }
            case ActionKind.Highlight:
            {
                var (s, targets) = await Read(ct);
                var (equip, problem) = UiTargets.Resolve(targets, "popup-equip");
                if (equip is null) throw new StopException("The popup's Equip button: " + problem);
                if (s.ChampionScreen?.InventoryArtifactId != a.ArtifactId) throw new StopException("The selection changed before the Equip highlight.");
                var png = Shot(a, $"step {a.Step}: WOULD CLICK Equip (dry run: NOT pressed) at {equip.Rect}", equip.Rect!.Value);
                Clicks.Add((a.Step, "popup: Equip (not pressed)", equip.Rect!.Value, false, png ?? "-"));
                await Click(a, "popup-close-selected", null, null, (s2, _) => s2.ChampionScreen?.InventoryArtifactId is null, "the piece's popup closes", ct);
                return DriverResult.Success($"would click Equip at {equip.Rect} (NOT pressed) → {png}; popup closed");
            }
            default:
                return DriverResult.Failure($"{a.Kind} is never done by the navigating dry run.");
        }
    }

    /// <summary>
    /// One click: allowed kind, no battle, nothing covering the champion screen, the target resolved fresh with its id, the standard
    /// window, then the click, then reads until <paramref name="expect"/> holds (or stop).
    /// </summary>
    private async Task Click(UiAction a, string kind, int? id, string? slot, Func<UiState, IReadOnlyList<UiTarget>, bool> expect, string expected, CancellationToken ct)
    {
        if (!AllowedClicks.Contains(kind)) throw new StopException($"Refused: {kind} is not an allowed click.");
        await NoBattle(ct);
        var (s, targets) = await Read(ct);
        if (Screen.Covering(s) is { } cover && kind is not ("manage-close")) throw new StopException($"Unexpected popup {cover} over the champion screen.");
        var (t, problem) = UiTargets.Resolve(targets, kind, id, slot);
        if (t is null) throw new StopException($"{kind}: {problem}");
        if (id is not null && t.Id != id) throw new StopException($"{kind}: id {t.Id}, expected {id}.");
        if (t.Forbidden || !AllowedClicks.Contains(t.Kind)) throw new StopException($"Refused: {t.Name} may not be clicked.");
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if ((w.NotUsable ?? standard.Mismatch(w)) is { } bad) throw new StopException(bad);

        var r = t.Rect!.Value;
        var x = r.X + r.Width * (0.35f + 0.3f * (float)_random.NextDouble());
        var y = r.Y + r.Height * (0.35f + 0.3f * (float)_random.NextDouble());
        var png = Shot(a, $"step {a.Step}: CLICK {t.Name} at {r}", r);
        await Task.Delay(_random.Next(250, 600), ct);
        ct.ThrowIfCancellationRequested();
        if (MouseInput.Click(w, x, y, _random) is { } notSent) throw new StopException($"Not clicked ({t.Name}): {notSent}");
        Clicks.Add((a.Step, t.Name, r, true, png ?? "-"));
        log($"step {a.Step}: clicked {t.Name} at ({x:0},{y:0})");

        var until = DateTime.Now + TimeSpan.FromSeconds(6);
        while (DateTime.Now < until)
        {
            await Task.Delay(300, ct);
            var (s2, t2) = await Read(ct);
            if (expect(s2, t2)) return;
        }
        var (sEnd, _) = await Read(ct);
        throw new StopException($"After clicking {t.Name}, expected {expected}; the screen shows {sEnd.Top?.Name}, champion #{sEnd.ChampionScreen?.Champion}, artifact #{sEnd.ChampionScreen?.InventoryArtifactId}.");
    }

    /// <summary>Scrolls a list with the wheel until the target with <paramref name="id"/> may be clicked.</summary>
    private async Task<UiTarget> Scrolled(UiAction a, string kind, int? id, string areaKind, CancellationToken ct)
    {
        var down = true;
        string? lastSeen = null;
        for (var i = 0; i < 80; i++)
        {
            var (s, targets) = await Read(ct);
            if (Screen.Covering(s) is { } cover) throw new StopException($"Unexpected popup {cover} over the champion screen.");
            var (t, _) = UiTargets.Resolve(targets, kind, id);
            if (t is not null) return t;
            var (area, problem) = UiTargets.Resolve(targets, areaKind);
            if (area is null) throw new StopException($"{areaKind}: {problem}");
            var ar = area.Rect!.Value;
            // Direction: towards the target when the list holds it (above or below the viewport); else down, then up at the end.
            var known = targets.FirstOrDefault(x => x.Kind == kind && x.Id == id && x.Active && x.Rect is not null);
            if (known is not null) down = known.Rect!.Value.Y > ar.Y;
            // Big turns while the target is more than a viewport away, small ones near it (or when its place is unknown).
            var away = known is null ? 0 : Math.Abs(known.Rect!.Value.Center.Y - ar.Center.Y) / Math.Max(1, ar.Height);
            var notches = away > 4 ? 15 : away > 2 ? 8 : away > 1 ? 4 : 2;
            var seen = string.Join(",", targets.Where(x => x.Kind == kind && x.FullyVisible).Select(x => x.Id));
            if (known is null && seen == lastSeen) down = !down; // nothing moved: an end of the list
            lastSeen = seen;
            var (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
            await NoBattle(ct);
            var (cx, cy) = ar.Center;
            if (MouseInput.Wheel(w, cx, cy, down ? -notches : notches) is { } notSent) throw new StopException($"Not scrolled: {notSent}");
            log($"step {a.Step}: scrolled {area.Name} {(down ? "down" : "up")}");
            await Task.Delay(450, ct);
        }
        throw new StopException($"{kind} {id} not found after scrolling the list.");
    }

    /// <summary>Waits (no input) while a battle or its result screen is open.</summary>
    private async Task NoBattle(CancellationToken ct)
    {
        var until = DateTime.Now + BattleWait;
        var said = false;
        while (true)
        {
            var (s, why) = game.ReadUi();
            if (s is null) throw new StopException("UI state unavailable: " + why);
            if (!s.InBattle && !s.UiBlocked) return;
            if (DateTime.Now > until) throw new StopException("A battle (or its result screen) stayed open for 30 minutes.");
            if (!said && s.InBattle) log("A battle (or its result screen) is open: waiting for it to end, no input.");
            said = true;
            await Task.Delay(2000, ct);
        }
    }

    /// <summary>The standard window size (window fit), when the window is minimized or another size.</summary>
    private void FitWindow()
    {
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if (w.NotUsable is null && standard.Mismatch(w) is null) return;
        var plan = WindowFit.Plan(w, standard.Width, standard.Height);
        var (after, problem) = WindowFit.Apply(w, plan);
        if (after is null) throw new StopException("window fit: " + problem);
        if (standard.Mismatch(after) is { } still) throw new StopException("window fit: " + still);
        log($"window fit: {w.Client.Width}x{w.Client.Height} → {after.Client.Width}x{after.Client.Height}");
    }

    private async Task<(UiState State, List<UiTarget> Targets)> Read(CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (s, _) = game.ReadUi();
            if (s is not null) return (s, Screen.Targets(game, ui, s));
            await Task.Delay(200, ct);
        }
        throw new StopException("The UI state could not be read.");
    }

    private string? Shot(UiAction a, string caption, ClientRect r) =>
        Screen.Overlay(folder, $"{++_shot:00}-step{a.Step}-{a.Kind}.png", caption, r, standard.Mismatch);

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

    private sealed class StopException(string message) : Exception(message);
}
