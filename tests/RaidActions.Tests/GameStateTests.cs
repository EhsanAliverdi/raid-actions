using System.Text.Json.Nodes;
using OpenCvSharp;
using RaidActions.Game.Memory;
using RaidActions.Game.Vision;

namespace RaidActions.Tests;

/// <summary>The UI state reader on a fake object graph (the same reads the live game gets, no game).</summary>
public class UiStateReaderTests
{
    /// <summary>Objects with a class name and named fields; lists are objects with an "items" list.</summary>
    private sealed class FakeObjects : IGameObjects
    {
        private readonly Dictionary<ulong, (string Class, Dictionary<string, object?> Fields)> _objects = [];
        private readonly Dictionary<string, Dictionary<int, string>> _enums = new()
        {
            [UiStateReader.ViewKeyEnum] = new() { [1032] = "Village", [1018] = "HeroesInfoOverlay", [1015] = "BattleHUD", [3007] = "BattleTextNotificationsView", [1100] = "BattleFinishAllianceBossDialog", [2072] = "SwapArtifactsOverlay" },
            ["Client.RaidApp.ViewState"] = new() { [0] = "NotCreatedYet", [1] = "Active", [2] = "ClosePending", [3] = "CreationFailed" },
            ["Client.RaidApp.ViewVisibility"] = new() { [0] = "Hidden", [1] = "Visible" },
        };
        private ulong _next = 0x1000;
        public ulong App;
        public ulong Views;

        public ulong Add(string cls, Dictionary<string, object?> fields)
        {
            var id = _next += 0x100;
            _objects[id] = (cls, fields);
            return id;
        }

        public void Set(ulong obj, string field, object? value) => _objects[obj].Fields[field] = value;

        public ulong Root(string name, string expectedType) => App;
        public string ClassName(ulong obj) => _objects[obj].Class;
        public ulong Ref(ulong obj, string type, string member) => _objects[obj].Fields.TryGetValue(member, out var v) && v is ulong p ? p : 0;
        public int Int(ulong obj, string type, string member) => (int)_objects[obj].Fields[member]!;
        public bool Bool(ulong obj, string type, string member) => (bool)_objects[obj].Fields[member]!;
        public IReadOnlyList<string> ClassChain(ulong obj) => [_objects[obj].Class];
        public int? NullableInt(ulong obj, string type, string member) => (int?)_objects[obj].Fields[member];
        public IReadOnlyList<ulong> RefList(ulong list, int max) => ((List<ulong>)_objects[list].Fields["items"]!).Take(max).ToList();
        public string? EnumName(string enumType, int value) => _enums[enumType].TryGetValue(value, out var n) ? n : null;

        public ulong View(int key, int state = 1, int visible = 1, ulong context = 0)
        {
            var view = context == 0 ? 0UL : Add("Client.View.SomeView", new() { ["Context"] = context });
            var meta = Add("Client.RaidApp.ViewMeta", new() { ["Key"] = key, ["State"] = state, ["Visibility"] = visible, ["View"] = view == 0 ? null : view });
            ((List<ulong>)_objects[Views].Fields["items"]!).Add(meta);
            return meta;
        }

        public ulong HeroScreen(int? hero, int? selected, int? details, int? artifact, int tab = 0)
        {
            ulong Prop(int v) => Add("Client.MVVM.Base.ViewModel.Properties.IntProperty", new() { ["_value"] = v });
            var d = Add("Client.ViewModel.Contextes.HeroesInfoDialog.HeroDetails.HeroDetailsContext",
                new() { ["_heroId"] = details, ["_inventoryArtifactId"] = artifact, ["_activeTabIndex"] = Prop(tab) });
            var s = Add("Client.ViewModel.Contextes.HeroesInfoDialog.SelectedHero.SelectedHeroContext", new() { ["_heroId"] = selected });
            return Add("Client.ViewModel.Contextes.HeroesInfoDialog.HeroesInfoOverlayContext",
                new() { ["_heroId"] = hero, ["_heroDetailsContext"] = d, ["_selectedHeroContext"] = s, ["_viewState"] = Prop(2) });
        }

        public static FakeObjects Game(int blocking = 0)
        {
            var g = new FakeObjects();
            g.Views = g.Add("System.Collections.Generic.List`1", new() { ["items"] = new List<ulong>() });
            var master = g.Add("Client.RaidApp.RaidViewMaster", new() { ["_views"] = g.Views });
            var overlays = g.Add("Client.ViewModel.OverlayManager", new() { ["_viewMaster"] = master });
            var block = g.Add("Client.ViewModel.BlockUi", new() { ["_blockingEventsCounter"] = blocking });
            g.App = g.Add("Client.ViewModel.AppViewModel", new() { ["_overlayManager"] = overlays, ["_blockUi"] = block });
            return g;
        }
    }

    [Fact]
    public void Reads_the_champion_screen_and_the_selected_artifact()
    {
        var g = FakeObjects.Game();
        g.View(1032, visible: 0);
        g.View(1018, context: g.HeroScreen(3, 3, 3, 2585, tab: 0));
        var s = new UiStateReader(g).Read();
        Assert.True(s.ChampionScreenOpen);
        Assert.Equal("HeroesInfoOverlay", s.Top!.Name);
        Assert.Equal(new ChampionScreenState(3, 3, 3, 2585, 0, 2), s.ChampionScreen);
        Assert.Equal(3, s.ChampionScreen!.Champion);
        Assert.False(s.InBattle);
        Assert.False(s.UiBlocked);
    }

    [Fact]
    public void The_champion_is_known_only_when_every_part_agrees()
    {
        Assert.Null(new ChampionScreenState(3, 1315, 3, null, 0, 0).Champion);
        Assert.Null(new ChampionScreenState(3, 3, 1315, null, 0, 0).Champion);
        Assert.Null(new ChampionScreenState(null, 3, 3, null, 0, 0).Champion);
        Assert.Equal(3, new ChampionScreenState(3, null, 3, null, 0, 0).Champion);
    }

    [Fact]
    public void Battle_views_and_result_screens_count_as_a_battle_but_notifications_do_not()
    {
        var g = FakeObjects.Game();
        g.View(3007); // BattleTextNotificationsView: always open
        g.View(1032);
        Assert.False(new UiStateReader(g).Read().InBattle);
        var hud = g.View(1015);
        Assert.True(new UiStateReader(g).Read().InBattle);
        g.Set(hud, "State", 2); // closing: no longer active
        Assert.False(new UiStateReader(g).Read().InBattle);
        g.View(1100); // a battle result screen
        Assert.True(new UiStateReader(g).Read().InBattle);
    }

    [Fact]
    public void A_blocked_ui_and_unknown_view_keys_are_reported()
    {
        var g = FakeObjects.Game(blocking: 2);
        g.View(9999);
        var s = new UiStateReader(g).Read();
        Assert.True(s.UiBlocked);
        Assert.Equal("#9999", s.Views.Single().Name);
    }

    [Fact]
    public void A_wrong_class_anywhere_stops_the_read()
    {
        var g = FakeObjects.Game();
        var ctx = g.HeroScreen(3, 3, 3, null);
        g.Set(ctx, "_heroDetailsContext", g.Add("Client.Something.Else", []));
        g.View(1018, context: ctx);
        var e = Assert.Throws<UiReadException>(() => new UiStateReader(g).Read());
        Assert.Contains("Client.Something.Else", e.Message);
    }

    [Fact]
    public void A_champion_screen_without_its_context_yet_is_open_but_unread()
    {
        var g = FakeObjects.Game();
        g.View(1018);
        var s = new UiStateReader(g).Read();
        Assert.True(s.ChampionScreenOpen);
        Assert.Null(s.ChampionScreen);
    }
}

public class OffsetMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private LiveGamePaths Write(string coreHash, string uiHash, string manualVersion = "11.85.5", string? extraUiType = null)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "re", "config"));
        JsonObject Build(string hash) => new() { ["gameVersion"] = "11.85.5", ["binarySha256"] = hash };
        var core = new JsonObject
        {
            ["build"] = Build(coreHash), ["roots"] = new JsonObject { ["AppModel"] = new JsonObject() },
            ["types"] = new JsonObject { ["Client.Model.AppModel"] = new JsonObject { ["fields"] = new JsonObject() } }, ["enums"] = new JsonObject(),
        };
        var ui = new JsonObject
        {
            ["build"] = Build(uiHash), ["roots"] = new JsonObject { ["AppViewModel"] = new JsonObject() },
            ["types"] = new JsonObject { [extraUiType ?? "Client.ViewModel.AppViewModel"] = new JsonObject { ["fields"] = new JsonObject() } },
            ["enums"] = new JsonObject { ["Client.ViewModel.DTO.ViewKey"] = new JsonObject { ["Village"] = 1032 } },
        };
        var manual = new JsonObject
        {
            ["build"] = new JsonObject { ["gameVersion"] = manualVersion },
            ["fields"] = new JsonObject { ["Client.MVVM.Base.ViewModel.Properties.IntProperty._value"] = new JsonObject { ["offset"] = "0x24", ["type"] = "int", ["status"] = "unverified" } },
        };
        File.WriteAllText(Path.Combine(_dir, "re", "config", "offsets.json"), core.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "ui.json"), ui.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "manual.json"), manual.ToJsonString());
        return new LiveGamePaths(Path.Combine(_dir, "re"), Path.Combine(_dir, "ui.json"), Path.Combine(_dir, "manual.json"), Path.Combine(_dir, "out"));
    }

    [Fact]
    public void Merges_ui_offsets_and_reviewed_manual_fields_into_cores()
    {
        var (merged, problem) = LiveGame.MergeOffsets(Write("AA", "aa"));
        Assert.Null(problem);
        Assert.NotNull(merged!["roots"]!["AppModel"]);
        Assert.NotNull(merged["roots"]!["AppViewModel"]);
        Assert.Equal(1032, (int)merged["enums"]!["Client.ViewModel.DTO.ViewKey"]!["Village"]!);
        var v = merged["types"]!["Client.MVVM.Base.ViewModel.Properties.IntProperty"]!["fields"]!["_value"]!;
        Assert.Equal("0x24", (string)v["offset"]!);
        Assert.Equal("manual", (string)v["source"]!);
    }

    [Fact]
    public void Offsets_from_different_game_builds_are_refused() =>
        Assert.Contains("re-run scripts/gen-ui-offsets.ps1", LiveGame.MergeOffsets(Write("AA", "BB")).Problem);

    [Fact]
    public void A_manual_entry_for_another_game_version_is_refused() =>
        Assert.Contains("re-check", LiveGame.MergeOffsets(Write("AA", "AA", manualVersion: "11.90.0")).Problem);

    [Fact]
    public void A_type_in_both_files_is_refused() =>
        Assert.Contains("in both", LiveGame.MergeOffsets(Write("AA", "AA", extraUiType: "Client.Model.AppModel")).Problem);

    [Fact]
    public void The_committed_ui_offsets_cover_every_field_the_reader_uses()
    {
        var ui = JsonNode.Parse(File.ReadAllText(Path.Combine(AppPathsForTests.Config, "ui-offsets.json")))!.AsObject();
        string[] needed =
        [
            "Client.ViewModel.AppViewModel._overlayManager", "Client.ViewModel.AppViewModel._blockUi", "Client.ViewModel.OverlayManagerBase._viewMaster",
            "Client.RaidApp.RaidViewMaster._views", "Client.RaidApp.ViewMeta.Key", "Client.RaidApp.ViewMeta.State", "Client.RaidApp.ViewMeta.View",
            "Client.RaidApp.ViewMeta.Visibility", "Client.MVVM.Base.View.BaseView.Context", "Client.ViewModel.BlockUi._blockingEventsCounter",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroesInfoOverlayContext._heroId",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroesInfoOverlayContext._heroDetailsContext",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroesInfoOverlayContext._selectedHeroContext",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroesInfoOverlayContext._viewState",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroDetails.HeroDetailsContext._heroId",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroDetails.HeroDetailsContext._inventoryArtifactId",
            "Client.ViewModel.Contextes.HeroesInfoDialog.HeroDetails.HeroDetailsContext._activeTabIndex",
            "Client.ViewModel.Contextes.HeroesInfoDialog.SelectedHero.SelectedHeroContext._heroId",
        ];
        foreach (var n in needed)
        {
            var dot = n.LastIndexOf('.');
            Assert.True(ui["types"]?[n[..dot]]?["fields"]?[n[(dot + 1)..]] is not null, $"{n} missing from config/ui-offsets.json");
        }
        foreach (var e in new[] { "Client.ViewModel.DTO.ViewKey", "Client.RaidApp.ViewState", "Client.RaidApp.ViewVisibility" })
            Assert.NotNull(ui["enums"]?[e]);
        Assert.Equal(1018, (int)ui["enums"]!["Client.ViewModel.DTO.ViewKey"]!["HeroesInfoOverlay"]!);
    }
}

/// <summary>Recognition on raid-extractor's reference screenshots (skipped when they are not on this machine; never committed).</summary>
public class ReferenceScreenshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));
    private static string Reference => Path.Combine(AppPathsForTests.RaidExtractor, "output", "ui-reference");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Recognition Recognize(string file, out TemplateLibrary lib)
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Reference, file)), "raid-extractor's reference screenshots are not on this machine.");
        (lib, var missing) = TemplateLibrary.SeedFrom(Reference, _dir);
        Assert.Empty(missing);
        using var scene = Cv2.ImRead(Path.Combine(Reference, file), ImreadModes.Color);
        return new ScreenRecognizer(lib, UiLayout.Load(Path.Combine(AppPathsForTests.Config, "ui-layout.json"))).Recognize(scene);
    }

    [Fact]
    public void Gear_panel_slots_land_on_the_measured_boxes()
    {
        var r = Recognize("02-gear-panel-dorothy.png", out var lib);
        using (lib)
        {
            Assert.Equal("champion-screen", r.Screen);
            // Boxes measured independently on this screenshot (44-45 px squares found by edge detection).
            var measured = new Dictionary<string, Point> { ["Weapon"] = new(24, 59), ["Helmet"] = new(74, 59), ["Shield"] = new(124, 58), ["Boots"] = new(124, 109), ["Banner"] = new(122, 169) };
            foreach (var (slot, p) in measured)
            {
                Assert.InRange(r.GearSlots[slot].X, p.X - 2, p.X + 2);
                Assert.InRange(r.GearSlots[slot].Y, p.Y - 2, p.Y + 2);
            }
        }
    }

    [Fact]
    public void The_picker_and_the_piece_popup_are_seen_and_the_village_is_not_a_champion_screen()
    {
        var picker = Recognize("05-artifact-inventory-curing.png", out var lib);
        lib.Dispose();
        Assert.True(picker.ArtifactPickerOpen);
        Assert.True(picker.PiecePopupOpen);
        Assert.Equal("champion-screen", picker.Screen);

        var village = Recognize("09-village-dashboard-style.png", out lib);
        lib.Dispose();
        Assert.Equal("unknown", village.Screen);
        Assert.Empty(village.Anchors);

        // Another screenshot than the one the gear anchor was cut from.
        var stats = Recognize("10-total-stats-dorothy.png", out lib);
        lib.Dispose();
        Assert.Equal("champion-screen", stats.Screen);
        Assert.False(stats.PiecePopupOpen);
    }
}
