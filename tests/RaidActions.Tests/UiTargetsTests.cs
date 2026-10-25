using RaidActions.Game.Memory;

namespace RaidActions.Tests;

/// <summary>Click targets from a UI tree: ids from contexts, slots from binding paths, the never-click list, visibility.</summary>
public class UiTargetsTests
{
    private sealed class Ctx : IGameObjects
    {
        public readonly Dictionary<ulong, Dictionary<string, object>> Fields = [];
        private ulong _next = 0x5000;

        public ulong Add(Dictionary<string, object> f)
        {
            Fields[_next += 0x10] = f;
            return _next;
        }

        private ulong Prop(object v) => Add(new() { ["_value"] = v });

        public UiContext Tile(int heroId, bool selected) =>
            new(Add(new() { ["_heroId"] = Prop(heroId), ["_isSelected"] = Prop(selected) }), UiTargets.HeroSlotContext);

        public UiContext Item(int artifactId, bool selected = false, bool worn = false) =>
            new(Add(new()
            {
                ["Avatar"] = Add(new() { ["Artifact"] = Add(new() { ["Id"] = artifactId }) }),
                ["_isSelected"] = Prop(selected), ["_ownerIsCurrentHero"] = Prop(worn),
            }), UiTargets.InventoryArtifactContext);

        public ulong Root(string name, string expectedType) => throw new NotSupportedException();
        public string ClassName(ulong obj) => throw new NotSupportedException();
        public IReadOnlyList<string> ClassChain(ulong obj) => throw new NotSupportedException();
        public ulong Ref(ulong obj, string type, string member) => Fields[obj].TryGetValue(member, out var v) ? (ulong)v : 0;
        public int Int(ulong obj, string type, string member) => (int)Fields[obj][member];
        public bool Bool(ulong obj, string type, string member) => (bool)Fields[obj][member];
        public string? String(ulong obj, string type, string member) => null;
        public int? NullableInt(ulong obj, string type, string member) => null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => throw new NotSupportedException();
        public string? EnumName(string enumType, int value) => null;
    }

    private static int _index;

    /// <summary>A node; depth from the path (nodes must be listed in depth-first order, as the reader returns them).</summary>
    private static UiNode N(string path, ClientRect? rect = null, ClientRect? visible = null, bool? interactable = null, UiContext? ctx = null,
        string? binding = null, bool? isOn = null, bool active = true) =>
        new(++_index, 0, path.Count(c => c == '/'), path[(path.LastIndexOf('/') + 1)..], path, active, active, rect, [], ctx, interactable, binding, visible ?? rect, isOn);

    private static ClientRect R(float x, float y, float w = 100, float h = 100) => new(x, y, w, h);

    [Fact]
    public void Champion_tiles_with_the_same_name_each_get_their_own_button()
    {
        var g = new Ctx();
        // Three tiles named alike (the game's pooled "HeroSlot(Clone)"); each button must come from its own tile.
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/List/Content/HeroSlot(Clone)", R(30, 160), ctx: g.Tile(1, true)),
            N("O/List/Content/HeroSlot(Clone)/EmptyPanel_h", R(30, 160), interactable: true, active: false),
            N("O/List/Content/HeroSlot(Clone)/BASE_HeroAvatar_Selectable_h", R(30, 160), interactable: true),
            N("O/List/Content/HeroSlot(Clone)", R(140, 160), ctx: g.Tile(3, false)),
            N("O/List/Content/HeroSlot(Clone)/BASE_HeroAvatar_Selectable_h", R(140, 160), interactable: true),
            N("O/List/Content/HeroSlot(Clone)", R(30, 820), ctx: g.Tile(2120, false)),
            N("O/List/Content/HeroSlot(Clone)/BASE_HeroAvatar_Selectable_h", R(30, 820), visible: R(30, 820, 100, 20), interactable: true),
            N("O/List/Content/HeroSlot(Clone)", R(250, 160), ctx: g.Tile(0, false)),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, g);
        Assert.Equal([1, 3, 2120], t.Select(x => x.Id));                     // the empty slot (hero 0) is not a target
        Assert.Equal([30f, 140f, 30f], t.Select(x => x.Rect!.Value.X));        // each tile's own button
        Assert.Equal("selected", t[0].Note);
        Assert.Null(t[0].NotClickable);
        Assert.Contains("not fully visible", t[2].NotClickable);              // cut off by the list's viewport

        Assert.Equal(140f, UiTargets.Resolve(t, "champion-tile", 3).Target!.Rect!.Value.X);
        Assert.Contains("not fully visible", UiTargets.Resolve(t, "champion-tile", 2120).Problem);
        Assert.Contains("not on screen", UiTargets.Resolve(t, "champion-tile", 999).Problem);
    }

    [Fact]
    public void Two_visible_tiles_claiming_one_champion_are_refused()
    {
        var g = new Ctx();
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/C/HeroSlot(Clone)", R(30, 160), ctx: g.Tile(7, false)),
            N("O/C/HeroSlot(Clone)/B", R(30, 160), interactable: true),
            N("O/C/HeroSlot(Clone)", R(140, 160), ctx: g.Tile(7, false)),
            N("O/C/HeroSlot(Clone)/B", R(140, 160), interactable: true),
        ], 1600, 900, null, 0.8333f);
        Assert.Contains("2 clickable elements", UiTargets.Resolve(UiTargets.Find(tree, g), "champion-tile", 7).Problem);
    }

    [Fact]
    public void Inventory_items_are_found_by_the_artifact_in_their_context()
    {
        var g = new Ctx();
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/Inv/Items/0", R(200, 300), ctx: g.Item(3185, selected: true)),
            N("O/Inv/Items/0/BASE_ArtifactAvatar", R(200, 300), interactable: true),
            N("O/Inv/Items/1", R(300, 300), ctx: g.Item(2484, worn: true)),
            N("O/Inv/Items/1/BASE_ArtifactAvatar", R(300, 300), interactable: true),
            N("O/Inv/Items/2", R(400, 300), ctx: g.Item(0)),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, g);
        Assert.Equal([3185, 2484], t.Select(x => x.Id));
        Assert.Equal("selected", t[0].Note);
        Assert.Equal("worn by this champion", t[1].Note);
        Assert.Equal(300f, UiTargets.Resolve(t, "inventory-item", 2484).Target!.Rect!.Value.X);
    }

    [Fact]
    public void An_item_whose_context_sits_on_two_nested_elements_is_one_target()
    {
        var g = new Ctx();
        var ctx = g.Item(2639, selected: true);
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/Inv/Items/0", R(537, 641, 83, 83), ctx: ctx),
            N("O/Inv/Items/0/Item", R(537, 641, 83, 83), ctx: ctx),
            N("O/Inv/Items/0/Item/BASE_ArtifactAvatar", R(537, 641, 83, 83), interactable: true),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, g);
        Assert.Single(t);
        Assert.Null(UiTargets.Resolve(t, "inventory-item", 2639).Problem);
    }

    [Fact]
    public void Gear_slots_use_the_game_names_and_keep_the_slot_id()
    {
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/Artifacts/EquipmentArtifactSlot (1)", R(1221, 181), binding: "WeaponSlot"),
            N("O/Artifacts/EquipmentArtifactSlot (1)/BASE_ArtifactAvatar_h", R(1221, 181), interactable: true),
            N("O/Artifacts/EquipmentArtifactSlot (5)", R(1341, 301), binding: "ClothesSlot"),
            N("O/Artifacts/EquipmentArtifactSlot (5)/BASE_ArtifactAvatar_h", R(1341, 301), interactable: true),
            N("O/Artifacts/EquipmentArtifactSlot (6)", R(1461, 301), binding: "SomethingElse"),
            N("O/Artifacts/EquipmentArtifactSlot (6)/BASE_ArtifactAvatar_h", R(1461, 301), interactable: true),
            N("O/Jewelry/EquipmentArtifactRingSlot", R(1215, 448), binding: "RingSlot"),
            N("O/Jewelry/EquipmentArtifactRingSlot/ArtifactAvatar_h/BASE_ArtifactAvatar", R(1215, 448), interactable: true),
            N("O/Jewelry/EquipmentArtifactPendantSlot", R(1335, 448), binding: "CloakSlot"),
            N("O/Jewelry/EquipmentArtifactPendantSlot/ArtifactAvatar_h/BASE_ArtifactAvatar", R(1335, 448), interactable: true),
            N("O/Jewelry/EquipmentArtifactBannerSlot", R(1455, 448), binding: "BannerSlot"),
            N("O/Jewelry/EquipmentArtifactBannerSlot/ArtifactAvatar_h/BASE_ArtifactAvatar", R(1455, 448), interactable: true),
            N("O/Tabs/1", R(1192, 70, 103, 88), interactable: true, isOn: true),
            N("O/Tabs/2", R(1294, 70, 97, 88), interactable: true, isOn: false),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, new Ctx());
        Assert.Equal(["gear slot Weapon", "gear slot Chest", "gear slot slot (6)", "gear slot Ring", "gear slot Amulet", "gear slot Banner", "gear tab 1", "gear tab 2"],
            t.Select(x => x.Name));
        Assert.Equal(["Weapon", "Chest", null, "Ring", "Cloak", "Banner"], t.Where(x => x.Kind == "gear-slot").Select(x => x.Slot));
        Assert.Contains("does not name a slot", t[2].Note);                  // never guessed from the position
        Assert.Equal(1335f, UiTargets.Resolve(t, "gear-slot", slot: "Cloak").Target!.Rect!.Value.X);
        Assert.Equal(["on", "off"], t.Where(x => x.Kind == "gear-tab").Select(x => x.Note));
    }

    [Fact]
    public void Sell_auto_equip_and_upgrade_are_never_clickable()
    {
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/Inv/Top/SellMode", R(513, 48, 53, 26), interactable: true, isOn: false),
            N("O/Inv/Top/QuickEquipButton", R(459, 92, 109, 32), interactable: true),
            N("O/Popup/Selected/PowerUpButton", R(10, 406, 93, 31), interactable: true),
            N("O/Popup/Selected/EquipButton_h", R(110, 406, 93, 31), interactable: true),
            N("O/Popup/Worn/RemoveButton_h", R(110, 184, 93, 31), interactable: true),
            N("O/Popup/SelectedItemWindow/Artifact_h/HideButton", R(174, 222, 36, 36), interactable: true),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, new Ctx());
        Assert.Equal(["forbidden", "forbidden", "forbidden", "popup-equip", "popup-remove", "popup-close-selected"], t.Select(x => x.Kind));
        Assert.All(t.Take(3), x => Assert.Equal("forbidden (never clicked)", x.NotClickable));
        Assert.Null(UiTargets.Resolve(t, "popup-equip").Problem);
        Assert.Contains("not on screen", UiTargets.Resolve(t, "manage-remove").Problem);

        var manage = new UiTree("RemoveArtifactOverlay",
        [
            N("[OV] RemoveArtifactOverlay/Box/Buttons/Unequip_h", R(443, 593, 340, 107), interactable: true),
            N("[OV] RemoveArtifactOverlay/Box/Buttons/Sell_h", R(817, 593, 340, 107), interactable: true),
            N("[OV] RemoveArtifactOverlay/Box/BTN_Close1", R(1153, 131, 73, 73), interactable: true),
        ], 1600, 900, null, 0.8333f);
        var m = UiTargets.Find(manage, new Ctx());
        Assert.Equal(["manage-remove", "forbidden", "manage-close"], m.Select(x => x.Kind));
        Assert.Equal("manage gear: Sell", m[1].Name);
        Assert.True(m[1].Forbidden);
    }

    [Theory]
    [InlineData("WeaponSlot", "Weapon")]
    [InlineData("ClothesSlot", "Chest")]
    [InlineData("CloakSlot", "Cloak")]
    [InlineData("Equipment.Amulet", "Cloak")]
    [InlineData("Equipment.Slot4", null)]
    [InlineData(null, null)]
    public void Slot_ids_in_binding_paths(string? path, string? slot) => Assert.Equal(slot, UiTargets.SlotFromBinding(path));

    [Fact]
    public void The_amulet_is_the_cloak_slot() => Assert.Equal(["Weapon", "Helmet", "Shield", "Gloves", "Chest", "Boots", "Ring", "Amulet", "Banner"],
        UiTargets.SlotIds.Select(UiTargets.DisplayName));
}

public class TabCheckTests
{
    private static UiTarget Tab(int n, bool on) => new("gear-tab", $"gear tab {n}", n, "HeroesInfoOverlay", new(0, 0, 10, 10), true, true, true, false, $"O/Tabs/{n}", on ? "on" : "off");

    [Fact]
    public void The_active_tab_index_must_name_the_tab_that_is_on()
    {
        Assert.StartsWith("tab check: AGREES", App.UiCommands.TabCheck([Tab(1, false), Tab(2, true)], 1));
        Assert.StartsWith("tab check: DISAGREES", App.UiCommands.TabCheck([Tab(1, true), Tab(2, false)], 1));
        Assert.Contains("inconclusive", App.UiCommands.TabCheck([Tab(1, false), Tab(2, false)], 0));
    }
}

public class NavigationTargetsTests
{
    private static int _i;

    private static UiNode N(string path, float x, float y, bool? interactable = true, string? binding = null) =>
        new(++_i, 0, path.Count(c => c == '/'), path[(path.LastIndexOf('/') + 1)..], path, true, true, new ClientRect(x, y, 70, 70), [], null, interactable,
            binding, new ClientRect(x, y, 70, 70));

    [Fact]
    public void Close_buttons_scroll_areas_and_the_champions_button_are_told_apart()
    {
        var champion = new UiTree("HeroesInfoOverlay",
        [
            N("[OV] HeroesInfoOverlay/Workspace/Content/HeroesHeader/CloseButton", 1450, 0),
            N("[OV] HeroesInfoOverlay/Workspace/Content/HeroBlockLoader/HeroesBlock(Clone)/HeroesCompact_h/Viewport", 13, 138, interactable: null),
            N("[OV] HeroesInfoOverlay/Workspace/Inv/InventoryWindow/HideButton", 1097, 0),
            N("[OV] HeroesInfoOverlay/Workspace/Inv/InventoryWindow/Content/Artifacts_h/Tab1.Content/GroupsViewport", 432, 300, interactable: null),
            N("[OV] HeroesInfoOverlay/Workspace/Inv/ArtifactWindowsHolder/EquipedItemWindowHolder/EquipedItemWindow/HideButton", 350, 0),
            N("[OV] HeroesInfoOverlay/Workspace/Inv/ArtifactWindowsHolder/SelectedItemWindowHolder/SelectedItemWindow/Artifact_h/HideButton", 350, 453),
        ], 1600, 900, null, 0.8333f);
        var t = RaidActions.Game.Memory.UiTargets.Find(champion, new NoObjects());
        Assert.Equal(["screen-close", "scroll-champions", "picker-close", "scroll-picker", "popup-close-worn", "popup-close-selected"], t.Select(x => x.Kind));
        Assert.All(t, x => Assert.Null(x.NotClickable));
        Assert.Equal(453f, RaidActions.Game.Memory.UiTargets.Resolve(t, "popup-close-selected").Target!.Rect!.Value.Y);

        var village = new UiTree("VillageHUD", [N("[DV] VillageHUD/Workspace/Bottom/HeroesButton", 727, 375, binding: "HeroesInfo")], 1600, 900, null, 0.8333f);
        Assert.Equal("open-champions", Assert.Single(RaidActions.Game.Memory.UiTargets.Find(village, new NoObjects())).Kind);
    }

    [Fact]
    public void A_popup_offer_can_only_be_closed()
    {
        var offer = new UiTree("AggressiveOfferOverlay",
        [
            N("[OV] AggressiveOfferOverlay/Box/Button", 333, 224),
            N("[OV] AggressiveOfferOverlay/Box/Prizes/ArtifactAvatar_h", 432, 78),
            N("[OV] AggressiveOfferOverlay/Box/BTN_Close1", 527, 3),
        ], 1600, 900, null, 0.8333f);
        var t = RaidActions.Game.Memory.UiTargets.Find(offer, new NoObjects());
        Assert.Equal("dialog-close", Assert.Single(t).Kind);   // the buy button and prizes are no targets at all
        Assert.Contains("dialog-close", App.Input.NavigatingDriver.AllowedClicks);
    }

    [Fact]
    public void Navigation_may_never_click_equip_remove_sell_upgrade_or_auto_equip()
    {
        var allowed = App.Input.NavigatingDriver.AllowedClicks;
        foreach (var never in new[] { "popup-equip", "popup-remove", "manage-remove", "manage-sell", "forbidden", "scroll-champions", "scroll-picker" })
            Assert.DoesNotContain(never, allowed);
        Assert.Contains("champion-tile", allowed);
        Assert.Contains("inventory-item", allowed);
        foreach (var name in new[] { "Sell_h", "SellMode", "QuickEquipButton", "PowerUpButton" })
            Assert.Contains(name, RaidActions.Game.Memory.UiTargets.NeverClick);
    }

    private sealed class NoObjects : IGameObjects
    {
        public ulong Root(string name, string expectedType) => 0;
        public string ClassName(ulong obj) => "";
        public IReadOnlyList<string> ClassChain(ulong obj) => [];
        public ulong Ref(ulong obj, string type, string member) => 0;
        public int Int(ulong obj, string type, string member) => 0;
        public bool Bool(ulong obj, string type, string member) => false;
        public string? String(ulong obj, string type, string member) => null;
        public int? NullableInt(ulong obj, string type, string member) => null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => [];
        public string? EnumName(string enumType, int value) => null;
    }
}
