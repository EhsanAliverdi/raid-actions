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

/// <summary>What Auto Play reads on the screens it plays: click targets and the facts next to them.</summary>
public sealed record PlayScan(List<UiTarget> Targets, ArenaBoardRead? Arena, bool? AutoOn, ArenaResultRead? Result, List<HudResource> Hud,
    List<InboxEntry> Inbox, List<string> Notes)
{
    public static PlayScan Empty => new([], null, null, null, [], [], []);

    public PlayScan Merge(PlayScan o) => new([.. Targets, .. o.Targets], Arena ?? o.Arena, AutoOn ?? o.AutoOn, Result ?? o.Result, [.. Hud, .. o.Hud],
        [.. Inbox, .. o.Inbox], [.. Notes, .. o.Notes]);
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
        VillageHudView = "VillageHUD", RefillView = "ResourceRefillOverlay";

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
    private const string IntProperty = "Client.MVVM.Base.ViewModel.Properties.IntProperty";
    private const string BoolProperty = "Client.MVVM.Base.ViewModel.Properties.BoolProperty";
    private const string StringProperty = "Client.MVVM.Base.ViewModel.Properties.StringProperty";

    /// <summary>The kinds Auto Play may click (each flow narrows it further; the spending ones only in a real run).</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>
    {
        "village-battle", "village-inbox", "mode-arena", "arena-classic", "arena-tab", "arena-opponent", "arena-refresh", "team-start",
        "battle-auto", "inbox-collect", "result-close",
    };

    [GeneratedRegex(@"Battle|Start|Fight|Attack", RegexOptions.IgnoreCase)] private static partial Regex FightName();
    [GeneratedRegex(@"Info|Avatar|Profile|Hero|Skin|Preset|Auto|Relic|Filter|Sort", RegexOptions.IgnoreCase)] private static partial Regex NotFightName();
    [GeneratedRegex(@"Refresh|Update|Reload", RegexOptions.IgnoreCase)] private static partial Regex RefreshName();
    [GeneratedRegex(@"Collect|Claim|Take|Get|Receive|Accept", RegexOptions.IgnoreCase)] private static partial Regex CollectName();
    [GeneratedRegex(@"Auto", RegexOptions.IgnoreCase)] private static partial Regex AutoName();
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

        IEnumerable<UiNode> Below(int i)
        {
            for (var k = i + 1; k < nodes.Count && nodes[k].Depth > nodes[i].Depth; k++) yield return nodes[k];
        }
        IEnumerable<UiNode> SelfAndBelow(int i) => new[] { nodes[i] }.Concat(Below(i));

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

        // Pass 1: contexts that point at other elements (the arena map's classic arena item).
        ulong classic = 0;
        foreach (var n in nodes.Where(n => n.Context?.Class == ArenaMap))
            classic = Try(() => g.Ref(n.Context!.Address, ArenaMap, "_arena1x1"), 0UL, notes, "ArenaMapContext._arena1x1");

        List<ArenaRow>? rows = null;
        bool? free = null, loading = null;
        string? freeText = null;
        int? tab = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var c = n.Context;
            if (c is null) continue;
            switch (c.Class)
            {
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
                case BattleHud:
                {
                    auto = Try(() => BoolValue(g, g.Ref(c.Address, BattleHud, "_isAutoEnabled")), null, notes, "BattleHUDContext._isAutoEnabled");
                    var (button, why) = Named(i, AutoName(), NotAutoName());
                    targets.Add(Make("battle-auto", "battle: Auto", n, button, (auto == true ? "auto on" : auto == false ? "auto off" : "auto unknown") + (why is null ? "" : "; " + why)));
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
                // The battle menu's Arena card (seen 2026-10-29: "Arena" bound to Arenas, its clickable Panel below).
                (BattleMenuView, "Arenas") => ("mode-arena", "battle menu: Arena"),
                // The arena map's buildings: labels that follow the 3D scene (a canvas inside the view); Arena1x1 is the Classic Arena.
                (ArenaMapView, "Arena1x1") => ("arena-classic", "arena map: Classic Arena"),
                _ => ((string?)null, (string?)null),
            };
            if (byPath.Item1 is not { } kind) continue;
            var own = Own(i);
            if (own is not null && !targets.Any(x => x.Kind == kind && x.Path == own.Path)) targets.Add(Make(kind, byPath.Item2!, n, own, $"binding '{bp}'"));
        }

        // The arena result closes on a tap (seen 2026-10-29: "CloseBlock", a click area with no button, over the lower part of the
        // screen with the champion avatars on it): the target is a strip at its bottom, clear of the avatars.
        if (t.View == ArenaResultView)
            foreach (var cb in nodes.Where(x => x.Name == "CloseBlock" && x.ActiveInHierarchy && x.Screen is not null).Take(1))
            {
                // Its "Content" child is the RETURN TO ARENA bar (seen 2026-10-29): the middle of that bar.
                var bar = nodes.FirstOrDefault(x => x.Parent == cb.Index && x.Name == "Content" && x.ActiveInHierarchy && x.Screen is not null)?.Screen ?? cb.Screen!.Value;
                var middle = new ClientRect(bar.X + bar.Width * 0.35f, bar.Y + bar.Height * 0.3f, bar.Width * 0.3f, bar.Height * 0.4f);
                targets.Add(new UiTarget("result-close", "arena result: Return to Arena", null, t.View, middle, true, true, true, false, cb.Path, "the RETURN TO ARENA bar",
                    ClientWidth: t.ClientWidth, ClientHeight: t.ClientHeight));
            }

        // The team selection's Start (seen 2026-10-29: "Default_h", the big button bottom right; its Checkbox_h above is left alone).
        if (t.View == TeamView)
        {
            var start = nodes.Where(x => x.Name == "Default_h" && x.Interactable is not null && x.ActiveInHierarchy).ToList();
            if (start.Count >= 1)
                targets.Add(Make("team-start", "team selection: Start", start[0], start.Count == 1 ? start[0] : null,
                    start.Count == 1 ? null : $"{start.Count} elements named Default_h"));
        }

        // The arena dialog's tabs (toggles in the ArenaDialog view, in their order): tab 0 is the battle tab.
        if (t.View == ArenaDialogView)
        {
            var toggles = nodes.Where(x => x.IsOn is not null && x.ActiveInHierarchy && x.Path.Contains("Tab", StringComparison.OrdinalIgnoreCase)).ToList();
            for (var k = 0; k < toggles.Count; k++)
                targets.Add(Make("arena-tab", $"arena: tab {k}", toggles[k], toggles[k], toggles[k].IsOn == true ? "on" : "off", id: k));
        }

        if (rows is not null || free is not null || tab is not null) arena = new ArenaBoardRead(rows ?? [], free, freeText, loading, tab);
        return new PlayScan(targets, arena, auto, result, hud, inbox, notes.Distinct().ToList());
    }

    /// <summary>An inbox entry that holds Classic Arena tokens (not Tag/Live arena tokens or auto-battle tickets), by its text and icon.</summary>
    public static bool IsArenaTokens(InboxEntry e)
    {
        var text = $"{e.Title} {e.Message} {e.Icon}";
        return TokenWord().IsMatch(text) && !OtherTokens().IsMatch(text);
    }

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
