using RaidActions.Game.Window;
using RaidActions.Execution;
using RaidActions.Game.Memory;

namespace RaidActions.App.Input;

/// <summary>
/// The champion vaults (the vault commands only): champions kept in a vault (the game's storage and its reserve, the "bathhouse") are
/// not in the champion list, so their gear cannot be changed until they are taken out.
/// </summary>
public sealed partial class NavigatingDriver
{
    /// <summary>The clicks only the vault commands add.</summary>
    public static readonly IReadOnlySet<string> VaultOnlyClicks = new HashSet<string> { "vaults-open", "vault-tab-master", "vault-tab-reserve", "vault-close", "champion-tile", "vault-move-left" };

    private static bool VaultsOpen(IReadOnlyList<UiTarget> t) => t.Any(x => x.Kind == "vault-close" && x.NotClickable is null);

    /// <summary>From the champion screen: Champion Vaults, until another view is on top. Returns that view's name.</summary>
    public async Task<string> OpenVaults(UiAction a, CancellationToken ct)
    {
        if (!vault) throw new StopException("Refused: this driver is not a vault command's.");
        var s = await Settled(ct);
        if (s.Top?.Name != UiState.ChampionScreenView) throw new StopException($"Expected the champion screen; the screen shows {s.Top?.Name}.");
        await Click(a, "vaults-open", null, null, (_, t) => VaultsOpen(t), "the Vaults panel opens", ct, view: UiState.ChampionScreenView);
        var after = await Settled(ct);
        return after.Top?.Name ?? "none";
    }

    /// <summary>In the Vaults panel's grid: champion #id found (scrolling the grid) and tapped once. Returns its tile as it is after.</summary>
    public async Task<UiTarget?> TapInVault(UiAction a, int id, CancellationToken ct)
    {
        if (!vault) throw new StopException("Refused: this driver is not a vault command's.");
        var tile = await FindInVault(a, id, ct);
        if (!tile.Path.Contains("/StorageWindow/", StringComparison.Ordinal)) throw new StopException($"Champion #{id} was found outside the Vaults panel ({tile.Path}).");
        await Click(a, "champion-tile", id, null, (_, _) => true, $"#{id} is tapped", ct, view: UiState.ChampionScreenView);
        var (_, t) = await Read(ct);
        return t.FirstOrDefault(x => x.Kind == "champion-tile" && x.Id == id);
    }

    private static UiTarget? VaultTile(IReadOnlyList<UiTarget> t, int id) =>
        t.FirstOrDefault(x => x.Kind == "champion-tile" && x.Id == id && x.Path.Contains("/StorageWindow/", StringComparison.Ordinal));

    /// <summary>
    /// A champion's tile in the vault grid, clickable: the grid is a virtual list (only the rows near the viewport exist), so it is swept
    /// from the top, a few rows per turn, until the tile shows (then nudged fully into view) or the bottom is reached.
    /// </summary>
    private async Task<UiTarget> FindInVault(UiAction a, int id, CancellationToken ct)
    {
        async Task<string> Wheel(UiTarget area, int notches)
        {
            var (w, why) = GameWindow.Find();
            if (w is null) throw new StopException(why!);
            var (cx, cy) = area.Rect!.Value.Center;
            if (MouseInput.Wheel(w, cx, cy, notches) is { } notSent) throw new StopException($"Not scrolled: {notSent}");
            log($"step {a.Step}: scrolled {area.Name} {(notches < 0 ? "down" : "up")} {Math.Abs(notches)} notches");
            await Task.Delay(450, ct);
            var (_, t) = await Read(ct);
            return string.Join(",", t.Where(x => x.Kind == "champion-tile" && x.FullyVisible && x.Path.Contains("/StorageWindow/", StringComparison.Ordinal)).Select(x => x.Id));
        }
        var (_, t0) = await Read(ct);
        if (VaultTile(t0, id) is { NotClickable: null } now) return now;
        var (area, problem) = UiTargets.Resolve(t0, "scroll-vault");
        if (area is null) throw new StopException($"scroll-vault: {problem}");
        // To the top.
        string? seen = null;
        for (var i = 0; i < 10; i++) { var s1 = await Wheel(area, 40); if (s1 == seen) break; seen = s1; }
        for (var i = 0; i < 80; i++)
        {
            var (_, t) = await Read(ct);
            var tile = VaultTile(t, id);
            if (tile is { NotClickable: null }) return tile;
            if (tile is { Active: true, Rect: not null })
            {
                // Partly shown: a notch or two towards it.
                var down = tile.Rect!.Value.Center.Y > area.Rect!.Value.Center.Y;
                await Wheel(area, down ? -2 : 2);
                continue;
            }
            var s2 = await Wheel(area, -6);
            if (s2 == seen) break;   // the bottom
            seen = s2;
        }
        throw new StopException($"Champion #{id} is not in this vault's grid.");
    }

    /// <summary>
    /// Takes champions out of the vaults into the champion list ("To Collection"): per vault (Reserve for the bathhouse, Master for the
    /// storage), its tab, each champion found in its grid and ticked (memory must show it selected), then the left button, which reads
    /// "To Collection (n)" once champions are ticked (the right one, to the other vault, is never clicked). After it, the account must
    /// show every one of them out of the vaults; anything else stops. A dry run ticks them, shows the click it would make, and unticks
    /// them again. Returns the champions moved.
    /// </summary>
    public async Task<List<int>> Withdraw(UiAction a, IReadOnlyCollection<int> ids, bool real, CancellationToken ct)
    {
        if (!vault) throw new StopException("Refused: this driver is not a vault command's.");
        var account = await game.Account.ReadAsync(ct);
        var champions = account.Champions.ToDictionary(c => c.Id);
        var missing = ids.Where(i => !champions.ContainsKey(i)).ToList();
        if (missing.Count > 0) throw new StopException($"Not on the account: {string.Join(", ", missing.Select(i => $"#{i}"))}.");
        var moved = new List<int>();
        var groups = new[] { (Reserve: true, Ids: ids.Where(i => champions[i].InBathhouse).ToList()), (Reserve: false, Ids: ids.Where(i => champions[i].InVault && !champions[i].InBathhouse).ToList()) };
        if (groups.All(g => g.Ids.Count == 0)) return moved;
        await NoBattle(ct);
        FitWindow();
        var s0 = await Settled(ct);
        if (s0.Top?.Name != UiState.ChampionScreenView) { await GoHome(a, ct); await OpenChampions(a, ct); }
        var (_, t0) = await Read(ct);
        if (!VaultsOpen(t0)) await OpenVaults(a, ct);
        foreach (var (reserve, list) in groups.Where(g => g.Ids.Count > 0))
        {
            await VaultTab(a, reserve, ct);
            var tickedHere = new List<int>();
            foreach (var id in list)
            {
                var before = await FindInVault(a, id, ct);
                if (before.Note?.Contains("selected") == true) { log($"vault: #{id} {champions[id].Name} was already ticked"); continue; }
                var tile = await TapInVault(a, id, ct);
                if (tile?.Note?.Contains("selected") != true) throw new StopException($"#{id} ({champions[id].Name}) does not show ticked after the tap.");
                tickedHere.Add(id);
                log($"vault: #{id} {champions[id].Name} ticked in the {(reserve ? "Reserve" : "Master")} Vault");
            }
            // Exactly these must be ticked in this grid (a tick left from before would move a champion nobody asked for).
            var (_, tt) = await Read(ct);
            var extra = tt.Where(x => x.Kind == "champion-tile" && x.Path.Contains("/StorageWindow/", StringComparison.Ordinal) && x.Note?.Contains("selected") == true
                && x.Id is { } xi && !list.Contains(xi)).Select(x => x.Id).ToList();
            if (extra.Count > 0) throw new StopException($"Other champions are ticked in the vault too ({string.Join(", ", extra.Select(i => $"#{i}"))}); nothing moved.");
            var (_, t) = await Read(ct);
            var (button, problem) = UiTargets.Resolve(t, "vault-move-left");
            if (button is null) throw new StopException("The To Collection button is not there: " + problem);
            if (!t.Any(x => x.Path.EndsWith("/RightButton_h", StringComparison.Ordinal) && x.Active))
                throw new StopException("Only one move button shows: it may not be To Collection. Nothing moved.");
            if (!real)
            {
                Shot(a, $"vault: WOULD CLICK To Collection ({list.Count}) at {button.Rect} (dry run: NOT pressed)", button.Rect!.Value);
                Clicks.Add((0, $"vaults: To Collection ({list.Count}) (not pressed)", button.Rect!.Value, false, "-"));
                foreach (var id in tickedHere) await TapInVault(a, id, ct);   // untick again
                continue;
            }
            await Click(a, "vault-move-left", null, null, (_, t2) => !t2.Any(x => x.Kind == "champion-tile" && x.Id is { } i && list.Contains(i) && x.Path.Contains("/StorageWindow/", StringComparison.Ordinal)),
                "the champions leave the vault", ct, view: UiState.ChampionScreenView);
            await Settled(ct);
            var after = (await game.Account.ReadAsync(ct)).Champions.ToDictionary(c => c.Id);
            var still = list.Where(i => !after.TryGetValue(i, out var c) || c.InVault || c.InBathhouse).ToList();
            if (still.Count > 0) throw new StopException($"After To Collection the account still shows in a vault: {string.Join(", ", still.Select(i => $"#{i}"))}.");
            foreach (var id in list) log($"vault: #{id} {champions[id].Name} is in the champion list");
            moved.AddRange(list);
        }
        var (_, t3) = await Read(ct);
        if (VaultsOpen(t3)) await Click(a, "vault-close", null, null, (_, t4) => !VaultsOpen(t4), "the Vaults panel closes", ct, view: UiState.ChampionScreenView);
        return moved;
    }

    /// <summary>In the Vaults panel: the Reserve Vault tab (or the Master Vault tab).</summary>
    public async Task VaultTab(UiAction a, bool reserve, CancellationToken ct)
    {
        if (!vault) throw new StopException("Refused: this driver is not a vault command's.");
        await Click(a, reserve ? "vault-tab-reserve" : "vault-tab-master", null, null, (_, t) => VaultsOpen(t), $"the {(reserve ? "Reserve" : "Master")} Vault shows", ct, view: UiState.ChampionScreenView);
        await Settled(ct);
    }
}
