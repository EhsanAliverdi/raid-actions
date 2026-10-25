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

        public ulong Tile(int heroId, bool selected) => Add(new()
        {
            ["_heroId"] = Add(new() { ["_value"] = heroId }),
            ["_isSelected"] = Add(new() { ["_value"] = selected }),
        });

        public ulong Root(string name, string expectedType) => throw new NotSupportedException();
        public string ClassName(ulong obj) => throw new NotSupportedException();
        public IReadOnlyList<string> ClassChain(ulong obj) => throw new NotSupportedException();
        public ulong Ref(ulong obj, string type, string member) => (ulong)Fields[obj][member];
        public int Int(ulong obj, string type, string member) => (int)Fields[obj][member];
        public bool Bool(ulong obj, string type, string member) => (bool)Fields[obj][member];
        public string? String(ulong obj, string type, string member) => null;
        public int? NullableInt(ulong obj, string type, string member) => null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => throw new NotSupportedException();
        public string? EnumName(string enumType, int value) => null;
    }

    private static int _index;

    private static UiNode N(string path, ClientRect? rect = null, ClientRect? visible = null, bool? interactable = null, UiContext? ctx = null,
        string? binding = null, bool? isOn = null, bool active = true) =>
        new(++_index, 0, path.Count(c => c == '/'), path[(path.LastIndexOf('/') + 1)..], path, active, active, rect, [], ctx, interactable, binding, visible ?? rect, isOn);

    private static ClientRect R(float x, float y, float w = 100, float h = 100) => new(x, y, w, h);

    [Fact]
    public void Champion_tiles_are_found_by_the_hero_id_in_their_context()
    {
        var g = new Ctx();
        var ctx = (int id, bool sel) => new UiContext(g.Tile(id, sel), UiTargets.HeroSlotContext);
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/List/Content/HeroSlot(Clone)", R(30, 160), ctx: ctx(1, true)),
            N("O/List/Content/HeroSlot(Clone)/EmptyPanel_h", R(30, 160), interactable: true, active: false),
            N("O/List/Content/HeroSlot(Clone)/BASE_HeroAvatar_Selectable_h", R(30, 160), interactable: true),
            N("O/List/Content/HeroSlot(Clone) ", R(30, 820), ctx: ctx(2120, false)),
            N("O/List/Content/HeroSlot(Clone) /BASE_HeroAvatar_Selectable_h", R(30, 820), visible: R(30, 820, 100, 20), interactable: true),
            N("O/List/Content/HeroSlot(Clone)  ", R(140, 160), ctx: ctx(0, false)),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, g);
        Assert.Equal([1, 2120], t.Select(x => x.Id));                  // the empty slot (hero 0) is not a target
        var kael = t[0];
        Assert.Equal("champion-tile", kael.Kind);
        Assert.Equal("selected", kael.Note);
        Assert.EndsWith("BASE_HeroAvatar_Selectable_h", kael.Path);       // the active button, not the inactive empty panel
        Assert.Null(kael.NotClickable);
        Assert.Contains("not fully visible", t[1].NotClickable);         // cut off by the list's viewport
    }

    [Fact]
    public void Gear_slots_take_their_kind_from_the_binding_path_and_tabs_their_toggle()
    {
        var tree = new UiTree("HeroesInfoOverlay",
        [
            N("O/Swipe/1/UpperPanel/Artifacts/EquipmentArtifactSlot (1)", R(1221, 181), binding: "Equipment.Weapon"),
            N("O/Swipe/1/UpperPanel/Artifacts/EquipmentArtifactSlot (1)/BASE_ArtifactAvatar_h", R(1221, 181), interactable: true),
            N("O/Swipe/1/UpperPanel/Artifacts/EquipmentArtifactSlot (4)", R(1221, 301), binding: "Equipment.Slot4"),
            N("O/Swipe/1/UpperPanel/Artifacts/EquipmentArtifactSlot (4)/BASE_ArtifactAvatar_h", R(1221, 301), interactable: true),
            N("O/Swipe/1/UpperPanel/Jewelry/EquipmentArtifactPendantSlot", R(1335, 448)),
            N("O/Swipe/1/UpperPanel/Jewelry/EquipmentArtifactPendantSlot/ArtifactAvatar_h/BASE_ArtifactAvatar", R(1335, 448), interactable: true),
            N("O/Tabs/1", R(1192, 70, 103, 88), interactable: true, isOn: true),
            N("O/Tabs/2", R(1294, 70, 97, 88), interactable: true, isOn: false),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, new Ctx());
        Assert.Equal(["gear slot Weapon", "gear slot slot (4)", "gear slot Cloak", "gear tab 1", "gear tab 2"], t.Select(x => x.Name));
        Assert.Contains("does not name a slot", t[1].Note);              // never guessed from the position
        Assert.Equal([1, 2], t.Where(x => x.Kind == "gear-tab").Select(x => x.Id!.Value));
        Assert.Equal(["on", "off"], t.Where(x => x.Kind == "gear-tab").Select(x => x.Note));
    }

    [Fact]
    public void The_sell_button_is_never_clickable()
    {
        var tree = new UiTree("RemoveArtifactOverlay",
        [
            N("[OV] RemoveArtifactOverlay/BoxContainer/Box/Content/Buttons/Unequip_h", R(443, 593, 340, 107), interactable: true),
            N("[OV] RemoveArtifactOverlay/BoxContainer/Box/Content/Buttons/Sell_h", R(817, 593, 340, 107), interactable: true),
            N("[OV] RemoveArtifactOverlay/BoxContainer/Box/BTN_Close1", R(1153, 131, 73, 73), interactable: true),
        ], 1600, 900, null, 0.8333f);
        var t = UiTargets.Find(tree, new Ctx());
        Assert.Equal(["manage-remove", "manage-sell", "manage-close"], t.Select(x => x.Kind));
        Assert.Null(t[0].NotClickable);
        Assert.True(t[1].Forbidden);
        Assert.Equal("forbidden (never clicked)", t[1].NotClickable);
        Assert.Contains("Sell_h", UiTargets.NeverClick);
    }

    [Theory]
    [InlineData("Equipment.Weapon", "Weapon")]
    [InlineData("Hero.Artifacts.Helmet", "Helmet")]
    [InlineData("Equipment.Amulet", "Cloak")]
    [InlineData("Equipment.Armor", "Chest")]
    [InlineData("Equipment.Slot4", null)]
    [InlineData(null, null)]
    public void Slot_names_in_binding_paths(string? path, string? slot) => Assert.Equal(slot, UiTargets.SlotFromBinding(path));
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
