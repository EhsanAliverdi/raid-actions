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
/// <param name="equipSteps">Steps whose Equip may be clicked (a real run, after the go-ahead): each step's Equip once, then locked
/// again. Empty: Equip is never clicked (dry run).</param>
public sealed class NavigatingDriver(LiveGame game, UnityUi ui, Plan plan, string folder, WindowPolicy policy, Action<string> log,
    IReadOnlySet<int>? equipSteps = null) : IGameDriver
{
    private readonly HashSet<int> _equipUnlocked = equipSteps is null ? [] : [.. equipSteps];

    /// <summary>The only kinds this driver may click. Equip, Remove, Sell, PowerUp, Auto-Equip and everything else are refused.</summary>
    public static readonly IReadOnlySet<string> AllowedClicks = new HashSet<string>
    {
        "open-champions", "screen-close", "champion-tile", "gear-tab", "gear-slot", "inventory-item",
        "picker-close", "popup-close-selected", "popup-close-worn", "manage-close", "dialog-close",
    };

    private static readonly TimeSpan BattleWait = TimeSpan.FromMinutes(30);
    private readonly Random _random = new();
    private int _shot;

    public string Name => _equipUnlocked.Count == 0 ? "navigate (dry run: Equip never pressed)" : $"navigate (Equip unlocked once for step {string.Join(", ", _equipUnlocked)})";
    public bool SendsInput => true;

    /// <summary>Steps whose Equip is still unlocked (each is locked again the moment its one click is sent).</summary>
    public IReadOnlyCollection<int> EquipStillUnlocked => _equipUnlocked;

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
                return DriverResult.Success("no battle, window usable for clicks");
            case ActionKind.FocusGame:
                return DriverResult.Success("the game is brought to the front only when a click needs it");
            case ActionKind.OpenChampion:
            {
                // Popups over the village (offers and the like) come in a queue: close each with its own close button, let the screen
                // settle, read again; at most four. Then the top view must have stayed the village for a moment before Champions is clicked.
                var s = await Settled(ct);
                for (var i = 0; i < 4 && !s.ChampionScreenOpen && s.Top?.Name is { } top && top.EndsWith("Overlay", StringComparison.Ordinal); i++)
                {
                    await Click(a, "dialog-close", null, null, (s2, _) => s2.Top?.Name != top, $"the {top} popup closes", ct);
                    s = await Settled(ct);
                }
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
            case ActionKind.Equip:
                return await Equip(a, step!, ct);
            default:
                return DriverResult.Failure($"{a.Kind} is never done by the navigating driver.");
        }
    }

    /// <summary>
    /// The one Equip click a go-ahead unlocks for this step: memory must show the step's champion with the step's piece selected,
    /// the button must be the selected piece's panel's Equip, and the step's Equip must not have been used. It is locked again before
    /// the click is sent. Then live account reads until the piece is on the champion (or stop).
    /// </summary>
    private async Task<DriverResult> Equip(UiAction a, PlanStep step, CancellationToken ct)
    {
        if (!_equipUnlocked.Contains(a.Step)) return DriverResult.Failure($"Refused: Equip is not unlocked for step {a.Step}.");
        await NoBattle(ct);
        var (s, targets) = await Read(ct);
        if (Screen.Covering(s) is { } cover) throw new StopException($"Unexpected popup {cover} over the champion screen.");
        if (s.ChampionScreen?.Champion != step.ToChampionId || s.ChampionScreen.InventoryArtifactId != step.ArtifactId)
            throw new StopException($"Before Equip: memory shows champion #{s.ChampionScreen?.Champion} and artifact #{s.ChampionScreen?.InventoryArtifactId}; expected #{step.ToChampionId} and #{step.ArtifactId}.");
        var (equip, problem) = UiTargets.Resolve(targets, "popup-equip");
        if (equip is null) throw new StopException("The popup's Equip button: " + problem);
        if (!equip.Path.Contains("/SelectedItemWindow/", StringComparison.Ordinal))
            throw new StopException($"Refused: the Equip button is not on the selected piece's panel ({equip.Path}).");
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if (policy.Problem(w) is { } bad) throw new StopException(bad);

        var r = equip.Rect!.Value;
        var x = r.X + r.Width * (0.35f + 0.3f * (float)_random.NextDouble());
        var y = r.Y + r.Height * (0.35f + 0.3f * (float)_random.NextDouble());
        var png = Shot(a, $"step {a.Step}: CLICK Equip (#{step.ArtifactId} onto {step.ToName}) at {r}", r);
        await Task.Delay(_random.Next(300, 700), ct);
        ct.ThrowIfCancellationRequested();
        _equipUnlocked.Remove(a.Step); // locked again before the click is sent, whatever happens next
        if (MouseInput.Click(w, x, y, _random) is { } notSent) throw new StopException($"Equip not clicked: {notSent}");
        Clicks.Add((a.Step, $"popup: Equip #{step.ArtifactId}", r, true, png ?? "-"));
        log($"step {a.Step}: clicked Equip for #{step.ArtifactId} at ({x:0},{y:0}); Equip locked again");

        // The click's effect, from the account itself.
        var until = DateTime.Now + TimeSpan.FromSeconds(25);
        while (DateTime.Now < until)
        {
            await Task.Delay(1500, ct);
            var (after, _) = game.ReadUi();
            if (after is not null && Screen.Covering(after) is { } popup) throw new StopException($"After Equip: unexpected popup {popup}.");
            var fresh = await game.Account.ReadAsync(ct);
            var owner = fresh.Artifacts.FirstOrDefault(x => x.Id == step.ArtifactId)?.OwnerHeroId;
            if (owner == step.ToChampionId) return DriverResult.Success($"Equip clicked; the account shows #{step.ArtifactId} on {step.ToName} (#{step.ToChampionId}) → {png}");
        }
        throw new StopException($"After Equip: the account does not show #{step.ArtifactId} on {step.ToName} within 25 s.");
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
        if (Screen.Covering(s) is { } cover && kind is not ("manage-close" or "dialog-close")) throw new StopException($"Unexpected popup {cover} over the champion screen.");
        var (t, problem) = UiTargets.Resolve(targets, kind, id, slot);
        if (t is null) throw new StopException($"{kind}: {problem}");
        if (id is not null && t.Id != id) throw new StopException($"{kind}: id {t.Id}, expected {id}.");
        if (t.Forbidden || !AllowedClicks.Contains(t.Kind)) throw new StopException($"Refused: {t.Name} may not be clicked.");
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if (policy.Problem(w) is { } bad)
        {
            FitWindow();
            (w, why) = GameWindow.Find();
            if (w is null || policy.Problem(w) is { } still) throw new StopException(why ?? policy.Problem(w!)!);
        }

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

    /// <summary>Reads until the top view has stayed the same for 1.5 s (popups that queue up have appeared), at most 10 s.</summary>
    private async Task<UiState> Settled(CancellationToken ct)
    {
        var (s, _) = await Read(ct);
        var since = DateTime.Now;
        var until = DateTime.Now + TimeSpan.FromSeconds(10);
        while (DateTime.Now < until)
        {
            await Task.Delay(500, ct);
            var (s2, _) = await Read(ct);
            if (s2.Top?.Name != s.Top?.Name || s2.ChampionScreenOpen != s.ChampionScreenOpen) since = DateTime.Now;
            s = s2;
            if (DateTime.Now - since >= TimeSpan.FromSeconds(1.5)) break;
        }
        return s;
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

    private WindowPlacement? _before;

    /// <summary>
    /// When the window is minimized or below the minimum: restores it to the minimum size (the smallest that keeps click targets
    /// big enough), remembering where it was. Any other size is used as it is.
    /// </summary>
    private void FitWindow()
    {
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if (policy.Problem(w) is null) return;
        _before ??= WindowFit.Save(w);
        var fit = WindowFit.Plan(w, policy.MinWidth, policy.MinHeight);
        var (after, problem) = WindowFit.Apply(w, fit);
        if (after is null) throw new StopException("window fit: " + problem);
        if (policy.Problem(after) is { } still) throw new StopException("window fit: " + still);
        log($"window fit: {(w.Minimized ? "minimized" : $"{w.Client.Width}x{w.Client.Height}")} → {after.Client.Width}x{after.Client.Height} (the minimum)");
    }

    /// <summary>After the run: the window back to the size, position and state it had before this driver resized it.</summary>
    public string? RestoreWindow()
    {
        if (_before is null) return null;
        var (w, why) = GameWindow.Find();
        if (w is null) return "window not restored: " + why;
        var problem = WindowFit.Restore(w, _before);
        return problem is null
            ? $"window restored to {_before.Normal.Width}x{_before.Normal.Height} at ({_before.Normal.X},{_before.Normal.Y}){(_before.Minimized ? ", minimized" : "")}"
            : "window not restored: " + problem;
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
        Screen.Overlay(folder, $"{++_shot:00}-step{a.Step}-{a.Kind}.png", caption, r, WindowPolicy.CaptureProblem);

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
