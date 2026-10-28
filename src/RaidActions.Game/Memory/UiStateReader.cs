namespace RaidActions.Game.Memory;

/// <summary>
/// Reads the game's UI state (read-only), every hop checked by class name:
/// AppViewModel (static) → _overlayManager → _viewMaster (RaidViewMaster) → _views (List&lt;ViewMeta&gt;); the champion screen's
/// view → BaseView.Context (HeroesInfoOverlayContext) → _heroId, _selectedHeroContext._heroId, _heroDetailsContext (_heroId,
/// _inventoryArtifactId, _activeTabIndex), _viewState. Offsets: config/ui-offsets.json (+ ui-offsets.manual.json).
/// </summary>
public sealed class UiStateReader(IGameObjects g)
{
    private const string AppViewModel = "Client.ViewModel.AppViewModel";
    private const string OverlayManagerBase = "Client.ViewModel.OverlayManagerBase";
    private const string RaidViewMaster = "Client.RaidApp.RaidViewMaster";
    private const string ViewMeta = "Client.RaidApp.ViewMeta";
    private const string BaseView = "Client.MVVM.Base.View.BaseView";
    private const string BlockUi = "Client.ViewModel.BlockUi";
    private const string HeroesInfo = "Client.ViewModel.Contextes.HeroesInfoDialog.HeroesInfoOverlayContext";
    private const string HeroDetails = "Client.ViewModel.Contextes.HeroesInfoDialog.HeroDetails.HeroDetailsContext";
    private const string SelectedHero = "Client.ViewModel.Contextes.HeroesInfoDialog.SelectedHero.SelectedHeroContext";
    private const string IntProperty = "Client.MVVM.Base.ViewModel.Properties.IntProperty";
    private const string StringProperty = "Client.MVVM.Base.ViewModel.Properties.StringProperty";
    private const string SwapContext = "Client.ViewModel.Contextes.HeroesInfoDialog.Inventory.Overlays.SwapArtifactsOverlayContext";
    private const string ArtifactAvatar = "Client.ViewModel.Contextes.Generic.ArtifactAvatarContext";
    private const string Artifact = "SharedModel.Meta.Artifacts.Artifact";
    private const string InventoryView = "Client.ViewModel.Contextes.HeroesInfoDialog.Inventory.InventoryViewContext";
    private const string SellArtifacts = "Client.ViewModel.Contextes.HeroesInfoDialog.Inventory.SellArtifactsContext";
    private const string InventoryArtifact = "Client.ViewModel.Contextes.HeroesInfoDialog.Inventory.Items.InventoryArtifactContext";
    private const string MessageBoxContext = "Client.ViewModel.Contextes.Generic.MessageBox.MessageBoxContext";
    public const string ViewKeyEnum = "Client.ViewModel.DTO.ViewKey";
    private const string ViewStateEnum = "Client.RaidApp.ViewState";
    private const string VisibilityEnum = "Client.RaidApp.ViewVisibility";
    private const int MaxViews = 256;

    /// <summary>One read. Throws <see cref="UiReadException"/> when the object graph is not what the offsets describe.</summary>
    public UiState Read()
    {
        var app = g.Root("AppViewModel", AppViewModel);
        var overlays = Expect(g.Ref(app, AppViewModel, "_overlayManager"), "Client.ViewModel.OverlayManager", "AppViewModel._overlayManager");
        var master = Expect(g.Ref(overlays, OverlayManagerBase, "_viewMaster"), RaidViewMaster, "OverlayManager._viewMaster");
        var list = g.Ref(master, RaidViewMaster, "_views");
        if (list == 0) throw new UiReadException("RaidViewMaster._views is null.");

        var views = new List<OpenView>();
        ChampionScreenState? champion = null;
        SwapDialogState? swap = null;
        SellState? sell = null;
        MessageBoxInfo? box = null;
        foreach (var meta in g.RefList(list, MaxViews))
        {
            Expect(meta, ViewMeta, "RaidViewMaster._views[]");
            var key = g.Int(meta, ViewMeta, "Key");
            var name = g.EnumName(ViewKeyEnum, key) ?? $"#{key}";
            var state = g.EnumName(ViewStateEnum, g.Int(meta, ViewMeta, "State")) ?? throw new UiReadException($"{name}: unknown ViewState.");
            var visible = (g.EnumName(VisibilityEnum, g.Int(meta, ViewMeta, "Visibility")) ?? throw new UiReadException($"{name}: unknown visibility.")) == "Visible";
            var view = new OpenView(key, name, state, visible);
            views.Add(view);
            if (name == UiState.ChampionScreenView && view.IsActive && g.Ref(meta, ViewMeta, "View") is var v and not 0)
            {
                champion = ReadChampionScreen(g.Ref(v, BaseView, "Context"));
                sell = Optional(() => ReadSell(g.Ref(v, BaseView, "Context")));
            }
            if (name == UiState.SwapView && view.IsActive && g.Ref(meta, ViewMeta, "View") is var sv and not 0 && g.Ref(sv, BaseView, "Context") is var sc and not 0)
                swap = ReadSwap(Expect(sc, SwapContext, "SwapArtifactsOverlay view's Context"));
            if (name == UiState.MessageBoxView && view.IsActive && g.Ref(meta, ViewMeta, "View") is var mv and not 0 && g.Ref(mv, BaseView, "Context") is var mc and not 0)
                box = Optional(() => g.ClassName(mc) == MessageBoxContext
                    ? new MessageBoxInfo(StringValue(g.Ref(mc, MessageBoxContext, "_titleText")), StringValue(g.Ref(mc, MessageBoxContext, "_mainMessage")))
                    : null);
        }

        var block = g.Ref(app, AppViewModel, "_blockUi");
        var blocking = block == 0 ? 0 : g.Int(Expect(block, BlockUi, "AppViewModel._blockUi"), BlockUi, "_blockingEventsCounter");
        return new UiState(views, blocking, champion, swap, sell, box);
    }

    private ChampionScreenState? ReadChampionScreen(ulong ctx)
    {
        if (ctx == 0) return null; // view created, context not bound yet
        Expect(ctx, HeroesInfo, "HeroesInfoOverlay view's Context");
        var details = Expect(g.Ref(ctx, HeroesInfo, "_heroDetailsContext"), HeroDetails, "_heroDetailsContext");
        var selected = Expect(g.Ref(ctx, HeroesInfo, "_selectedHeroContext"), SelectedHero, "_selectedHeroContext");
        return new ChampionScreenState(
            g.NullableInt(ctx, HeroesInfo, "_heroId"),
            g.NullableInt(selected, SelectedHero, "_heroId"),
            g.NullableInt(details, HeroDetails, "_heroId"),
            g.NullableInt(details, HeroDetails, "_inventoryArtifactId"),
            IntValue(g.Ref(details, HeroDetails, "_activeTabIndex")),
            IntValue(g.Ref(ctx, HeroesInfo, "_viewState")));
    }

    /// <summary>
    /// The picker's sell mode: HeroesInfoOverlayContext._inventoryContext (InventoryViewContext) → _sellArtifacts (SellArtifactsContext)
    /// → _sellArtifactsCount and _markedForSell (each an InventoryArtifactContext → Avatar → Artifact.Id).
    /// </summary>
    private SellState? ReadSell(ulong ctx)
    {
        if (ctx == 0) return null;
        var inventory = g.Ref(ctx, HeroesInfo, "_inventoryContext");
        if (inventory == 0) return null;
        var sell = Expect(g.Ref(Expect(inventory, InventoryView, "_inventoryContext"), InventoryView, "_sellArtifacts"), SellArtifacts, "_sellArtifacts");
        var count = IntValue(g.Ref(sell, SellArtifacts, "_sellArtifactsCount"));
        var marked = new List<int>();
        var list = g.Ref(sell, SellArtifacts, "_markedForSell");
        if (list != 0)
            foreach (var item in g.RefList(list, 2000))
            {
                if (g.ClassName(item) != InventoryArtifact) { marked.Add(-1); continue; }
                var avatar = g.Ref(item, InventoryArtifact, "Avatar");
                var artifact = avatar == 0 ? 0 : g.Ref(Expect(avatar, ArtifactAvatar, "marked item's avatar"), ArtifactAvatar, "Artifact");
                marked.Add(artifact == 0 ? -1 : g.Int(Expect(artifact, Artifact, "marked item's artifact"), Artifact, "Id"));
            }
        return new SellState(count, marked);
    }

    /// <summary>A part of the state that older offsets do not cover (re-run scripts/gen-ui-offsets.ps1): null instead of a failed read.</summary>
    private static T? Optional<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception e) when (e is KeyNotFoundException or UiReadException or RaidExtractor.ReadCheckException) { return null; }
    }

    private SwapDialogState ReadSwap(ulong ctx) => new(
        ArtifactId(g.Ref(ctx, SwapContext, "_equippedArtifactContext")),
        ArtifactId(g.Ref(ctx, SwapContext, "_artifactToEquipContext")),
        StringValue(g.Ref(ctx, SwapContext, "_title")),
        StringValue(g.Ref(ctx, SwapContext, "_warning")));

    private int? ArtifactId(ulong avatar)
    {
        if (avatar == 0) return null;
        var artifact = g.Ref(Expect(avatar, ArtifactAvatar, "swap dialog avatar"), ArtifactAvatar, "Artifact");
        return artifact == 0 ? null : g.Int(Expect(artifact, Artifact, "swap dialog artifact"), Artifact, "Id");
    }

    private string? StringValue(ulong property) => property == 0 ? null : g.String(Expect(property, StringProperty, "StringProperty"), StringProperty, "_value");

    private int? IntValue(ulong property) => property == 0 ? null : g.Int(Expect(property, IntProperty, "IntProperty"), IntProperty, "_value");

    private ulong Expect(ulong obj, string fullName, string what)
    {
        if (obj == 0) throw new UiReadException($"{what} is null.");
        var actual = g.ClassName(obj);
        if (actual != fullName) throw new UiReadException($"{what} is a {actual}, expected {fullName}.");
        return obj;
    }
}

public sealed class UiReadException(string message) : Exception(message);
