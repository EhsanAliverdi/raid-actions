using RaidActions.App.Input;
using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor;
using RaidExtractor.Autoplay;
using RaidExtractor.Battle;

namespace RaidActions.App.Play;

/// <summary>
/// Chores on the live game (raid-extractor plan/chores.md, seen live 2026-10-31): the Market (a fixed village spot; a slot's price opens the
/// item, its BuyButton_h buys it), the Tavern's Upgrade Level tab (main champion, food, LevelUpButton), artifact upgrades through the
/// upgrade command's driver. The Shop (real money) is never opened.
/// </summary>
public sealed partial class LivePlayGame : IChoresGame
{
    public async Task<IReadOnlyList<string>> BuyAtMarketAsync(ChoresSettings s, bool quest, bool dryRun, CancellationToken ct)
    {
        await OpenMarket(ct);
        var (_, targets, _) = await Read(ct);
        var (data, _, _, _) = await Account(ct);
        var offers = targets.Where(t => t.Kind == "market-item").Select(t => new MarketOffer(t.Id ?? 0, (int)(NoteInt(t.Note, "type") ?? 0),
            t.Note?.Contains("silver", StringComparison.Ordinal) == true ? NoteInt(t.Note, "price") : null, t.Name)).ToList();
        log($"autoplay: the Market offers {string.Join("; ", offers.Select(o => $"{o.Name}: {o.Silver?.ToString("N0") ?? "not silver"}"))}");
        var buy = ChoreRules.Buy(offers, (long)data.Account.Resources.GetValueOrDefault("Silver"), s, quest);
        var bought = new List<string>();
        foreach (var o in buy)
        {
            if (dryRun) { log($"autoplay: WOULD BUY {o.Name} (dry run)"); bought.Add(o.Name); continue; }
            bool Mine(UiTarget t) => t.Kind == "market-item" && t.Id == o.Slot;
            // The slots run sideways, off to the right (seen 2026-10-31): dragged until this one is wholly shown.
            for (var i = 0; i < 6; i++)
            {
                var (_, now, _) = await Read(ct);
                var row = now.FirstOrDefault(Mine);
                if (row is null) throw new PlayStopException($"{o.Name} left the Market. Screen dump: {DumpScreen("market item")}");
                if (row.NotClickable is null) break;
                var (w, why) = Game.Window.GameWindow.Find();
                if (w is null) throw new PlayStopException(why!);
                var dx = Math.Clamp((row.Rect?.Center.X ?? 0) - w.Client.Width * 0.6f, -w.Client.Width * 0.4f, w.Client.Width * 0.4f);
                var x = w.Client.Width * 0.6f + dx / 2;
                if (Input.MouseInput.Drag(w, x, w.Client.Height * 0.55f, x - dx, w.Client.Height * 0.55f) is { } notSent) throw new PlayStopException("Not dragged: " + notSent);
                await Task.Delay(900, ct);
            }
            await Click("market-item", Mine, (_, scan) => scan().Targets.Any(t => t.Kind == "market-get"), $"{o.Name} opens", ct, within: TimeSpan.FromSeconds(8));
            await Task.Delay(800, ct);
            var (s0, _, _) = await Read(ct);
            var top = Top(s0)!;
            await Click("market-get", null, (_, scan) => !scan().Targets.Any(t => t.Kind == "market-get"), $"{o.Name} bought", ct, within: TimeSpan.FromSeconds(8), shot: true);
            await Task.Delay(1000, ct);
            var (s1, _, _) = await Read(ct);
            if (Top(s1) == top) await Back(top, ct);
            bought.Add(o.Name);
            log($"autoplay: bought {o.Name} for {o.Silver:N0} silver");
            await Task.Delay(800, ct);
        }
        await _nav.GoHome(Action("from the Market"), ct);
        return bought;
    }

    private static long? NoteInt(string? note, string word) =>
        note is not null && System.Text.RegularExpressions.Regex.Match(note, word + @" (\d+)") is { Success: true } m ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The village with no screen on it (GoHome stops on the champion screen).</summary>
    private async Task ToVillage(CancellationToken ct)
    {
        await _nav.GoHome(Action("to the village"), ct);
        for (var i = 0; i < 3; i++)
        {
            var s = await Settled(ct);
            if (Top(s) is PlayTargets.VillageHudView) return;
            await Back(Top(s)!, ct);
        }
    }

    /// <summary>The Market from the village: its fixed spot (the building has no screen rectangle), as the Portal.</summary>
    private async Task OpenMarket(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 4; hop++)
        {
            var (s, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "market-item")) return;
            if (Top(s) != PlayTargets.VillageHudView) { await ToVillage(ct); continue; }
            await Click("village-market", null, (_, scan) => scan().Targets.Any(t => t.Kind == "market-item"), "the Market opens", ct, within: TimeSpan.FromSeconds(12));
            await Task.Delay(1000, ct);
        }
        throw new PlayStopException($"The Market was not reached. Screen dump: {DumpScreen("market not reached")}");
    }

    public async Task<bool> LevelUpFoodAsync(FoodSettings food, IReadOnlyCollection<int> farmers, bool dryRun, CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        if (ChoreRules.LevelUp(data.Champions, food, farmers) is not { } pair) return false;
        var levels = data.Champions.ToDictionary(c => c.Id, c => c.Level);
        log($"autoplay: STEP Tavern level-up: {Who(pair.Main)} (level {levels[pair.Main]}) with {Who(pair.Food)}");
        await OpenTavernLevel(ct);
        var (_, t0, _) = await Read(ct);
        if (t0.Any(t => t.Kind == "tavern-deselect" && t.NotClickable is null))
        {
            await Click("tavern-deselect", null, (_, _) => true, "the main slot emptied", ct);
            await Task.Delay(800, ct);
        }
        await SelectInTavern(pair.Main, ct);
        await SelectInTavern(pair.Food, ct);
        await Task.Delay(800, ct);
        var (_, t1, _) = await Read(ct);
        var go = t1.FirstOrDefault(t => t.Kind == "tavern-levelup");
        if (go is null || go.NotClickable is not null) throw new PlayStopException($"The Upgrade button is not ready ({go?.NotClickable}). Screen dump: {DumpScreen("tavern upgrade")}");
        if (dryRun)
        {
            if (go.Rect is { } r) Shot($"autoplay: WOULD CLICK Upgrade, {Who(pair.Main)} with {Who(pair.Food)} (dry run: NOT pressed)", r, always: true);
            await _nav.GoHome(Action("from the Tavern"), ct);
            return true;
        }
        try { await Click("tavern-levelup", null, (s2, _) => Top(s2) == UiState.MessageBoxView, "the level-up", ct, within: TimeSpan.FromSeconds(4), shot: true); }
        catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal)) { }
        await Task.Delay(1500, ct);
        var after = await Settled(ct);
        if (Top(after) == UiState.MessageBoxView)
        {
            var dump = DumpScreen("tavern message box");
            await Back(UiState.MessageBoxView, ct);
            throw new PlayStopException($"The Tavern asked something (closed, nothing levelled). Screen dump: {dump}");
        }
        var now = (await Account(ct)).Data.Champions;
        if (now.First(c => c.Id == pair.Main).Level <= levels[pair.Main])
            throw new PlayStopException($"Upgrade was pressed but {Who(pair.Main)} did not level. Screen dump: {DumpScreen("tavern not levelled")}");
        log($"autoplay: {Who(pair.Main)} is level {now.First(c => c.Id == pair.Main).Level}");
        await _nav.GoHome(Action("from the Tavern"), ct);
        return true;
    }

    /// <summary>A champion clicked in the Tavern's list (scrolled to: the 1-stars are far down).</summary>
    private async Task SelectInTavern(int id, CancellationToken ct)
    {
        bool Mine(UiTarget t) => t.Kind == "tavern-hero" && t.Id == id;
        for (var i = 0; i < 16; i++)
        {
            var (_, targets, _) = await Read(ct);
            var row = targets.FirstOrDefault(Mine);
            if (row is { NotClickable: null })
            {
                await Click("tavern-hero", Mine, (_, _) => true, $"{Who(id)} selected", ct);
                await Task.Delay(700, ct);
                return;
            }
            var (w, why) = Game.Window.GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            if (Input.MouseInput.Wheel(w, 160, 400, -4) is { } notSent) throw new PlayStopException(notSent);
            await Task.Delay(500, ct);
        }
        throw new PlayStopException($"{Who(id)} was not found in the Tavern's list. Screen dump: {DumpScreen("tavern list")}");
    }

    private async Task OpenTavernLevel(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 8; hop++)
        {
            var s = await Settled(ct);
            var (_, targets, _) = await Read(ct);
            if (Has(s, PlayTargets.TavernView))
            {
                if (targets.FirstOrDefault(t => t.Kind == "tavern-level-tab") is { Note: "off" })
                {
                    await Click("tavern-level-tab", null, (_, scan) => scan().Targets.Any(t => t.Kind == "tavern-level-tab" && t.Note == "on"), "the Upgrade Level tab", ct);
                    await Task.Delay(800, ct);
                    continue;
                }
                return;
            }
            if (targets.Any(t => t.Kind == "champion-tavern"))
            {
                await Click("champion-tavern", null, (s2, _) => Has(s2, PlayTargets.TavernView), "the Tavern opens", ct, within: TimeSpan.FromSeconds(10));
                continue;
            }
            if (!NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-heroes", null, (s2, _) => Has(s2, "HeroesInfoOverlay"), "the champion screen opens", ct, within: TimeSpan.FromSeconds(10));
        }
        throw new PlayStopException($"The Tavern was not reached. Screen dump: {DumpScreen("tavern not reached")}");
    }

    public async Task<int> CollectFreeAsync(bool dryRun, CancellationToken ct)
    {
        _nav.FitWindow();
        var (s, _, _) = await Read(ct);
        if (Top(s) != PlayTargets.VillageHudView) await ToVillage(ct);
        // The Mine's bubble: a tap on its spot; nothing there when no gems wait (and a screen a stray tap opened is closed).
        if (!dryRun)
        {
            await Click("village-gems", null, (_, _) => true, "the gem bubble", ct);
            await Task.Delay(1500, ct);
            var (after, _, _) = await Read(ct);
            if (Top(after) != PlayTargets.VillageHudView)
            {
                await Back(Top(after)!, ct); // no gems waiting: the tap opened the Mine's window
                await Task.Delay(2000, ct);
                await Settled(ct);
            }
        }
        await Click("village-shop", null, (s2, _) => Top(s2) == PlayTargets.ShopView, "the Shop opens", ct, within: TimeSpan.FromSeconds(10));
        await Task.Delay(2000, ct);
        var claimed = 0;
        for (var i = 0; i < 4; i++)
        {
            var (_, targets, _) = await Read(ct);
            var offer = targets.FirstOrDefault(t => t.Kind == "shop-free" && t.NotClickable is null);
            if (offer is null) break;
            if (dryRun) { log($"autoplay: WOULD CLAIM {offer.Name} (dry run)"); break; }
            // The tile opens "Get this for free?" (seen 2026-11-01); its Claim is the same button a paid offer's Buy would be, so it is only
            // ever pressed here, right after a tile the game marks free opened it.
            await Click("shop-free", t => t.Path == offer.Path, (s2, _) => Top(s2) == "StaticOfferInfoOverlay", $"{offer.Name} opens", ct, within: TimeSpan.FromSeconds(8));
            await Task.Delay(800, ct);
            await Click("shop-claim", null, (s2, _) => Top(s2) != "StaticOfferInfoOverlay", $"{offer.Name} claimed", ct, within: TimeSpan.FromSeconds(8), shot: true);
            await Task.Delay(2000, ct);
            for (var j = 0; j < 3; j++)
            {
                var s3 = await Settled(ct);
                if (Top(s3) is not { } top || top == PlayTargets.ShopView) break;
                await Back(top, ct);
            }
            if ((await Read(ct)).Targets.Any(t => t.Kind == "shop-free" && t.Path == offer.Path)) throw new PlayStopException($"{offer.Name} is still free after Claim. Screen dump: {DumpScreen("shop claim")}");
            claimed++;
            log($"autoplay: claimed {offer.Name}");
        }
        await Back(PlayTargets.ShopView, ct);
        return claimed;
    }

    private NavigatingDriver? _upgrader;

    public async Task<ArtifactOutcome?> UpgradeArtifactAsync(int? minLevel, bool attempts, long keepSilver, bool dryRun, CancellationToken ct)
    {
        var (data, gd, _, _) = await Account(ct);
        if (gd.ArtifactRules is not { } rules || data.Account.Resources.GetValueOrDefault("Silver") <= keepSilver) return null;
        var stage = GearRating.Stage(data, null);
        var protection = GearAdvice.Protection(data, ExtractorOutput);
        var context = AdviceContext.From(data);
        var advice = data.Artifacts.Select(a => GearAdvice.Advise(a, rules, gd, stage.Stage, protection.GetValueOrDefault(a.Id), context: context)).ToList();
        var pieces = data.Artifacts.ToDictionary(a => a.Id);
        if (ChoreRules.Upgrade(advice, pieces, minLevel, attempts) is not { } pick) return null;
        var piece = pieces[pick.Id];
        log($"autoplay: STEP upgrade {piece.SetName} {piece.Slot} #{pick.Id} +{piece.Level} to +{pick.Target}: {pick.Reason}");
        _upgrader ??= new NavigatingDriver(game, ui, null, folder, policy, log, upgrade: true);
        long? PriceOf(ArtifactOut a) => GearRating.AttemptPrice(a, rules);
        NavigatingDriver.UpgradeResult r;
        try { r = await _upgrader.UpgradePiece(pick.Id, pick.Slot, pick.Target, real: !dryRun, PriceOf, 1, 1, ct); }
        catch (NavigatingDriver.StopException e) { throw new PlayStopException(e.Message); }
        await _nav.GoHome(Action("from the upgrade"), ct);
        return new ArtifactOutcome($"#{pick.Id} {piece.Slot} +{r.From} to +{r.To} (target +{pick.Target}): {r.Outcome}, {r.Attempts} attempt(s), {r.SilverSpent:N0} silver", r.Attempts, r.To);
    }
}
