using RaidActions.Execution;
using RaidActions.Game.Dry;
using RaidActions.Game.Memory;
using RaidExtractor;

namespace RaidActions.App.Input;

/// <summary>
/// Upgrading one piece (the upgrade command only): the champion screen, the piece's picker list, the piece found by its id and selected,
/// its popup's Upgrade (the upgrade screen), then the screen's one-level Upgrade, one attempt at a time, each followed by a live read of
/// the account until the piece's level or the silver changes. Never past the target; stops before an attempt it cannot pay for. The
/// upgrade screen's Sell and its "upgrade to level" button are never clicked. A dry run opens the screen, shows the click it would make
/// and closes it: nothing is upgraded.
/// </summary>
public sealed partial class NavigatingDriver
{
    /// <summary>The clicks only the upgrade command adds: the selected piece's popup Upgrade and the upgrade screen's one-level Upgrade.</summary>
    public static readonly IReadOnlySet<string> UpgradeOnlyClicks = new HashSet<string> { "popup-upgrade", "powerup" };

    /// <summary>How a piece's upgrade ended: done (at target), out of silver, piece gone, changed, or a dry run.</summary>
    public sealed record UpgradeResult(int Id, int From, int To, int Target, int Attempts, long SilverSpent, string Outcome)
    {
        public bool Done => Outcome == "done";
    }

    /// <summary>
    /// Upgrades #<paramref name="id"/> (a <paramref name="slot"/> piece) to +<paramref name="target"/>. <paramref name="priceOf"/> gives the
    /// silver of one attempt at a level (null: unknown). Throws <see cref="StopException"/> on anything unexpected; a piece that is gone
    /// or out of silver ends with its outcome instead, so the run can say what was done.
    /// </summary>
    public async Task<UpgradeResult> UpgradePiece(int id, string slot, int target, bool real, Func<ArtifactOut, long?> priceOf, int index, int total,
        CancellationToken ct)
    {
        if (!upgrade) throw new StopException("Refused: this driver is not the upgrade command's.");
        var a = new UiAction(ActionKind.FindPiece, 0, $"upgrade #{id}", Slot: slot);
        var account = await game.Account.ReadAsync(ct);
        var piece = account.Artifacts.FirstOrDefault(x => x.Id == id);
        if (piece is null) return new UpgradeResult(id, 0, 0, target, 0, 0, "piece not found in the account");
        var from = piece.Level;
        if (from >= target) return new UpgradeResult(id, from, from, target, 0, 0, "already at its target");
        var silverStart = account.Account.Resources.GetValueOrDefault("Silver");
        if (priceOf(piece) is { } first && silverStart < first)
            return new UpgradeResult(id, from, from, target, 0, 0, $"out of silver ({silverStart:N0}, one attempt costs {first:N0})");

        await NoBattle(ct);
        FitWindow();
        // The piece's list (any champion: the picker shows every piece of the slot, worn ones too), the piece found and selected.
        await OpenPicker(a, PickerSlot(slot), ct);
        UiTarget item;
        try { item = await Scrolled(a, "inventory-item", id, "scroll-picker", ct); }
        catch (StopException e) { return new UpgradeResult(id, from, from, target, 0, 0, "not found in the game's list: " + e.Message); }
        await Click(a, "inventory-item", id, null, (s, _) => s.ChampionScreen?.InventoryArtifactId == id, $"#{id} is selected", ct);
        await Click(a, "popup-upgrade", null, null, (s, _) => s.Top?.Name == UiState.PowerUpView, "the upgrade screen opens", ct,
            before: s => s.ChampionScreen?.InventoryArtifactId == id ? null : $"the selection changed before Upgrade (#{s.ChampionScreen?.InventoryArtifactId})");
        await Settled(ct);

        var attempts = 0;
        var level = from;
        var silver = silverStart;
        var outcome = "done";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            account = await game.Account.ReadAsync(ct);
            piece = account.Artifacts.FirstOrDefault(x => x.Id == id);
            if (piece is null) throw new StopException($"#{id} is gone from the account while its upgrade screen is open.");
            level = piece.Level;
            silver = account.Account.Resources.GetValueOrDefault("Silver");
            if (level >= target) break;
            if (priceOf(piece) is { } price && silver < price) { outcome = $"out of silver at +{level} ({silver:N0}, the next attempt costs {price:N0})"; break; }
            var (s, targets) = await Read(ct);
            if (s.Top?.Name != UiState.PowerUpView) throw new StopException($"Expected the upgrade screen on top; the screen shows {s.Top?.Name}.");
            var (button, problem) = UiTargets.Resolve(targets.Where(t => t.View == UiState.PowerUpView).ToList(), "powerup");
            if (button is null) { outcome = $"the Upgrade button is not available at +{level} ({problem})"; break; }
            if (!real)
            {
                Shot(a, $"upgrade #{id}: WOULD CLICK Upgrade at {button.Rect} (dry run: NOT pressed)", button.Rect!.Value);
                Clicks.Add((0, $"upgrade screen: Upgrade #{id} (not pressed)", button.Rect!.Value, false, "-"));
                outcome = "dry run";
                break;
            }
            log($"upgrade #{id} ({UiTargets.DisplayName(slot)}): +{level} → +{level + 1} ({index} of {total})");
            var levelBefore = level;
            var silverBefore = silver;
            await Click(a, "powerup", null, null, (_, _) => true, "the attempt starts", ct, view: UiState.PowerUpView);
            attempts++;
            // The attempt's effect, from the account: the level (success) or only the silver (a failed attempt) changes.
            var until = DateTime.Now + TimeSpan.FromSeconds(20);
            var changed = false;
            while (DateTime.Now < until)
            {
                await Task.Delay(1200, ct);
                var (after, _) = game.ReadUi();
                if (after is not null && after.Top?.Name != UiState.PowerUpView && Screen.Covering(after) is { } popup)
                    throw new StopException($"After Upgrade: {popup} opened (not enough silver, or an offer). Stopped at +{levelBefore}.");
                var now = await game.Account.ReadAsync(ct);
                var p = now.Artifacts.FirstOrDefault(x => x.Id == id);
                var sv = now.Account.Resources.GetValueOrDefault("Silver");
                if (p is not null && (p.Level != levelBefore || sv != silverBefore))
                {
                    changed = true;
                    log(p.Level > levelBefore ? $"#{id}: +{p.Level} (silver {sv:N0})" : $"#{id}: attempt failed, still +{p.Level} (silver {sv:N0})");
                    break;
                }
            }
            if (!changed) throw new StopException($"After Upgrade: neither the level of #{id} nor the silver changed within 20 s. Stopped at +{levelBefore}.");
        }

        // Back from the upgrade screen and the popup.
        await Click(a, "back", null, null, (s, _) => s.Top?.Name != UiState.PowerUpView, "the upgrade screen closes", ct, view: UiState.PowerUpView);
        var (s2, _) = await Read(ct);
        if (s2.ChampionScreen?.InventoryArtifactId is not null)
            await Click(a, "popup-close-selected", null, null, (s3, _) => s3.ChampionScreen?.InventoryArtifactId is null, "the piece's popup closes", ct);
        if (outcome == "done") log($"#{id}: done at +{level}");
        return new UpgradeResult(id, from, level, target, attempts, (long)Math.Max(0, silverStart - silver), outcome);
    }
}
