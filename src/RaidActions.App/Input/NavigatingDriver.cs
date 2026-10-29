using System.Text.RegularExpressions;
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
/// <param name="sell">The sell command's driver: its sell-mode clicks are allowed too (<see cref="SellOnlyClicks"/>); swap runs never have them.</param>
/// <param name="upgrade">The upgrade command's driver: the popup's Upgrade and the upgrade screen's one-level Upgrade are allowed too
/// (<see cref="UpgradeOnlyClicks"/>); swap and sell runs never have them.</param>
public sealed partial class NavigatingDriver(LiveGame game, UnityUi ui, Plan? plan, string folder, WindowPolicy policy, Action<string> log,
    IReadOnlySet<int>? equipSteps = null, bool sell = false, bool upgrade = false, bool vault = false, bool unequip = false, bool deposit = false) : IGameDriver, IStepPicker
{
    /// <summary>The kinds this driver may click: <see cref="AllowedClicks"/>, plus the sell-mode clicks for the sell command.</summary>
    private bool IsAllowed(string kind) => AllowedClicks.Contains(kind) || sell && SellOnlyClicks.Contains(kind) || upgrade && UpgradeOnlyClicks.Contains(kind)
        || vault && VaultOnlyClicks.Contains(kind) || deposit && DepositOnlyClicks.Contains(kind);

    private readonly HashSet<int> _equipUnlocked = equipSteps is null ? [] : [.. equipSteps];
    private int _equipUsed;
    /// <summary>The read in which <see cref="Scrolled"/> found its target without scrolling (when, what, the read).</summary>
    private (DateTime Time, string Kind, int? Id, UiState State, IReadOnlyList<UiTarget> Targets)? _found;

    /// <summary>The only kinds this driver may click. Equip, Remove, Sell, PowerUp, Auto-Equip and everything else are refused.</summary>
    public static readonly IReadOnlySet<string> AllowedClicks = new HashSet<string>
    {
        "open-champions", "screen-close", "champion-tile", "gear-tab", "gear-slot", "inventory-item",
        "picker-close", "popup-close-selected", "popup-close-worn", "manage-close", "dialog-close", "swap-cancel", "back",
    };

    /// <summary>The ways back go home may take, innermost first: in the top view, the first of these kinds found is clicked.</summary>
    public static readonly IReadOnlyList<string> BackKinds =
        ["swap-cancel", "popup-close-selected", "popup-close-worn", "picker-close", "manage-close", "dialog-close", "back", "screen-close"];

    /// <summary>The one click outside the allow-list that the next <see cref="Click"/> may make (swap-continue, inside a step's Equip).</summary>
    private string? _once;

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
        var step = a.Step > 0 ? plan!.Steps.Single(s => s.Number == a.Step) : null;
        if (step is not null && a.Kind is ActionKind.OpenChampion or ActionKind.OpenSlot or ActionKind.FindPiece or ActionKind.SelectPiece or ActionKind.VerifySelection)
        {
            var s = await ReadState(ct);
            if (SwapOpenFor(s, step))
                return DriverResult.Success($"the swap confirmation for #{step.ArtifactId} onto {step.ToName} is already open: no click (Equip handles it)");
        }
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
                // Already on this champion (the next piece of the same champion): one read, no wait, no click.
                var s0 = await ReadState(ct);
                if (s0.ChampionScreenOpen && s0.Top?.Name == UiState.ChampionScreenView && s0.ChampionScreen?.Champion == a.ChampionId && Screen.Covering(s0) is null)
                    return DriverResult.Success($"{step!.ToName} (#{a.ChampionId}) is already shown: no click");
                // From whatever screen is open (popups, other screens): back to the village with back/close only, then Champions.
                // The champion screen already on top (another champion of the run): no wait for popups to queue up.
                var s = s0.ChampionScreenOpen && s0.Top?.Name == UiState.ChampionScreenView ? s0 : await Settled(ct);
                if (!(s.ChampionScreenOpen && s.Top?.Name == UiState.ChampionScreenView))
                {
                    await GoHome(a, ct);
                    await OpenChampions(a, ct);
                    (s, _) = await Read(ct);
                }
                if (s.ChampionScreen?.Champion == a.ChampionId) return DriverResult.Success($"{step!.ToName} (#{a.ChampionId}) is already shown: no click");
                // After a real Equip the worn piece's popup (and the picker) stay open over the champion list: close them first,
                // or the click lands on the popup instead of the champion's tile.
                await ClosePiecePanels(a, ct);
                await Scrolled(a, "champion-tile", a.ChampionId, "scroll-champions", ct);
                await Click(a, "champion-tile", a.ChampionId, null, (s2, _) => s2.ChampionScreen?.Champion == a.ChampionId, $"{step!.ToName} is shown", ct);
                return DriverResult.Success($"{step.ToName} (#{a.ChampionId}) shown");
            }
            case ActionKind.OpenSlot:
            {
                var s = await ReadState(ct);
                if (s.ChampionScreen?.Champion != a.ChampionId) throw new StopException($"Expected champion #{a.ChampionId}, the screen shows #{s.ChampionScreen?.Champion}.");
                if (s.ChampionScreen.InventoryArtifactId is not null)
                    await Click(a, "popup-close-selected", null, null, (s2, _) => s2.ChampionScreen?.InventoryArtifactId is null, "the piece's popup closes", ct);
                // The worn piece's popup (left by a real Equip) is closed before anything else, also when no slot click follows.
                var r0 = await ClosePiecePanels(a, ct, picker: false) ?? await Read(ct);
                s = r0.State;
                // The picker lists every piece of its tab (all six gear slots, grouped by set), whichever slot opened it: when it is open
                // and holds the step's piece, no slot click (Equip puts a piece in its own slot; selection and owner are checked after).
                if (r0.Targets.Any(x => x.Kind == "inventory-item" && x.Active) && r0.Targets.Any(x => x.Kind == "inventory-item" && x.Id == a.ArtifactId))
                    return DriverResult.Success($"the picker is open and lists #{a.ArtifactId}: no slot click");
                if (s.ChampionScreen?.DetailsTab != 0)
                {
                    await Click(a, "gear-tab", 1, null, (s2, _) => s2.ChampionScreen?.DetailsTab == 0, "the gear tab is on", ct, read: r0);
                    r0 = await Read(ct);
                }
                await Click(a, "gear-slot", null, a.Slot, (_, t) => t.Any(x => x.Kind == "inventory-item" && x.Active), "the picker shows pieces", ct, read: r0);
                return DriverResult.Success($"{UiTargets.DisplayName(a.Slot!)} slot open");
            }
            case ActionKind.FindPiece:
            {
                var t = await Scrolled(a, "inventory-item", a.ArtifactId, "scroll-picker", ct);
                var png = Shot(a, $"step {a.Step} FindPiece: {t.Name} in the picker at {t.Rect}", t.Rect!.Value);
                return DriverResult.Success($"{t.Name} in the picker at {t.Rect} → {png}");
            }
            case ActionKind.SelectPiece:
            {
                // FindPiece's read, when it found this piece on a still list a moment ago (no input since); else Click reads afresh.
                var found = _found is { } f && f.Kind == "inventory-item" && f.Id == a.ArtifactId && DateTime.Now - f.Time < TimeSpan.FromSeconds(1)
                    ? (f.State, f.Targets) : ((UiState, IReadOnlyList<UiTarget>)?)null;
                _found = null;
                await Click(a, "inventory-item", a.ArtifactId, null, (s, _) => s.ChampionScreen?.InventoryArtifactId == a.ArtifactId, $"artifact #{a.ArtifactId} is selected", ct, read: found);
            }
                return DriverResult.Success($"artifact #{a.ArtifactId} selected");
            case ActionKind.VerifySelection:
            {
                var s = await ReadState(ct);
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
        if (SwapOpenFor(s, step))
        {
            _equipUnlocked.Remove(a.Step); // this step's Equip was clicked before; its confirmation counts as its one use
            log($"step {a.Step}: the swap confirmation is already open (Equip clicked earlier); Equip locked");
            return await ConfirmSwap(a, step, ct);
        }
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
        var png = Shot(a, $"step {a.Step}: CLICK Equip (#{step.ArtifactId} onto {step.ToName}) at {r}", r, always: true);
        await Task.Delay(_random.Next(150, 350), ct);
        ct.ThrowIfCancellationRequested();
        if (_equipUnlocked.Remove(a.Step)) _equipUsed++; // locked again before the click is sent, whatever happens next
        if (MouseInput.Click(w, x, y, _random) is { } notSent) throw new StopException($"Equip not clicked: {notSent}");
        Clicks.Add((a.Step, $"popup: Equip #{step.ArtifactId}", r, true, png ?? "-"));
        log($"step {a.Step}: clicked Equip for #{step.ArtifactId} at ({x:0},{y:0}); Equip locked again");

        // The click's effect, from the account itself.
        var until = DateTime.Now + TimeSpan.FromSeconds(25);
        while (DateTime.Now < until)
        {
            await Task.Delay(400, ct);
            var (after, _) = game.ReadUi();
            if (after?.Top?.Name == UiState.SwapView) return await ConfirmSwap(a, step, ct);
            if (after is not null && Screen.Covering(after) is { } popup) throw new StopException($"After Equip: unexpected popup {popup}.");
            var fresh = await game.Account.ReadAsync(ct);
            var owner = fresh.Artifacts.FirstOrDefault(x => x.Id == step.ArtifactId)?.OwnerHeroId;
            if (owner == step.ToChampionId) return DriverResult.Success($"Equip clicked; the account shows #{step.ArtifactId} on {step.ToName} (#{step.ToChampionId}) → {png}");
        }
        throw new StopException($"After Equip: the account does not show #{step.ArtifactId} on {step.ToName} within 25 s.");
    }

    public static bool AtVillage(UiState s) => s.Top?.Name is "Village" or "VillageHUD";

    /// <summary>
    /// Back to the village from whatever screen is open, with back/close elements only: the top view first, one click at a time, each
    /// read again after. A running battle is waited out (no input); its result screen is then left by its way back. Stops when the
    /// top view has no back/close element memory can identify, when one is ambiguous, or when a click does not change the screen.
    /// Returns the screens it left, in order.
    /// </summary>
    public async Task<List<string>> GoHome(UiAction a, CancellationToken ct)
    {
        var left = new List<string>();
        string? lastPath = null;
        for (var hop = 0; hop < 12; hop++)
        {
            await NoBattle(ct, resultScreenOk: true);
            var s = await Settled(ct);
            if (AtVillage(s)) return left;
            var top = s.Top?.Name ?? throw new StopException("No view is on top.");
            var (_, targets) = await Read(ct);
            var mine = targets.Where(t => t.View == top).ToList();
            // A message box right after an offer was closed ("leave the offer?", seen 2026-10-30: its X brings the offer back, a loop):
            // the button that confirms leaving, never one that names buying, a price or a number.
            if (top == UiState.MessageBoxView && left.LastOrDefault()?.Contains("Offer", StringComparison.Ordinal) == true)
            {
                log($"go home: message box after an offer: \"{s.MessageBox?.Title}\": \"{s.MessageBox?.Message}\"; buttons {string.Join(", ", mine.Where(t => t.Kind == "message-button").Select(t => $"\"{t.Note}\""))}");
                var leave = mine.Where(t => t.Kind == "message-button" && t.NotClickable is null && t.Note is { } text
                    && Regex.IsMatch(text, @"close|leave|yes|ok|exit|skip|later|no,? thanks|decline|cancel|confirm", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(text, @"buy|purchase|get|claim|pay|\d|gem|back to|return", RegexOptions.IgnoreCase)).ToList();
                if (leave.Count == 1)
                {
                    _once = "message-button";
                    await Click(a, "message-button", leave[0].Id, null, (s2, _) => s2.Top?.Name != UiState.MessageBoxView, "the message box closes", ct, view: top);
                    left.Add($"{top} via \"{leave[0].Note}\"");
                    continue;
                }
            }
            var kind = BackKinds.FirstOrDefault(k => mine.Any(t => t.Kind == k && t.NotClickable is null && !t.Forbidden));
            if (kind is null)
            {
                var seen = mine.Where(t => t.NotClickable is null).Select(t => t.Name).Distinct().Take(12);
                throw new StopException($"{top}: no back/close element identified in memory; nothing pressed (its targets: {string.Join(", ", seen.DefaultIfEmpty("none"))}).");
            }
            var (target, problem) = UiTargets.Resolve(mine, kind);
            if (target is null) throw new StopException($"{top}: {problem} Nothing pressed.");
            if (target.Path == lastPath) throw new StopException($"{top}: {target.Name} was clicked and is still there; stopped.");
            lastPath = target.Path;
            var where = top == UiState.ChampionScreenView && s.ChampionScreen?.Champion is { } c ? $"{top} (champion #{c})" : top;
            await Click(a, kind, null, null, (s2, t2) => s2.Top?.Name != top || !t2.Any(t => t.Path == target.Path && t.NotClickable is null),
                $"{target.Name} takes the screen back", ct, view: top);
            left.Add($"{where} via {target.Name}");
        }
        throw new StopException($"Not at the village after 12 back/close clicks ({string.Join(" → ", left)}).");
    }

    /// <summary>
    /// Closes what a previous step left open on the champion screen: the selected piece's popup, the worn piece's popup (the game
    /// opens it after a real Equip) and, with <paramref name="picker"/>, the picker. Close buttons only; nothing open: no click, and
    /// the read it made is returned (still fresh: no input since); null after a click.
    /// </summary>
    private async Task<(UiState State, IReadOnlyList<UiTarget> Targets)?> ClosePiecePanels(UiAction a, CancellationToken ct, bool picker = true)
    {
        string[] kinds = picker ? ["popup-close-selected", "popup-close-worn", "picker-close"] : ["popup-close-selected", "popup-close-worn"];
        var (s, t) = await Read(ct);
        var (stale, clicked) = (false, false);
        foreach (var kind in kinds)
        {
            if (stale)
            {
                (s, t) = await Read(ct);
                stale = false;
            }
            var open = t.FirstOrDefault(x => x.Kind == kind && x.NotClickable is null);
            if (open is null) continue;
            await Click(a, kind, null, null, (_, t2) => !t2.Any(x => x.Kind == kind && x.NotClickable is null), $"{open.Name} closes", ct, read: (s, t));
            (stale, clicked) = (true, true);
            log($"step {a.Step}: closed {open.Name} before going on");
        }
        return clicked ? null : (s, t);
    }

    /// <summary>From the village: Champions, until the champion screen is open.</summary>
    public async Task OpenChampions(UiAction a, CancellationToken ct)
    {
        var s = await Settled(ct);
        if (!AtVillage(s)) throw new StopException($"Expected the village; the screen shows {s.Top?.Name}.");
        await Click(a, "open-champions", null, null, (s2, _) => s2.ChampionScreenOpen && s2.Top?.Name == UiState.ChampionScreenView, "the champion screen opens", ct);
    }

    /// <summary>What go home would click now (read only).</summary>
    public async Task<string> WouldGoHome(CancellationToken ct)
    {
        var s = await Settled(ct);
        if (s.BattleRunning) return "a battle is running: go home waits for it to end";
        if (AtVillage(s)) return "at the village: nothing to close";
        var top = s.Top?.Name ?? "none";
        var (_, targets) = await Read(ct);
        var mine = targets.Where(t => t.View == top).ToList();
        var kind = BackKinds.FirstOrDefault(k => mine.Any(t => t.Kind == k && t.NotClickable is null && !t.Forbidden));
        if (kind is null) return $"{top}: no back/close element identified (targets: {string.Join(", ", mine.Select(t => $"{t.Name} [{t.NotClickable ?? "ok"}]").Distinct().Take(16).DefaultIfEmpty("none"))})";
        var (target, problem) = UiTargets.Resolve(mine, kind);
        return target is null ? $"{top}: {problem}" : $"{top}: would click {target.Name} at {target.Rect} ({target.Path})";
    }

    /// <summary>The piece the plan has <paramref name="step"/>'s champion wearing in its slot just before the step (null: empty).</summary>
    public static int? PlannedWorn(Plan plan, PlanStep step)
    {
        var state = new GearState(plan.Pieces.Select(p => (p.ArtifactId, p.Slot, p.Owner)));
        foreach (var earlier in plan.Steps.Where(x => x.Number < step.Number).OrderBy(x => x.Number)) state.Apply(earlier);
        var worn = state.WornIn(step.ToChampionId, step.Slot);
        return worn == step.ArtifactId ? null : worn;
    }

    private static bool SwapOpenFor(UiState s, PlanStep step) =>
        s.Top?.Name == UiState.SwapView && s.ChampionScreen?.Champion == step.ToChampionId && s.Swap?.ArtifactToEquipId == step.ArtifactId;

    /// <summary>
    /// Why the swap confirmation's text may not be confirmed: unreadable, or it mentions a cost or a currency (any number counts,
    /// rich-text tags aside). Null when it may.
    /// </summary>
    public static string? SwapTextProblem(string? title, string? warning)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(warning)) return "its title or warning could not be read from memory";
        var text = Regex.Replace($"{title} {warning}", "<[^>]*>", " ");
        var cost = Regex.Match(text, @"\d|silver|gold|gem|rub(y|ies)|coin|crystal|energy|currenc|cost|price|pay|spend|fee\b|charge|purchase|buy",
            RegexOptions.IgnoreCase);
        return cost.Success ? $"its text mentions \"{cost.Value}\"" : null;
    }

    /// <summary>
    /// The confirmation the game shows when Equip replaces a worn piece, a planned part of the step's Equip: Continue only when memory
    /// shows exactly the step's champion, the planned worn piece and the step's piece, and the text names no cost or currency.
    /// Wrong ids: Cancel, then stop. A cost (or unreadable text): stop with the dialog left open, nothing pressed.
    /// </summary>
    private async Task<DriverResult> ConfirmSwap(UiAction a, PlanStep step, CancellationToken ct)
    {
        await NoBattle(ct);
        var (s, _) = await Read(ct);
        if (s.Top?.Name != UiState.SwapView || s.Swap is not { } d) throw new StopException($"Expected the swap confirmation; the screen shows {s.Top?.Name}.");
        var worn = PlannedWorn(plan!, step);
        var shown = $"champion #{s.ChampionScreen?.Champion}, worn #{d.EquippedArtifactId}, to equip #{d.ArtifactToEquipId}";
        log($"step {a.Step}: swap confirmation: {shown}; title \"{d.Title}\"; text \"{d.Warning}\"");
        if (worn is null || s.ChampionScreen?.Champion != step.ToChampionId || d.EquippedArtifactId != worn || d.ArtifactToEquipId != step.ArtifactId)
        {
            await Click(a, "swap-cancel", null, null, (s2, _) => s2.Top?.Name != UiState.SwapView, "the swap confirmation closes", ct);
            throw new StopException($"The swap confirmation shows {shown}; planned: champion #{step.ToChampionId}, worn #{(worn is null ? "none" : worn)}, to equip #{step.ArtifactId}. Pressed Cancel.");
        }
        if (SwapTextProblem(d.Title, d.Warning) is { } why)
            throw new StopException($"The swap confirmation is left open, nothing pressed: {why}. Title \"{d.Title}\", text \"{d.Warning}\".");

        _once = "swap-continue";
        await Click(a, "swap-continue", null, null, (s2, _) => s2.Top?.Name != UiState.SwapView, "the swap confirmation closes", ct,
            before: s2 => s2.Swap is { } d2 && d2.EquippedArtifactId == worn && d2.ArtifactToEquipId == step.ArtifactId && s2.ChampionScreen?.Champion == step.ToChampionId
                ? null : "the swap confirmation changed before Continue");
        log($"step {a.Step}: confirmed the swap (#{worn} off, #{step.ArtifactId} on {step.ToName})");

        var until = DateTime.Now + TimeSpan.FromSeconds(25);
        while (DateTime.Now < until)
        {
            await Task.Delay(1500, ct);
            var (after, _) = game.ReadUi();
            if (after is not null && Screen.Covering(after) is { } popup) throw new StopException($"After Continue: unexpected popup {popup}.");
            var fresh = await game.Account.ReadAsync(ct);
            var owner = fresh.Artifacts.FirstOrDefault(x => x.Id == step.ArtifactId)?.OwnerHeroId;
            var wornOwner = fresh.Artifacts.FirstOrDefault(x => x.Id == worn)?.OwnerHeroId;
            if (owner == step.ToChampionId && wornOwner is null)
                return DriverResult.Success($"Equip and its swap confirmation done; the account shows #{step.ArtifactId} on {step.ToName} (#{step.ToChampionId}) and #{worn} in the inventory");
        }
        throw new StopException($"After Continue: the account does not show #{step.ArtifactId} on {step.ToName} and #{worn} in the inventory within 25 s.");
    }

    /// <summary>
    /// One click: allowed kind, no battle, nothing covering the champion screen, the target resolved fresh with its id, the standard
    /// window, then the click, then reads until <paramref name="expect"/> holds (or stop). <paramref name="read"/>: the caller's read,
    /// made just before with no input since (a walk of the champion screen's tree costs about 0.6 s); else a fresh one.
    /// </summary>
    private async Task Click(UiAction a, string kind, int? id, string? slot, Func<UiState, IReadOnlyList<UiTarget>, bool> expect, string expected, CancellationToken ct,
        Func<UiState, string?>? before = null, string? view = null, (UiState State, IReadOnlyList<UiTarget> Targets)? read = null)
    {
        var once = _once == kind;
        _once = null;
        if (!IsAllowed(kind) && !once) throw new StopException($"Refused: {kind} is not an allowed click.");
        await NoBattle(ct, resultScreenOk: view is not null && UiState.IsBattleResultView(view));
        // The window first: rectangles are read at the size the click is sent at.
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if (policy.Problem(w) is not null)
        {
            FitWindow();
            await Task.Delay(800, ct);
            read = null; // rectangles read before the resize are wrong now
        }
        var (s, targets) = read is { } r0 ? (r0.State, r0.Targets) : await Read(ct);
        if (view is not null)
        {
            if (s.Top?.Name != view) throw new StopException($"Expected {view} on top; the screen shows {s.Top?.Name}.");
            targets = targets.Where(t => t.View == view).ToList();
        }
        if (Screen.Covering(s) is { } cover && kind is not ("manage-close" or "manage-remove" or "vault-gear-remove" or "dialog-close" or "swap-cancel" or "swap-continue" or "back" or "message-button" or "powerup-level" or "powerup-cancel")) throw new StopException($"Unexpected popup {cover} over the champion screen.");
        if (before?.Invoke(s) is { } changed) throw new StopException(changed);
        var (t, problem) = UiTargets.Resolve(targets, kind, id, slot);
        if (t is null) throw new StopException($"{kind}: {problem}");
        if (id is not null && t.Id != id) throw new StopException($"{kind}: id {t.Id}, expected {id}.");
        if (t.Forbidden || !(IsAllowed(t.Kind) || once && t.Kind == kind)) throw new StopException($"Refused: {t.Name} may not be clicked.");
        if (kind is "swap-cancel" or "swap-continue" && t.View != UiState.SwapView) throw new StopException($"Refused: {t.Path} is not on the swap confirmation.");
        if (kind == "message-button" && (t.View != UiState.MessageBoxView || s.Top?.Name != UiState.MessageBoxView)) throw new StopException($"Refused: {t.Path} is not on the message box on top.");
        // An offer's close: only its own close element (BTN_Close1), on the popup that is on top; nothing else on an offer.
        if (kind == "dialog-close" && (t.View != s.Top?.Name || !t.Path.EndsWith("/BTN_Close1", StringComparison.Ordinal)))
            throw new StopException($"Refused: the close found ({t.Path} in {t.View}) is not the close of the popup on top ({s.Top?.Name}).");
        // A back/close click the game ignored (a popup still opening, e.g. a combo offer, or a click lost while the window came to the
        // front) is sent again, up to twice, after a longer wait each time; every other click is sent once.
        var attempts = BackKinds.Contains(kind) ? 3 : 1;
        for (var attempt = 1; ; attempt++)
        {
            (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
            if (policy.Problem(w) is { } bad) throw new StopException($"The window changed before the click: {bad}");
            if (t.ClientWidth is { } cw && (cw != w.Client.Width || t.ClientHeight != w.Client.Height))
                throw new StopException($"The window changed size ({t.ClientWidth}x{t.ClientHeight} → {w.Client.Width}x{w.Client.Height}) before the click.");

            var r = t.Rect!.Value;
            var x = r.X + r.Width * (0.35f + 0.3f * (float)_random.NextDouble());
            var y = r.Y + r.Height * (0.35f + 0.3f * (float)_random.NextDouble());
            var png = Shot(a, $"step {a.Step}: CLICK {t.Name} at {r}", r);
            await Task.Delay(_random.Next(120, 300), ct);
            ct.ThrowIfCancellationRequested();
            if (MouseInput.Click(w, x, y, _random) is { } notSent) throw new StopException($"Not clicked ({t.Name}): {notSent}");
            Clicks.Add((a.Step, t.Name, r, true, png ?? "-"));
            log($"step {a.Step}: clicked {t.Name} at ({x:0},{y:0})" + (attempt > 1 ? $" (attempt {attempt} of {attempts})" : ""));

            var until = DateTime.Now + TimeSpan.FromSeconds(6);
            while (DateTime.Now < until)
            {
                await Task.Delay(200, ct);
                var s2 = await ReadState(ct);
                // The targets (a walk of the whole UI tree) only when the check looks at them.
                if (expect(s2, new LazyTargets(() => Screen.Targets(game, ui, s2)))) return;
            }
            var (sEnd, tEnd) = await Read(ct);
            var shows = $"the screen shows {sEnd.Top?.Name}, champion #{sEnd.ChampionScreen?.Champion}, artifact #{sEnd.ChampionScreen?.InventoryArtifactId}";
            if (attempt >= attempts)
                throw new StopException($"After clicking {t.Name}{(attempts > 1 ? $" {attempts} times" : "")}, expected {expected}; {shows}." +
                    (attempts > 1 ? " The game may be stuck: run restart-game, then go-home." : ""));
            // The same element again, read afresh (it may have moved while the popup opened); anything else on top: stop.
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            (sEnd, tEnd) = await Read(ct);
            if (view is not null && sEnd.Top?.Name != view) throw new StopException($"After clicking {t.Name}: expected {view} on top; {shows}.");
            var again = UiTargets.Resolve(view is null ? tEnd : tEnd.Where(x2 => x2.View == view).ToList(), kind, id, slot);
            if (again.Target is not { } t3 || t3.Path != t.Path) throw new StopException($"After clicking {t.Name}, expected {expected}; {shows}.");
            log($"step {a.Step}: {t.Name} is still there; clicking it again");
            t = t3;
        }
    }

    /// <summary>Scrolls a list with the wheel until the target with <paramref name="id"/> may be clicked.</summary>
    private async Task<UiTarget> Scrolled(UiAction a, string kind, int? id, string areaKind, CancellationToken ct)
    {
        var down = true;
        string? lastSeen = null;
        // Pixels one wheel notch moves the list, measured from the target's own rectangle between turns (null until measured).
        float? perNotch = null;
        float? lastY = null;
        var lastNotches = 0;
        for (var i = 0; i < 80; i++)
        {
            var (s, targets) = await Read(ct);
            if (Screen.Covering(s) is { } cover) throw new StopException($"Unexpected popup {cover} over the champion screen.");
            var (t, _) = UiTargets.Resolve(targets, kind, id);
            // Found without scrolling: the list is still, so the click that follows may use this read (see SelectPiece).
            _found = t is not null && i == 0 ? (DateTime.Now, kind, id, s, targets) : null;
            if (t is not null) return t;
            var (area, problem) = UiTargets.Resolve(targets, areaKind);
            if (area is null) throw new StopException($"{areaKind}: {problem}");
            var ar = area.Rect!.Value;
            // Direction: towards the target when the list holds it (above or below the viewport); else down, then up at the end.
            var known = targets.FirstOrDefault(x => x.Kind == kind && x.Id == id && x.Active && x.Rect is not null);
            if (known is not null) down = known.Rect!.Value.Y > ar.Y;
            // Big turns while the target is more than a viewport away, small ones near it (or when its place is unknown). Once one
            // turn has shown how far a notch moves the list, the turn brings the target to the middle of the viewport in one go.
            var gap = known is null ? 0 : Math.Abs(known.Rect!.Value.Center.Y - ar.Center.Y);
            if (known is not null && lastY is { } y0 && lastNotches > 0 && Math.Abs(known.Rect!.Value.Y - y0) is var moved and > 1)
                perNotch = moved / lastNotches;
            lastY = known?.Rect!.Value.Y;
            var away = gap / Math.Max(1, ar.Height);
            var notches = perNotch is { } ppn && known is not null ? Math.Clamp((int)Math.Round(gap / ppn), 1, 60)
                : away > 4 ? 15 : away > 2 ? 8 : away > 1 ? 4 : 2;
            lastNotches = notches;
            var seen = string.Join(",", targets.Where(x => x.Kind == kind && x.FullyVisible).Select(x => x.Id));
            if (known is null && seen == lastSeen) down = !down; // nothing moved: an end of the list
            lastSeen = seen;
            var (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
            await NoBattle(ct);
            var (cx, cy) = ar.Center;
            // The picker takes fast notches (8 ms apart, as the sell command's turns); other lists keep the default.
            if (MouseInput.Wheel(w, cx, cy, down ? -notches : notches, gapMs: areaKind == "scroll-picker" ? 8 : 35) is { } notSent) throw new StopException($"Not scrolled: {notSent}");
            log($"step {a.Step}: scrolled {area.Name} {(down ? "down" : "up")} {notches} notch{(notches == 1 ? "" : "es")}{(perNotch is { } pn ? $" ({pn:0.#} px each)" : "")}");
            await Task.Delay(300, ct);
        }
        throw new StopException($"{kind} {id} not found after scrolling the list.");
    }

    /// <summary>Reads until the top view has stayed the same for 1.5 s (popups that queue up have appeared), at most 10 s.</summary>
    private async Task<UiState> Settled(CancellationToken ct)
    {
        var s = await ReadState(ct);
        var since = DateTime.Now;
        var until = DateTime.Now + TimeSpan.FromSeconds(10);
        while (DateTime.Now < until)
        {
            await Task.Delay(500, ct);
            var s2 = await ReadState(ct);
            if (s2.Top?.Name != s.Top?.Name || s2.ChampionScreenOpen != s.ChampionScreenOpen) since = DateTime.Now;
            s = s2;
            if (DateTime.Now - since >= TimeSpan.FromSeconds(1.5)) break;
        }
        return s;
    }

    /// <summary>Waits (no input) while a battle or its result screen is open.</summary>
    /// <param name="resultScreenOk">A battle's result screen (the battle is over) does not count: go home leaves it by its way back.</param>
    private async Task NoBattle(CancellationToken ct, bool resultScreenOk = false)
    {
        var until = DateTime.Now + BattleWait;
        var said = false;
        while (true)
        {
            var (s, why) = game.ReadUi();
            // A read can fail while the game swaps screens: retried for a few seconds before it stops the run.
            for (var retry = 0; s is null && retry < 10; retry++)
            {
                await Task.Delay(300, ct);
                (s, why) = game.ReadUi();
            }
            if (s is null) throw new StopException("UI state unavailable: " + why);
            var running = resultScreenOk ? s.BattleRunning : s.InBattle;
            if (!running && !s.UiBlocked) return;
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
    public void FitWindow()
    {
        var (w, why) = GameWindow.Find();
        if (w is null) throw new StopException(why!);
        if (policy.Problem(w) is null) return;
        _before ??= WindowFit.Save(w);
        var from = w.Minimized ? "minimized" : $"{w.Client.Width}x{w.Client.Height}";
        // A minimized window is still restoring (and changing DPI) when the first size is applied: measured again and sized again.
        for (var attempt = 1; ; attempt++)
        {
            var fit = WindowFit.Plan(w, policy.MinWidth, policy.MinHeight);
            var (after, problem) = WindowFit.Apply(w, fit);
            if (after is null) throw new StopException("window fit: " + problem);
            if (policy.Problem(after) is not { } still)
            {
                log($"window fit: {from} → {after.Client.Width}x{after.Client.Height} (the minimum){(attempt > 1 ? $", attempt {attempt}" : "")}");
                return;
            }
            if (attempt == 3) throw new StopException("window fit: " + still);
            Thread.Sleep(500);
            (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
        }
    }

    /// <summary>
    /// After the run: the window is left at the size this driver gave it (Ehsan, 2026-10-28: no putting back, so the next run needs
    /// no resize). Only says what changed; null when nothing was resized.
    /// </summary>
    public string? RestoreWindow()
    {
        if (_before is null) return null;
        var before = _before;
        _before = null;
        return $"window left at the minimum size (it was {before.Normal.Width}x{before.Normal.Height}{(before.Minimized ? ", minimized" : "")})";
    }

    private async Task<(UiState State, List<UiTarget> Targets)> Read(CancellationToken ct)
    {
        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (s, _) = game.ReadUi();
            if (s is not null) return (s, Screen.Targets(game, ui, s));
            await Task.Delay(300, ct);
        }
        throw new StopException("The UI state could not be read.");
    }

    /// <summary>An overlay picture of the click. In a real run only the Equip clicks (<paramref name="always"/>): a capture per navigation
    /// click costs time; dry runs keep every one.</summary>
    private string? Shot(UiAction a, string caption, ClientRect r, bool always = false) =>
        !always && _equipUnlocked.Count + _equipUsed > 0 ? null
        : Screen.Overlay(folder, $"{++_shot:00}-step{a.Step}-{a.Kind}.png", caption, r, WindowPolicy.CaptureProblem);

    /// <summary>The UI state alone (no target walk), for checks that need no rectangles.</summary>
    private async Task<UiState> ReadState(CancellationToken ct)
    {
        for (var i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (s, _) = game.ReadUi();
            if (s is not null) return s;
            await Task.Delay(300, ct);
        }
        throw new StopException("The UI state could not be read.");
    }

    /// <summary>
    /// Which of the steps (same champion, each from the inventory) to do next: the one whose piece the open picker shows fully,
    /// else the one nearest the middle of the picker; the first when the picker is not open or shows none of them. Reads only.
    /// </summary>
    public async Task<int> PickAsync(IReadOnlyList<int> steps, CancellationToken ct)
    {
        if (plan is null || steps.Count == 0) return steps.FirstOrDefault();
        var (s, t) = await Read(ct);
        var first = plan.Steps.Single(x => x.Number == steps[0]);
        if (s.ChampionScreen?.Champion != first.ToChampionId || Screen.Covering(s) is not null) return steps[0];
        var items = t.Where(x => x.Kind == "inventory-item" && x.Active && x.Rect is not null).ToList();
        if (items.Count == 0) return steps[0];
        var area = t.FirstOrDefault(x => x.Kind == "scroll-picker" && x.Rect is not null);
        var cy = area?.Rect!.Value.Center.Y ?? items.Average(x => x.Rect!.Value.Center.Y);
        var best = steps.Select(n => (N: n, Item: items.FirstOrDefault(x => x.Id == plan.Steps.Single(p => p.Number == n).ArtifactId)))
            .Where(x => x.Item is not null)
            .OrderBy(x => x.Item!.FullyVisible ? 0 : 1).ThenBy(x => Math.Abs(x.Item!.Rect!.Value.Center.Y - cy))
            .Select(x => (int?)x.N).FirstOrDefault();
        return best ?? steps[0];
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

    public sealed class StopException(string message) : Exception(message);
}

/// <summary>A target list walked from the UI tree only when first used.</summary>
internal sealed class LazyTargets(Func<List<UiTarget>> make) : IReadOnlyList<UiTarget>
{
    private List<UiTarget>? _list;
    private List<UiTarget> List => _list ??= make();
    public UiTarget this[int index] => List[index];
    public int Count => List.Count;
    public IEnumerator<UiTarget> GetEnumerator() => List.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
