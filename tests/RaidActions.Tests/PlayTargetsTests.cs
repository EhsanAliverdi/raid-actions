using RaidActions.Game.Memory;

namespace RaidActions.Tests;

/// <summary>Auto Play's click targets: found by the context bound to each element, the right button inside a row, the facts next to them.</summary>
public class PlayTargetsTests
{
    private sealed class Objects : IGameObjects
    {
        public readonly Dictionary<ulong, Dictionary<string, object>> Fields = [];
        private ulong _next = 0x9000;

        public ulong Add(Dictionary<string, object> f)
        {
            Fields[_next += 0x10] = f;
            return _next;
        }

        public ulong Prop(object v) => Add(new() { ["_value"] = v });

        public UiContext Ctx(string cls, Dictionary<string, object>? f = null) => new(Add(f ?? []), cls);

        public UiContext Opponent(long id, string name, int power, int status = 0, bool playable = true) =>
            Ctx("Client.ViewModel.Contextes.ArenaDialog.CurrentOpponentContext", new()
            {
                ["_opponent"] = Add(new() { ["UserId"] = id, ["Name"] = name, ["TotalPower"] = power, ["Status"] = status }),
                ["_playable"] = Prop(playable), ["_onQuarantine"] = Prop(false), ["_teamPower"] = Prop($"{power:N0}"),
            });

        public ulong Root(string name, string expectedType) => throw new NotSupportedException();
        public string ClassName(ulong obj) => throw new NotSupportedException();
        public IReadOnlyList<string> ClassChain(ulong obj) => throw new NotSupportedException();
        public ulong Ref(ulong obj, string type, string member) => Fields[obj].TryGetValue(member, out var v) ? (ulong)v : 0;
        public int Int(ulong obj, string type, string member) => (int)Fields[obj][member];
        public long Long(ulong obj, string type, string member) => (long)Fields[obj][member];
        public bool Bool(ulong obj, string type, string member) => (bool)Fields[obj][member];
        public string? String(ulong obj, string type, string member) => Fields[obj].TryGetValue(member, out var v) ? v as string : null;
        public int? NullableInt(ulong obj, string type, string member) => null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => throw new NotSupportedException();
        public string? EnumName(string enumType, int value) => value == 3 ? "Token" : null;
    }

    private static int _index;

    private static UiNode N(string path, float y = 0, bool? interactable = null, UiContext? ctx = null, string? binding = null, bool? isOn = null) =>
        new(++_index, 0, path.Count(c => c == '/'), path[(path.LastIndexOf('/') + 1)..], path, true, true, new ClientRect(10, y, 80, 30), [], ctx, interactable,
            binding, new ClientRect(10, y, 80, 30), isOn);

    private static UiTree Tree(string view, params UiNode[] nodes) => new(view, nodes, 1280, 720, null, 1);

    [Fact]
    public void The_arena_board_gives_each_row_its_battle_button_and_reads_power_status_and_the_free_refresh()
    {
        var g = new Objects();
        var tree = Tree("ArenaDialog",
            N("Arena", ctx: g.Ctx("Client.ViewModel.Contextes.ArenaDialogContext", new() { ["_tabIndex"] = g.Prop(0) })),
            N("Arena/BattleTab", ctx: g.Ctx("Client.ViewModel.Contextes.ArenaDialog.ArenaBattleTabContext",
                new() { ["_freeRefreshAvailable"] = g.Prop(false), ["_isLoading"] = g.Prop(false), ["_freeRefreshTimeLeft"] = g.Prop("12:30") })),
            N("Arena/BattleTab/RefreshBlock/FreeResfreshAvailable_h/BTN_1", 50, interactable: true),
            N("Arena/BattleTab/RefreshBlock/FreeResfreshNotAvailable_h/BTN_1_MultiResources_1Row", 50, interactable: true),
            N("Arena/BattleTab/Rows/Row", 100, ctx: g.Opponent(4_000_000_123L, "Alpha", 85_000)),
            N("Arena/BattleTab/Rows/Row/AvatarButton", 100, interactable: true),
            N("Arena/BattleTab/Rows/Row/BattleButton", 100, interactable: true),
            N("Arena/BattleTab/Rows/Row", 200, ctx: g.Opponent(7L, "Beaten", 60_000, status: 1)),
            N("Arena/BattleTab/Rows/Row/AvatarButton", 200, interactable: true),
            N("Arena/BattleTab/Rows/Row/BattleButton", 200, interactable: true));

        var scan = PlayTargets.Scan(tree, g);

        var board = scan.Arena!;
        Assert.Equal(0, board.Tab);
        Assert.False(board.FreeRefreshAvailable);
        Assert.Equal("12:30", board.FreeRefreshText);
        Assert.Equal([4_000_000_123L, 7L], board.Rows.Select(r => r.UserId));
        var alpha = board.Rows[0];
        Assert.Equal(("Alpha", 85_000, 0, true), (alpha.Name, alpha.Power, alpha.Status, alpha.Playable)); // team power from the row text, not the account's
        Assert.Equal(1, board.Rows[1].Status);
        // The row's own Battle button, not its avatar (which opens the player's profile).
        Assert.EndsWith("/BattleButton", alpha.Battle!.Path);
        Assert.Null(alpha.Battle.NotClickable);
        Assert.Equal(4_000_000_123L, alpha.Battle.Key);
        var refresh = scan.Targets.Single(t => t.Kind == "arena-refresh");
        Assert.EndsWith("/FreeResfreshAvailable_h/BTN_1", refresh.Path);
        Assert.True(scan.Targets.Single(t => t.Path.EndsWith("BTN_1_MultiResources_1Row")).Forbidden); // the paid refresh (gems): never
        Assert.StartsWith("not free now", refresh.Note);
    }

    [Fact]
    public void A_row_with_two_buttons_that_both_say_battle_is_left_unclickable_with_the_candidates()
    {
        var g = new Objects();
        var tree = Tree("ArenaDialog",
            N("Row", 100, ctx: g.Opponent(5, "Twin", 50_000)),
            N("Row/StartBattle", 100, interactable: true),
            N("Row/QuickBattle", 100, interactable: true));
        var t = PlayTargets.Scan(tree, g).Targets.Single(x => x.Kind == "arena-opponent");
        Assert.NotNull(t.NotClickable);
        Assert.Contains("2 of 2 buttons match (StartBattle, QuickBattle)", t.Note);
    }

    [Fact]
    public void The_arena_map_s_classic_arena_is_the_item_the_map_keeps_as_arena1x1()
    {
        var g = new Objects();
        var tag = g.Ctx("Client.ViewModel.Contextes.ArenaMap.ArenaMapItemContext");
        var classic = g.Ctx("Client.ViewModel.Contextes.ArenaMap.ArenaMapItemContext");
        var tree = Tree("ArenaMap",
            N("Map", ctx: g.Ctx("Client.ViewModel.Contextes.ArenaMap.ArenaMapContext", new() { ["_arena1x1"] = classic.Address })),
            N("Map/Tag", 100, interactable: true, ctx: tag),
            N("Map/Classic", 200, interactable: true, ctx: classic));
        var t = PlayTargets.Scan(tree, g).Targets.Single(x => x.Kind == "arena-classic");
        Assert.Equal("Map/Classic", t.Path);
    }

    [Fact]
    public void Village_buttons_battle_auto_and_hud_counters_are_found_by_their_contexts()
    {
        var g = new Objects();
        var village = PlayTargets.Scan(Tree("VillageHUD",
            N("Hud/Battle", 10, interactable: true, ctx: g.Ctx("Client.ViewModel.Contextes.Village.HUD.BattleMapButtonContext")),
            N("Hud/Inbox", 60, interactable: true, ctx: g.Ctx("Client.ViewModel.Contextes.InboxButtonContext")),
            N("Hud/Tokens", 110, ctx: g.Ctx("Client.ViewModel.Contextes.Generic.UserResources.UserResourceContext",
                new() { ["_resource"] = 3, ["_value"] = g.Prop("7/10"), ["_incrementStatusText"] = g.Prop("05:12") }))), g);
        Assert.Contains(village.Targets, t => t.Kind == "village-battle" && t.Path == "Hud/Battle");
        Assert.Contains(village.Targets, t => t.Kind == "village-inbox" && t.Path == "Hud/Inbox");
        Assert.Equal(new HudResource("Token", "7/10", "05:12"), village.Hud.Single());

        var battle = PlayTargets.Scan(Tree("BattleHUD",
            N("Hud", ctx: g.Ctx("ECS.ViewModel.BattleHUDContext", new() { ["_isAutoEnabled"] = g.Prop(false) })),
            N("Hud/AutoButton", 10, interactable: true),
            N("Hud/AutoRepeatButton", 50, interactable: true),
            N("Hud/PauseButton", 90, interactable: true)), g);
        Assert.False(battle.AutoOn);
        Assert.EndsWith("/AutoButton", battle.Targets.Single(t => t.Kind == "battle-auto").Path);
    }

    [Theory]
    [InlineData("Arena Tokens", "x5 Arena Tokens for you", true)]
    [InlineData("Compensation", "Tokens", true)]
    [InlineData("Tag Arena Tokens", "", false)]
    [InlineData("Live Arena", "Live Arena Tokens", false)]
    [InlineData("Auto-Battle", "tickets", false)]
    [InlineData("Gems", "x100", false)]
    public void Inbox_arena_tokens_are_told_apart_from_other_tokens(string title, string message, bool tokens) =>
        Assert.Equal(tokens, PlayTargets.IsArenaTokens(new InboxEntry(title, message, null, true, null)));
}
