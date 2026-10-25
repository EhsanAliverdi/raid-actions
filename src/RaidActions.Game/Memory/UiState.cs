namespace RaidActions.Game.Memory;

/// <summary>Read access to the game's objects by the names in the offsets (live: RaidExtractor.Core; tests: an object graph).</summary>
public interface IGameObjects
{
    /// <summary>A static root instance, checked to be <paramref name="expectedType"/>.</summary>
    ulong Root(string name, string expectedType);

    /// <summary>The object's full class name (namespace.name).</summary>
    string ClassName(ulong obj);

    /// <summary>A reference field; 0 when null.</summary>
    ulong Ref(ulong obj, string type, string member);

    int Int(ulong obj, string type, string member);

    int? NullableInt(ulong obj, string type, string member);

    /// <summary>The non-null elements of a List&lt;T&gt; of references.</summary>
    IReadOnlyList<ulong> RefList(ulong list, int max);

    /// <summary>The game's name for an enum value, or null when it is not a member.</summary>
    string? EnumName(string enumType, int value);
}

/// <param name="Key">The game's ViewKey value; <see cref="Name"/> its name.</param>
/// <param name="State">ViewState: NotCreatedYet, Active, ClosePending, CreationFailed.</param>
public sealed record OpenView(int Key, string Name, string State, bool Visible)
{
    public bool IsActive => State == "Active";
    public override string ToString() => $"{Name} ({Key}) {State}{(Visible ? "" : ", hidden")}";
}

/// <summary>
/// The champion screen (HeroesInfoOverlay): the champion it shows, the champion of the details (gear) panel, the artifact
/// selected in the inventory picker, and the details tab.
/// </summary>
public sealed record ChampionScreenState(int? HeroId, int? SelectedHeroId, int? DetailsHeroId, int? InventoryArtifactId, int? DetailsTab, int? ViewState)
{
    /// <summary>The champion the screen shows, only when every part of it agrees.</summary>
    public int? Champion => HeroId is { } h && (SelectedHeroId is null || SelectedHeroId == h) && (DetailsHeroId is null || DetailsHeroId == h) ? h : null;
}

/// <summary>The game's UI as read from memory: the open views (in the view master's order), whether the UI is blocked, the champion screen.</summary>
public sealed record UiState(IReadOnlyList<OpenView> Views, int BlockingEvents, ChampionScreenState? ChampionScreen)
{
    public const string ChampionScreenView = "HeroesInfoOverlay";

    /// <summary>
    /// Views of a battle being loaded, fought, paused or left (explicit: many other view names contain "Battle", such as the always-open
    /// BattleTextNotificationsView or the BattlePass dialogs). Background (auto) battles fought while in menus have no such view.
    /// </summary>
    public static readonly IReadOnlySet<string> BattleRunningViews = new HashSet<string>
    {
        "Battle", "BattleHUD", "BattleLoadingDialog", "BattlePauseDialog", "BattleValidationDialog", "BattleDisconnectOverlay",
        "BattleLeavingOverlay", "LiveBattlePauseOverlay", "BattleResultBetweenRoundsOverlayArena3x3",
    };

    /// <summary>A battle's result screen (the battle is over, but the game is still in the battle flow).</summary>
    public static bool IsBattleResultView(string name) =>
        name.StartsWith("BattleFinish", StringComparison.Ordinal) || name is "LiveArenaBattleFinishOverlay" or "Arena3x3QuickBattleFinishDialog" or "Arena3x3QuickBattleFinishRewardDialog";

    public static bool IsBattleView(string name) => BattleRunningViews.Contains(name) || IsBattleResultView(name);

    public IEnumerable<OpenView> Active => Views.Where(v => v.IsActive);

    /// <summary>A battle is loading, running, paused, or showing its result: no gear changes.</summary>
    public bool InBattle => Active.Any(v => IsBattleView(v.Name));

    public bool UiBlocked => BlockingEvents > 0;

    /// <summary>The last visible active view (the one on top, as far as the view master's order tells).</summary>
    public OpenView? Top => Active.LastOrDefault(v => v.Visible);

    public bool ChampionScreenOpen => Active.Any(v => v.Name == ChampionScreenView);
}
