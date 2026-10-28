using System.Text.RegularExpressions;

namespace RaidActions.Game.Memory;

/// <param name="Kind">champion-tile, gear-slot, gear-tab, inventory-item, popup-equip, popup-remove, popup-close, manage-remove,
/// manage-close, sell-mode, sell-cancel, sell-apply, message-button, forbidden.</param>
/// <param name="Id">What the target stands for: champion id (tile), artifact id (inventory item), tab number; else null.</param>
/// <param name="Slot">Gear slots: the game's data id for the slot (Weapon … Ring, Cloak, Banner), as in plan files.</param>
/// <param name="Rect">Where to click (client pixels), from memory.</param>
/// <param name="FullyVisible">Entirely inside its scroll viewport / mask: only then may it be clicked.</param>
/// <param name="Forbidden">Never clicked, whatever a step asks.</param>
/// <param name="Note">Extra state: selected, tab on, the binding path, why it cannot be clicked; a message box button: its text.</param>
/// <param name="SellMarked">Inventory items: ticked for sale (InventoryItemContext._withSellMark); null when not read.</param>
/// <param name="HasOwner">Inventory items: worn by a champion (InventoryItemContext._hasOwner); null when not read.</param>
public sealed record UiTarget(string Kind, string Name, int? Id, string View, ClientRect? Rect, bool Active, bool? Interactable,
    bool FullyVisible, bool Forbidden, string Path, string? Note, string? Slot = null, int? ClientWidth = null, int? ClientHeight = null,
    bool? SellMarked = null, bool? HasOwner = null)
{
    public bool IsScrollArea => Kind.StartsWith("scroll-", StringComparison.Ordinal);

    /// <summary>Why this target may not be clicked now, or null when it may (a scroll area: when it is shown).</summary>
    public string? NotClickable =>
        IsScrollArea ? (Active && Rect is not null ? null : "scroll area not shown")
        : Forbidden ? "forbidden (never clicked)"
        : !Active ? "not active"
        : Rect is null ? "no rectangle"
        : Interactable != true ? "not interactable"
        : !FullyVisible ? "not fully visible (outside its scroll viewport)"
        : null;
}

/// <summary>
/// The click targets of the champion screen and its dialogs, found in a UI tree by the game's own element names and contexts,
/// each with the id it stands for. A champion tile is "the tile whose HeroSlotContext says hero X" and an inventory item "the
/// item whose context holds artifact Y", never a position from a sort order. Names are what the game's UI shows (Amulet); the
/// slot key is the game's data id (Cloak), which plan files use.
/// </summary>
public static partial class UiTargets
{
    public const string HeroSlotContext = "Client.ViewModel.Contextes.HeroesInfoDialog.HeroSlot.HeroSlotContext";
    public const string InventoryArtifactContext = "Client.ViewModel.Contextes.HeroesInfoDialog.Inventory.Items.InventoryArtifactContext";
    private const string InventoryItemContext = "Client.ViewModel.Contextes.HeroesInfoDialog.Inventory.Items.InventoryItemContext";
    private const string ArtifactAvatarContext = "Client.ViewModel.Contextes.Generic.ArtifactAvatarContext";
    private const string Artifact = "SharedModel.Meta.Artifacts.Artifact";
    private const string IntProperty = "Client.MVVM.Base.ViewModel.Properties.IntProperty";
    private const string BoolProperty = "Client.MVVM.Base.ViewModel.Properties.BoolProperty";
    private const string StringProperty = "Client.MVVM.Base.ViewModel.Properties.StringProperty";
    public const string MessageBoxButtonContext = "Client.ViewModel.Contextes.MessageBoxButtonContext";
    private const string ButtonContextBase = "Client.ViewModel.Contextes.ButtonContextBase";

    /// <summary>
    /// Element names that are never clicked: Sell (manage-gear dialog: sells the piece at once), the sell panel's Select all (would tick
    /// every piece), Auto-Equip (re-gears the champion by itself), PowerUp (Upgrade: spends resources; only the upgrade command
    /// clicks the popup's Upgrade). The picker's Sell mode toggle is
    /// a target of its own (sell-mode), which only the sell command may click: it is not in the swap runs' allow-list.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverClick = new HashSet<string> { "Sell_h", "SelectAll_h", "QuickEquipButton", "PowerUpButton" };

    /// <summary>The game's slot ids (ArtifactKindId, as in plan files), in the gear panel's order.</summary>
    public static readonly string[] SlotIds = ["Weapon", "Helmet", "Shield", "Gloves", "Chest", "Boots", "Ring", "Cloak", "Banner"];

    /// <summary>The name the game's UI shows for a slot id: the Cloak slot is the Amulet.</summary>
    public static string DisplayName(string slotId) => slotId == "Cloak" ? "Amulet" : slotId;

    [GeneratedRegex(@"^EquipmentArtifact(Slot \((?<n>\d)\)|(?<j>Ring|Pendant|Banner)Slot)$")]
    private static partial Regex GearSlotName();

    public static List<UiTarget> Find(UiTree t, IGameObjects g)
    {
        var nodes = t.Nodes;
        var targets = new List<UiTarget>();

        // The nodes below nodes[i]: the following nodes in depth-first order while deeper (never by path text: siblings share names).
        IEnumerable<UiNode> Below(int i)
        {
            for (var k = i + 1; k < nodes.Count && nodes[k].Depth > nodes[i].Depth; k++) yield return nodes[k];
        }

        UiNode? ClickableIn(int i) =>
            (nodes[i].Interactable is not null ? [nodes[i]] : Enumerable.Empty<UiNode>()).Concat(Below(i)).FirstOrDefault(c => c.Interactable is not null && c.ActiveInHierarchy)
            ?? Below(i).FirstOrDefault(c => c.Interactable is not null);

        UiTarget Make(string kind, string name, int? id, UiNode owner, UiNode? button, string? note, string? slot = null, bool forbidden = false)
        {
            var b = button ?? owner;
            return new(kind, name, id, t.View, b.Screen, owner.ActiveInHierarchy && b.ActiveInHierarchy, button?.Interactable,
                b.FullyVisible, forbidden || NeverClick.Contains(b.Name) && kind is not "popup-upgrade", b.Path, note, slot, t.ClientWidth, t.ClientHeight);
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];

            // Champion tiles: the tile's own context says which champion it shows.
            if (n.Context?.Class == HeroSlotContext)
            {
                var heroId = IntValue(g, g.Ref(n.Context.Address, HeroSlotContext, "_heroId"));
                if (heroId is null or <= 0) continue; // an empty or placeholder slot of the virtual list (0 or -1)
                var selected = BoolValue(g, g.Ref(n.Context.Address, HeroSlotContext, "_isSelected"));
                targets.Add(Make("champion-tile", $"champion #{heroId}", heroId, n, ClickableIn(i), selected == true ? "selected" : null));
                continue;
            }

            // Inventory picker items: the item's context holds the artifact.
            if (n.Context?.Class == InventoryArtifactContext)
            {
                var avatar = g.Ref(n.Context.Address, InventoryArtifactContext, "Avatar");
                var artifact = avatar == 0 ? 0 : g.Ref(avatar, ArtifactAvatarContext, "Artifact");
                var artifactId = artifact == 0 ? (int?)null : g.Int(artifact, Artifact, "Id");
                if (artifactId is null or 0) continue; // an empty or stub item
                var notes = new List<string>();
                if (BoolValue(g, g.Ref(n.Context.Address, InventoryItemContext, "_isSelected")) == true) notes.Add("selected");
                if (BoolValue(g, g.Ref(n.Context.Address, InventoryItemContext, "_ownerIsCurrentHero")) == true) notes.Add("worn by this champion");
                var marked = Optional(() => BoolValue(g, g.Ref(n.Context.Address, InventoryItemContext, "_withSellMark")));
                var owned = Optional(() => BoolValue(g, g.Ref(n.Context.Address, InventoryItemContext, "_hasOwner")));
                if (marked == true) notes.Add("ticked for sale");
                if (owned == true) notes.Add("worn");
                targets.Add(Make("inventory-item", $"artifact #{artifactId}", artifactId, n, ClickableIn(i), notes.Count > 0 ? string.Join(", ", notes) : null)
                    with { SellMarked = marked, HasOwner = owned });
                continue;
            }

            // Message box buttons (MessageBox view): each button's own context holds its text; the id is its place (0, 1, …).
            if (n.Context?.Class == MessageBoxButtonContext)
            {
                var text = Optional(() => g.Ref(n.Context.Address, ButtonContextBase, "Text") is var p and not 0 ? g.String(p, StringProperty, "_value") : null);
                targets.Add(Make("message-button", $"message box: {text ?? "?"}", int.TryParse(n.Name, out var place) ? place : null, n, ClickableIn(i), text));
                continue;
            }

            // Gear slots: named by the game's prefab; the slot comes from its binding path (WeaponSlot … ClothesSlot, CloakSlot).
            if (GearSlotName().Match(n.Name) is { Success: true } m)
            {
                var slot = SlotFromBinding(n.BindingPath)
                    ?? (m.Groups["j"].Value switch { "Ring" => "Ring", "Pendant" => "Cloak", "Banner" => "Banner", _ => null });
                var label = slot is null ? $"slot ({m.Groups["n"].Value})" : DisplayName(slot);
                targets.Add(Make("gear-slot", $"gear slot {label}", null, n, ClickableIn(i),
                    slot is null ? $"binding path '{n.BindingPath}' does not name a slot" : $"binding '{n.BindingPath}'", slot));
                continue;
            }

            // Gear panel tabs: toggles named 1..4 under Tabs.
            if (n.IsOn is not null && n.Path.EndsWith("/Tabs/" + n.Name, StringComparison.Ordinal) && int.TryParse(n.Name, out var tab))
            {
                targets.Add(Make("gear-tab", $"gear tab {tab}", tab, n, n, n.IsOn == true ? "on" : "off"));
                continue;
            }

            // Scroll areas: the champion list's viewport and the picker's groups viewport (scrolled with the mouse wheel).
            if (n.Name == "Viewport" && n.Path.EndsWith("/HeroesCompact_h/Viewport", StringComparison.Ordinal))
            {
                targets.Add(Make("scroll-champions", "champion list (scroll)", null, n, n, null));
                continue;
            }
            if (n.Name is "BathhouseViewport_h" or "VaultViewport_h" && n.Path.Contains("/StorageWindow/", StringComparison.Ordinal))
            {
                targets.Add(Make("scroll-vault", $"vaults: {(n.Name.StartsWith("Bath", StringComparison.Ordinal) ? "Reserve" : "Master")} grid (scroll)", null, n, n, null));
                continue;
            }
            if (n.Name == "GroupsViewport" && n.Path.Contains("/InventoryWindow/", StringComparison.Ordinal))
            {
                targets.Add(Make("scroll-picker", "picker (scroll)", null, n, n, null));
                continue;
            }

            if (n.Interactable is null) continue;
            var named = n.Name switch
            {
                // Opening the champion screen from the village, and leaving it.
                "HeroesButton" when n.BindingPath == "HeroesInfo" => ("open-champions", "village: Champions"),
                "CloseButton" when n.Path.EndsWith("/HeroesHeader/CloseButton", StringComparison.Ordinal) => ("screen-close", "champion screen: close"),
                // The picker and the artifact popup's two panels (the selected piece, the piece worn now), each with its own close.
                "HideButton" when n.Path.EndsWith("/InventoryWindow/HideButton", StringComparison.Ordinal) => ("picker-close", "picker: close"),
                // The champion list's Champion Vaults button (the vault commands only).
                "StorageButton_h" when t.View == UiState.ChampionScreenView => ("vaults-open", "champion screen: Champion Vaults"),
                // The Vaults panel (StorageWindow): its two tabs, its close, and its move buttons (bottom).
                "VaultButton" when n.Path.Contains("/StorageWindow/", StringComparison.Ordinal) => ("vault-tab-master", "vaults: Master Vault tab"),
                "BathhouseButton" when n.Path.Contains("/StorageWindow/", StringComparison.Ordinal) => ("vault-tab-reserve", "vaults: Reserve Vault tab"),
                "HideButton" when n.Path.Contains("/StorageWindow/", StringComparison.Ordinal) => ("vault-close", "vaults: close"),
                // With champions ticked in a vault's grid: left "To Collection" (the champion list), right "To <the other vault>".
                "SingleOrLeftButton" when n.Path.Contains("/StorageWindow/", StringComparison.Ordinal) => ("vault-move-left", "vaults: left button (To Collection when champions are ticked)"),
                "RightButton_h" when n.Path.Contains("/StorageWindow/", StringComparison.Ordinal) => ("forbidden", "vaults: right button (to the other vault; never)"),
                "HideButton" when n.Path.Contains("/SelectedItemWindow/", StringComparison.Ordinal) => ("popup-close-selected", "popup (selected piece): close"),
                "HideButton" when n.Path.Contains("/EquipedItemWindow/", StringComparison.Ordinal) => ("popup-close-worn", "popup (worn piece): close"),
                // The artifact popup's actions: never clicked by navigation (Equip only in a real run, after the go-ahead).
                "EquipButton_h" => ("popup-equip", "popup: Equip"),
                "RemoveButton_h" => ("popup-remove", "popup: Remove"),
                // The manage-gear dialog (RemoveArtifactOverlay).
                "Unequip_h" when t.View == "RemoveArtifactOverlay" => ("manage-remove", "manage gear: Remove"),
                "BTN_Close1" when t.View == "RemoveArtifactOverlay" => ("manage-close", "manage gear: close"),
                // The confirmation Equip opens when it replaces a worn piece: Cancel (allowed), Continue (only inside a step's Equip).
                "Cancel" when t.View == UiState.SwapView => ("swap-cancel", "swap dialog: Cancel"),
                "Continue" when t.View == UiState.SwapView => ("swap-continue", "swap dialog: Continue"),
                // Upgrading (the upgrade command only): the selected piece's popup Upgrade (opens the upgrade screen) and that screen's one-level
                // Upgrade. The upgrade screen's Sell and its "upgrade to level" button are never clicked.
                "PowerUpButton" when n.Path.Contains("/SelectedItemWindow/", StringComparison.Ordinal) => ("popup-upgrade", "popup (selected piece): Upgrade"),
                // The upgrade screen: "upgrade to level" (the game keeps trying until the level set in its dropdown, 1 by default, is
                // reached) and its Cancel while it runs. The one-attempt Upgrade, Sell and the level dropdown are never clicked.
                "UpgradeButton" when t.View == UiState.PowerUpView && n.Path.Contains("/LevelUpGradationDropdown_h/UpgradeButton", StringComparison.Ordinal) => ("powerup-level", "upgrade screen: Upgrade (to level)"),
                "CancelMultiPowerUpButton_h" when t.View == UiState.PowerUpView => ("powerup-cancel", "upgrade screen: Cancel (stop upgrading)"),
                "SellButton" or "PowerUpButton" or "Dropdown" when t.View == UiState.PowerUpView => ("forbidden", $"upgrade screen: {n.Name} (never)"),
                // The picker's sell mode: its toggle, and the sell panel's Cancel and Apply (Apply opens the game's own confirmation).
                "SellMode" when n.BindingPath?.Contains("SellArtifactsButton", StringComparison.Ordinal) == true => ("sell-mode", "picker: Sell mode"),
                "Cancel" when n.Path.Contains("/SellArtifactsPanel_h/", StringComparison.Ordinal) => ("sell-cancel", "sell panel: Cancel"),
                "Apply" when n.Path.Contains("/SellArtifactsPanel_h/", StringComparison.Ordinal) => ("sell-apply", "sell panel: Sell"),
                // Any other popup's close button (offers and the like): closing only; their other buttons are never targets.
                "BTN_Close1" when t.View.EndsWith("Overlay", StringComparison.Ordinal) => ("dialog-close", $"{t.View}: close"),
                // Any other screen's back or close element (by its own name), and a battle result's way back (never Replay or Next).
                _ when IsBackName(n.Name) || UiState.IsBattleResultView(t.View) && IsResultLeaveName(n.Name) => ("back", $"{t.View}: {n.Name}"),
                _ when NeverClick.Contains(n.Name) => ("forbidden", n.Name switch
                {
                    "Sell_h" => "manage gear: Sell",
                    "SelectAll_h" => "sell panel: Select all",
                    "QuickEquipButton" => "picker: Auto-Equip",
                    _ => "popup: Upgrade (PowerUp)",
                }),
                _ => ((string?)null, (string?)null),
            };
            if (named.Item1 is { } kind) targets.Add(Make(kind, named.Item2!, null, n, n, null, forbidden: kind == "forbidden"));
        }
        // Scroll areas are not clickable elements: they are kept with their rectangles for the wheel.
        // One element found twice (an inventory item carries its context on two nested elements, both leading to the same button)
        // is one target. Different elements claiming one id stay separate, and Resolve refuses them.
        return targets.DistinctBy(t => (t.Kind, t.Id, t.Slot, t.Path, t.Rect)).ToList();
    }

    /// <summary>
    /// The one target of <paramref name="kind"/> for <paramref name="id"/> (or <paramref name="slot"/>) that may be clicked now, or why
    /// there is none: not found, not clickable, or more than one (a pooled list item with a stale id) — which refuses.
    /// </summary>
    public static (UiTarget? Target, string? Problem) Resolve(IReadOnlyList<UiTarget> targets, string kind, int? id = null, string? slot = null)
    {
        var all = targets.Where(t => t.Kind == kind && (id is null || t.Id == id) && (slot is null || t.Slot == slot)).ToList();
        var what = $"{kind}{(id is { } i ? $" {i}" : "")}{(slot is { } s ? $" {DisplayName(s)}" : "")}";
        if (all.Count == 0) return (null, $"{what}: not on screen.");
        var clickable = all.Where(t => t.NotClickable is null).ToList();
        if (clickable.Count > 1) return (null, $"{what}: {clickable.Count} clickable elements claim it; refused.");
        if (clickable.Count == 1) return (clickable[0], null);
        return (null, $"{what}: {string.Join("; ", all.Select(t => t.NotClickable).Distinct())}.");
    }

    /// <summary>A back or close element by its name: Close, Back, Hide or Return, optionally BTN_ / Button / a number / _h.</summary>
    public static bool IsBackName(string name) => BackName().IsMatch(name);

    /// <summary>On a battle result screen only: the way back to the hub or the screen the battle started from.</summary>
    public static bool IsResultLeaveName(string name) =>
        ResultLeaveName().IsMatch(name) && !ResultNever().IsMatch(name);

    [GeneratedRegex(@"^(BTN_?|Button_?)?(Close|Back|Hide|Return)(_?(Button|Btn))?\d*(_h)?$|^\w*(Close|Back)Button\d*(_h)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BackName();

    [GeneratedRegex(@"Return|Hub|Home|Exit|Leave|Lobby", RegexOptions.IgnoreCase)]
    private static partial Regex ResultLeaveName();

    [GeneratedRegex(@"Replay|Repeat|Next|Retry|Again|Start|Battle|Buy|Shop|Refill", RegexOptions.IgnoreCase)]
    private static partial Regex ResultNever();

    /// <summary>The slot id named in a binding path ("WeaponSlot" → Weapon, "ClothesSlot" → Chest, "CloakSlot" → Cloak).</summary>
    public static string? SlotFromBinding(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var s in SlotIds)
            if (path.Contains(s, StringComparison.OrdinalIgnoreCase)) return s;
        if (path.Contains("Clothes", StringComparison.OrdinalIgnoreCase) || path.Contains("Armor", StringComparison.OrdinalIgnoreCase)) return "Chest";
        if (path.Contains("Amulet", StringComparison.OrdinalIgnoreCase) || path.Contains("Pendant", StringComparison.OrdinalIgnoreCase)) return "Cloak";
        return null;
    }

    /// <summary>A field that older offsets do not have (re-run scripts/gen-ui-offsets.ps1): null instead of a failed read.</summary>
    private static T? Optional<T>(Func<T?> read)
    {
        try { return read(); }
        catch (KeyNotFoundException) { return default; }
    }

    private static int? IntValue(IGameObjects g, ulong property) => property == 0 ? null : g.Int(property, IntProperty, "_value");
    private static bool? BoolValue(IGameObjects g, ulong property) => property == 0 ? null : g.Bool(property, BoolProperty, "_value");
}
