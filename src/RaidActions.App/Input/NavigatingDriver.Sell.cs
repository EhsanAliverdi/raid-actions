using System.Text.RegularExpressions;
using RaidActions.Execution;
using RaidActions.Game.Dry;
using RaidActions.Game.Memory;

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
    /// which exactly one sells, and its text must not mention worn (equipped) pieces, which this command never sells.
    /// </summary>
    public static string? ConfirmationProblem(UiState s, IReadOnlyList<UiTarget> targets)
    {
        if (s.Top?.Name != UiState.MessageBoxView) return $"the confirmation is not on top ({s.Top?.Name})";
        var buttons = targets.Where(t => t.View == UiState.MessageBoxView && t.Kind == "message-button").ToList();
        if (buttons.Count != 2) return $"it has {buttons.Count} buttons, expected 2 ({string.Join(", ", buttons.Select(b => b.Note ?? "?"))})";
        var sellButtons = buttons.Count(b => IsSellText(b.Note));
        if (sellButtons != 1) return $"{sellButtons} of its buttons say Sell ({string.Join(", ", buttons.Select(b => $"\"{b.Note ?? "?"}\""))})";
        var text = $"{s.MessageBox?.Title} {s.MessageBox?.Message}";
        if (Regex.IsMatch(text, @"equip|worn|champion|hero", RegexOptions.IgnoreCase)) return $"its text mentions worn pieces: \"{text.Trim()}\"";
        return null;
    }

    public sealed record SellSlotResult(string Slot, IReadOnlyList<int> Ids, bool Sold, string Detail);

    /// <summary>
    /// Sells <paramref name="ids"/> (all of one <paramref name="slot"/>): champion screen, the slot's picker, sell mode, tick each piece,
    /// check, Sell, the game's confirmation. Stops (throws) on anything unexpected; a dry run ends with nothing ticked.
    /// </summary>
    public async Task<SellSlotResult> SellSlot(string slot, IReadOnlyList<int> ids, bool real, CancellationToken ct)
    {
        if (!sell) throw new StopException("Refused: this driver is not the sell command's.");
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new StopException($"{slot}: no pieces, or a piece twice.");
        var a = new UiAction(ActionKind.FindPiece, 0, $"sell {ids.Count} {UiTargets.DisplayName(slot)}", Slot: slot);
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

        // Each piece: found by id, unequipped and not ticked in memory, then ticked; the game's list must then be the pieces so far.
        var ticked = new List<int>();
        try
        {
            foreach (var id in ids)
            {
                var item = await Scrolled(a, "inventory-item", id, "scroll-picker", ct);
                if (item.HasOwner != false) throw new StopException($"#{id}: memory does not show it unequipped ({(item.HasOwner is null ? "unread" : "worn")}); not ticked.");
                if (item.SellMarked == true) throw new StopException($"#{id} is already ticked for sale.");
                var sofar = ticked.ToList();
                await Click(a, "inventory-item", id, null, (s2, _) => s2.Sell is { } x && x.Is([.. sofar, id]), $"#{id} ticked (and only the pieces so far)", ct,
                    before: s2 => s2.Sell is { } x && x.Is(sofar) ? null : $"the ticked pieces changed before #{id} ({s2.Sell})");
                ticked.Add(id);
                log($"sell {slot}: #{id} ticked ({ticked.Count} of {ids.Count})");
            }

            (s, var targets) = await Read(ct);
            if (s.Sell is not { } all || !all.Is(ids)) throw new StopException($"Before Sell: the game shows {s.Sell} ticked; planned [{string.Join(", ", ids)}].");
            if (targets.FirstOrDefault(x => x.Kind == "sell-apply") is { Rect: { } r }) Shot(a, $"sell {slot}: {ids.Count} ticked, Sell at {r}", r);

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
        log($"sell {slot}: confirmation: {shown}");
        var problem = ConfirmationProblem(box, boxTargets);
        var sellButton = buttons.FirstOrDefault(b => IsSellText(b.Note));
        if (sellButton?.Rect is { } sr) Shot(a, real && problem is null ? $"sell {slot}: CLICK Sell at {sr}" : $"sell {slot}: WOULD CLICK Sell at {sr} (NOT pressed)", sr);

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
        log($"sell {slot}: clicked Sell on the confirmation for [{string.Join(", ", ids)}]");

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

    /// <summary>The champion screen with <paramref name="slot"/>'s picker open (an earlier picker and sell mode closed first).</summary>
    private async Task OpenPicker(UiAction a, string slot, CancellationToken ct)
    {
        var s = await Settled(ct);
        if (!(s.ChampionScreenOpen && s.Top?.Name == UiState.ChampionScreenView))
        {
            await GoHome(a, ct);
            await OpenChampions(a, ct);
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
        await Click(a, "gear-slot", null, slot, (_, t) => t.Any(x => x.Kind == "inventory-item" && x.Active), $"the {UiTargets.DisplayName(slot)} picker shows pieces", ct);
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
