using System.Text.RegularExpressions;

namespace RaidActions.Game.Memory;

/// <param name="Kind">champion-tile, gear-slot, gear-tab, inventory-item, popup-equip, popup-remove, popup-close, manage-remove,
/// manage-close, manage-sell, forbidden.</param>
/// <param name="Id">What the target stands for: champion id (tile), artifact id (inventory item), tab number; else null.</param>
/// <param name="Slot">Gear slots: the game's data id for the slot (Weapon … Ring, Cloak, Banner), as in plan files.</param>
/// <param name="Rect">Where to click (client pixels), from memory.</param>
/// <param name="FullyVisible">Entirely inside its scroll viewport / mask: only then may it be clicked.</param>
/// <param name="Forbidden">Never clicked, whatever a step asks.</param>
/// <param name="Note">Extra state: selected, tab on, the binding path, why it cannot be clicked.</param>
public sealed record UiTarget(string Kind, string Name, int? Id, string View, ClientRect? Rect, bool Active, bool? Interactable,
    bool FullyVisible, bool Forbidden, string Path, string? Note, string? Slot = null)
{
    /// <summary>Why this target may not be clicked now, or null when it may.</summary>
    public string? NotClickable =>
        Forbidden ? "forbidden (never clicked)"
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

    /// <summary>
    /// Element names that are never clicked: Sell (manage-gear dialog), the picker's Sell mode, Auto-Equip (re-gears the champion
    /// by itself), PowerUp (Upgrade: spends resources).
    /// </summary>
    public static readonly IReadOnlySet<string> NeverClick = new HashSet<string> { "Sell_h", "SellMode", "QuickEquipButton", "PowerUpButton" };

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
                b.FullyVisible, forbidden || NeverClick.Contains(b.Name), b.Path, note, slot);
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];

            // Champion tiles: the tile's own context says which champion it shows.
            if (n.Context?.Class == HeroSlotContext)
            {
                var heroId = IntValue(g, g.Ref(n.Context.Address, HeroSlotContext, "_heroId"));
                if (heroId is null or 0) continue; // an empty slot of the virtual list
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
                targets.Add(Make("inventory-item", $"artifact #{artifactId}", artifactId, n, ClickableIn(i), notes.Count > 0 ? string.Join(", ", notes) : null));
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

            if (n.Interactable is null) continue;
            var named = n.Name switch
            {
                // The artifact popup (compare panels): Equip on the selected piece, Remove on the worn one, the close button.
                "EquipButton_h" => ("popup-equip", "popup: Equip"),
                "RemoveButton_h" => ("popup-remove", "popup: Remove"),
                "HideButton" when t.View == UiState.ChampionScreenView => ("popup-close", "popup: close"),
                // The manage-gear dialog (RemoveArtifactOverlay).
                "Unequip_h" when t.View == "RemoveArtifactOverlay" => ("manage-remove", "manage gear: Remove"),
                "BTN_Close1" when t.View == "RemoveArtifactOverlay" => ("manage-close", "manage gear: close"),
                _ when NeverClick.Contains(n.Name) => ("forbidden", n.Name switch
                {
                    "Sell_h" => "manage gear: Sell",
                    "SellMode" => "picker: Sell mode",
                    "QuickEquipButton" => "picker: Auto-Equip",
                    _ => "popup: Upgrade (PowerUp)",
                }),
                _ => ((string?)null, (string?)null),
            };
            if (named.Item1 is { } kind) targets.Add(Make(kind, named.Item2!, null, n, n, null, forbidden: kind == "forbidden"));
        }
        return targets;
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

    private static int? IntValue(IGameObjects g, ulong property) => property == 0 ? null : g.Int(property, IntProperty, "_value");
    private static bool? BoolValue(IGameObjects g, ulong property) => property == 0 ? null : g.Bool(property, BoolProperty, "_value");
}
