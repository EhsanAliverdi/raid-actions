using RaidActions.Execution;
using RaidActions.Game.Dry;
using RaidActions.Game.Memory;
using RaidExtractor;

namespace RaidActions.App.Input;

/// <summary>
/// Taking a champion's gear off (the unequip command only): on the champion screen, each worn slot is clicked, which opens the worn
/// piece's popup; its Manage (<c>RemoveButton_h</c>) opens the manage-gear dialog, whose Remove (<c>Unequip_h</c>) is pressed once (a
/// real run only; a dry run shows it and closes the dialog); the dialog's Sell is never a target. After each piece the account must show it in the inventory and nothing else of the champion changed.
/// </summary>
public sealed partial class NavigatingDriver
{
    private const string ManageGearView = "RemoveArtifactOverlay";

    /// <summary>Takes every piece champion #<paramref name="championId"/> wears back to the inventory (dry run: shows each Remove,
    /// presses none). Returns the pieces taken off (dry run: the ones that would be).</summary>
    public async Task<List<(string Slot, int ArtifactId)>> Unequip(UiAction a, int championId, bool real, CancellationToken ct)
    {
        if (!unequip) throw new StopException("Refused: this driver is not the unequip command's.");
        var account = await game.Account.ReadAsync(ct);
        var c = account.Champions.FirstOrDefault(x => x.Id == championId) ?? throw new StopException($"Champion #{championId} is not on the account.");
        if (c.InVault || c.InBathhouse) throw new StopException($"{c.Name} (#{championId}) is in a vault: take it out first.");
        var worn = Display.Slots.Where(s => c.Artifacts.ContainsKey(s)).Select(s => (Slot: s, ArtifactId: c.Artifacts[s])).ToList();
        log($"unequip: {c.Name} (#{championId}) wears {(worn.Count == 0 ? "nothing" : string.Join(", ", worn.Select(w => $"{UiTargets.DisplayName(w.Slot)} #{w.ArtifactId}")))}");
        if (worn.Count == 0) return [];

        await NoBattle(ct);
        FitWindow();
        await ShowChampion(a, championId, c.Name ?? $"#{championId}", ct);
        var done = new List<(string, int)>();
        foreach (var (slot, artifactId) in worn)
        {
            var what = $"{UiTargets.DisplayName(slot)} #{artifactId}";
            await ClosePiecePanels(a, ct, picker: false);
            var (s, _) = await Read(ct);
            if (s.ChampionScreen?.Champion != championId) throw new StopException($"Expected {c.Name} (#{championId}); the screen shows #{s.ChampionScreen?.Champion}.");
            if (s.ChampionScreen.DetailsTab != 0)
                await Click(a, "gear-tab", 1, null, (s2, _) => s2.ChampionScreen?.DetailsTab == 0, "the gear tab is on", ct);
            // The worn slot: the game opens the worn piece's popup (Upgrade, Manage) next to the picker.
            await Click(a, "gear-slot", null, slot, (_, t) => WornRemove(t) is not null, $"the worn {UiTargets.DisplayName(slot)}'s popup opens", ct);
            var r0 = await Read(ct);
            if (WornRemove(r0.Targets) is null) throw new StopException($"{what}: the worn piece's Manage is not there.");
            // Manage (RemoveButton_h) only opens the manage-gear dialog: pressed in a dry run too. The dialog's Remove (Unequip_h) is
            // what takes the piece off; its Sell (Sell_h) is never a target.
            _once = "popup-remove";
            await Click(a, "popup-remove", null, null, (s2, _) => s2.Top?.Name == ManageGearView, "the manage-gear dialog opens", ct,
                before: s1 => s1.ChampionScreen?.Champion == championId ? null : $"The screen changed to champion #{s1.ChampionScreen?.Champion}.", read: r0);
            var (_, td) = await Read(ct);
            var (unequipButton, why) = UiTargets.Resolve(td, "manage-remove");
            if (unequipButton is null || unequipButton.View != ManageGearView) throw new StopException($"{what}: the manage-gear dialog's Remove is not there ({why}).");
            if (!real)
            {
                var png = Shot(a, $"unequip: WOULD CLICK Remove ({what} off {c.Name}) at {unequipButton.Rect} (dry run: NOT pressed)", unequipButton.Rect!.Value);
                Clicks.Add((0, $"manage gear: Remove {what} (not pressed)", unequipButton.Rect!.Value, false, png ?? "-"));
                await Click(a, "manage-close", null, null, (s2, _) => s2.Top?.Name != ManageGearView, "the manage-gear dialog closes", ct, view: ManageGearView);
                log($"unequip: {what}: Remove shown, not pressed");
                done.Add((slot, artifactId));
                continue;
            }
            _once = "manage-remove";
            await Click(a, "manage-remove", null, null, (s2, _) => s2.Top?.Name != ManageGearView, "the manage-gear dialog closes", ct, view: ManageGearView);
            var after = await Settled(ct);
            if (Screen.Covering(after) is { } popup) throw new StopException($"After Remove ({what}): unexpected popup {popup}.");

            // The account: this piece in the inventory, the champion's other pieces where they were.
            var fresh = await game.Account.ReadAsync(ct);
            var piece = fresh.Artifacts.FirstOrDefault(x => x.Id == artifactId) ?? throw new StopException($"{what} is no longer on the account (sold?). Stopped.");
            if (piece.OwnerHeroId is { } owner && owner != 0) throw new StopException($"After Remove, {what} is still worn by #{owner}.");
            var now = fresh.Champions.First(x => x.Id == championId).Artifacts;
            var expected = worn.Where(w => w.ArtifactId != artifactId && !done.Any(d => d.Item2 == w.ArtifactId)).ToDictionary(w => w.Slot, w => w.ArtifactId);
            if (now.Count != expected.Count || now.Any(kv => !expected.TryGetValue(kv.Key, out var id) || id != kv.Value))
                throw new StopException($"After Remove ({what}) {c.Name} wears {string.Join(", ", now.Select(kv => $"{kv.Key} #{kv.Value}"))}; expected {string.Join(", ", expected.Select(kv => $"{kv.Key} #{kv.Value}"))}.");
            done.Add((slot, artifactId));
            log($"unequip: {what} taken off {c.Name}: the account shows it in the inventory");
        }
        await ClosePiecePanels(a, ct);
        return done;
    }

    /// <summary>The worn piece's popup Manage, when exactly one is clickable (in EquipedItemWindow); null otherwise.</summary>
    private static UiTarget? WornRemove(IReadOnlyList<UiTarget> t)
    {
        var found = t.Where(x => x.Kind == "popup-remove" && x.NotClickable is null).ToList();
        return found.Count == 1 && found[0].Path.Contains("/EquipedItemWindow/", StringComparison.Ordinal) ? found[0] : null;
    }

    /// <summary>The champion screen showing champion #id: home and Champions when another screen is open, then its tile.</summary>
    private async Task ShowChampion(UiAction a, int id, string name, CancellationToken ct)
    {
        var s = await Settled(ct);
        if (!(s.ChampionScreenOpen && s.Top?.Name == UiState.ChampionScreenView))
        {
            await GoHome(a, ct);
            await OpenChampions(a, ct);
            s = await ReadState(ct);
        }
        if (s.ChampionScreen?.Champion == id) return;
        await ClosePiecePanels(a, ct);
        await Scrolled(a, "champion-tile", id, "scroll-champions", ct);
        await Click(a, "champion-tile", id, null, (s2, _) => s2.ChampionScreen?.Champion == id, $"{name} is shown", ct);
    }
}
