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

        public readonly Dictionary<ulong, List<ulong>> Lists = [];
        public Dictionary<ulong, ulong> Raw = [];

        public UiContext OpponentWithTeam(long id, string name, int power, params (int slot, int type, int level, int grade)[] heroes)
        {
            var list = Add([]);
            Lists[list] = [.. heroes.Select(h => Add(new() { ["Slot"] = h.slot, ["HeroTypeId"] = h.type, ["Level"] = h.level, ["Grade"] = h.grade, ["EmpowerLevel"] = 0 }))];
            var setup = Add([]);
            Raw[setup] = list;
            return Ctx("Client.ViewModel.Contextes.ArenaDialog.CurrentOpponentContext", new()
            {
                ["_opponent"] = Add(new() { ["UserId"] = id, ["Name"] = name, ["TotalPower"] = power, ["Status"] = 0, ["TeamSetup"] = setup }),
                ["_playable"] = Prop(true), ["_onQuarantine"] = Prop(false), ["_teamPower"] = Prop($"{power:N0}"),
            });
        }

        public ulong RefAt(ulong obj, int offset) => (offset == PlayTargets.TeamHeroesOffset || offset == PlayTargets.ResourcesRawValuesOffset) && Raw.TryGetValue(obj, out var v) ? v : 0;
        public int IntAt(ulong obj, int offset) => (int)Fields[obj]["__count"];

        public UiContext Opponent(long id, string name, int power, int status = 0, bool playable = true) =>
            Ctx("Client.ViewModel.Contextes.ArenaDialog.CurrentOpponentContext", new()
            {
                ["_opponent"] = Add(new() { ["UserId"] = id, ["Name"] = name, ["TotalPower"] = power, ["Status"] = status }),
                ["_playable"] = Prop(playable), ["_onQuarantine"] = Prop(false), ["_teamPower"] = Prop($"{power:N0}"),
            });

        public ulong Root(string name, string expectedType) => throw new NotSupportedException();
        public string ClassName(ulong obj) => Fields.TryGetValue(obj, out var f) && f.TryGetValue("__class", out var c) ? (string)c : throw new NotSupportedException();
        public IReadOnlyList<string> ClassChain(ulong obj) => throw new NotSupportedException();
        public ulong Ref(ulong obj, string type, string member) => Fields[obj].TryGetValue(member, out var v) ? (ulong)v : 0;
        public int Int(ulong obj, string type, string member) => (int)Fields[obj][member];
        public long Long(ulong obj, string type, string member) => (long)Fields[obj][member];
        public bool Bool(ulong obj, string type, string member) => (bool)Fields[obj][member];
        public string? String(ulong obj, string type, string member) => Fields[obj].TryGetValue(member, out var v) ? v as string : null;
        public int? NullableInt(ulong obj, string type, string member) => Fields.TryGetValue(obj, out var f) && f.TryGetValue(member, out var v) ? (int?)v : null;
        public IReadOnlyList<ulong> RefList(ulong list, int max) => Lists.TryGetValue(list, out var l) ? l : throw new NotSupportedException();
        public string? EnumName(string enumType, int value) => value == 3 ? "Token" : null;
    }

    private const string Gift = "Client.ViewModel.Contextes.Bank.Offers.Gift.GiftOfferContext";
    private const string Pkg = "Client.ViewModel.Contextes.Bank.PurchaseTypes.Implementation.";

    private static UiTree GiftTree(Objects g, string purchase, ulong offer)
    {
        var ctx = g.Ctx(Gift, new() { ["_purchaseMethod"] = g.Add(new() { ["__class"] = Pkg + purchase }), ["_offer"] = offer });
        return Tree("BankOverlay", N("GiftOffer", ctx: ctx), N("GiftOffer/Button", 50, interactable: true));
    }

    private static ulong Give(Objects g, int priceId, int? resources)
    {
        var price = resources is null ? 0UL : g.Add(new() { ["__class"] = "SharedModel.Meta.Account.Resources" });
        if (resources is not null) { g.Raw[price] = g.Add(new() { ["__count"] = resources.Value }); }
        var user = g.Add(new() { ["PriceId"] = priceId, ["Price"] = price });
        return g.Add(new() { ["__class"] = "Client.ViewModel.Contextes.Bank.Offers.ViewModelOffer.GiveOffer", ["_offer"] = user });
    }

    [Fact]
    public void A_gift_the_game_prices_at_nothing_is_a_shop_gift_target()
    {
        var g = new Objects();
        Assert.Contains(PlayTargets.Scan(GiftTree(g, "ResourcesPurchase", Give(g, -1, 0)), g).Targets, t => t.Kind == "shop-gift");
        Assert.Contains(PlayTargets.Scan(GiftTree(g, "ResourcesPurchase", Give(g, -1, null)), g).Targets, t => t.Kind == "shop-gift");
    }

    [Theory]
    [InlineData("BillingPurchase", -1, 0)]          // billed: never
    [InlineData("AllianceBossKeyResourcesPurchase", -1, 0)]
    [InlineData("ResourcesPurchase", 12, 0)]        // has a client price
    [InlineData("ResourcesPurchase", -1, 1)]        // costs resources (gems)
    public void A_gift_that_costs_anything_is_never_a_target(string purchase, int priceId, int resources)
    {
        var g = new Objects();
        Assert.DoesNotContain(PlayTargets.Scan(GiftTree(g, purchase, Give(g, priceId, resources)), g).Targets, t => t.Kind == "shop-gift");
    }

    [Fact]
    public void A_static_gift_is_free_only_with_no_price_id_and_an_empty_price()
    {
        ulong Static(Objects g, int? priceId, int count)
        {
            var price = g.Add(new() { ["__class"] = "SharedModel.Meta.Account.Resources" });
            g.Raw[price] = g.Add(new() { ["__count"] = count });
            var f = new Dictionary<string, object> { ["Price"] = price };
            if (priceId is { } p) f["PriceId"] = p;
            return g.Add(new() { ["__class"] = "Client.ViewModel.Contextes.Bank.Offers.ViewModelOffer.StaticOffer", ["_Offer_k__BackingField"] = g.Add(f) });
        }
        var g1 = new Objects();
        Assert.Contains(PlayTargets.Scan(GiftTree(g1, "ResourcesPurchase", Static(g1, null, 0)), g1).Targets, t => t.Kind == "shop-gift");
        var g2 = new Objects();
        Assert.DoesNotContain(PlayTargets.Scan(GiftTree(g2, "ResourcesPurchase", Static(g2, null, 1)), g2).Targets, t => t.Kind == "shop-gift");
        var g3 = new Objects();
        Assert.DoesNotContain(PlayTargets.Scan(GiftTree(g3, "ResourcesPurchase", Static(g3, 5, 0)), g3).Targets, t => t.Kind == "shop-gift");
    }

    private static int _index;

    private static UiNode N(string path, float y = 0, bool? interactable = null, UiContext? ctx = null, string? binding = null, bool? isOn = null) =>
        new(++_index, 0, path.Count(c => c == '/'), path[(path.LastIndexOf('/') + 1)..], path, true, true, new ClientRect(10, y, 80, 30), [], ctx, interactable,
            binding, new ClientRect(10, y, 80, 30), isOn);

    private static UiTree Tree(string view, params UiNode[] nodes) => new(view, nodes, 1280, 720, null, 1);

    [Fact]
    public void The_arena_row_reads_the_opponents_defence_heroes_when_the_game_holds_them()
    {
        var g = new Objects();
        var tree = Tree("ArenaDialog",
            N("Arena", ctx: g.Ctx("Client.ViewModel.Contextes.ArenaDialogContext", new() { ["_tabIndex"] = g.Prop(0) })),
            N("Arena/BattleTab/Rows/Row", 100, ctx: g.OpponentWithTeam(5L, "Foe", 90_000, (0, 1101, 60, 6), (1, 2202, 50, 5))),
            N("Arena/BattleTab/Rows/Row/BattleButton", 100, interactable: true),
            N("Arena/BattleTab/Rows/Row", 200, ctx: g.Opponent(6L, "NoTeam", 50_000)),
            N("Arena/BattleTab/Rows/Row/BattleButton", 200, interactable: true));

        var rows = PlayTargets.Scan(tree, g).Arena!.Rows;

        Assert.Equal([(0, 1101, 60, 6), (1, 2202, 50, 5)], rows[0].Team!.Select(h => (h.Slot, h.TypeId, h.Level, h.Grade)));
        Assert.Equal("team [1101 L60 G6, 2202 L50 G5]", rows[0].TeamText);
        Assert.Null(rows[1].Team); // the field is absent: no team, the row still reads
        Assert.Equal("team not read", rows[1].TeamText);
    }

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

    [Fact]
    public void The_loyalty_programs_give_a_collect_for_a_ready_day_and_never_a_paid_or_shared_button()
    {
        var g = new Objects();
        var day = "Client.ViewModel.Contextes.LoyaltyProgram.LoyaltyDayItemContext";
        var tree = Tree("DailyRewardProgramOverlay",
            N("Prog/Days/Day1", 100, interactable: true, ctx: g.Ctx(day)),
            N("Prog/Days/Day1/CollectButton", 100, interactable: true),
            N("Prog/Days/Day2", 140, interactable: true, ctx: g.Ctx(day)),
            N("Prog/Days/Day2/Lock", 140),
            N("Prog/Days/Day3", 180, interactable: true, ctx: g.Ctx(day)),
            N("Prog/Days/Day3/CollectAndShareButton", 180, interactable: true),
            N("Prog/BuyPremiumButton", 220, interactable: true),
            N("Prog/Footer/ClaimAllButton", 260, interactable: true));

        var claims = PlayTargets.Scan(tree, new Objects()).Targets.Where(t => t.Kind == "loyalty-claim").Select(t => t.Path).ToList();

        Assert.Contains("Prog/Days/Day1", claims);
        Assert.Contains("Prog/Footer/ClaimAllButton", claims);
        Assert.DoesNotContain("Prog/Days/Day2", claims);
        Assert.DoesNotContain("Prog/Days/Day3", claims);
        Assert.DoesNotContain("Prog/BuyPremiumButton", claims);
    }

    [Fact]
    public void Loyalty_targets_exist_only_on_the_loyalty_views() =>
        Assert.DoesNotContain(PlayTargets.Scan(Tree("ArenaDialog", N("A/CollectButton", 10, interactable: true)), new Objects()).Targets, t => t.Kind == "loyalty-claim");

    [Fact]
    public void The_Events_buttons_HasNew_node_is_a_red_dot()
    {
        Assert.True(PlayTargets.RedDot("HasNewEventsOrRewards_h"));
        Assert.True(PlayTargets.RedDot("IndicatorForLimitedButton"));
        Assert.False(PlayTargets.RedDot("SpiningProgressVisualizer"));
        Assert.False(PlayTargets.RedDot("NameLabel"));
    }

    [Fact]
    public void The_win_streak_overlays_register_button_is_a_target_and_the_dont_ask_toggle_is_not()
    {
        var g = new Objects();
        var tree = Tree("LiveArenaWinStreakRegisterOverlay",
            N("Overlay/RegisterButton", 10, interactable: true, ctx: g.Ctx("Client.ViewModel.Contextes.LiveArenaWinStreakRegisterOverlay.LiveArenaWinStreakRegisterButtonContext")),
            N("Overlay/DontAskAgain", 50, interactable: true));
        var targets = PlayTargets.Scan(tree, g).Targets;
        Assert.Single(targets, t => t.Kind == "live-register");
        Assert.Contains("live-register", PlayTargets.Kinds);
        Assert.DoesNotContain(targets, t => t.Kind == "live-register" && t.Path.Contains("DontAsk", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("LiveArenaWinStreakRegisterOverlay", 1, "register")]
    [InlineData("LiveArenaWinStreakRegisterOverlay", 0, "close")]
    [InlineData("SomeNewOverlay", 5, "close")]
    [InlineData("LiveArenaDraftDialog", 5, "none")]
    [InlineData("LiveArenaMatchMakingDialog", 5, "none")]
    [InlineData("ResourceRefillOverlay", 5, "none")]
    public void The_live_arena_flow_registers_with_a_ticket_and_closes_what_it_does_not_know(string view, double tickets, string want) =>
        Assert.Equal(want, PlayTargets.LiveOverlayAction(view, tickets));

    [Fact]
    public void A_locked_battle_menu_card_is_noted_locked()
    {
        var locked = PlayTargets.Scan(Tree("BattleModeSelectionDialog", N("D/Cards/CursedCity_h", 100, interactable: true, binding: "CursedCity"),
            N("D/Cards/CursedCity_h/Locked_h", 120)), new Objects()).Targets.First(t => t.Kind == "mode-cc");
        Assert.Equal("locked", locked.Note);
        var open = PlayTargets.Scan(Tree("BattleModeSelectionDialog", N("D/Cards/CursedCity_h", 100, interactable: true, binding: "CursedCity")), new Objects())
            .Targets.First(t => t.Kind == "mode-cc");
        Assert.NotEqual("locked", open.Note);
    }
}
