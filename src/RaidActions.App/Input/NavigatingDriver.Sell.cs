using System.Text.RegularExpressions;
using RaidActions.Execution;
using RaidActions.Game.Dry;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;

namespace RaidActions.App.Input;

/// <summary>
/// Selling with the picker's sell mode, one slot at a time (the sell command only). Every piece is found by its id and ticked only
/// when memory shows it unequipped and not yet ticked; after each tick the game's own list of ticked pieces must be exactly the pieces
/// ticked so far. Before Sell, and again before the game's confirmation is accepted, that list must be exactly the slot's planned pieces.
/// The confirmation's Sell is clicked once per slot, only in a real run (<paramref name="real"/>); a dry run opens the confirmation,
/// reads it, and closes it with its X, then cancels the sell mode, so nothing is sold.
/// </summary>
public sealed partial class NavigatingDriver
{
    /// <summary>The clicks only the sell command adds: the picker's Sell mode toggle and the sell panel's Cancel and Sell (Apply).</summary>
    public static readonly IReadOnlySet<string> SellOnlyClicks = new HashSet<string> { "sell-mode", "sell-cancel", "sell-apply" };

    /// <summary>The confirmation's button that sells: its text is exactly "Sell" (rich-text tags and case aside).</summary>
    public static bool IsSellText(string? text) => text is not null && Regex.Replace(text, "<[^>]*>", "").Trim().Equals("Sell", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why the game's sell confirmation may not be accepted, or null: it must be the message box on top, with exactly two buttons of
    /// which exactly one sells, and its text must be the game's "sell <paramref name="count"/> Artifacts? (0 equipped)": the planned
    /// number of pieces and no worn piece (seen 2026-10-28: "Are you sure you want to sell 3 Artifacts? (0 equipped)").
    /// </summary>
    public static string? ConfirmationProblem(UiState s, IReadOnlyList<UiTarget> targets, int count)
    {
        if (s.Top?.Name != UiState.MessageBoxView) return $"the confirmation is not on top ({s.Top?.Name})";
        var buttons = targets.Where(t => t.View == UiState.MessageBoxView && t.Kind == "message-button").ToList();
        if (buttons.Count != 2) return $"it has {buttons.Count} buttons, expected 2 ({string.Join(", ", buttons.Select(b => b.Note ?? "?"))})";
        var sellButtons = buttons.Count(b => IsSellText(b.Note));
        if (sellButtons != 1) return $"{sellButtons} of its buttons say Sell ({string.Join(", ", buttons.Select(b => $"\"{b.Note ?? "?"}\""))})";
        var text = Regex.Replace(s.MessageBox?.Message ?? "", "<[^>]*>", "");
        var m = Regex.Match(text, @"\bsell\s+(?<n>\d+)\s+artifacts?\b.*?\((?<e>\d+)\s+equipped\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return $"its text is not the sell confirmation this command knows: \"{text.Trim()}\"";
        if (int.Parse(m.Groups["n"].Value) != count) return $"it sells {m.Groups["n"].Value} pieces, planned {count}";
        if (m.Groups["e"].Value != "0") return $"it includes {m.Groups["e"].Value} worn (equipped) pieces";
        return null;
    }

    /// <summary>The accessories (rings, amulets, banners): the picker's second list. Every other slot is in the gear list.</summary>
    public static readonly IReadOnlySet<string> AccessorySlots = new HashSet<string> { "Ring", "Cloak", "Banner" };

    /// <summary>
    /// The picker list a piece is in, and the gear slot that opens it: the gear list (all six gear slots together, grouped by set; seen
    /// 2026-10-28: a helmet in view while the weapon slot's list was scrolled) opens from the Weapon slot, the accessories from the Ring.
    /// </summary>
    public static string PickerSlot(string slot) => AccessorySlots.Contains(slot) ? "Ring" : "Weapon";

    /// <summary>The list's name in the log: gear or accessories.</summary>
    public static string ListName(string pickerSlot) => AccessorySlots.Contains(pickerSlot) ? "accessories" : "gear";

    public sealed record SellSlotResult(string Slot, IReadOnlyList<int> Ids, bool Sold, string Detail);

    /// <summary>
    /// Sells <paramref name="ids"/> (all of one <paramref name="slot"/>): champion screen, the slot's picker, sell mode, tick each piece,
    /// check, Sell, the game's confirmation. Stops (throws) on anything unexpected; a dry run ends with nothing ticked.
    /// </summary>
    public async Task<SellSlotResult> SellSlot(string slot, IReadOnlyList<int> ids, bool real, CancellationToken ct)
    {
        if (!sell) throw new StopException("Refused: this driver is not the sell command's.");
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new StopException($"{slot}: no pieces, or a piece twice.");
        var a = new UiAction(ActionKind.FindPiece, 0, $"sell {ids.Count} {ListName(slot)}", Slot: slot);
        await NoBattle(ct);
        FitWindow();

        // The champion screen (any champion: the picker shows every piece of the slot), with the slot's picker open.
        await OpenPicker(a, slot, ct);

        // Sell mode on, nothing ticked yet.
        await Click(a, "sell-mode", null, null, (_, t) => t.Any(x => x.Kind == "sell-apply" && x.Active), "the sell panel opens", ct);
        var (s, _) = await Read(ct);
        if (s.Sell is not { } start) throw new StopException("The sell mode's ticked pieces cannot be read from memory (re-run scripts/gen-ui-offsets.ps1).");
        if (start.Count != 0 || start.Marked.Count != 0)
        {
            await LeaveSellMode(a, ct);
            throw new StopException($"Pieces were already ticked for sale ({start}); cancelled, nothing sold.");
        }

        // One sweep down the picker: from the top, every planned piece is ticked as it comes into view, then the list is turned
        // down; it is never turned back up. (The picker keeps only the items near the viewport in memory, so the order cannot be
        // known in advance.)
        var ticked = new List<int>();
        try
        {
            await SweepTick(a, slot, ids, ticked, ct);

            (s, var targets) = await Read(ct);
            if (s.Sell is not { } all || !all.Is(ids)) throw new StopException($"Before Sell: the game shows {s.Sell} ticked; planned [{string.Join(", ", ids)}].");
            if (targets.FirstOrDefault(x => x.Kind == "sell-apply") is { Rect: { } r }) Shot(a, $"sell {ListName(slot)}: {ids.Count} ticked, Sell at {r}", r);

            // Sell opens the game's own confirmation.
            await Click(a, "sell-apply", null, null, (s2, _) => s2.Top?.Name == UiState.MessageBoxView, "the sell confirmation opens", ct,
                before: s2 => s2.Sell is { } x && x.Is(ids) ? null : $"the ticked pieces changed before Sell ({s2.Sell})");
        }
        catch (StopException)
        {
            // Leave nothing ticked behind (only when the screen is still the sell panel; otherwise stop as found).
            try { if ((await Read(ct)).State.Top?.Name == UiState.ChampionScreenView) await LeaveSellMode(a, ct); } catch (StopException) { }
            throw;
        }

        await Settled(ct);
        var (box, boxTargets) = await Read(ct);
        var buttons = boxTargets.Where(x => x.View == UiState.MessageBoxView && x.Kind == "message-button").ToList();
        var shown = $"title \"{box.MessageBox?.Title}\", text \"{box.MessageBox?.Message}\", buttons {string.Join(", ", buttons.Select(b => $"{b.Id}: \"{b.Note}\""))}";
        log($"sell {ListName(slot)}: confirmation: {shown}");
        var problem = ConfirmationProblem(box, boxTargets, ids.Count);
        var sellButton = buttons.FirstOrDefault(b => IsSellText(b.Note));
        if (sellButton?.Rect is { } sr) Shot(a, real && problem is null ? $"sell {ListName(slot)}: CLICK Sell at {sr}" : $"sell {ListName(slot)}: WOULD CLICK Sell at {sr} (NOT pressed)", sr);

        if (!real || problem is not null)
        {
            // Close the confirmation with its X, then cancel the sell mode: nothing is sold.
            await Click(a, "back", null, null, (s2, _) => s2.Top?.Name != UiState.MessageBoxView, "the confirmation closes", ct, view: UiState.MessageBoxView);
            await LeaveSellMode(a, ct);
            if (problem is not null) throw new StopException($"The sell confirmation was closed, nothing sold: {problem}. ({shown})");
            Clicks.Add((0, $"confirmation: Sell {ids.Count} {slot} (not pressed)", sellButton!.Rect!.Value, false, "-"));
            return new SellSlotResult(slot, ids, false, $"dry run: {ids.Count} ticked, confirmation read and closed, sell mode cancelled ({shown})");
        }

        // The one irreversible click: the confirmation's Sell, with the ticked pieces checked once more right before it.
        var account = await game.Account.ReadAsync(ct);
        var have = account.Artifacts.ToDictionary(x => x.Id);
        foreach (var id in ids)
            if (!have.TryGetValue(id, out var piece) || piece.OwnerHeroId is not null)
                throw new StopException($"Before Sell: #{id} is {(piece is null ? "gone" : "worn")} in the account; the confirmation is left open, nothing pressed.");
        _once = "message-button";
        await Click(a, "message-button", sellButton!.Id, null, (s2, _) => s2.Top?.Name != UiState.MessageBoxView, "the confirmation closes", ct,
            before: s2 => s2.Sell is { } x && x.Is(ids) && s2.Top?.Name == UiState.MessageBoxView ? null : $"the ticked pieces or the dialog changed before Sell ({s2.Sell}, {s2.Top?.Name})");
        log($"sell {ListName(slot)}: clicked Sell on the confirmation for [{string.Join(", ", ids)}]");

        // The effect, from the account itself: every planned piece gone, nothing else.
        var until = DateTime.Now + TimeSpan.FromSeconds(30);
        while (DateTime.Now < until)
        {
            await Task.Delay(1500, ct);
            var fresh = await game.Account.ReadAsync(ct);
            var left = fresh.Artifacts.Select(x => x.Id).ToHashSet();
            if (ids.All(i => !left.Contains(i)))
            {
                var (_, afterTargets) = await Read(ct);
                if (afterTargets.Any(x => x.Kind == "sell-cancel" && x.NotClickable is null)) await LeaveSellMode(a, ct);
                return new SellSlotResult(slot, ids, true, $"sold: the account no longer has [{string.Join(", ", ids)}]");
            }
        }
        throw new StopException($"After Sell: the account still has some of [{string.Join(", ", ids)}] after 30 s.");
    }

    /// <summary>
    /// Ticks <paramref name="ids"/> in one pass down the picker: the list is turned to the top first, then each planned piece fully in
    /// view is ticked (top to bottom), then the list is turned down by about one viewport (straight to a planned piece further down
    /// when memory already holds it). Stops at the end of the list with the pieces it did not find.
    /// </summary>
    private async Task SweepTick(UiAction a, string slot, IReadOnlyList<int> ids, List<int> ticked, CancellationToken ct)
    {
        var left = ids.ToHashSet();
        await TurnToTop(a, ct);
        float? perNotch = null;
        string? lastSeen = null;
        var atEndOnce = false;
        for (var turn = 0; turn < 200 && left.Count > 0; turn++)
        {
            var (s, targets) = await Read(ct);
            if (Screen.Covering(s) is { } cover) throw new StopException($"Unexpected popup {cover} over the champion screen.");
            // Planned pieces that may be clicked now, top to bottom.
            var here = targets.Where(t => t.Kind == "inventory-item" && t.Id is { } id && left.Contains(id)).Select(t => t.Id!.Value).Distinct()
                .Select(id => UiTargets.Resolve(targets, "inventory-item", id).Target).OfType<UiTarget>()
                .OrderBy(t => Math.Round(t.Rect!.Value.Y / Math.Max(1f, t.Rect.Value.Height))).ThenBy(t => t.Rect!.Value.X).ToList();
            foreach (var item in here)
            {
                var id = item.Id!.Value;
                await Tick(a, slot, id, item, ticked, ids.Count, ct);
                left.Remove(id);
            }
            if (left.Count == 0) return;

            // Turn down: straight to the nearest planned piece memory holds below, else about one viewport.
            (s, targets) = await Read(ct);
            var (area, problem) = UiTargets.Resolve(targets, "scroll-picker");
            if (area is null) throw new StopException($"scroll-picker: {problem}");
            var ar = area.Rect!.Value;
            var seen = SeenIds(targets);
            if (seen == lastSeen)
            {
                // Nothing moved since the last turn: the end, unless the list was still gliding. One more turn decides.
                if (atEndOnce)
                {
                    if (targets.FirstOrDefault(t => t.Kind == "scroll-picker")?.Rect is { } er) Shot(a, $"sell {ListName(slot)}: end of the list, not found [{string.Join(", ", left)}]", er);
                    throw new StopException($"End of the {ListName(slot)} list: not found [{string.Join(", ", left.Select(i => $"#{i}"))}] (ticked so far: {ticked.Count}; in view at the end: {seen}).");
                }
                atEndOnce = true;
            }
            else atEndOnce = false;
            lastSeen = seen;
            var below = targets.Where(t => t.Kind == "inventory-item" && t.Id is { } id && left.Contains(id) && t.Rect is { } r && r.Y > ar.Y)
                .Select(t => t.Rect!.Value.Center.Y - ar.Center.Y).Where(d => d > 0).DefaultIfEmpty().Min();
            var distance = below > 0 ? below : ar.Height * 0.8f;
            var notches = perNotch is { } ppn ? Math.Clamp((int)Math.Round(distance / ppn), 1, 60) : 4;
            var anchor = targets.FirstOrDefault(t => t.Kind == "inventory-item" && t.FullyVisible && t.Id is not null && t.Rect is not null);
            var (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
            await NoBattle(ct);
            var (cx, cy) = ar.Center;
            if (MouseInput.Wheel(w, cx, cy, -(atEndOnce ? Math.Max(notches, 6) : notches)) is { } notSent) throw new StopException($"Not scrolled: {notSent}");
            // How far one notch moved the list: measured on an item seen before and after the turn, once the list stopped gliding.
            var afterTurn = await Steady(ct);
            if (anchor is not null && afterTurn.FirstOrDefault(t => t.Kind == "inventory-item" && t.Id == anchor.Id && t.Rect is not null) is { } same
                && anchor.Rect!.Value.Y - same.Rect!.Value.Y is var moved and > 1)
                perNotch = moved / notches;
            log($"sell {ListName(slot)}: turned the picker down {notches} notch{(notches == 1 ? "" : "es")}{(perNotch is { } pn ? $" ({pn:0.#} px each)" : "")}, {left.Count} to find; in view: {SeenIds(afterTurn)}");
        }
        if (left.Count > 0) throw new StopException($"Not found in the {ListName(slot)} list: [{string.Join(", ", left.Select(i => $"#{i}"))}].");
    }

    /// <summary>
    /// Turns the picker to the top: one long turn (250 notches, 8 ms apart: about 2 s, more than the longest list measured, ~200
    /// notches), then a check turn; the top is where a turn no longer changes the pieces in view. (A single wheel event carrying many
    /// notches counts as one notch in the game, seen 2026-10-28; and the picker keeps pooled items with old rectangles in memory, so
    /// "nothing above the viewport" cannot be read.)
    /// </summary>
    private async Task TurnToTop(UiAction a, CancellationToken ct)
    {
        var (_, targets) = await Read(ct);
        var last = SeenIds(targets);
        for (var i = 0; i < 6; i++)
        {
            var (area, problem) = UiTargets.Resolve(targets, "scroll-picker");
            if (area is null) throw new StopException($"scroll-picker: {problem}");
            var (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
            await NoBattle(ct);
            var (cx, cy) = area.Rect!.Value.Center;
            var notches = i == 0 ? 250 : 60;
            if (MouseInput.Wheel(w, cx, cy, notches, gapMs: 8) is { } notSent) throw new StopException($"Not scrolled: {notSent}");
            targets = await Steady(ct);
            var seen = SeenIds(targets);
            log($"sell: turned the picker up {notches} notches");
            if (seen == last) return; // nothing moved: the top
            last = seen;
        }
        throw new StopException("The picker did not reach the top.");
    }

    /// <summary>The ids of the pieces fully in view, sorted (to see whether a turn moved the list).</summary>
    private static string SeenIds(IReadOnlyList<UiTarget> t) =>
        string.Join(",", t.Where(x => x.Kind == "inventory-item" && x.FullyVisible).Select(x => x.Id).OrderBy(x => x));

    /// <summary>
    /// Reads until the list has stopped gliding after a wheel turn (Unity's scroll keeps moving for a moment): the pieces in view and
    /// their places the same in two reads 300 ms apart, at most 4 s.
    /// </summary>
    private async Task<List<UiTarget>> Steady(CancellationToken ct)
    {
        static string Places(IReadOnlyList<UiTarget> t) =>
            string.Join(";", t.Where(x => x.Kind == "inventory-item" && x.FullyVisible).OrderBy(x => x.Id).Select(x => $"{x.Id}@{x.Rect?.Y:0}"));
        await Task.Delay(400, ct);
        var (_, t) = await Read(ct);
        var until = DateTime.Now + TimeSpan.FromSeconds(4);
        while (DateTime.Now < until)
        {
            await Task.Delay(300, ct);
            var (_, t2) = await Read(ct);
            if (Places(t2) == Places(t)) return t2;
            t = t2;
        }
        return t;
    }

    /// <summary>One tick: the piece unequipped and not ticked in memory; after the click the game's list is the pieces so far plus it.</summary>
    private async Task Tick(UiAction a, string slot, int id, UiTarget item, List<int> ticked, int total, CancellationToken ct)
    {
        if (item.HasOwner != false) throw new StopException($"#{id}: memory does not show it unequipped ({(item.HasOwner is null ? "unread" : "worn")}); not ticked.");
        if (item.SellMarked == true) throw new StopException($"#{id} is already ticked for sale.");
        var sofar = ticked.ToList();
        await Click(a, "inventory-item", id, null, (s2, _) => s2.Sell is { } x && x.Is([.. sofar, id]), $"#{id} ticked (and only the pieces so far)", ct,
            before: s2 => s2.Sell is { } x && x.Is(sofar) ? null : $"the ticked pieces changed before #{id} ({s2.Sell})");
        ticked.Add(id);
        log($"sell {ListName(slot)}: #{id} ticked ({ticked.Count} of {total})");
    }

    /// <summary>
    /// <paramref name="ids"/> by their place in the picker (every item is in the UI tree with its rectangle, also outside the viewport):
    /// top to bottom, then left to right. Pieces the picker does not hold come last, in plan order (the scroll then finds them or stops).
    /// </summary>
    public static IReadOnlyList<int> InListOrder(IReadOnlyList<int> ids, IReadOnlyList<UiTarget> targets)
    {
        var place = targets.Where(t => t.Kind == "inventory-item" && t.Id is not null && t.Rect is not null)
            .GroupBy(t => t.Id!.Value).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Rect!.Value);
        return ids.Select((id, i) => (id, i)).OrderBy(x => place.ContainsKey(x.id) ? 0 : 1)
            .ThenBy(x => place.TryGetValue(x.id, out var r) ? Math.Round(r.Y / Math.Max(1f, r.Height)) : 0)
            .ThenBy(x => place.TryGetValue(x.id, out var r) ? r.X : 0).ThenBy(x => x.i).Select(x => x.id).ToList();
    }

    /// <summary>The champion screen with <paramref name="slot"/>'s picker open (an earlier picker and sell mode closed first).</summary>
    private async Task OpenPicker(UiAction a, string slot, CancellationToken ct)
    {
        var s = await Settled(ct);
        if (!(s.ChampionScreenOpen && s.Top?.Name == UiState.ChampionScreenView))
        {
            await GoHome(a, ct);
            await OpenChampions(a, ct);
            // The champion screen loads its gear icons after it opens (spinners); a click before that is ignored.
            await Task.Delay(3000, ct);
            await Settled(ct);
        }
        var (s1, t1) = await Read(ct);
        if (t1.Any(x => x.Kind == "sell-cancel" && x.NotClickable is null)) await LeaveSellMode(a, ct);
        if (s1.ChampionScreen?.InventoryArtifactId is not null)
            await Click(a, "popup-close-selected", null, null, (s2, _) => s2.ChampionScreen?.InventoryArtifactId is null, "the piece's popup closes", ct);
        (s1, t1) = await Read(ct);
        if (t1.Any(x => x.Kind == "picker-close" && x.NotClickable is null))
            await Click(a, "picker-close", null, null, (_, t2) => !t2.Any(x => x.Kind == "inventory-item" && x.Active), "the picker closes", ct);
        (s1, _) = await Read(ct);
        if (s1.ChampionScreen?.DetailsTab != 0)
            await Click(a, "gear-tab", 1, null, (s2, _) => s2.ChampionScreen?.DetailsTab == 0, "the gear tab is on", ct);
        try
        {
            await Click(a, "gear-slot", null, slot, (_, t) => t.Any(x => x.Kind == "inventory-item" && x.Active), $"the {UiTargets.DisplayName(slot)} picker shows pieces", ct);
        }
        catch (StopException e)
        {
            // A slot click the game ignored (still loading): once more after a pause, only when nothing else opened.
            var (s2, t2) = await Read(ct);
            if (s2.Top?.Name != UiState.ChampionScreenView || s2.ChampionScreen?.InventoryArtifactId is not null || t2.Any(x => x.Kind == "inventory-item" && x.Active)) throw;
            log($"{e.Message} Clicking the slot once more.");
            await Task.Delay(2000, ct);
            await Click(a, "gear-slot", null, slot, (_, t) => t.Any(x => x.Kind == "inventory-item" && x.Active), $"the {UiTargets.DisplayName(slot)} picker shows pieces", ct);
        }
    }

    /// <summary>The sell panel's Cancel: sell mode off, nothing ticked.</summary>
    private async Task LeaveSellMode(UiAction a, CancellationToken ct)
    {
        var (_, t) = await Read(ct);
        if (!t.Any(x => x.Kind == "sell-cancel" && x.NotClickable is null)) return;
        await Click(a, "sell-cancel", null, null, (s2, t2) => !t2.Any(x => x.Kind == "sell-apply" && x.Active) && (s2.Sell is null || s2.Sell.Marked.Count == 0),
            "the sell mode is cancelled, nothing ticked", ct);
    }
}
