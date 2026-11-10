using RaidActions.Game.Window;
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
                await Pause(900, ct);
            }
            await Click("market-item", Mine, (_, scan) => scan().Targets.Any(t => t.Kind == "market-get"), $"{o.Name} opens", ct, within: TimeSpan.FromSeconds(8));
            await Pause(800, ct);
            var (s0, _, _) = await Read(ct);
            var top = Top(s0)!;
            await Click("market-get", null, (_, scan) => !scan().Targets.Any(t => t.Kind == "market-get"), $"{o.Name} bought", ct, within: TimeSpan.FromSeconds(8), shot: true);
            await Pause(1000, ct);
            var (s1, _, _) = await Read(ct);
            if (Top(s1) == top) await Back(top, ct);
            bought.Add(o.Name);
            log($"autoplay: bought {o.Name} for {o.Silver:N0} silver");
            await Pause(800, ct);
        }
        await HomeAsync(Action("from the Market"), ct);
        return bought;
    }

    private static long? NoteInt(string? note, string word) =>
        note is not null && System.Text.RegularExpressions.Regex.Match(note, word + @" (\d+)") is { Success: true } m ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The village with no screen on it (GoHome stops on the champion screen).</summary>
    private async Task ToVillage(CancellationToken ct)
    {
        await HomeAsync(Action("to the village"), ct);
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
            // The tap can end on the Missions screen seconds after coming back to the village (seen twice): the loop closes it and tries again.
            if (await TapBuilding("market", ct)) { await Pause(2500, ct); continue; }
            try { await Click("village-market", null, (_, scan) => scan().Targets.Any(t => t.Kind == "market-item"), "the Market opens", ct, within: TimeSpan.FromSeconds(12)); }
            catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal)) { log($"autoplay: the Market did not open: {e.Message}"); }
            await Pause(1000, ct);
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
            await Pause(800, ct);
        }
        await SelectInTavern(pair.Main, ct);
        await SelectInTavern(pair.Food, ct);
        await Pause(800, ct);
        var (_, t1, _) = await Read(ct);
        var go = t1.FirstOrDefault(t => t.Kind == "tavern-levelup");
        if (go is null || go.NotClickable is not null) throw new PlayStopException($"The Upgrade button is not ready ({go?.NotClickable}). Screen dump: {DumpScreen("tavern upgrade")}");
        if (dryRun)
        {
            if (go.Rect is { } r) Shot($"autoplay: WOULD CLICK Upgrade, {Who(pair.Main)} with {Who(pair.Food)} (dry run: NOT pressed)", r, always: true);
            await HomeAsync(Action("from the Tavern"), ct);
            return true;
        }
        try { await Click("tavern-levelup", null, (s2, _) => Top(s2) == UiState.MessageBoxView, "the level-up", ct, within: TimeSpan.FromSeconds(4), shot: true); }
        catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal)) { }
        await Pause(1500, ct);
        var after = await Settled(ct);
        if (Top(after) == UiState.MessageBoxView)
        {
            var dump = DumpScreen("tavern message box");
            await Back(UiState.MessageBoxView, ct);
            throw new PlayStopException($"The Tavern asked something (closed, nothing levelled). Screen dump: {dump}");
        }
        var now = (await Account(ct)).Data.Champions;
        // The food gone is the proof: one level-1 food is not always a whole level (2026-11-01: #3339 at 9 ate one and stayed 9, with the XP).
        if (now.First(c => c.Id == pair.Main).Level <= levels[pair.Main] && now.Any(c => c.Id == pair.Food))
            throw new PlayStopException($"Upgrade was pressed but {Who(pair.Main)} did not level. Screen dump: {DumpScreen("tavern not levelled")}");
        log($"autoplay: {Who(pair.Main)} is level {now.First(c => c.Id == pair.Main).Level}");
        await HomeAsync(Action("from the Tavern"), ct);
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
                await Pause(700, ct);
                return;
            }
            var (w, why) = Game.Window.GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            if (Input.MouseInput.Wheel(w, 160, 400, -4) is { } notSent) throw new PlayStopException(notSent);
            await Pause(500, ct);
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
                    await Pause(800, ct);
                    continue;
                }
                return;
            }
            if (targets.Any(t => t.Kind == "champion-tavern"))
            {
                await Click("champion-tavern", null, (s2, _) => Has(s2, PlayTargets.TavernView), "the Tavern opens", ct, within: TimeSpan.FromSeconds(10));
                continue;
            }
            if (!NavigatingDriver.AtVillage(s)) { await HomeAsync(Action("to the village"), ct); continue; }
            await Click("village-heroes", null, (s2, _) => Has(s2, "HeroesInfoOverlay"), "the champion screen opens", ct, within: TimeSpan.FromSeconds(10));
        }
        throw new PlayStopException($"The Tavern was not reached. Screen dump: {DumpScreen("tavern not reached")}");
    }

    public async Task<int> CollectExpiringTokensAsync(bool dryRun, CancellationToken ct)
    {
        // A Classic Arena Refill fills the tokens to the top (2026-11-03): Classic Arena takes it when they run out; here only in its last 2 hours.
        static bool LastHours(string? left) => ChoreRules.ExpiresWithinADay(left)
            && System.Text.RegularExpressions.Regex.Match(left!, @"(\d+)\s*h") is var h && (!h.Success || int.Parse(h.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 2);
        bool Soon(InboxEntry e) => PlayTargets.IsAnyTokens(e) && (e.Icon?.Contains("TokensMax", StringComparison.Ordinal) == true ? LastHours(e.Expires) : ChoreRules.ExpiresWithinADay(e.Expires));
        // The entries taken, not the token counts that rose (2026-11-10 09:36: one left at the over-the-cap warning was reported claimed, the regen counted).
        await CollectInbox(Soon, "Token", "tokens expiring within a day", dryRun, ct);
        return _inboxTaken;
    }

    private string MasteryFile => RaidExtractor.Battle.MasteryRequest.PathIn(ExtractorOutput);

    public bool MasteryRequested() => File.Exists(MasteryFile);

    /// <summary>
    /// The app's Apply: the champion, its masteries screen, and Learn on each mastery asked for, in order, checked in the account. Before anything is pressed, what the
    /// screen reads must be the champion's masteries in the account, so a wrong id can never be learned. Whatever happens the request is used once.
    /// </summary>
    public async Task<string> ApplyMasteriesAsync(CancellationToken ct)
    {
        RaidExtractor.Battle.MasteryRequest? request;
        try { request = System.Text.Json.JsonSerializer.Deserialize<RaidExtractor.Battle.MasteryRequest>(File.ReadAllText(MasteryFile), AutoplayJson.Options); }
        finally { File.Delete(MasteryFile); }
        if (request is null) return "the mastery request could not be read";
        log($"autoplay: STEP masteries for {Who(request.ChampionId)}: {string.Join(", ", request.Ids)}{(request.DryRun ? " (dry run)" : "")}");
        await _nav.ShowChampion(request.ChampionId, ct);
        await Pause(800, ct);
        await Click("mastery-tab", null, (_, scan) => scan().Targets.Any(t => t.Kind == "mastery-tab" && t.Note == "on"), "the masteries tab", ct, within: TimeSpan.FromSeconds(6));
        await Pause(800, ct);
        await Click("mastery-open", null, (s2, _) => Top(s2) == "MasteriesOverlay", "the masteries screen", ct, within: TimeSpan.FromSeconds(8));
        await Pause(1500, ct);
        var (_, nodes, _) = await Read(ct);
        var read = nodes.Where(t => t.Kind == "mastery-node" && t.Id is > 0).ToList();
        var (data, _, _, _) = await Account(ct);
        var mine = data.Champions.First(c => c.Id == request.ChampionId).Masteries.ToHashSet();
        var states = read.GroupBy(t => t.Note?.Split(' ')[^1] ?? "").ToDictionary(g => g.Key, g => g.Select(t => t.Id!.Value).ToHashSet());
        log($"autoplay: the masteries screen: {read.Count} nodes, by state {string.Join(", ", states.Select(kv => $"{kv.Key}:{kv.Value.Count}"))}; account {mine.Count}");
        var matched = mine.Count > 0 ? states.Any(kv => kv.Value.SetEquals(mine)) : read.Count == 66;
        if (read.Count != 66 || !matched || request.Ids.Any(id => read.All(t => t.Id != id)))
        {
            var dump = DumpScreen("masteries did not match the account");
            await HomeAsync(Action("from the masteries"), ct);
            return $"nothing learned: the screen did not match the account. Screen dump: {dump}";
        }
        if (request.DryRun)
        {
            var price = "";
            if (request.BuyWithGems && request.Ids.Count > 0)
            {
                await Click("mastery-node", t => t.Id == request.Ids[0], (s2, _) => Top(s2) == "MasteryInfoOverlay", "the first mastery opens", ct, within: TimeSpan.FromSeconds(6));
                await Pause(800, ct);
                log($"autoplay: mastery panel. Screen dump: {DumpScreen("mastery panel")}");
                if ((await Read(ct)).Targets.Any(t => t.Kind == "mastery-getmax"))
                {
                    await Click("mastery-getmax", null, (s2, _) => Top(s2) != "MasteryInfoOverlay", "the buy screen opens", ct, within: TimeSpan.FromSeconds(8));
                    await Pause(1000, ct);
                    var (s4, t4, p4) = await Read(ct);
                    price = $"; buy screen {Top(s4)}, {string.Join("; ", p4.Notes.Where(n => n.Contains("price", StringComparison.Ordinal)))}, buy button: {t4.Any(t => t.Kind == "mastery-buy")}";
                    log($"autoplay: buy screen. Screen dump: {DumpScreen("mastery buy")}");
                    Shot("autoplay: the buy screen, not pressed", new ClientRect(0, 0, 1, 1), always: true);
                }
            }
            await HomeAsync(Action("from the masteries"), ct);
            return $"dry run: the screen matches the account; {request.Ids.Count} mastery(ies) would be learned{price}";
        }
        // The scroll set, only when this request allows it and the champion cannot pay: the price is checked, then the account proves it.
        var champion = data.Champions.First(c => c.Id == request.ChampionId);
        if (request.BuyWithGems && request.Ids.Count > 0 && (champion.MasteryScrolls?.Values.Sum() ?? 0) == 0)
        {
            var gemsBefore = data.Account.Resources.GetValueOrDefault("Gem");
            await Click("mastery-node", t => t.Id == request.Ids[0], (s2, _) => Top(s2) == "MasteryInfoOverlay", "the first mastery opens", ct, within: TimeSpan.FromSeconds(6));
            await Pause(800, ct);
            await Click("mastery-getmax", null, (s2, _) => Top(s2) == "BuyMasteriesOverlay", "the scroll set opens", ct, within: TimeSpan.FromSeconds(8));
            await Pause(1000, ct);
            var (_, offer, _) = await Read(ct);
            var button = offer.FirstOrDefault(t => t.Kind == "mastery-buy");
            var price = int.TryParse((button?.Note ?? "").Replace(",", "", StringComparison.Ordinal), out var p) ? p : -1;
            log($"autoplay: the scroll set costs {price} gems, {gemsBefore:0} held, {request.MaxGems} allowed");
            if (button is null || price <= 0 || price > request.MaxGems || price > gemsBefore)
            {
                await HomeAsync(Action("from the masteries"), ct);
                return $"nothing bought: the scroll set costs {price} gems and {request.MaxGems} were allowed";
            }
            await Click("mastery-buy", null, (s2, _) => Top(s2) != "BuyMasteriesOverlay", "the scroll set is bought", ct, within: TimeSpan.FromSeconds(10));
            await Pause(2500, ct);
            var (after, _, _, _) = await Account(ct);
            var gemsAfter = after.Account.Resources.GetValueOrDefault("Gem");
            var scrolls = after.Champions.First(c => c.Id == request.ChampionId).MasteryScrolls;
            log($"autoplay: bought: gems {gemsBefore:0} to {gemsAfter:0}, scrolls {string.Join(", ", (scrolls ?? []).Select(kv => $"{kv.Key} {kv.Value}"))}");
            if (gemsBefore - gemsAfter > price || (scrolls?.Values.Sum() ?? 0) == 0)
            {
                var dump = DumpScreen("masteries after the purchase");
                await HomeAsync(Action("from the masteries"), ct);
                return $"stopped after the purchase: the account does not show what was expected. Screen dump: {dump}";
            }
            var (s5, _, _) = await Read(ct);
            if (Top(s5) == "MasteryInfoOverlay") { await Back("MasteryInfoOverlay", ct); await Pause(800, ct); }
        }
        var learned = 0;
        foreach (var id in request.Ids)
        {
            await Click("mastery-node", t => t.Id == id, (s2, _) => Top(s2) == "MasteryInfoOverlay", $"mastery {id} opens", ct, within: TimeSpan.FromSeconds(6));
            await Pause(700, ct);
            if (!(await Read(ct)).Targets.Any(t => t.Kind == "mastery-learn"))
            {
                await Back("MasteryInfoOverlay", ct);
                break;
            }
            await Click("mastery-learn", null, (_, _) => true, $"mastery {id}", ct);
            await Pause(1800, ct);
            // The build's second tree: a third tree holding nothing is what gets locked (the build was checked before it was sent).
            if (Top(await ReadState(ct)) == "OpenLastAvailableTreeOverlay")
            {
                await Click("mastery-unlock", null, (s2, _) => Top(s2) != "OpenLastAvailableTreeOverlay", "the second tree", ct, within: TimeSpan.FromSeconds(8));
                await Pause(1800, ct);
            }
            var (s6, _, _) = await Read(ct);
            if (Top(s6) == "MasteryInfoOverlay") { await Back("MasteryInfoOverlay", ct); await Pause(800, ct); }
            var now = (await Account(ct)).Data.Champions.First(c => c.Id == request.ChampionId).Masteries;
            if (!now.Contains(id)) { log($"autoplay: mastery {id} is not in the account after Learn: stopping"); break; }
            learned++;
        }
        await HomeAsync(Action("from the masteries"), ct);
        return $"{Who(request.ChampionId)}: {learned} of {request.Ids.Count} mastery(ies) learned";
    }

    /// <summary>The building above its label tapped; false when the label is not on screen, so the caller falls back to the fixed spot.</summary>
    private async Task<bool> TapBuilding(string name, CancellationToken ct)
    {
        var (w, why) = GameWindow.Find();
        if (w is null) throw new PlayStopException(why!);
        var path = Path.Combine(AppPaths.Config(), "village", name + ".png");
        if (!File.Exists(path)) return false;
        using var label = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Color);
        // A window on top leaves the capture blank: the game comes to the front first.
        if (MouseInput.EnsureForeground(w) is { } behind) { log($"autoplay: {behind}"); return false; }
        await Pause(500, ct);
        var (capture, problem) = WindowCapture.Take(w);
        if (capture is null) { log($"autoplay: the village could not be captured: {problem}"); return false; }
        (float X, float Y)? spot;
        using (capture) spot = VillageSpot.Find(capture.Image, label);
        if (spot is not { } at) { log($"autoplay: the {name} label is not on screen"); return false; }
        var scale = w.Client.Width / 1298f;
        log($"autoplay: the {name} building, by its label at ({at.X:0},{at.Y:0})");
        // The Market's tent is tapped high: the game's "Progress Mission available" banner covers the part just above its label, and a tap there opens Missions.
        var above = (name == "market" ? 130 : 60) * scale;
        if (MouseInput.Click(w, at.X, at.Y - above, new Random()) is { } notSent) throw new PlayStopException(notSent);
        await Pause(800, ct);
        return true;
    }

    public async Task<string?> FillSparringPitAsync(bool dryRun, CancellationToken ct)
    {
        _nav.FitWindow();
        var (s0, _, _) = await Read(ct);
        if (Top(s0) != PlayTargets.VillageHudView) await ToVillage(ct);
        if (await TapBuilding("guardian", ct))
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < until && Top(await ReadState(ct)) != "AcademyDialog") await Task.Delay(500, ct);
            if (Top(await ReadState(ct)) != "AcademyDialog") throw new PlayStopException("A tap on the Guardian Ring's label opened nothing.");
        }
        else await Click("village-academy", null, (s2, _) => Top(s2) == "AcademyDialog", "the Guardian Ring opens", ct, within: TimeSpan.FromSeconds(10));
        await Pause(1500, ct);
        // A filled slot does not open the list (2026-11-03): its champion is training, nothing to do.
        try { await Click("pit-slot", null, (s2, _) => Top(s2) == "TrainingCampHeroSelectionOverlay", "the Sparring Pit's list opens", ct, within: TimeSpan.FromSeconds(10)); }
        catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal))
        {
            log("autoplay: the Sparring Pit's slot is filled: training");
            await Back("AcademyDialog", ct);
            await Pause(1000, ct);
            return null;
        }
        await Pause(1000, ct);
        var (_, items, _) = await Read(ct);
        // HeroSelectionItemState: 0 free, 1 in this slot, 3 at max level, 4 blocked.
        var inSlot = items.FirstOrDefault(t => t.Kind == "pit-hero" && t.Note == "state 1");
        string? put = null;
        if (inSlot is null)
        {
            var (data, _, _, _) = await Account(ct);
            var offered = items.Where(t => t.Kind == "pit-hero" && t.Note == "state 0" && t.Id is > 0).Select(t => t.Id!.Value).ToList();
            var settings = AutoplaySettings.Load(ExtractorOutput);
            if (ChoreRules.PitPick(offered, data.Champions, settings.Energy.Food, [.. settings.Energy.Food.ExtraIds]) is { } id)
            {
                log($"autoplay: STEP Sparring Pit: {Who(id)}");
                if (!dryRun)
                {
                    await DragIntoView("pit-hero", ct, t => t.Id == id);
                    await Click("pit-hero", t => t.Id == id, (_, _) => true, Who(id), ct);
                    await Pause(600, ct);
                    await Click("pit-confirm", null, (s2, _) => Top(s2) != "TrainingCampHeroSelectionOverlay", "the champion goes in", ct, within: TimeSpan.FromSeconds(8));
                    put = Who(id);
                }
            }
        }
        var (s3, _, _) = await Read(ct);
        if (Top(s3) == "TrainingCampHeroSelectionOverlay") { await Back("TrainingCampHeroSelectionOverlay", ct); await Pause(800, ct); }
        await Back("AcademyDialog", ct);
        await Pause(1000, ct);
        return put;
    }

    public async Task<int> ClaimPassRewardsAsync(bool dryRun, CancellationToken ct)
    {
        _nav.FitWindow();
        var claimed = 0;
        foreach (var (open, view, tab) in new[] { ("village-pass", "BattlePassDialog", "pass-tab"), ("village-challenges", "ChallengesDialog", "challenge-tab") })
        {
            var (s, _, _) = await Read(ct);
            if (Top(s) != PlayTargets.VillageHudView) await ToVillage(ct);
            const string Hub = "BattlePassAggregatorDialog";
            await Click(open, null, (s2, _) => Top(s2) == view || Top(s2) == Hub, $"{view} opens", ct, within: TimeSpan.FromSeconds(10));
            await Pause(1500, ct);
            if (Top(await ReadState(ct)) == Hub)
            {
                // Several passes (a new account): each card's pass in turn; a locked card opens nothing and is left.
                var cards = (await Read(ct)).Targets.Count(t => t.Kind == "pass-card");
                for (var k = 0; k < cards; k++)
                {
                    try { await Click("pass-card", t => t.Id == k, (s2, _) => Top(s2) == view, $"pass {k + 1} opens", ct, within: TimeSpan.FromSeconds(6)); }
                    catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal)) { continue; }
                    await Pause(1500, ct);
                    claimed += await ClaimOn(view, tab, dryRun, ct);
                    await Back(view, ct);
                    await Pause(1200, ct);
                }
                await Back(Hub, ct);
            }
            else
            {
                claimed += await ClaimOn(view, tab, dryRun, ct);
                await Back(view, ct);
            }
            await Pause(1500, ct);
        }
        // The rest of the free rewards (a new account, 2026-11-03): the side menu's, the events'. One failing leaves the others.
        try { claimed += await ClaimMenuRewardsAsync(dryRun, ct); }
        catch (PlayStopException e) { log($"autoplay: side menu rewards: {e.Message}"); await HomeAsync(Action("from the side menu"), ct); }
        try { claimed += await ClaimEventsAsync(dryRun, ct); }
        catch (PlayStopException e) { log($"autoplay: event rewards: {e.Message}"); await HomeAsync(Action("from the events"), ct); }
        return claimed;
    }

    /// <summary>The side menu's reward screens (Progression Rewards, Plarium Points and its Treasure Tickets): every Claim and Collect.</summary>
    private async Task<int> ClaimMenuRewardsAsync(bool dryRun, CancellationToken ct)
    {
        var claimed = 0;
        foreach (var item in new[] { "MilestoneReward", "PlariumProgram_h" })
        {
            var (s, _, _) = await Read(ct);
            if (Top(s) != PlayTargets.VillageHudView) await ToVillage(ct);
            // The menu slides in: its buttons are tapped once they are on the screen (2026-11-03: a tap at x -214 was refused).
            await Click("menu-open", null, (_, scan) => scan().Targets.Any(t => t.Kind == "menu-item" && t.Note == item && t.Rect is { X: >= 0 }), "the side menu opens", ct,
                within: TimeSpan.FromSeconds(6));
            await Pause(500, ct);
            await Click("menu-item", t => t.Note == item, (s2, _) => Top(s2) != PlayTargets.VillageHudView, $"{item} opens", ct, within: TimeSpan.FromSeconds(8));
            await Pause(1500, ct);
            var view = Top(await ReadState(ct))!;
            var tabs = (await Read(ct)).Targets.Where(t => t.Kind == "reward-tab").Select(t => t.Path).ToList();
            foreach (var path in tabs.DefaultIfEmpty(""))
            {
                if (path.Length > 0) { await Click("reward-tab", t => t.Path == path, (_, _) => true, "the tab", ct); await Pause(1200, ct); }
                for (var i = 0; i < 12; i++)
                {
                    var (_, now, _) = await Read(ct);
                    var claim = now.FirstOrDefault(t => t.Kind == "reward-claim" && t.NotClickable is null);
                    if (claim is null) break;
                    if (dryRun) { log($"autoplay: WOULD CLAIM {claim.Name} (dry run)"); break; }
                    await Click("reward-claim", t => t.Path == claim.Path && t.Id == claim.Id && t.Rect == claim.Rect, (_, _) => true, claim.Name, ct);
                    log($"autoplay: claimed {claim.Name}");
                    claimed++;
                    await Pause(1800, ct);
                    await DismissLevelUp(ct);
                    var s3 = await Settled(ct);
                    if (Top(s3) is { } top && top != view) { await Back(top, ct); await Pause(800, ct); }
                }
            }
            await Back(view, ct);
            await Pause(1200, ct);
        }
        return claimed;
    }

    /// <summary>Each event's milestones: its card, its page's Claim All (a web page: its fixed spot), its X.</summary>
    private async Task<int> ClaimEventsAsync(bool dryRun, CancellationToken ct)
    {
        var (s, _, _) = await Read(ct);
        if (Top(s) != PlayTargets.VillageHudView) await ToVillage(ct);
        if (!(await Read(ct)).Targets.Any(t => t.Kind == "event-card" && t.Id == -1)) return 0;
        await Click("event-card", t => t.Id == -1, (s2, _) => Top(s2) is "SoloEventsDialog" or "SoloEventInfoWebView", "the events open", ct, within: TimeSpan.FromSeconds(8));
        await Pause(1500, ct);
        // One event left (2026-11-03): the button opens its page straight away.
        if (Top(await ReadState(ct)) == "SoloEventInfoWebView")
        {
            await Pause(2500, ct);
            if (!dryRun) await Click("event-claimall", null, (_, _) => true, "the event's Claim All", ct);
            await Pause(2000, ct);
            await CloseWebPages(ct);
            log("autoplay: events: Claim All tapped on the one event");
            return 0;
        }
        var cards = (await Read(ct)).Targets.Count(t => t.Kind == "event-card" && t.Id >= 0);
        var tapped = 0;
        for (var k = 0; k < cards; k++)
        {
            await Click("event-card", t => t.Id == k, (s2, _) => Top(s2) == "SoloEventInfoWebView", $"event {k + 1} opens", ct, within: TimeSpan.FromSeconds(8));
            await Task.Delay(3500, ct); // the page loads
            if (dryRun) log("autoplay: WOULD TAP the event's Claim All (dry run)");
            else { await Click("event-claimall", null, (_, _) => true, "the event's Claim All", ct); tapped++; await Pause(2000, ct); }
            var s3 = await Settled(ct);
            if (Top(s3) is { } over && over != "SoloEventInfoWebView") { await Back(over, ct); await Pause(800, ct); }
            await CloseWebPages(ct);
            await Pause(1000, ct);
        }
        await Back("SoloEventsDialog", ct);
        log($"autoplay: events: Claim All tapped on {tapped} event(s)");
        return 0;
    }

    /// <summary>Every claim of an open pass or challenges screen, tab by tab.</summary>
    private async Task<int> ClaimOn(string view, string tab, bool dryRun, CancellationToken ct)
    {
        var claimed = 0;
        // A pass's challenges first (Daily, Weekly, Apprentice: their points), its Rewards (LevelReward) last, which those points unlock (Ehsan 2026-11-04).
        var tabs = (await Read(ct)).Targets.Where(t => t.Kind == tab).OrderBy(t => t.Name.Contains("LevelReward", StringComparison.Ordinal)).Select(t => t.Path).ToList();
        foreach (var path in tabs.DefaultIfEmpty(""))
        {
            if (path.Length > 0)
            {
                await Click(tab, t => t.Path == path, (_, _) => true, "the tab", ct);
                await Pause(1000, ct);
            }
            for (var i = 0; i < 12; i++)
            {
                var (_, now, _) = await Read(ct);
                var claim = now.FirstOrDefault(t => t.Kind == "pass-claim" && t.NotClickable is null);
                if (claim is null) break;
                if (dryRun) { log($"autoplay: WOULD CLAIM {claim.Name} (dry run)"); break; }
                await Click("pass-claim", t => t.Path == claim.Path, (_, _) => true, $"{claim.Name}", ct);
                await Pause(1500, ct);
                // A challenge's card opens its window: its own Claim takes the reward (closing it, as before, took nothing).
                var confirmed = Top(await ReadState(ct)) == "ChallengeQuestOverlay" && (await Read(ct)).Targets.Any(t => t.Kind == "challenge-confirm" && t.NotClickable is null);
                if (confirmed) await Click("challenge-confirm", null, (_, _) => true, "the challenge's Claim", ct);
                // The reward's popup is waited for by Settled (a fixed 2 s here made every claim take 6 s; Ehsan 2026-11-06: "very slow between the clicks").
                var s3 = await Settled(ct);
                // Collect All stays clickable with nothing left (Ehsan 2026-11-06: "it was clicking on claim 100 times while there was nothing to
                // claim"): a tap that brings no reward box took nothing, and the tab is done. A game notification on top (the Clan Boss's element) is no box.
                var box = Top(s3) is { } top && top != view && !top.EndsWith("NotificationView", StringComparison.Ordinal) ? top : null;
                if (box is null && !confirmed)
                {
                    log($"autoplay: {claim.Name} on {view}: nothing came of it, nothing left to claim on this tab");
                    break;
                }
                log($"autoplay: claimed {claim.Name} on {view}");
                claimed++;
                if (box is not null) await Back(box, ct);
                await Pause(800, ct);
            }
        }
        return claimed;
    }

    /// <summary>
    /// The game's Quick Join for an account without a clan (Ehsan 2026-11-06: "for a new account use quick clan join"): battle menu → the Clan Boss card →
    /// the "no clan" screen → Quick Join; the account says whether it worked. Nothing that creates a clan or costs is ever a target.
    /// </summary>
    public async Task<string> JoinClanAsync(bool dryRun, CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        if (data.Progress?.Clan is { AllianceId: not null } clan) return $"already in a clan ({clan.Name})";
        log("autoplay: STEP auto join clan: the account has no clan");
        await HomeAsync(Action("to the village"), ct);
        await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        await DragIntoView("mode-clan", ct);
        await Click("mode-clan", null, (s2, _) => Top(s2)?.EndsWith("AllianceLessDialog", StringComparison.Ordinal) == true || Has(s2, PlayTargets.ClanMapView),
            "the clan screen opens", ct, within: TimeSpan.FromSeconds(12));
        await Pause(1500, ct);
        var (s, targets, _) = await Read(ct);
        var dump = DumpScreen("no clan screen");
        log($"autoplay: the clan screen ({Top(s)}). Screen dump: {dump}");
        var join = targets.FirstOrDefault(t => t.Kind == "clan-quickjoin" && t.NotClickable is null);
        if (join is null)
        {
            await HomeAsync(Action("from the clan screen"), ct);
            return $"no Quick Join recognised on {Top(s)}: nothing pressed (screen dump {dump})";
        }
        if (dryRun)
        {
            await HomeAsync(Action("from the clan screen"), ct);
            return $"dry run: Quick Join ({join.Note}) shown, not pressed";
        }
        await Click("clan-quickjoin", null, (_, _) => true, "Quick Join", ct);
        await Task.Delay(4000, ct);
        var afterDump = DumpScreen("after Quick Join");
        var (after, _, _, _) = await Account(ct);
        await HomeAsync(Action("from the clan screen"), ct);
        return after.Progress?.Clan is { AllianceId: not null } joined
            ? $"joined the clan {joined.Name} [{joined.Tag}] with Quick Join"
            : $"Quick Join pressed, but the account has no clan yet (screen dump {afterDump})";
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
            await Pause(1500, ct);
            var (after, _, _) = await Read(ct);
            if (Top(after) != PlayTargets.VillageHudView)
            {
                await Back(Top(after)!, ct); // no gems waiting: the tap opened the Mine's window
                await Pause(2000, ct);
                await Settled(ct);
            }
        }
        await Click("village-shop", null, (s2, _) => Top(s2) == PlayTargets.ShopView, "the Shop opens", ct, within: TimeSpan.FromSeconds(10));
        await Pause(2000, ct);
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
            await Pause(800, ct);
            await Click("shop-claim", null, (s2, _) => Top(s2) != "StaticOfferInfoOverlay", $"{offer.Name} claimed", ct, within: TimeSpan.FromSeconds(8), shot: true);
            await Pause(2000, ct);
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
        // A piece whose price would take the silver under the floor is not started (2026-11-02: one went from 500K to 420K).
        var maxSilver = keepSilver > 0 ? data.Account.Resources.GetValueOrDefault("Silver") - keepSilver : double.MaxValue;
        if (ChoreRules.Upgrade(advice, pieces, minLevel, attempts, maxSilver) is not { } pick) return null;
        var piece = pieces[pick.Id];
        log($"autoplay: STEP upgrade {piece.SetName} {piece.Slot} #{pick.Id} +{piece.Level} to +{pick.Target}: {pick.Reason}");
        _upgrader ??= new NavigatingDriver(game, ui, null, folder, policy, log, upgrade: true);
        long? PriceOf(ArtifactOut a) => GearRating.AttemptPrice(a, rules);
        NavigatingDriver.UpgradeResult r;
        try { r = await _upgrader.UpgradePiece(pick.Id, pick.Slot, pick.Target, real: !dryRun, PriceOf, 1, 1, ct); }
        catch (NavigatingDriver.StopException e) { throw new PlayStopException(e.Message); }
        await HomeAsync(Action("from the upgrade"), ct);
        return new ArtifactOutcome($"#{pick.Id} {piece.Slot} +{r.From} to +{r.To} (target +{pick.Target}): {r.Outcome}, {r.Attempts} attempt(s), {r.SilverSpent:N0} silver", r.Attempts, r.To);
    }
}
