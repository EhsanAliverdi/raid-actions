using RaidActions.Execution;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;

namespace RaidActions.App.Input;

/// <summary>
/// Remove gear through the vaults (the remove-gear command only; Ehsan, 2026-10-29): champions moved from the champion list into the
/// Reserve Vault lose all their gear and accessories at once, which is much faster than taking pieces off one by one. Then, as chosen:
/// they stay there, go on to the Master Vault, or come back to the champion list. Every move is checked in the account.
/// <para>Which move button sends the ticked champions where is told by the label it shows (seen 2026-10-29 with four list champions
/// ticked on the Reserve tab: SingleOrLeftButton showing <c>AddToBathhouseText_h</c>; <c>AddToVaultText_h</c> = Master Vault,
/// <c>MoveToInventoryText_h</c> = the list), never by its place; when that is not clear the run stops and lists the buttons it saw.
/// A game prompt after the move is confirmed only when it is about the gear.</para>
/// </summary>
public sealed partial class NavigatingDriver
{
    /// <summary>The clicks only the deposit command adds (the vaults' right move button).</summary>
    public static readonly IReadOnlySet<string> DepositOnlyClicks = new HashSet<string> { "vault-move-right" };

    /// <summary>Where Remove gear leaves the champions.</summary>
    public enum After { Reserve, Master, List }

    /// <summary>
    /// Takes all the gear off champions <paramref name="ids"/>, wherever they are (Ehsan, 2026-10-29): from the champion list or the Master
    /// Vault they are moved into the Reserve Vault (the game takes all their gear off at once); ones already in the Reserve Vault wear
    /// nothing. Then all of them go on to <paramref name="after"/>. A dry run ticks them, shows the move it would click (and every
    /// move button it sees), unticks them. Returns the champions whose gear is off.
    /// </summary>
    public async Task<List<int>> RemoveGearByVault(UiAction a, IReadOnlyCollection<int> ids, After after, bool real, CancellationToken ct)
    {
        if (!vault || !deposit) throw new StopException("Refused: this driver is not the remove-gear command's.");
        var account = await game.Account.ReadAsync(ct);
        var champions = account.Champions.ToDictionary(c => c.Id);
        var missing = ids.Where(i => !champions.ContainsKey(i)).ToList();
        if (missing.Count > 0) throw new StopException($"Not on the account: {string.Join(", ", missing.Select(i => $"#{i}"))}.");
        var inList = ids.Where(i => !champions[i].InVault && !champions[i].InBathhouse).ToList();
        var inMaster = ids.Where(i => champions[i].InVault && !champions[i].InBathhouse).ToList();
        foreach (var id in ids)
            log($"remove-gear: {champions[id].Name} (#{id}) wears {champions[id].Artifacts.Count} piece(s), "
                + (inList.Contains(id) ? "in the champion list" : inMaster.Contains(id) ? "in the Master Vault" : "in the Reserve Vault"));

        await NoBattle(ct);
        FitWindow();
        var s0 = await Settled(ct);
        if (s0.Top?.Name != UiState.ChampionScreenView) { await GoHome(a, ct); await OpenChampions(a, ct); }
        await ClosePiecePanels(a, ct);
        var (_, t0) = await Read(ct);
        if (!VaultsOpen(t0)) await OpenVaults(a, ct);

        if (inList.Count > 0) await ListToReserve(a, inList, champions, real, ct);
        if (inMaster.Count > 0) await MasterToReserve(a, inMaster, champions, real, ct);
        if (real)
        {
            // Everyone is in the Reserve Vault now, wearing nothing.
            var now = (await game.Account.ReadAsync(ct)).Champions.ToDictionary(c => c.Id);
            var wrong = ids.Where(i => !now.TryGetValue(i, out var c) || !c.InBathhouse || c.Artifacts.Count > 0).ToList();
            if (wrong.Count > 0) throw new StopException($"Not in the Reserve Vault without gear: {string.Join(", ", wrong.Select(i => $"{champions[i].Name} #{i}"))}.");
            if (after == After.List) await Withdraw(a, ids, real: true, ct);
            else if (after == After.Master) await ReserveToMaster(a, ids, champions, ct);
        }
        else if (after != After.Reserve && ids.Where(i => champions[i].InBathhouse).ToList() is { Count: > 0 } inReserve)
            await ShowReserveMove(a, inReserve, after, ct);
        var (_, t3) = await Read(ct);
        if (VaultsOpen(t3)) await Click(a, "vault-close", null, null, (_, t4) => !VaultsOpen(t4), "the Vaults panel closes", ct, view: UiState.ChampionScreenView);
        return [.. ids];
    }

    /// <summary>Champion list → Reserve Vault: each ticked in the list (memory: marked), the button showing "to the Reserve Vault", the game's gear prompt.</summary>
    private async Task ListToReserve(UiAction a, List<int> ids, Dictionary<int, RaidExtractor.ChampionOut> champions, bool real, CancellationToken ct)
    {
        await VaultTab(a, reserve: true, ct);
        foreach (var id in ids)
        {
            var tile = await ListTile(a, id, ct);
            if (!Marked(tile))
                await Click(a, "champion-tile", id, null, (_, t) => t.Any(x => IsListTile(x, id) && Marked(x)), $"{champions[id].Name} is ticked", ct,
                    view: UiState.ChampionScreenView);
            log($"remove-gear: {champions[id].Name} (#{id}) ticked in the champion list");
        }
        var (_, tt) = await Read(ct);
        var extra = tt.Where(x => x.Kind == "champion-tile" && !x.Path.Contains("/StorageWindow/", StringComparison.Ordinal) && Marked(x)
            && x.Id is { } xi && !ids.Contains(xi)).Select(x => x.Id).ToList();
        if (extra.Count > 0) throw new StopException($"Other champions are ticked too ({string.Join(", ", extra.Select(i => $"#{i}"))}); nothing moved.");
        var toReserve = MoveButton(tt, "AddToBathhouseText_h", "Reserve Vault");
        if (!real)
        {
            Shot(a, $"remove-gear: WOULD CLICK {toReserve.Name} ({toReserve.Note}) at {toReserve.Rect} (dry run: NOT pressed)", toReserve.Rect!.Value);
            Clicks.Add((0, $"vaults: list to the Reserve Vault ({ids.Count}) (not pressed)", toReserve.Rect!.Value, false, "-"));
            await Untick(a, ids, ct);
            return;
        }
        await Click(a, toReserve.Kind, null, null, (s2, t2) => s2.Top?.Name is UiState.MessageBoxView or GearRemovalView || !t2.Any(x => x.Kind == "champion-tile" && x.Id is { } i && ids.Contains(i)
            && !x.Path.Contains("/StorageWindow/", StringComparison.Ordinal)), "the champions leave the list (or the game asks)", ct, view: UiState.ChampionScreenView);
        await RemoveAllGearPrompt(a, ct);
        await ConfirmGearPrompt(a, ct);
        await InReserveWithoutGear(ids, champions, "the champion list", ct);
    }

    /// <summary>Master Vault → Reserve Vault: each ticked in the Master grid, the button showing "to the Reserve Vault", the game's gear prompt.</summary>
    private async Task MasterToReserve(UiAction a, List<int> ids, Dictionary<int, RaidExtractor.ChampionOut> champions, bool real, CancellationToken ct)
    {
        await VaultTab(a, reserve: false, ct);
        var tapped = new List<int>();
        foreach (var id in ids)
        {
            var tile = await FindInVault(a, id, ct);
            if (!Marked(tile)) { await TapInVault(a, id, ct); tapped.Add(id); }
            log($"remove-gear: {champions[id].Name} (#{id}) ticked in the Master Vault");
        }
        var (_, tt) = await Read(ct);
        var toReserve = MoveButton(tt, "AddToBathhouseText_h", "Reserve Vault");
        if (!real)
        {
            Shot(a, $"remove-gear: WOULD CLICK {toReserve.Name} ({toReserve.Note}) at {toReserve.Rect} (dry run: NOT pressed)", toReserve.Rect!.Value);
            Clicks.Add((0, $"vaults: Master to the Reserve Vault ({ids.Count}) (not pressed)", toReserve.Rect!.Value, false, "-"));
            foreach (var id in tapped) await TapInVault(a, id, ct);   // untick again
            return;
        }
        await Click(a, toReserve.Kind, null, null, (s2, t2) => s2.Top?.Name is UiState.MessageBoxView or GearRemovalView || !t2.Any(x => x.Kind == "champion-tile" && x.Id is { } i && ids.Contains(i)
            && x.Active && x.Path.Contains("/StorageWindow/", StringComparison.Ordinal)), "the champions leave the Master Vault (or the game asks)", ct, view: UiState.ChampionScreenView);
        await RemoveAllGearPrompt(a, ct);
        await ConfirmGearPrompt(a, ct);
        await InReserveWithoutGear(ids, champions, "the Master Vault", ct);
    }

    /// <summary>The account shows every one of <paramref name="ids"/> in the Reserve Vault, wearing nothing; else stops.</summary>
    private async Task InReserveWithoutGear(List<int> ids, Dictionary<int, RaidExtractor.ChampionOut> champions, string from, CancellationToken ct)
    {
        var now = (await game.Account.ReadAsync(ct)).Champions.ToDictionary(c => c.Id);
        var notMoved = ids.Where(i => !now.TryGetValue(i, out var c) || !c.InBathhouse).ToList();
        if (notMoved.Count > 0) throw new StopException($"After the move from {from} the account does not show in the Reserve Vault: {string.Join(", ", notMoved.Select(i => $"#{i}"))}.");
        var stillGeared = ids.Where(i => now[i].Artifacts.Count > 0).ToList();
        if (stillGeared.Count > 0) throw new StopException($"In the Reserve Vault but still wearing gear: {string.Join(", ", stillGeared.Select(i => $"{now[i].Name} #{i}"))}.");
        foreach (var id in ids) log($"remove-gear: {champions[id].Name} (#{id}) is in the Reserve Vault (from {from}), its gear in the inventory");
    }

    /// <summary>Reserve Vault → Master Vault: each ticked in the Reserve grid, then the move button whose binding names the storage.</summary>
    private async Task ReserveToMaster(UiAction a, IReadOnlyCollection<int> ids, Dictionary<int, RaidExtractor.ChampionOut> champions, CancellationToken ct)
    {
        await VaultTab(a, reserve: true, ct);
        foreach (var id in ids)
        {
            var tile = await FindInVault(a, id, ct);
            if (!Marked(tile)) await TapInVault(a, id, ct);
        }
        var (_, t) = await Read(ct);
        var toMaster = MoveButton(t, "AddToVaultText_h", "Master Vault");
        await Click(a, toMaster.Kind, null, null, (s2, t2) => s2.Top?.Name is UiState.MessageBoxView || !t2.Any(x => x.Kind == "champion-tile" && x.Active && x.Id is { } i && ids.Contains(i) && x.Path.Contains("/StorageWindow/", StringComparison.Ordinal)),
            "the champions leave the Reserve Vault", ct, view: UiState.ChampionScreenView);
        await ConfirmGearPrompt(a, ct);
        var now = (await game.Account.ReadAsync(ct)).Champions.ToDictionary(c => c.Id);
        var wrong = ids.Where(i => !now.TryGetValue(i, out var c) || !c.InVault || c.InBathhouse).ToList();
        if (wrong.Count > 0) throw new StopException($"After the move the account does not show in the Master Vault: {string.Join(", ", wrong.Select(i => $"#{i}"))}.");
        foreach (var id in ids) log($"remove-gear: {champions[id].Name} (#{id}) is in the Master Vault");
    }

    /// <summary>
    /// Dry run of the second leg for champions already in the Reserve Vault: ticked in the Reserve grid, the button to the Master Vault
    /// (or back to the list) shown, not pressed, unticked.
    /// </summary>
    private async Task ShowReserveMove(UiAction a, List<int> ids, After after, CancellationToken ct)
    {
        await VaultTab(a, reserve: true, ct);
        var tapped = new List<int>();
        foreach (var id in ids)
        {
            var tile = await FindInVault(a, id, ct);
            if (!Marked(tile)) { await TapInVault(a, id, ct); tapped.Add(id); }
        }
        var (_, t) = await Read(ct);
        var move = after == After.Master ? MoveButton(t, "AddToVaultText_h", "Master Vault") : MoveButton(t, "MoveToInventoryText_h", "champion list");
        Shot(a, $"remove-gear: WOULD CLICK {move.Name} ({move.Note}) at {move.Rect} (dry run: NOT pressed)", move.Rect!.Value);
        Clicks.Add((0, $"vaults: Reserve Vault to the {(after == After.Master ? "Master Vault" : "list")} ({ids.Count}) (not pressed)", move.Rect!.Value, false, "-"));
        foreach (var id in tapped) await TapInVault(a, id, ct);   // untick again
    }

    /// <summary>
    /// The vault move button that shows the label <paramref name="bindingWord"/>; stops (listing every move button seen) when none or
    /// more than one does: the place of a button is never trusted.
    /// </summary>
    private UiTarget MoveButton(IReadOnlyList<UiTarget> t, string bindingWord, string where)
    {
        var moves = t.Where(x => x.Kind is "vault-move-left" or "vault-move-right" && x.Active).ToList();
        log("remove-gear: move buttons: " + string.Join("; ", moves.Select(x => $"{x.Name} {x.Note ?? "(no binding)"}{(x.NotClickable is { } why ? $" [{why}]" : "")}")));
        var match = moves.Where(x => x.NotClickable is null && x.Note?.Contains(bindingWord, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (match.Count != 1)
            throw new StopException($"Cannot tell which button moves to the {where} ({match.Count} name '{bindingWord}'); nothing moved. Buttons: "
                + string.Join("; ", moves.Select(x => $"{x.Name} {x.Note ?? "(no binding)"}")));
        return match[0];
    }

    private const string GearRemovalView = "BathhouseArtifactRemovalOverlay";

    /// <summary>
    /// The game's "Manage Gear" prompt (moving champions that wear gear to the Reserve Vault): its Remove (Deactivate) once, which takes
    /// all their gear back to the storage. Its Sell is a forbidden target. No prompt: nothing to do.
    /// </summary>
    private async Task RemoveAllGearPrompt(UiAction a, CancellationToken ct)
    {
        var s = await Settled(ct);
        if (s.Top?.Name != GearRemovalView) return;
        log("remove-gear: the game asks to remove or sell the gear: Remove");
        _once = "vault-gear-remove";
        await Click(a, "vault-gear-remove", null, null, (s2, _) => s2.Top?.Name != GearRemovalView, "the prompt closes", ct, view: GearRemovalView);
        await Settled(ct);
    }

    /// <summary>A game prompt after a vault move: confirmed only when it is about gear (its OK/Confirm/Yes/Move/Continue button); any other stops.</summary>
    private async Task ConfirmGearPrompt(UiAction a, CancellationToken ct)
    {
        var s = await Settled(ct);
        if (s.Top?.Name != UiState.MessageBoxView) return;
        var text = $"{s.MessageBox?.Title} {s.MessageBox?.Message}";
        log($"remove-gear: the game asks: \"{text.Trim()}\"");
        string[] gearWords = ["artifact", "gear", "equip", "accessor", "remove"];
        if (!gearWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            throw new StopException($"Unexpected prompt after the move (left open): \"{text.Trim()}\".");
        var (_, t) = await Read(ct);
        string[] yes = ["ok", "confirm", "yes", "move", "continue"];
        var buttons = t.Where(x => x.Kind == "message-button" && x.NotClickable is null).ToList();
        var ok = buttons.Where(x => yes.Any(y => string.Equals(x.Note?.Trim(), y, StringComparison.OrdinalIgnoreCase) || x.Note?.StartsWith(y, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        if (ok.Count != 1) throw new StopException($"The prompt's confirm button is not clear ({string.Join(", ", buttons.Select(b => b.Note))}); left open.");
        _once = "message-button";
        await Click(a, "message-button", ok[0].Id, null, (s2, _) => s2.Top?.Name != UiState.MessageBoxView, "the prompt closes", ct, view: UiState.MessageBoxView);
        await Settled(ct);
    }

    /// <summary>Unticks every one of <paramref name="ids"/> still ticked in the champion list (also ones a stopped run left ticked).</summary>
    private async Task Untick(UiAction a, IEnumerable<int> ids, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            var (_, t) = await Read(ct);
            if (t.Any(x => IsListTile(x, id) && Marked(x)))
                await Click(a, "champion-tile", id, null, (_, t2) => !t2.Any(x => IsListTile(x, id) && Marked(x)), "unticked", ct, view: UiState.ChampionScreenView);
        }
    }

    /// <summary>Ticked in the champion list while the vaults are open (HeroSlotContext._isMarked; _isSelected is the champion shown).</summary>
    private static bool Marked(UiTarget x) => x.Note?.Split(", ").Contains("marked") == true;

    private static bool IsListTile(UiTarget x, int id) => x.Kind == "champion-tile" && x.Id == id && !x.Path.Contains("/StorageWindow/", StringComparison.Ordinal);

    /// <summary>Champion #id's tile in the champion list (not the vault grid), scrolled into view.</summary>
    private async Task<UiTarget> ListTile(UiAction a, int id, CancellationToken ct)
    {
        var (_, t) = await Read(ct);
        if (t.FirstOrDefault(x => IsListTile(x, id) && x.NotClickable is null) is { } shown) return shown;
        await Scrolled(a, "champion-tile", id, "scroll-champions", ct);
        var (_, t2) = await Read(ct);
        return t2.FirstOrDefault(x => IsListTile(x, id) && x.NotClickable is null) ?? throw new StopException($"Champion #{id} is not in the champion list.");
    }
}
