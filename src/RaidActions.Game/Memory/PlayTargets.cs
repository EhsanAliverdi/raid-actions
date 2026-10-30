using System.Text.RegularExpressions;

namespace RaidActions.Game.Memory;

/// <summary>An opponent row on the Classic Arena board, from its CurrentOpponentContext.</summary>
/// <param name="Status">ArenaOpponentStatus: 0 not fought, 1 defeated (beaten by us), 2 won (they beat us).</param>
public sealed record ArenaRow(long UserId, string? Name, int Power, int Status, bool Playable, bool Quarantined, string? TeamPowerText, UiTarget? Battle);

/// <param name="Tab">ArenaDialogContext._tabIndex: 0 the battle tab.</param>
public sealed record ArenaBoardRead(List<ArenaRow> Rows, bool? FreeRefreshAvailable, string? FreeRefreshText, bool? Loading, int? Tab);

/// <param name="ResultType">BattleResultType: 1 victory, 2 defeat, 3 draw.</param>
public sealed record ArenaResultRead(int? ResultType, int? EnemyPower, string? EnemyName);

/// <summary>A resource counter on a HUD (UserResourceContext): its resource, the text it shows and its "next one in" text.</summary>
public sealed record HudResource(string? Resource, string? Value, string? Timer);

/// <summary>An inbox entry (InboxItemContext) and its collect button.</summary>
public sealed record InboxEntry(string? Title, string? Message, string? Icon, bool? Valid, UiTarget? Collect);

/// <summary>A crypt on the Faction Wars map (FractionWarsRegionItemContext): the faction (HeroFraction id), its name, state and key count text.</summary>
public sealed record CryptRead(int FactionId, string? Name, int? State, string? Keys, string? OpenInfo, UiTarget? Open);

/// <summary>A stage in a crypt's list (StageContext): its game id, name ("Stage 11"), state and its button.</summary>
public sealed record StageRead(int Id, string? Name, int? State, int? ButtonState, UiTarget? Start);

/// <summary>A Cursed City level on the map (CursedCityMapItemContext): stage id, state (0 closed, 1 opened, 2 completed), type (0 common, 2 boss, 4 main boss).</summary>
public sealed record CcMapRead(int StageId, int? State, int? Type, int? District, string? Label, UiTarget? Open);

/// <summary>A champion in a team screen's list (SelectableHeroAvatarContext): its id, its state (0 idle, 1 selected, 2 disabled, 3+ locked), the target that picks it.</summary>
public sealed record TeamPickRead(int HeroId, int? State, UiTarget? Pick);

/// <summary>A PvE result screen (BattleFinishPveDialogContext): 1 victory, 2 defeat, 3 draw; stars; the stage.</summary>
public sealed record PveResultRead(int? ResultType, int? Stars, string? StageName, bool? NextStageAvailable);

/// <summary>A champion in Fast Rank Up's list (HeroRankUpItemContext): its id, whether it is ticked, the food the game picked for it.</summary>
public sealed record RankUpItemRead(int HeroId, bool? Active, List<int> FoodIds, UiTarget? Toggle);

/// <summary>What Auto Play reads on the screens it plays: click targets and the facts next to them.</summary>
public sealed record PlayScan(List<UiTarget> Targets, ArenaBoardRead? Arena, bool? AutoOn, ArenaResultRead? Result, List<HudResource> Hud,
    List<InboxEntry> Inbox, List<string> Notes, List<CryptRead>? Crypts = null, List<StageRead>? Stages = null, List<int>? TeamIds = null,
    PveResultRead? Pve = null, List<CcMapRead>? CcLevels = null, List<TeamPickRead>? Picks = null, int? CcDifficulty = null,
    List<RankUpItemRead>? RankUps = null, int? SelectedShard = null, string? ClanDamage = null)
{
    public static PlayScan Empty => new([], null, null, null, [], [], []);

    public PlayScan Merge(PlayScan o) => new([.. Targets, .. o.Targets], Arena ?? o.Arena, AutoOn ?? o.AutoOn, Result ?? o.Result, [.. Hud, .. o.Hud],
        [.. Inbox, .. o.Inbox], [.. Notes, .. o.Notes], [.. Crypts ?? [], .. o.Crypts ?? []], [.. Stages ?? [], .. o.Stages ?? []],
        [.. TeamIds ?? [], .. o.TeamIds ?? []], Pve ?? o.Pve, [.. CcLevels ?? [], .. o.CcLevels ?? []], [.. Picks ?? [], .. o.Picks ?? []], CcDifficulty ?? o.CcDifficulty,
        [.. RankUps ?? [], .. o.RankUps ?? []], SelectedShard ?? o.SelectedShard, ClanDamage ?? o.ClanDamage);
}

/// <summary>
/// Auto Play's click targets (plan/autoplay.md), found like the gear targets by the game's own contexts, never by position: the
/// village Battle and Inbox buttons are the elements bound to BattleMapButtonContext and InboxButtonContext, the battle menu's Arena
/// is bound to ArenaModesContainerContext, the arena map's Classic Arena is the ArenaMapItemContext that ArenaMapContext keeps as
/// _arena1x1, an opponent row is bound to CurrentOpponentContext (its _opponent names the player). Where a context holds several
/// buttons the one whose name says what it does is taken (Battle/Start/Fight, Refresh, Collect/Claim, Auto); when that is not
/// exactly one, the target is left unclickable with a note listing the candidates (autoplay-explore shows it; fix the rule here).
/// Found offline in the dump (2026-10-29); every rule is to be checked on the first live session.
/// </summary>
public static partial class PlayTargets
{
    public const string ArenaDialogView = "ArenaDialog", ArenaMapView = "ArenaMap", ArenaHudView = "ArenaHUD", TeamView = "HeroesSelectionDialogToArena",
        BattleHudView = "BattleHUD", ArenaResultView = "BattleFinishArenaDialog", InboxView = "InboxDialog", BattleMenuView = "BattleModeSelectionDialog",
        VillageHudView = "VillageHUD", RefillView = "ResourceRefillOverlay", FwMapView = "FractionWarsMap", FwDialogView = "FractionWarsDialog",
        FwTeamView = "HeroesSelectionDialogToFractionWars", FwResultView = "BattleFinishFractionWarsDialog",
        CcMapView = "CursedCityMap", CcTeamView = "CursedCityHeroesSelectionDialog", CcResultView = "BattleFinishCursedCityDialog",
        CcHudView = "CursedCityHUD",
        // The dungeon's result (from BattleFinishDungeonDialogContext's name, as the other results; to confirm live).
        DungeonResultView = "BattleFinishDungeonDialog",
        // The dungeon map (the 3D scene under DungeonsHUD; seen live 2026-10-30).
        DungeonsMapView = "DungeonsMap",
        // A dungeon's stage list (seen live 2026-10-30).
        DungeonsDialogView = "DungeonsDialog",
        // Campaign (plan/campaign.md; from the dump 2026-10-30, names to confirm live): the stage's result, the Tavern, its Fast Rank Up.
        // The campaign map: the 3D scene under MapHUD (seen live 2026-10-30).
        CampaignMapView = "Map",
        // Clan Boss (plan/clan-boss.md; from the dump 2026-10-31, names to confirm live).
        ClanMapView = "AllianceActivityMap", ClanDialogView = "AllianceBossesDialog", ClanResultView = "BattleFinishAllianceBossDialog",
        StoryResultView = "BattleFinishStoryDialog", TavernView = "HeroesDevelopmentDialog", FastRankUpView = "FastRankUpOverlay";

    /// <summary>A team screen (the views where a squad is picked before a battle).</summary>
    public static bool IsTeamView(string view) => view.StartsWith("HeroesSelectionDialog", StringComparison.Ordinal) || view.EndsWith("HeroesSelectionDialog", StringComparison.Ordinal);

    private const string BattleMapButton = "Client.ViewModel.Contextes.Village.HUD.BattleMapButtonContext";
    private const string InboxButton = "Client.ViewModel.Contextes.InboxButtonContext";
    private const string ArenaModes = "Client.ViewModel.Contextes.BattleModeSelectionDialog.ArenaModesContainerContext";
    private const string ArenaMap = "Client.ViewModel.Contextes.ArenaMap.ArenaMapContext";
    private const string ArenaMapItem = "Client.ViewModel.Contextes.ArenaMap.ArenaMapItemContext";
    private const string ArenaDialog = "Client.ViewModel.Contextes.ArenaDialogContext";
    private const string BattleTab = "Client.ViewModel.Contextes.ArenaDialog.ArenaBattleTabContext";
    private const string CurrentOpponent = "Client.ViewModel.Contextes.ArenaDialog.CurrentOpponentContext";
    private const string Opponent = "SharedModel.Meta.Battle.Arena.ArenaOpponent";
    private const string BattleHud = "ECS.ViewModel.BattleHUDContext";
    private const string ArenaResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishArenaDialogContext";
    private const string UserResource = "Client.ViewModel.Contextes.Generic.UserResources.UserResourceContext";
    private const string InboxItem = "Client.ViewModel.Contextes.InboxDialog.Contexts.InboxItemContext";
    private const string ResourceEnum = "SharedModel.Meta.Account.ResourceTypeId";
    private const string FwRegionItem = "Client.ViewModel.Contextes.FractionWars.FractionWarsRegionItemContext";
    private const string FwStage = "Client.ViewModel.Contextes.FractionWars.FactionWarsStageContext";
    // Dungeons (plan/energy.md; from the dump 2026-10-30, not seen live yet).
    private const string DungeonRegionItem = "Client.ViewModel.Contextes.DungeonMap.DungeonRegionItemContext";
    private const string DungeonStage = "Client.ViewModel.Contextes.RegionDialog.DungeonStageContext";
    private const string DungeonResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishDungeonDialogContext";
    // The PvE team screen's Super Raid box (seen live 2026-10-30: "Checkbox" under the 'MultiRun' block, no Toggle; its tick fades, so
    // its state is read from MultiRunContext._active, which the team context keeps as MultiRun).
    private const string PveTeam = "Client.ViewModel.Contextes.HeroesSelectionDialog.HeroesSelectionPveDialogContext";
    private const string MultiRunCtx = "Client.ViewModel.Contextes.MultiRun.MultiRunContext";
    private const string StageCtx = "Client.ViewModel.Contextes.RegionDialog.StageContext";
    private const string FwResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishFractionWarsDialogContext";
    private const string PveResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishPveDialogContext";
    private const string HeroAvatar = "Client.ViewModel.Contextes.Generic.HeroAvatarContext";
    private const string SelectionSlot = "Client.ViewModel.Contextes.HeroesSelectionDialog.HeroSlotContext";
    private const string SelectableAvatar = "Client.ViewModel.Contextes.HeroesSelectionDialog.SelectableHeroAvatarContext";
    private const string CcMapItem = "Client.ViewModel.Contextes.CursedCity.Map.Items.CursedCityMapItemContext";
    private const string CcMap = "Client.ViewModel.Contextes.CursedCity.Map.CursedCityMapContext";
    private const string CcResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishCursedCityDialogContext";
    // Campaign (plan/campaign.md; from the dump 2026-10-30, not seen live yet).
    private const string MapRegionItem = "Client.ViewModel.Contextes.MapRegionItemContext";
    private const string StoryStage = "Client.ViewModel.Contextes.RegionDialog.StorylineStageContext";
    private const string StoryRegionDialog = "Client.ViewModel.Contextes.RegionDialog.StorylineRegionDialogContext";
    private const string StoryResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishStorylineDialogContext";
    private const string TavernBuilding = "Client.ViewModel.Contextes.Village.TavernBuildingContext";
    // The champion screen's Tavern button (the village's Tavern is a 3D building with no screen rect; seen 2026-10-30).
    private const string TavernButton = "Client.ViewModel.Contextes.HeroesInfoDialog.SelectedHero.TavernButtonContext";
    private const string FastRankUp = "Client.ViewModel.Contextes.FastRankUpOverlay.FastRankUpOverlayContext";
    private const string RankUpGrade = "Client.ViewModel.Contextes.FastRankUpOverlay.GradeItemContext";
    private const string RankUpItem = "Client.ViewModel.Contextes.FastRankUpOverlay.HeroRankUpItemContext";
    private const string RankUpFood = "Client.ViewModel.Contextes.FastRankUpOverlay.FoodItemContext";
    private const string ShardItem = "Client.ViewModel.Contextes.PortalDialog.Summon.ShardItemContext";
    private const string SummonTab = "Client.ViewModel.Contextes.PortalDialog.Summon.SummonTabContext";
    // Clan Boss (plan/clan-boss.md; from the dump 2026-10-31).
    private const string ClanMap = "Client.ViewModel.Contextes.AllianceActivityMap.AllianceActivityMapContext";
    private const string ClanMapItem = "Client.ViewModel.Contextes.AllianceActivityMap.AllianceEnemyMapItemContext";
    private const string ClanEnemy = "Client.ViewModel.Contextes.AllianceEnemies.EnemyItem.AllianceEnemyItemContext";
    private const string ClanResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishAllianceBossDialogContext";
    private const string ClanEnemyResult = "Client.ViewModel.Contextes.BattleFinishDialog.BattleFinishAllianceEnemyDialogContext";
    private const string IntProperty = "Client.MVVM.Base.ViewModel.Properties.IntProperty";
    private const string BoolProperty = "Client.MVVM.Base.ViewModel.Properties.BoolProperty";
    private const string StringProperty = "Client.MVVM.Base.ViewModel.Properties.StringProperty";

    /// <summary>The kinds Auto Play may click (each flow narrows it further; the spending ones only in a real run).</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>
    {
        "village-battle", "village-inbox", "mode-arena", "arena-classic", "arena-tab", "arena-opponent", "arena-refresh", "team-start",
        "battle-auto", "battle-speed", "inbox-collect", "result-close", "mode-fw", "fw-crypt", "fw-stage", "mode-cc", "cc-level", "team-pick", "team-slot",
        "cc-difficulty", "cc-difficulty-item", "team-filter", "team-filter-vault", "filter-hide",
        "mode-dungeon", "dungeon-region", "dungeon-stage", "dungeon-difficulty", "dungeon-dropdown", "super-raid",
        "mode-campaign", "campaign-region", "village-tavern", "village-heroes", "champion-tavern", "tavern-rankup-tab", "tavern-fast-rankup", "fastrankup-grade", "fastrankup-item",
        "fastrankup-go", "fastrankup-filter", "food-filter-apply", "village-portal", "portal-shard", "portal-summon1",
        "mode-clan", "clan-demonlord", "clan-difficulty", "clan-battle",
    };

    [GeneratedRegex(@"Vault|Storage", RegexOptions.IgnoreCase)] private static partial Regex VaultName();
    [GeneratedRegex(@"SuperRaid|DoubleRun|MultiRun", RegexOptions.IgnoreCase)] private static partial Regex SuperRaidName();
    [GeneratedRegex(@"Normal|Hard|Brutal|Nightmare", RegexOptions.IgnoreCase)] private static partial Regex DifficultyName();

    [GeneratedRegex(@"^Item (\d+): ?(.*)$")] private static partial Regex DropdownItem();
    [GeneratedRegex(@"^Region(\d+)$")] private static partial Regex ChapterPoint();

    [GeneratedRegex(@"Battle|Start|Fight|Attack", RegexOptions.IgnoreCase)] private static partial Regex FightName();
    [GeneratedRegex(@"Info|Avatar|Profile|Hero|Skin|Preset|Auto|Relic|Filter|Sort", RegexOptions.IgnoreCase)] private static partial Regex NotFightName();
    [GeneratedRegex(@"Refresh|Update|Reload", RegexOptions.IgnoreCase)] private static partial Regex RefreshName();
    [GeneratedRegex(@"Collect|Claim|Take|Get|Receive|Accept", RegexOptions.IgnoreCase)] private static partial Regex CollectName();
    [GeneratedRegex(@"Auto", RegexOptions.IgnoreCase)] private static partial Regex AutoName();
    [GeneratedRegex(@"Speed", RegexOptions.IgnoreCase)] private static partial Regex SpeedName();
    [GeneratedRegex(@"RankUp|Rank_Up|Upgrade|Confirm", RegexOptions.IgnoreCase)] private static partial Regex RankUpName();
    [GeneratedRegex(@"Fast|Filter|Info|Faq|Close|Back|Help|Grade|Hero|Food|Avatar", RegexOptions.IgnoreCase)] private static partial Regex NotRankUpName();
    [GeneratedRegex(@"Single|Summon_?1|x1\b|One|Summon$|SummonButton", RegexOptions.IgnoreCase)] private static partial Regex SummonOneName();
    [GeneratedRegex(@"Multi|x10|10|Drop|Rate|Info|Dust|Event|Shard", RegexOptions.IgnoreCase)] private static partial Regex NotSummonOneName();
    [GeneratedRegex(@"Repeat|Multi|Climb|Settings", RegexOptions.IgnoreCase)] private static partial Regex NotAutoName();

    public static PlayScan Scan(UiTree t, IGameObjects g)
    {
        var nodes = t.Nodes;
        var targets = new List<UiTarget>();
        var notes = new List<string>();
        ArenaBoardRead? arena = null;
        bool? auto = null;
        ArenaResultRead? result = null;
        var hud = new List<HudResource>();
        var inbox = new List<InboxEntry>();
        List<CryptRead>? crypts = null;
        List<StageRead>? stages = null;
        List<int>? team = null;
        List<CcMapRead>? ccLevels = null;
        List<TeamPickRead>? picks = null;
        PveResultRead? pve = null;
        int? ccDifficulty = null;
        List<RankUpItemRead>? rankUps = null;
        int? selectedShard = null;
        string? clanDamage = null;

        IEnumerable<UiNode> Below(int i)
        {
            for (var k = i + 1; k < nodes.Count && nodes[k].Depth > nodes[i].Depth; k++) yield return nodes[k];
        }
        IEnumerable<UiNode> SelfAndBelow(int i) => new[] { nodes[i] }.Concat(Below(i));

        // Whether an ancestor of nodes[i] is bound to this path (the nodes before i that are shallower, nearest first).
        bool UnderBinding(int i, string binding)
        {
            var depth = nodes[i].Depth;
            for (var k = i - 1; k >= 0 && depth > 0; k--)
                if (nodes[k].Depth < depth)
                {
                    if (nodes[k].BindingPath == binding) return true;
                    depth = nodes[k].Depth;
                }
            return false;
        }

        // The one clickable element of a context (its own Button, else the first below), as the gear targets take it.
        UiNode? Own(int i) => SelfAndBelow(i).FirstOrDefault(c => c.Interactable is not null && c.ActiveInHierarchy) ?? SelfAndBelow(i).FirstOrDefault(c => c.Interactable is not null);

        // The button among several whose name (or binding) says what it does; null with a note when that is not exactly one.
        (UiNode? Button, string? Note) Named(int i, Regex want, Regex? avoid)
        {
            var all = SelfAndBelow(i).Where(c => c.Interactable is not null && c.ActiveInHierarchy).ToList();
            bool Says(UiNode c, Regex r) => r.IsMatch(c.Name) || c.BindingPath is { } b && r.IsMatch(b);
            var named = all.Where(c => Says(c, want) && (avoid is null || !Says(c, avoid))).ToList();
            if (named.Count == 1) return (named[0], null);
            if (named.Count == 0 && all.Count == 1) return (all[0], "the only button there");
            return (null, $"{named.Count} of {all.Count} buttons match ({string.Join(", ", all.Select(c => c.Name + (c.BindingPath is { } b ? $" [{b}]" : "")).Distinct().Take(10))})");
        }

        UiTarget Make(string kind, string name, UiNode owner, UiNode? button, string? note, int? id = null, long? key = null)
        {
            var b = button ?? owner;
            return new UiTarget(kind, name, id, t.View, b.Screen, owner.ActiveInHierarchy && b.ActiveInHierarchy, button?.Interactable, b.FullyVisible,
                Forbidden: false, b.Path, note, ClientWidth: t.ClientWidth, ClientHeight: t.ClientHeight, Key: key);
        }

        // Pass 1: contexts that point at other elements (the arena map's classic arena item, the alliance map's Demon Lord).
        ulong classic = 0, demonLord = 0;
        foreach (var n in nodes.Where(n => n.Context?.Class == ClanMap))
            demonLord = Try(() => g.Ref(n.Context!.Address, ClanMap, "_demonLord"), 0UL, notes, "AllianceActivityMapContext._demonLord");
        foreach (var n in nodes.Where(n => n.Context?.Class == ArenaMap))
            classic = Try(() => g.Ref(n.Context!.Address, ArenaMap, "_arena1x1"), 0UL, notes, "ArenaMapContext._arena1x1");

        List<ArenaRow>? rows = null;
        bool? free = null, loading = null;
        string? freeText = null;
        int? tab = null;
        // Cursed City's difficulty dropdown (bottom left of the map's HUD; seen 2026-10-29 as "Dropdown", no context) and, while it is
        // open, its items (Unity names them "Item <index>: <text>").
        // A team screen's champion filter (HeroFilterButton, binding 'Filter'; Ehsan 2026-10-30: its "include Master / Reserve Vault"
        // boxes make the list far longer) and, while its panel is open, the vault toggles (on or off in the note).
        for (var i = 0; i < nodes.Count; i++)
        {
            var x = nodes[i];
            if (PlayTargets.IsTeamView(t.View) && x.Name == "HeroFilterButton" && x.Screen is not null)
                targets.Add(Make("team-filter", "team screen: champion filter", x, x, null));
            else if (x.IsOn is not null && x.ActiveInHierarchy && x.Screen is not null && VaultName().IsMatch(x.Path)
                && x.Path.Contains("Filter", StringComparison.OrdinalIgnoreCase) && !x.Path.Contains("Avatar", StringComparison.OrdinalIgnoreCase))
                targets.Add(Make("team-filter-vault", $"filter: {x.Name} ({x.Path})", x, x, x.IsOn == true ? "on" : "off"));
        }
        // A team screen's Super Raid box (the game's DoubleRun; the FW team screen shows "SUPER RAIDS" with a check box; names to confirm
        // live) and a dungeon's Normal / Hard switch: toggles, on or off in the note.
        for (var i = 0; i < nodes.Count; i++)
        {
            var x = nodes[i];
            if (x.IsOn is null || !x.ActiveInHierarchy || x.Screen is null) continue;
            if (PlayTargets.IsTeamView(t.View) && SuperRaidName().IsMatch(x.Path))
                targets.Add(Make("super-raid", $"team screen: Super Raid ({x.Name})", x, x, x.IsOn == true ? "on" : "off"));
            else if (t.View.Contains("Dungeon", StringComparison.Ordinal) && DifficultyName().Match(x.Path) is { Success: true } dm)
                targets.Add(Make("dungeon-difficulty", $"dungeon: {dm.Value} ({x.Name})", x, x, $"{dm.Value} {(x.IsOn == true ? "on" : "off")}"));
        }
        // A Campaign chapter's stage list (StorylineRegionDialogContext; its difficulty is a Dropdown like the dungeons').
        var storyDialog = nodes.Any(x => x.Context?.Class == StoryRegionDialog);
        // The open list may be built outside the HUD's view (Unity puts it under the canvas): its items are looked for in every view.
        for (var i = 0; i < nodes.Count; i++)
            {
                if (t.View == PlayTargets.CcHudView && nodes[i].Name == "Dropdown" && nodes[i].Screen is not null)
                    targets.Add(Make("cc-difficulty", "cursed city: difficulty dropdown", nodes[i], nodes[i], null));
                // A dungeon's difficulty dropdown (bottom left of DungeonsDialog, under the set list; seen live 2026-10-30); its open
                // items are the same "Item <index>: <text>" kind as Cursed City's.
                else if ((t.View is DungeonsDialogView or "MapHUD" || storyDialog) && nodes[i].Name == "Dropdown" && nodes[i].Screen is not null)
                    targets.Add(Make("dungeon-dropdown", "dungeon: difficulty dropdown", nodes[i], nodes[i], null));
                else if (DropdownItem().Match(nodes[i].Name) is { Success: true } m && nodes[i].Screen is not null)
                    targets.Add(Make("cc-difficulty-item", $"cursed city: difficulty {m.Groups[2].Value}", nodes[i], nodes[i], m.Groups[2].Value,
                        id: int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var c = n.Context;
            if (c is null) continue;
            switch (c.Class)
            {
                case CcMap:
                    // DifficultyId? (1 Normal, 2 Hard, 3 Brutal): the difficulty the map shows.
                    ccDifficulty = Try(() => g.NullableInt(c.Address, CcMap, "_currentDifficultly"), null, notes, "CursedCityMapContext._currentDifficultly");
                    break;
                case BattleMapButton:
                    targets.Add(Make("village-battle", "village: Battle", n, Own(i), null));
                    break;
                case InboxButton:
                    targets.Add(Make("village-inbox", "village: Inbox", n, Own(i), null));
                    break;
                case ArenaModes:
                    targets.Add(Make("mode-arena", "battle menu: Arena", n, Own(i), null));
                    break;
                case ArenaMapItem when classic != 0 && c.Address == classic:
                    targets.Add(Make("arena-classic", "arena map: Classic Arena", n, Own(i), null));
                    break;
                case ArenaDialog:
                    tab = Try(() => IntValue(g, g.Ref(c.Address, ArenaDialog, "_tabIndex")), null, notes, "ArenaDialogContext._tabIndex");
                    break;
                case BattleTab:
                {
                    free = Try(() => BoolValue(g, g.Ref(c.Address, BattleTab, "_freeRefreshAvailable")), null, notes, "ArenaBattleTabContext._freeRefreshAvailable");
                    loading = Try(() => BoolValue(g, g.Ref(c.Address, BattleTab, "_isLoading")), null, notes, "ArenaBattleTabContext._isLoading");
                    freeText = Try(() => StringValue(g, g.Ref(c.Address, BattleTab, "_freeRefreshTimeLeft")), null, notes, "ArenaBattleTabContext._freeRefreshTimeLeft");
                    // The refresh (seen 2026-10-29): RefreshBlock/FreeResfreshAvailable_h/BTN_1 is the free one (the game's spelling);
                    // RefreshBlock/FreeResfreshNotAvailable_h/BTN_1_MultiResources_1Row is the paid one (gems): never clicked.
                    var freeButtons = SelfAndBelow(i).Where(x => x.Interactable is not null && x.ActiveInHierarchy && x.Name == "BTN_1"
                        && x.Path.Contains("/FreeResfreshAvailable_h/", StringComparison.Ordinal)).ToList();
                    targets.Add(Make("arena-refresh", "arena: Refresh (free)", n, freeButtons.Count == 1 ? freeButtons[0] : null,
                        (free == true ? "free now" : "not free now") + (freeButtons.Count == 1 ? "" : $"; {freeButtons.Count} free refresh buttons shown")));
                    foreach (var paid in SelfAndBelow(i).Where(x => x.Interactable is not null && x.Path.Contains("/FreeResfreshNotAvailable_h/", StringComparison.Ordinal)))
                        targets.Add(Make("forbidden", "arena: paid refresh (gems, never)", paid, paid, null) with { Forbidden = true });
                    rows ??= [];
                    break;
                }
                case CurrentOpponent:
                {
                    var o = Try(() => g.Ref(c.Address, CurrentOpponent, "_opponent"), 0UL, notes, "CurrentOpponentContext._opponent");
                    if (o == 0) break; // an empty pooled row
                    var id = Try(() => g.Long(o, Opponent, "UserId"), 0L, notes, "ArenaOpponent.UserId");
                    if (id == 0) break;
                    var name = Try(() => g.String(o, Opponent, "Name"), null, notes, "ArenaOpponent.Name");
                    // The arena team's power is the row's text ("198.04K"); ArenaOpponent.TotalPower is the whole account (seen 2026-10-29).
                    var accountPower = Try(() => g.Int(o, Opponent, "TotalPower"), 0, notes, "ArenaOpponent.TotalPower");
                    var teamText = Try(() => StringValue(g, g.Ref(c.Address, CurrentOpponent, "_teamPower")), null, notes, "CurrentOpponentContext._teamPower");
                    var power = RaidExtractor.Autoplay.GameText.Power(teamText) is { } tp ? (int)tp : 0;
                    var status = Try(() => g.Int(o, Opponent, "Status"), -1, notes, "ArenaOpponent.Status");
                    var playable = Try(() => BoolValue(g, g.Ref(c.Address, CurrentOpponent, "_playable")), null, notes, "CurrentOpponentContext._playable") == true;
                    var quarantine = Try(() => BoolValue(g, g.Ref(c.Address, CurrentOpponent, "_onQuarantine")), null, notes, "CurrentOpponentContext._onQuarantine") == true;
                    var (button, why) = Named(i, FightName(), NotFightName());
                    var target = Make("arena-opponent", $"arena: Battle {name ?? "?"} ({power:N0})", n, button,
                        $"user {id}, team power {power:N0} (account {accountPower:N0}), status {status}, {(playable ? "playable" : "not playable")}" + (why is null ? "" : "; " + why), key: id);
                    targets.Add(target);
                    rows ??= [];
                    rows.Add(new ArenaRow(id, name, power, status, playable, quarantine, teamText, target));
                    break;
                }
                case FwRegionItem:
                {
                    var id = Try(() => IntValue(g, g.Ref(c.Address, FwRegionItem, "_id")), null, notes, "FractionWarsRegionItemContext._id");
                    if (id is not { } fid) break;
                    var name = Try(() => StringValue(g, g.Ref(c.Address, FwRegionItem, "_name")), null, notes, "FractionWarsRegionItemContext._name");
                    var state = Try(() => IntValue(g, g.Ref(c.Address, FwRegionItem, "_regionState")), null, notes, "FractionWarsRegionItemContext._regionState");
                    var keys = Try(() => StringValue(g, g.Ref(c.Address, FwRegionItem, "_resourceCount")), null, notes, "FractionWarsRegionItemContext._resourceCount");
                    var info = Try(() => StringValue(g, g.Ref(c.Address, FwRegionItem, "_openInfo")), null, notes, "FractionWarsRegionItemContext._openInfo");
                    var target = Make("fw-crypt", $"faction wars map: {name ?? fid.ToString(System.Globalization.CultureInfo.InvariantCulture)}", n, Own(i),
                        $"faction {fid} ({g.EnumName("SharedModel.Meta.Heroes.HeroFraction", fid)}), state {state}, keys \"{keys}\", \"{info}\"", id: fid);
                    targets.Add(target);
                    (crypts ??= []).Add(new CryptRead(fid, name, state, keys, info, target));
                    break;
                }
                case FwStage:
                {
                    var id = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_id")), null, notes, "StageContext._id");
                    if (id is not { } sid || sid <= 0) break;
                    var name = Try(() => StringValue(g, g.Ref(c.Address, StageCtx, "_name")), null, notes, "StageContext._name");
                    var state = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_state")), null, notes, "StageContext._state");
                    var buttonState = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_buttonState")), null, notes, "StageContext._buttonState");
                    var (button, why) = Named(i, FightName(), NotFightName());
                    var target = Make("fw-stage", $"crypt: {name ?? $"stage {sid}"}", n, button, $"stage id {sid}, state {state}, button state {buttonState}" + (why is null ? "" : "; " + why), id: sid);
                    targets.Add(target);
                    (stages ??= []).Add(new StageRead(sid, name, state, buttonState, target));
                    break;
                }
                case DungeonRegionItem:
                {
                    // A dungeon's tile on the dungeon map: the region it opens (RegionTypeId, e.g. 206 Dragon's Lair) and its name.
                    var rid = Try(() => (int?)g.Int(c.Address, DungeonRegionItem, "_setRegionTypeId"), null, notes, "DungeonRegionItemContext._setRegionTypeId");
                    if (rid is not > 0) break;
                    var name = Try(() => StringValue(g, g.Ref(c.Address, DungeonRegionItem, "_regionName")), null, notes, "DungeonRegionItemContext._regionName");
                    var state = Try(() => IntValue(g, g.Ref(c.Address, DungeonRegionItem, "_regionState")), null, notes, "DungeonRegionItemContext._regionState");
                    targets.Add(Make("dungeon-region", $"dungeons: {name ?? g.EnumName("SharedModel.Meta.Stages.RegionTypeId", rid.Value)}", n, Own(i),
                        $"region {rid} ({g.EnumName("SharedModel.Meta.Stages.RegionTypeId", rid.Value)}), state {state}", id: rid));
                    break;
                }
                case DungeonStage:
                {
                    // A dungeon's stage row: a StageContext like Faction Wars' (id, name, state, its battle button).
                    var id = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_id")), null, notes, "StageContext._id");
                    if (id is not { } sid || sid <= 0) break;
                    var name = Try(() => StringValue(g, g.Ref(c.Address, StageCtx, "_name")), null, notes, "StageContext._name");
                    var state = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_state")), null, notes, "StageContext._state");
                    var buttonState = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_buttonState")), null, notes, "StageContext._buttonState");
                    var (button, why) = Named(i, FightName(), NotFightName());
                    var target = Make("dungeon-stage", $"dungeon: {name ?? $"stage {sid}"}", n, button, $"stage id {sid}, state {state}, button state {buttonState}" + (why is null ? "" : "; " + why), id: sid);
                    targets.Add(target);
                    (stages ??= []).Add(new StageRead(sid, name, state, buttonState, target));
                    break;
                }
                case ClanMapItem when demonLord != 0 && c.Address == demonLord:
                    targets.Add(Make("clan-demonlord", "clan: Demon Lord", n, Own(i), null));
                    break;
                case ClanEnemy:
                {
                    // A Clan Boss difficulty in the Demon Lord's list (its name, unlocked, selected, its stage id; enemy type 1 the Clan Boss).
                    var name = Try(() => StringValue(g, g.Ref(c.Address, ClanEnemy, "_name")), null, notes, "AllianceEnemyItemContext._name");
                    var unlocked = Try(() => BoolValue(g, g.Ref(c.Address, ClanEnemy, "_unlocked")), null, notes, "AllianceEnemyItemContext._unlocked");
                    var selected = Try(() => BoolValue(g, g.Ref(c.Address, ClanEnemy, "_selected")), null, notes, "AllianceEnemyItemContext._selected");
                    var stage = Try(() => (int?)g.Int(c.Address, ClanEnemy, "_StageId_k__BackingField"), null, notes, "AllianceEnemyItemContext._StageId");
                    var type = Try(() => IntValue(g, g.Ref(c.Address, ClanEnemy, "_enemyType")), null, notes, "AllianceEnemyItemContext._enemyType");
                    // The row is selected by a click on the row itself (its only button, BTN_1, is the reward list; seen 2026-10-31): the
                    // target is the row's left part, clear of that button.
                    if (n.Screen is not { } row) break;
                    var inside = row.Y >= 60 && row.Y + row.Height <= t.ClientHeight - 10;
                    targets.Add(new UiTarget("clan-difficulty", $"clan boss: {name ?? $"stage {stage}"}", stage, t.View,
                        new ClientRect(row.X + row.Width * 0.1f, row.Y + row.Height * 0.25f, row.Width * 0.5f, row.Height * 0.5f), n.ActiveInHierarchy, true, inside, false, n.Path,
                        $"{name}; {(selected == true ? "selected" : "not selected")}; {(unlocked == false ? "locked" : "unlocked")}; type {type}",
                        ClientWidth: t.ClientWidth, ClientHeight: t.ClientHeight));
                    break;
                }
                case ClanResult:
                    clanDamage = Try(() => StringValue(g, g.Ref(c.Address, ClanEnemyResult, "_damageDealt")), null, notes, "BattleFinishAllianceEnemyDialogContext._damageDealt");
                    break;
                case MapRegionItem:
                {
                    // A Campaign chapter on the campaign map (RegionTypeId 101..112 = Region1..Region12).
                    var rid = Try(() => (int?)g.Int(c.Address, MapRegionItem, "_regionType"), null, notes, "MapRegionItemContext._regionType");
                    if (rid is not > 0) break;
                    var name = Try(() => StringValue(g, g.Ref(c.Address, MapRegionItem, "_regionName")), null, notes, "MapRegionItemContext._regionName");
                    var locked = Try(() => BoolValue(g, g.Ref(c.Address, MapRegionItem, "_isLocked")), null, notes, "MapRegionItemContext._isLocked");
                    targets.Add(Make("campaign-region", $"campaign: {name ?? g.EnumName("SharedModel.Meta.Stages.RegionTypeId", rid.Value)}", n, Own(i),
                        $"region {rid} ({g.EnumName("SharedModel.Meta.Stages.RegionTypeId", rid.Value)}){(locked == true ? ", locked" : "")}", id: rid));
                    break;
                }
                case StoryStage:
                {
                    // A Campaign stage row: a StageContext like the dungeons' (the same kind, so the same stage steps play it).
                    var id = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_id")), null, notes, "StageContext._id");
                    if (id is not { } sid || sid <= 0) break;
                    var name = Try(() => StringValue(g, g.Ref(c.Address, StageCtx, "_name")), null, notes, "StageContext._name");
                    var state = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_state")), null, notes, "StageContext._state");
                    var buttonState = Try(() => IntValue(g, g.Ref(c.Address, StageCtx, "_buttonState")), null, notes, "StageContext._buttonState");
                    var (button, why) = Named(i, FightName(), NotFightName());
                    // The row's battle button is "DefaultButton_h" (seen live 2026-10-30, with the reward and enemy avatars around it).
                    if (button is null && SelfAndBelow(i).Where(x => x.Interactable is not null && x.ActiveInHierarchy && x.Name is "DefaultButton_h" or "DefaulCostButton_h").ToList() is [var only])
                        (button, why) = (only, null);
                    var target = Make("dungeon-stage", $"campaign: {name ?? $"stage {sid}"}", n, button, $"stage id {sid}, state {state}, button state {buttonState}" + (why is null ? "" : "; " + why), id: sid);
                    targets.Add(target);
                    (stages ??= []).Add(new StageRead(sid, name, state, buttonState, target));
                    break;
                }
                case TavernBuilding:
                    targets.Add(Make("village-tavern", "village: Tavern", n, Own(i), null));
                    break;
                case TavernButton:
                    targets.Add(Make("champion-tavern", "champion screen: Tavern", n, Own(i), null));
                    break;
                case RankUpGrade:
                {
                    // Fast Rank Up's grade tabs (1★, 2★, ...): the grade, whether it is selected, how many champions it would rank up.
                    var grade = Try(() => (int?)g.Int(c.Address, RankUpGrade, "_grade"), null, notes, "GradeItemContext._grade");
                    if (grade is null) break;
                    var selected = Try(() => BoolValue(g, g.Ref(c.Address, RankUpGrade, "_isSelected")), null, notes, "GradeItemContext._isSelected");
                    var count = Try(() => IntValue(g, g.Ref(c.Address, RankUpGrade, "_count")), null, notes, "GradeItemContext._count");
                    var locked = Try(() => BoolValue(g, g.Ref(c.Address, RankUpGrade, "_isLocked")), null, notes, "GradeItemContext._isLocked");
                    targets.Add(Make("fastrankup-grade", $"fast rank up: grade {grade}", n, Own(i),
                        $"{(selected == true ? "selected" : "not selected")}, count {count}{(locked == true ? ", locked" : "")}", id: grade));
                    break;
                }
                case RankUpItem:
                {
                    // A champion Fast Rank Up would rank up, with the food the game picked (FoodItemContext avatars below it).
                    var av = Try(() => g.Ref(c.Address, RankUpItem, "_heroAvatar"), 0UL, notes, "HeroRankUpItemContext._heroAvatar");
                    var hero = av == 0 ? null : Try(() => (int?)g.Int(av, HeroAvatar, "_EntityId_k__BackingField"), null, notes, "HeroAvatarContext._EntityId");
                    if (hero is not > 0) break;
                    var active = Try(() => BoolValue(g, g.Ref(c.Address, RankUpItem, "_isActive")), null, notes, "HeroRankUpItemContext._isActive");
                    var food = new List<int>();
                    foreach (var f in Below(i).Where(x => x.Context?.Class == RankUpFood))
                    {
                        var fav = Try(() => g.Ref(f.Context!.Address, RankUpFood, "_heroAvatar"), 0UL, notes, "FoodItemContext._heroAvatar");
                        var fid = fav == 0 ? null : Try(() => (int?)g.Int(fav, HeroAvatar, "_EntityId_k__BackingField"), null, notes, "HeroAvatarContext._EntityId");
                        if (fid is > 0 && !food.Contains(fid.Value)) food.Add(fid.Value);
                    }
                    var target = Make("fastrankup-item", $"fast rank up: #{hero}", n, Own(i), $"{(active == true ? "ticked" : "not ticked")}; food {string.Join(", ", food.Select(x => "#" + x))}", id: hero);
                    targets.Add(target);
                    (rankUps ??= []).Add(new RankUpItemRead(hero.Value, active, food, target));
                    break;
                }
                case FastRankUp:
                {
                    // Its Filter (bottom left; seen 2026-10-31): the food filter panel, where the vaults are included or not.
                    foreach (var f in SelfAndBelow(i).Where(x => x.Name == "FilterButton" && x.Interactable is not null && x.ActiveInHierarchy && x.Screen is not null).Take(1))
                        targets.Add(Make("fastrankup-filter", "fast rank up: Filter", f, f, null));
                    // The open filter panel (FoodFilterView) closes with its Apply.
                    foreach (var f in SelfAndBelow(i).Where(x => x.Name == "ApplyButton" && x.Interactable is not null && x.ActiveInHierarchy && x.Screen is not null
                        && x.Path.Contains("FoodFilterView", StringComparison.Ordinal)).Take(1))
                        targets.Add(Make("food-filter-apply", "fast rank up: filter Apply", f, f, null));
                    var available = Try(() => BoolValue(g, g.Ref(c.Address, FastRankUp, "_rankUpAvailable")), null, notes, "FastRankUpOverlayContext._rankUpAvailable");
                    var price = Try(() => IntValue(g, g.Ref(c.Address, FastRankUp, "_rankUpPrice")), null, notes, "FastRankUpOverlayContext._rankUpPrice");
                    var (button, why) = Named(i, RankUpName(), NotRankUpName());
                    targets.Add(Make("fastrankup-go", "fast rank up: Rank Up", n, button, $"available {available}, price {price}" + (why is null ? "" : "; " + why)));
                    break;
                }
                case ShardItem:
                {
                    var type = Try(() => IntValue(g, g.Ref(c.Address, ShardItem, "_shardTypeId")), null, notes, "ShardItemContext._shardTypeId");
                    if (type is null) break;
                    var count = Try(() => IntValue(g, g.Ref(c.Address, ShardItem, "_count")), null, notes, "ShardItemContext._count");
                    var selected = Try(() => BoolValue(g, g.Ref(c.Address, ShardItem, "_isSelected")), null, notes, "ShardItemContext._isSelected");
                    targets.Add(Make("portal-shard", $"portal: {g.EnumName("SharedModel.Meta.Shards.ShardTypeId", type.Value) ?? $"shard {type}"} shards", n, Own(i),
                        $"type {type}, count {count}, {(selected == true ? "selected" : "not selected")}", id: type));
                    break;
                }
                case SummonTab:
                {
                    selectedShard = Try(() => IntValue(g, g.Ref(c.Address, SummonTab, "_selectedShardTypeId")), null, notes, "SummonTabContext._selectedShardTypeId");
                    var (button, why) = Named(i, SummonOneName(), NotSummonOneName());
                    targets.Add(Make("portal-summon1", "portal: Summon x1", n, button, $"selected shard {selectedShard}" + (why is null ? "" : "; " + why)));
                    break;
                }
                case FwResult or CcResult or DungeonResult or StoryResult:
                    pve = new PveResultRead(
                        Try(() => IntValue(g, g.Ref(c.Address, PveResult, "_battleResultType")), null, notes, "BattleFinishPveDialogContext._battleResultType"),
                        Try(() => IntValue(g, g.Ref(c.Address, PveResult, "_stars")), null, notes, "BattleFinishPveDialogContext._stars"),
                        Try(() => StringValue(g, g.Ref(c.Address, PveResult, "_stageName")), null, notes, "BattleFinishPveDialogContext._stageName"),
                        Try(() => BoolValue(g, g.Ref(c.Address, PveResult, "_nextStageAvailable")), null, notes, "BattleFinishPveDialogContext._nextStageAvailable"));
                    break;
                case var slotClass when (slotClass == SelectionSlot || slotClass.EndsWith(".SelectableHeroSlotContext", StringComparison.Ordinal))
                    && IsTeamView(t.View) && UnderBinding(i, "MySquad"):
                {
                    // Our team's slots (seen 2026-10-29: the team screen's own HeroSlotContext under the group bound to MySquad; the enemy's
                    // under EnemySquad): the champion is the slot's Hero avatar's entity id; an empty slot is not filled.
                    var filled = Try(() => BoolValue(g, g.Ref(c.Address, SelectionSlot, "_filled")), null, notes, "HeroSlotContext._filled");
                    var avatar = Try(() => g.Ref(c.Address, SelectionSlot, "Hero"), 0UL, notes, "HeroSlotContext.Hero");
                    var hero = avatar == 0 ? null : Try(() => (int?)g.Int(avatar, HeroAvatar, "_EntityId_k__BackingField"), null, notes, "HeroAvatarContext._EntityId");
                    if (filled != false && hero is > 0 && n.ActiveInHierarchy)
                    {
                        (team ??= []).Add(hero.Value);
                        // A filled slot: clicking it takes the champion out of the team (Cursed City sets its own team per level).
                        targets.Add(Make("team-slot", $"team slot: #{hero}", n, Own(i), null, id: hero));
                    }
                    break;
                }
                case SelectableAvatar when IsTeamView(t.View):
                {
                    // A champion in the team screen's list: clicking it puts it in the first free slot.
                    var avatar = Try(() => g.Ref(c.Address, SelectableAvatar, "Avatar"), 0UL, notes, "SelectableHeroAvatarContext.Avatar");
                    var hero = avatar == 0 ? null : Try(() => (int?)g.Int(avatar, HeroAvatar, "_EntityId_k__BackingField"), null, notes, "HeroAvatarContext._EntityId");
                    if (hero is not > 0) break;
                    var state = Try(() => IntValue(g, g.Ref(c.Address, SelectableAvatar, "_avatarState")), null, notes, "SelectableHeroAvatarContext._avatarState");
                    var target = Make("team-pick", $"team list: #{hero}", n, Own(i), $"state {state}", id: hero);
                    targets.Add(target);
                    (picks ??= []).Add(new TeamPickRead(hero.Value, state, target));
                    break;
                }
                case CcMapItem:
                {
                    var stageText = Try(() => StringValue(g, g.Ref(c.Address, CcMapItem, "_stageId")), null, notes, "CursedCityMapItemContext._stageId");
                    var id = int.TryParse(stageText, out var sid) ? sid : Try(() => IntValue(g, g.Ref(c.Address, CcMapItem, "_id")), null, notes, "CursedCityMapItemContext._id") ?? 0;
                    if (id <= 0) break;
                    var state = Try(() => IntValue(g, g.Ref(c.Address, CcMapItem, "_state")), null, notes, "CursedCityMapItemContext._state");
                    var type = Try(() => IntValue(g, g.Ref(c.Address, CcMapItem, "_type")), null, notes, "CursedCityMapItemContext._type");
                    var district = Try(() => IntValue(g, g.Ref(c.Address, CcMapItem, "_districtId")), null, notes, "CursedCityMapItemContext._districtId");
                    var label = Try(() => StringValue(g, g.Ref(c.Address, CcMapItem, "_label")), null, notes, "CursedCityMapItemContext._label");
                    var target = Make("cc-level", $"cursed city: level {id % 1000} ({label})", n, Own(i), $"stage {id}, state {state}, type {type}, district {district}", id: id);
                    targets.Add(target);
                    (ccLevels ??= []).Add(new CcMapRead(id, state, type, district, label, target));
                    break;
                }
                case BattleHud:
                {
                    auto = Try(() => BoolValue(g, g.Ref(c.Address, BattleHud, "_isAutoEnabled")), null, notes, "BattleHUDContext._isAutoEnabled");
                    var (button, why) = Named(i, AutoName(), NotAutoName());
                    targets.Add(Make("battle-auto", "battle: Auto", n, button, (auto == true ? "auto on" : auto == false ? "auto off" : "auto unknown") + (why is null ? "" : "; " + why)));
                    // The speed button (Ehsan 2026-10-30: x2 is the most the game offers; button name to confirm live): the speed in the note.
                    var speedText = Try(() => StringValue(g, g.Ref(c.Address, BattleHud, "_speedString")), null, notes, "BattleHUDContext._speedString");
                    var speedInt = Try(() => IntValue(g, g.Ref(c.Address, BattleHud, "_speedInt")), null, notes, "BattleHUDContext._speedInt");
                    var (speedButton, speedWhy) = Named(i, SpeedName(), null);
                    targets.Add(Make("battle-speed", "battle: Speed", n, speedButton, $"speed {speedText ?? "?"} ({speedInt?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})"
                        + (speedWhy is null ? "" : "; " + speedWhy)));
                    break;
                }
                case ArenaResult:
                    result = new ArenaResultRead(
                        Try(() => IntValue(g, g.Ref(c.Address, ArenaResult, "_battleResultType")), null, notes, "BattleFinishArenaDialogContext._battleResultType"),
                        Try(() => IntValue(g, g.Ref(c.Address, ArenaResult, "_enemyCombatPower")), null, notes, "BattleFinishArenaDialogContext._enemyCombatPower"),
                        Try(() => StringValue(g, g.Ref(c.Address, ArenaResult, "_enemyUserName")), null, notes, "BattleFinishArenaDialogContext._enemyUserName"));
                    break;
                case UserResource:
                {
                    var id = Try(() => (int?)g.Int(c.Address, UserResource, "_resource"), null, notes, "UserResourceContext._resource");
                    hud.Add(new HudResource(id is { } r ? g.EnumName(ResourceEnum, r) ?? r.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                        Try(() => StringValue(g, g.Ref(c.Address, UserResource, "_value")), null, notes, "UserResourceContext._value"),
                        Try(() => StringValue(g, g.Ref(c.Address, UserResource, "_incrementStatusText")), null, notes, "UserResourceContext._incrementStatusText")));
                    break;
                }
                case InboxItem:
                {
                    var title = Try(() => StringValue(g, g.Ref(c.Address, InboxItem, "_title")), null, notes, "InboxItemContext._title");
                    var message = Try(() => StringValue(g, g.Ref(c.Address, InboxItem, "_message")), null, notes, "InboxItemContext._message");
                    var icon = Try(() => StringValue(g, g.Ref(c.Address, InboxItem, "_iconUrl")), null, notes, "InboxItemContext._iconUrl");
                    var valid = Try(() => BoolValue(g, g.Ref(c.Address, InboxItem, "_isValid")), null, notes, "InboxItemContext._isValid");
                    var (button, why) = Named(i, CollectName(), null);
                    var target = Make("inbox-collect", $"inbox: Collect \"{title ?? message ?? "?"}\"", n, button, why, id: inbox.Count);
                    targets.Add(target);
                    inbox.Add(new InboxEntry(title, message, icon, valid, target));
                    break;
                }
            }
        }

        // The Super Raid box: on or off from the team context's MultiRunContext; "locked" or "unavailable" when it cannot be ticked.
        if (PlayTargets.IsTeamView(t.View) && nodes.FirstOrDefault(x => x.Context?.Class == PveTeam) is { } pveTeam)
        {
            var mr = Try(() => g.Ref(pveTeam.Context!.Address, PveTeam, "MultiRun"), 0UL, notes, "HeroesSelectionPveDialogContext.MultiRun");
            if (mr != 0)
            {
                var active = Try(() => BoolValue(g, g.Ref(mr, MultiRunCtx, "_active")), null, notes, "MultiRunContext._active");
                var enabled = Try(() => BoolValue(g, g.Ref(mr, MultiRunCtx, "_enabled")), null, notes, "MultiRunContext._enabled");
                var locked = Try(() => BoolValue(g, g.Ref(mr, MultiRunCtx, "_locked")), null, notes, "MultiRunContext._locked");
                var blocks = nodes.Where(x => x.BindingPath == "MultiRun").Select(x => x.Index).ToHashSet();
                foreach (var x in nodes)
                {
                    if (x.Name != "Checkbox" || !x.ActiveInHierarchy || x.Screen is null || !blocks.Contains(x.Parent)) continue;
                    var state = locked == true ? "locked" : enabled == false ? "unavailable" : active switch { true => "on", false => "off", _ => "unknown" };
                    targets.Add(new UiTarget("super-raid", "team screen: Super Raid", null, t.View, x.Screen, true, true, x.FullyVisible, false, x.Path, state,
                        ClientWidth: t.ClientWidth, ClientHeight: t.ClientHeight));
                }
            }
        }

        // Buttons the game binds by path rather than by a context of their own (seen 2026-10-29: the village HUD's sub-contexts are
        // fields of VillageHUDContext, bound by name: WorldMapButton → "BattleMap", InboxButton → "Inbox").
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.BindingPath is not { } bp || !n.ActiveInHierarchy) continue;
            var byPath = (t.View, bp) switch
            {
                (VillageHudView, "BattleMap") => ("village-battle", "village: Battle"),
                (VillageHudView, "Inbox") => ("village-inbox", "village: Inbox"),
                // The village's Tavern and Portal (names to confirm live; the 3D buildings may only be reachable by their HUD buttons).
                (VillageHudView, var b) when b.Contains("Tavern", StringComparison.OrdinalIgnoreCase) => ("village-tavern", "village: Tavern"),
                (VillageHudView, "HeroesInfo") => ("village-heroes", "village: Champions"),
                (VillageHudView, var b) when b.Contains("Portal", StringComparison.OrdinalIgnoreCase) => ("village-portal", "village: Portal"),
                // The battle menu's Campaign card (AdventureModeContext, field _adventure: binding to confirm live).
                (BattleMenuView, var b) when b.Contains("Adventure", StringComparison.OrdinalIgnoreCase) || b.Contains("Campaign", StringComparison.OrdinalIgnoreCase)
                    => ("mode-campaign", "battle menu: Campaign"),
                // The battle menu's Clan Boss card (AllianceActivityModeContext, field _allianceActivity; binding to confirm live).
                (BattleMenuView, "ClanActivity") => ("mode-clan", "battle menu: Clan Boss"), // seen live 2026-10-31
                (ClanMapView, var b) when b.Contains("DemonLord", StringComparison.OrdinalIgnoreCase) => ("clan-demonlord", "clan: Demon Lord"),
                // The battle menu's Arena card (seen 2026-10-29: "Arena" bound to Arenas, its clickable Panel below).
                (BattleMenuView, "Arenas") => ("mode-arena", "battle menu: Arena"),
                // The battle menu's Faction Wars card: bound like the Arena card (name to confirm live: Fraction/Faction).
                (BattleMenuView, var b) when b.Contains("Fraction", StringComparison.OrdinalIgnoreCase) || b.Contains("FactionWars", StringComparison.OrdinalIgnoreCase)
                    => ("mode-fw", "battle menu: Faction Wars"),
                (BattleMenuView, var b) when b.Contains("CursedCity", StringComparison.OrdinalIgnoreCase) => ("mode-cc", "battle menu: Cursed City"),
                // The battle menu's Dungeons card (DungeonModeContext; binding name to confirm live).
                (BattleMenuView, var b) when b.Contains("Dungeon", StringComparison.OrdinalIgnoreCase) => ("mode-dungeon", "battle menu: Dungeons"),
                // The arena map's buildings: labels that follow the 3D scene (a canvas inside the view); Arena1x1 is the Classic Arena.
                (ArenaMapView, "Arena1x1") => ("arena-classic", "arena map: Classic Arena"),
                _ => ((string?)null, (string?)null),
            };
            if (byPath.Item1 is not { } kind) continue;
            var own = Own(i);
            if (own is not null && !targets.Any(x => x.Kind == kind && x.Path == own.Path)) targets.Add(Make(kind, byPath.Item2!, n, own, $"binding '{bp}'"));
        }

        // The dungeon map's dungeons (seen live 2026-10-30): a 3D scene with no contexts; each dungeon is a point named and bound by its
        // RegionTypeId name ("DragonsLair" at the building, 0x0), placed where the scene shows it (off screen until the map is dragged).
        // The target is a small square on that point; the click is checked by the stage list opening.
        if (t.View == DungeonsMapView)
            // The Iron Twins' point is "DoubleAscendDungeon" bound to "DoubleAscend" (seen 2026-10-31).
            foreach (var x in nodes.Where(x => x.ActiveInHierarchy && x.Screen is { Width: 0, Height: 0 }
                && (x.BindingPath == x.Name || x.Name == "DoubleAscendDungeon" && x.BindingPath == "DoubleAscend")))
            {
                var (px, py) = (x.Screen!.Value.X, x.Screen.Value.Y);
                var inside = px > 30 && py > 30 && px < t.ClientWidth - 30 && py < t.ClientHeight - 30;
                targets.Add(new UiTarget("dungeon-region", $"dungeons: {x.BindingPath}", null, t.View, new ClientRect(px - 25, py - 25, 50, 50), true, true, inside, false, x.Path,
                    $"region ({x.BindingPath})", ClientWidth: t.ClientWidth, ClientHeight: t.ClientHeight));
            }

        // The campaign map (view "Map", seen live 2026-10-30) is a 3D scene like the dungeons': each chapter is a 0x0 point
        // "MapRegionItem" bound to "Region<N>" (RegionTypeId 100 + N), placed where the scene shows it (off screen until dragged).
        if (t.View == CampaignMapView && !targets.Any(x => x.Kind == "campaign-region"))
            foreach (var x in nodes.Where(x => x.ActiveInHierarchy && x.Screen is { Width: 0, Height: 0 } && x.Name.StartsWith("MapRegionItem", StringComparison.Ordinal)))
            {
                if (ChapterPoint().Match(x.BindingPath ?? "") is not { Success: true } m) continue;
                var (px, py) = (x.Screen!.Value.X, x.Screen.Value.Y);
                var inside = px > 30 && py > 30 && px < t.ClientWidth - 30 && py < t.ClientHeight - 30;
                // The point is the foot of the chapter's banner (seen 2026-10-30: a click there does nothing); the banner stands above it.
                targets.Add(new UiTarget("campaign-region", $"campaign: {x.BindingPath}", 100 + int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    t.View, new ClientRect(px - 25, py - 95, 50, 60), true, true, inside && py > 110, false, x.Path, $"region ({x.BindingPath}), a map point", ClientWidth: t.ClientWidth,
                    ClientHeight: t.ClientHeight));
            }

        // The arena result closes on a tap (seen 2026-10-29: "CloseBlock", a click area with no button, over the lower part of the
        // screen with the champion avatars on it): the target is a strip at its bottom, clear of the avatars.
        if (t.View is ArenaResultView or FwResultView or DungeonResultView or StoryResultView or ClanResultView)
            foreach (var cb in nodes.Where(x => x.Name == "CloseBlock" && x.ActiveInHierarchy && x.Screen is not null).Take(1))
            {
                // Its "Content" child is the RETURN TO ARENA bar (seen 2026-10-29): the middle of that bar.
                var bar = nodes.FirstOrDefault(x => x.Parent == cb.Index && x.Name == "Content" && x.ActiveInHierarchy && x.Screen is not null)?.Screen ?? cb.Screen!.Value;
                var middle = new ClientRect(bar.X + bar.Width * 0.35f, bar.Y + bar.Height * 0.3f, bar.Width * 0.3f, bar.Height * 0.4f);
                targets.Add(new UiTarget("result-close", "arena result: Return to Arena", null, t.View, middle, true, true, true, false, cb.Path, "the RETURN TO ARENA bar",
                    ClientWidth: t.ClientWidth, ClientHeight: t.ClientHeight));
            }

        // The team selection's Start (seen 2026-10-29: "Default_h", the big button bottom right; its Checkbox_h above is left alone).
        if (IsTeamView(t.View))
        {
            var start = nodes.Where(x => x.Name == "Default_h" && x.Interactable is not null && x.ActiveInHierarchy).ToList();
            // The Iron Twins' Start (Fortress Keys) is "DoubleAscend_h" (seen 2026-10-31); "AutoBattleButton_h" above it is never Start.
            if (start.Count == 0) start = nodes.Where(x => x.Name == "DoubleAscend_h" && x.Interactable is not null && x.ActiveInHierarchy).ToList();
            if (start.Count >= 1)
                targets.Add(Make("team-start", "team selection: Start", start[0], start.Count == 1 ? start[0] : null,
                    start.Count == 1 ? null : $"{start.Count} elements named Default_h"));
        }

        // The champion screen's Tavern button: a "Button_h" whose path names the Tavern (when its context is not bound on the node).
        if (t.View == "HeroesInfoOverlay" && !targets.Any(x => x.Kind == "champion-tavern"))
            foreach (var x in nodes.Where(x => x.Interactable is not null && x.ActiveInHierarchy && x.Screen is not null
                && x.Path.Contains("Tavern", StringComparison.OrdinalIgnoreCase)).Take(1))
                targets.Add(Make("champion-tavern", "champion screen: Tavern", x, x, x.Path));
        if (t.View == "HeroesInfoOverlay" && !targets.Any(x => x.Kind == "champion-tavern"))
            notes.Add("champion screen buttons: " + string.Join("; ", nodes.Where(x => x.Interactable is not null && x.ActiveInHierarchy && x.Name == "Button_h").Select(x => x.Path)));

        // The Tavern (HeroesDevelopmentDialog): its Rank Up tab (a toggle whose path says RankUp) and the tab's Fast Rank Up button.
        if (t.View == TavernView)
            foreach (var x in nodes.Where(x => x.ActiveInHierarchy && x.Screen is not null))
            {
                if (x.IsOn is not null && x.Path.Contains("RankUp", StringComparison.OrdinalIgnoreCase) && !x.Path.Contains("Fast", StringComparison.OrdinalIgnoreCase))
                    targets.Add(Make("tavern-rankup-tab", "tavern: Rank Up tab", x, x, x.IsOn == true ? "on" : "off"));
                else if (x.Interactable is not null && (x.Name.Contains("FastRankUp", StringComparison.OrdinalIgnoreCase)
                    || x.BindingPath?.Contains("FastRankUp", StringComparison.OrdinalIgnoreCase) == true))
                    targets.Add(Make("tavern-fast-rankup", "tavern: Fast Rank Up", x, x, null));
            }

        // The Demon Lord's Battle button (in AllianceEnemiesDialog, by its name; to confirm live).
        if (t.View == ClanDialogView)
            foreach (var x in nodes.Where(x => x.Interactable is not null && x.ActiveInHierarchy && x.Screen is not null && FightName().IsMatch(x.Name)
                && !NotFightName().IsMatch(x.Name)).Take(1))
                targets.Add(Make("clan-battle", "clan boss: Battle", x, x, x.Path));

        // The arena dialog's tabs (toggles in the ArenaDialog view, in their order): tab 0 is the battle tab.
        if (t.View == ArenaDialogView)
        {
            var toggles = nodes.Where(x => x.IsOn is not null && x.ActiveInHierarchy && x.Path.Contains("Tab", StringComparison.OrdinalIgnoreCase)).ToList();
            for (var k = 0; k < toggles.Count; k++)
                targets.Add(Make("arena-tab", $"arena: tab {k}", toggles[k], toggles[k], toggles[k].IsOn == true ? "on" : "off", id: k));
        }

        if (rows is not null || free is not null || tab is not null) arena = new ArenaBoardRead(rows ?? [], free, freeText, loading, tab);
        return new PlayScan(targets, arena, auto, result, hud, inbox, notes.Distinct().ToList(), crypts, stages, team?.Distinct().ToList(), pve, ccLevels, picks, ccDifficulty,
            rankUps, selectedShard, clanDamage);
    }

    /// <summary>An inbox entry that holds Classic Arena tokens (not Tag/Live arena tokens or auto-battle tickets), by its text and icon.</summary>
    public static bool IsArenaTokens(InboxEntry e)
    {
        var text = $"{e.Title} {e.Message} {e.Icon}";
        return TokenWord().IsMatch(text) && !OtherTokens().IsMatch(text);
    }

    /// <summary>An inbox entry that holds energy (by its text and icon; to confirm live).</summary>
    public static bool IsEnergy(InboxEntry e) => EnergyWord().IsMatch($"{e.Title} {e.Message} {e.Icon}");

    [GeneratedRegex(@"energy", RegexOptions.IgnoreCase)] private static partial Regex EnergyWord();
    [GeneratedRegex(@"token", RegexOptions.IgnoreCase)] private static partial Regex TokenWord();
    [GeneratedRegex(@"tag|3\s*x\s*3|live|auto|multi|ticket", RegexOptions.IgnoreCase)] private static partial Regex OtherTokens();

    /// <summary>A read that older offsets may lack, or that fails on a stale object: the fallback, with a note (shown by autoplay-explore).</summary>
    private static T Try<T>(Func<T> read, T fallback, List<string> notes, string what)
    {
        try { return read(); }
        catch (Exception e) when (e is KeyNotFoundException or RaidExtractor.ReadCheckException or InvalidOperationException)
        {
            notes.Add($"{what}: {e.Message}");
            return fallback;
        }
    }

    private static int? IntValue(IGameObjects g, ulong p) => p == 0 ? null : g.Int(p, IntProperty, "_value");
    private static bool? BoolValue(IGameObjects g, ulong p) => p == 0 ? null : g.Bool(p, BoolProperty, "_value");
    private static string? StringValue(IGameObjects g, ulong p) => p == 0 ? null : g.String(p, StringProperty, "_value");
}
