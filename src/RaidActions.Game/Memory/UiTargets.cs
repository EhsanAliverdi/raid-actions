using System.Text.RegularExpressions;

namespace RaidActions.Game.Memory;

/// <param name="Kind">champion-tile, gear-slot, gear-tab, manage-remove, manage-close, manage-sell, ...</param>
/// <param name="Id">The id the target stands for: champion id (tile), tab number; null when the target has none.</param>
/// <param name="Rect">Where to click (client pixels), from memory.</param>
/// <param name="FullyVisible">Entirely inside its scroll viewport / mask: only then may it be clicked.</param>
/// <param name="Forbidden">Never clicked, whatever a step asks (e.g. the Sell button of the manage-gear dialog).</param>
/// <param name="Note">Extra state: selected tile, tab on, slot from the binding path, why it cannot be clicked.</param>
public sealed record UiTarget(string Kind, string Name, int? Id, string View, ClientRect? Rect, bool Active, bool? Interactable,
    bool FullyVisible, bool Forbidden, string Path, string? Note)
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
/// The click targets of the champion screen and its dialogs, found in a UI tree by the game's own names and contexts, each with
/// the id it stands for. A champion tile is "the tile whose HeroSlotContext says hero X", never a position from a sort order.
/// </summary>
public static partial class UiTargets
{
    public const string HeroSlotContext = "Client.ViewModel.Contextes.HeroesInfoDialog.HeroSlot.HeroSlotContext";
    private const string IntProperty = "Client.MVVM.Base.ViewModel.Properties.IntProperty";
    private const string BoolProperty = "Client.MVVM.Base.ViewModel.Properties.BoolProperty";

    /// <summary>Element names that are never clicked: the manage-gear dialog's Sell button (sells the piece for silver).</summary>
    public static readonly IReadOnlySet<string> NeverClick = new HashSet<string> { "Sell_h" };

    public static readonly string[] SlotNames = ["Weapon", "Helmet", "Shield", "Gloves", "Chest", "Boots", "Ring", "Cloak", "Banner"];

    [GeneratedRegex(@"^EquipmentArtifact(Slot \((?<n>\d)\)|(?<j>Ring|Pendant|Banner)Slot)$")]
    private static partial Regex GearSlotName();

    public static List<UiTarget> Find(UiTree t, IGameObjects g)
    {
        var targets = new List<UiTarget>();
        var byPath = t.Nodes.ToLookup(n => n.Path);

        // The first active clickable element at or below a node (the button inside a tile or slot).
        UiNode? ClickableIn(UiNode n) =>
            t.Nodes.FirstOrDefault(c => (c.Path == n.Path || c.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)) && c.Interactable is not null && c.ActiveInHierarchy)
            ?? t.Nodes.FirstOrDefault(c => (c.Path == n.Path || c.Path.StartsWith(n.Path + "/", StringComparison.Ordinal)) && c.Interactable is not null);

        UiTarget Make(string kind, string name, int? id, UiNode owner, UiNode? button, string? note, bool forbidden = false) =>
            new(kind, name, id, t.View, (button ?? owner).Screen, owner.ActiveInHierarchy && (button?.ActiveInHierarchy ?? true), button?.Interactable,
                (button ?? owner).FullyVisible, forbidden || NeverClick.Contains((button ?? owner).Name), (button ?? owner).Path, note);

        foreach (var n in t.Nodes)
        {
            // Champion tiles: the tile's own context says which champion it shows.
            if (n.Context?.Class == HeroSlotContext)
            {
                var heroId = IntValue(g, g.Ref(n.Context.Address, HeroSlotContext, "_heroId"));
                var selected = BoolValue(g, g.Ref(n.Context.Address, HeroSlotContext, "_isSelected"));
                if (heroId is null or 0) continue; // an empty slot of the virtual list
                targets.Add(Make("champion-tile", $"champion #{heroId}", heroId, n, ClickableIn(n), selected == true ? "selected" : null));
                continue;
            }

            // Gear slots: the slot is named by the game's prefab; its kind comes from its binding path.
            if (GearSlotName().Match(n.Name) is { Success: true } m)
            {
                var kind = SlotFromBinding(n.BindingPath) ?? SlotFromBinding(ClickableIn(n)?.BindingPath)
                    ?? (m.Groups["j"].Value switch { "Ring" => "Ring", "Pendant" => "Cloak", "Banner" => "Banner", _ => null });
                var label = kind ?? $"slot ({m.Groups["n"].Value})";
                targets.Add(Make("gear-slot", $"gear slot {label}", null, n, ClickableIn(n),
                    kind is null ? $"binding path '{n.BindingPath}' does not name a slot" : $"binding path '{n.BindingPath}'"));
                continue;
            }

            // Gear panel tabs: toggles named 1..4 under Tabs.
            if (n.IsOn is not null && n.Path.EndsWith("/Tabs/" + n.Name, StringComparison.Ordinal) && int.TryParse(n.Name, out var tab))
            {
                targets.Add(Make("gear-tab", $"gear tab {tab}", tab, n, n, n.IsOn == true ? "on" : "off"));
                continue;
            }

            // The manage-gear dialog (RemoveArtifactOverlay).
            if (t.View == "RemoveArtifactOverlay" && n.Interactable is not null)
            {
                var (kind, name) = n.Name switch
                {
                    "Unequip_h" => ("manage-remove", "manage gear: Remove"),
                    "BTN_Close1" => ("manage-close", "manage gear: close"),
                    "Sell_h" => ("manage-sell", "manage gear: Sell"),
                    _ => (null, null),
                };
                if (kind is not null) targets.Add(Make(kind, name!, null, n, n, n.Name == "Sell_h" ? "sells the piece for silver" : null, n.Name == "Sell_h"));
            }
        }
        return targets;
    }

    /// <summary>A slot name in a binding path ("…Weapon…" → Weapon; the amulet slot is the game's Cloak).</summary>
    public static string? SlotFromBinding(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var s in SlotNames)
            if (path.Contains(s, StringComparison.OrdinalIgnoreCase)) return s;
        if (path.Contains("Amulet", StringComparison.OrdinalIgnoreCase) || path.Contains("Pendant", StringComparison.OrdinalIgnoreCase)) return "Cloak";
        if (path.Contains("Armor", StringComparison.OrdinalIgnoreCase)) return "Chest";
        return null;
    }

    private static int? IntValue(IGameObjects g, ulong property) => property == 0 ? null : g.Int(property, IntProperty, "_value");
    private static bool? BoolValue(IGameObjects g, ulong property) => property == 0 ? null : g.Bool(property, BoolProperty, "_value");
}
