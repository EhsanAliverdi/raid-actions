using System.Globalization;
using System.Text.RegularExpressions;
using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Campaign on the live game (raid-extractor plan/campaign.md): the chapter's stage list (village → Battle → Campaign → the chapter on
/// the campaign map), food rank-ups in the Tavern's Fast Rank Up, Mystery shards in the Portal. Built from the dump on 2026-10-30 with
/// the game in Ehsan's hands: **every target rule is to confirm with autoplay-explore** (from 03:00) before a dry run.
/// </summary>
public sealed partial class LivePlayGame
{
    /// <summary>The campaign map's HUD (the battle menu's Campaign card opens it; seen live 2026-10-30).</summary>
    private const string CampaignMapView = "MapHUD";

    /// <summary>The difficulty this bot set on the campaign map (null: not set this run; the game keeps the last one).</summary>
    private string? _campaignDifficulty;

    /// <summary>The Campaign chapter's stage list on top (its region, "Region12"), from wherever the game is.</summary>
    private async Task OpenCampaign(string region, string difficulty, int? stageId, CancellationToken ct)
    {
        var switched = 0;
        var key = "Story:" + region;
        var rid = int.TryParse(region.Replace("Region", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? 100 + c : 0;
        if (rid == 0) throw new PlayStopException($"{region}: not a Campaign chapter.");
        _nav.FitWindow();
        for (var hop = 0; hop < 12; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.StoryResultView)) { await WaitBattle(ct, PlayTargets.StoryResultView); continue; }
            if (Has(s, PlayTargets.StoryResultView)) { await LeaveResult(ct, PlayTargets.StoryResultView); continue; }
            if (Top(s) is { } top && PlayTargets.IsTeamView(top)) { await Back(top, ct); continue; }
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "dungeon-stage"))
            {
                // The difficulty is the map's (its Dropdown, bottom left; seen 2026-10-30): the wanted stage id in the list means the right one.
                if (_openDungeon == key && (stageId is null || targets.Any(t => t.Kind == "dungeon-stage" && t.Id == stageId))) return;
                if (_openDungeon == key && switched >= 2)
                    throw new PlayStopException($"{region} does not show {difficulty} (stage id {stageId} not in the list). Screen dump: {DumpScreen("campaign difficulty")}");
                if (_openDungeon == key) _campaignDifficulty = null; // the list shows another difficulty: set it on the map
                _openDungeon = null;
                await Back(Top(s)!, ct); // a stage list this bot did not open (or another chapter's, or difficulty): back to the map
                continue;
            }
            if (targets.Any(t => t.Kind == "campaign-region"))
            {
                bool Mine(UiTarget t) => t.Id == rid;
                var mine = targets.FirstOrDefault(t => t.Kind == "campaign-region" && Mine(t));
                if (mine is null)
                {
                    // The map shows a few chapters at a time: dragged towards the higher chapters (or back), then read again.
                    var shown = targets.Where(t => t.Kind == "campaign-region" && t.Id is not null).Select(t => t.Id!.Value).ToList();
                    if (shown.Count == 0) throw new PlayStopException($"The campaign map shows no chapter. Screen dump: {DumpScreen("campaign chapters")}");
                    await DragMap(rid > shown.Max() ? -1 : 1, ct);
                    continue;
                }
                if (mine.Note?.Contains("locked", StringComparison.Ordinal) == true) throw new PlayStopException($"{region} is locked in the game.");
                if (_campaignDifficulty != difficulty)
                {
                    // Set once per run (the map keeps it): the dropdown opened, the item named for the difficulty clicked.
                    switched++;
                    log($"autoplay: STEP the campaign map to {difficulty}");
                    var item = targets.FirstOrDefault(t => t.Kind == "cc-difficulty-item" && t.Note?.Contains(difficulty, StringComparison.OrdinalIgnoreCase) == true);
                    if (item is null)
                    {
                        await Click("dungeon-dropdown", null, (_, scan) => scan().Targets.Any(t => t.Kind == "cc-difficulty-item"), "the difficulty list opens", ct, within: TimeSpan.FromSeconds(8));
                        await Task.Delay(500, ct);
                        (_, targets, _) = await Read(ct);
                        item = targets.FirstOrDefault(t => t.Kind == "cc-difficulty-item" && t.Note?.Contains(difficulty, StringComparison.OrdinalIgnoreCase) == true)
                            ?? throw new PlayStopException($"The campaign map's difficulty list has no {difficulty} ({string.Join(", ", targets.Where(t => t.Kind == "cc-difficulty-item").Select(t => t.Note))}).");
                    }
                    await Click("cc-difficulty-item", t => t.Path == item.Path, (_, scan) => !scan().Targets.Any(t => t.Kind == "cc-difficulty-item"), $"the map shows {difficulty}", ct, within: TimeSpan.FromSeconds(8));
                    await Task.Delay(1500, ct);
                    _campaignDifficulty = difficulty;
                    continue;
                }
                await DragIntoView("campaign-region", ct, Mine);
                await Task.Delay(1200, ct); // the map glides after a drag
                await Click("campaign-region", Mine, (_, scan) => scan().Targets.Any(t => t.Kind == "dungeon-stage"), $"{region}'s stages open", ct, within: TimeSpan.FromSeconds(15), shot: true);
                await Task.Delay(800, ct);
                _openDungeon = key;
                continue;
            }
            // The campaign map (MapHUD, seen live 2026-10-30) open but no chapter recognised: the rule is to fix from the dump.
            if (Has(s, CampaignMapView))
                throw new PlayStopException($"The campaign map is open but no chapter is recognised. Screen dump: {DumpScreen("campaign chapters not recognised")}");
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-campaign", ct);
                await Click("mode-campaign", null, (s2, scan) => Has(s2, CampaignMapView) || scan().Targets.Any(t => t.Kind is "campaign-region" or "dungeon-stage"),
                    "the campaign map opens", ct, within: TimeSpan.FromSeconds(15));
                await Task.Delay(1000, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not open {region}'s stages in 12 steps. Screen dump: {DumpScreen("campaign not reached")}");
    }

    /// <summary>One sideways drag of a map (towards the higher chapters: -1, the content moves left).</summary>
    private async Task DragMap(int direction, CancellationToken ct)
    {
        var (w, why) = Game.Window.GameWindow.Find();
        if (w is null) throw new PlayStopException(why!);
        var dx = direction * w.Client.Width * 0.45f;
        var (x1, y1) = (w.Client.Width / 2f - dx / 2, w.Client.Height * 0.55f);
        if (Input.MouseInput.Drag(w, x1, y1, x1 + dx, y1) is { } notSent) throw new PlayStopException("Not dragged: " + notSent);
        log($"autoplay: dragged the map {dx:0} px");
        await Task.Delay(700, ct);
    }

    /// <summary>
    /// The team screen's squad set to the run's team (the farmer first, then food), Cursed City's list search; a champion the screen
    /// will not take stops the turn.
    /// </summary>
    /// <summary>Champions a team screen refused (not food for this run's planning).</summary>
    private readonly HashSet<int> _notPickable = [];

    /// <returns>False when a planned champion could not be picked (the team screen closed; the next plan leaves it out).</returns>
    private async Task<bool> SetRunTeam(IReadOnlyList<int> team, string teamView, CancellationToken ct)
    {
        log($"autoplay: STEP team: {string.Join(", ", team.Select(Who))}");
        try { await SetTeam(team, ct, teamView); }
        catch (LockedHeroException e)
        {
            // Food the screen will not take (state 99 seen 2026-10-31): never planned again this run, nor any other the list showed so.
            _notPickable.Add(e.HeroId);
            foreach (var (id, _) in e.Shown) if (!team.Take(1).Contains(id)) _notPickable.Add(id);
            await Back(teamView, ct);
            return false;
        }
        catch (Exception e) when (e is not PlayStopException and not OperationCanceledException)
        {
            throw new PlayStopException($"The team could not be set: {e.Message}. Screen dump: {DumpScreen("team not set")}");
        }
        return true;
    }

    public async Task<int> RankUpFoodAsync(IReadOnlyList<RankUpPlan> rankUps, IReadOnlySet<int> pool, bool dryRun, CancellationToken ct)
    {
        log($"autoplay: STEP food rank-ups: {string.Join(", ", rankUps.Select(r => $"{Who(r.HeroId)} {r.Rank}* with {string.Join("+", r.FoodIds.Select(Who))}"))}");
        await OpenFastRankUp(ct);
        var done = 0;
        var levels = (await Account(ct)).Data.Champions.ToDictionary(c => c.Id, c => c.Level);
        await FoodVaultOff(ct);
        foreach (var grade in rankUps.Select(r => r.Rank).Distinct().Order())
        {
            await SelectGrade(grade, ct);
            // Only food: every champion the game ticked, and every food it picked, must be in the pool; anything else is unticked.
            // Champions only a team screen refused (state 99) are still food here. The food the game picked must never have been levelled
            // (Ehsan 2026-10-31: it took a level 16 common for a rank-up): any other is unticked with its rank-up.
            bool Food(int id) => pool.Contains(id) || _notPickable.Contains(id);
            bool Eatable(int id) => Food(id) && levels.TryGetValue(id, out var lv) && lv <= 1;
            bool Fine(RankUpItemRead r) => Food(r.HeroId) && r.FoodIds.Count > 0 && r.FoodIds.All(Eatable);
            for (var i = 0; i < 12; i++)
            {
                var (_, _, play) = await Read(ct);
                var wrong = (play.RankUps ?? []).FirstOrDefault(r => r.Active == true && !Fine(r));
                if (wrong is null) break;
                log($"autoplay: {Who(wrong.HeroId)} (food {string.Join(", ", wrong.FoodIds.Select(id => $"{Who(id)} L{levels.GetValueOrDefault(id)}"))}) is not food or eats levelled food: unticked");
                await Click("fastrankup-item", t => t.Id == wrong.HeroId, (_, scan) => scan().RankUps?.FirstOrDefault(r => r.HeroId == wrong.HeroId)?.Active != true,
                    $"{Who(wrong.HeroId)} unticked", ct);
            }
            var (_, targets, now) = await Read(ct);
            var ticked = (now.RankUps ?? []).Where(r => r.Active == true).ToList();
            if (ticked.Count == 0 || ticked.Any(r => !Fine(r)))
            {
                log($"autoplay: grade {grade}: nothing to rank up within the food ({(now.RankUps ?? []).Count} listed). Screen dump: {DumpScreen("fast rank up: nothing within the food")}");
                continue;
            }
            var go = targets.FirstOrDefault(t => t.Kind == "fastrankup-go");
            log($"autoplay: grade {grade}: ranking up {string.Join(", ", ticked.Select(r => Who(r.HeroId)))} ({go?.Note})");
            if (dryRun)
            {
                if (go?.Rect is { } r) Shot($"autoplay: WOULD CLICK Rank Up, grade {grade} (dry run: NOT pressed)", r, always: true);
                continue;
            }
            var ids = ticked.Select(r => r.HeroId).ToHashSet();
            var ranks = (await Account(ct)).Data.Champions.Where(c => ids.Contains(c.Id)).ToDictionary(c => c.Id, c => c.Rank);
            // The list keeps the ranked-up champions shown (seen 2026-10-31): the click is checked on the account, after a short wait.
            try
            {
                await Click("fastrankup-go", null, (s2, _) => Top(s2) == UiState.MessageBoxView, "the champions ranked up", ct, within: TimeSpan.FromSeconds(4), shot: true,
                    before: p => (p.RankUps ?? []).Any(r => r.Active == true && !Fine(r)) ? "a champion that is not food is ticked" : null);
            }
            catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal)) { }
            var after = await Settled(ct);
            if (Top(after) == UiState.MessageBoxView)
            {
                // A confirmation the dump does not tell (artifacts to remove, missing silver, ...): not answered yet; closed, the step stops.
                var dump = DumpScreen("rank-up message box");
                await Back(UiState.MessageBoxView, ct);
                throw new PlayStopException($"Fast Rank Up asked something (closed, nothing ranked up). Screen dump: {dump}");
            }
            var now2 = (await Account(ct)).Data.Champions.ToDictionary(c => c.Id, c => c.Rank);
            var up = ids.Count(id => now2.TryGetValue(id, out var r) && r > ranks.GetValueOrDefault(id));
            if (up == 0) throw new PlayStopException($"Rank Up was pressed but no champion's rank changed in the account. Screen dump: {DumpScreen("rank-up not done")}");
            done += up;
            log($"autoplay: ranked up {up} champion(s) of grade {grade}");
            await Task.Delay(1500, ct);
        }
        await _nav.GoHome(Action("from the Tavern"), ct);
        return done;
    }

    /// <summary>
    /// Fast Rank Up without the vaults (2026-10-31: a maxed 2★ in the vault took the food a maxed food champion needed): the filter panel
    /// opened, each ticked vault box unticked, the panel closed.
    /// </summary>
    private async Task FoodVaultOff(CancellationToken ct)
    {
        var (_, targets, _) = await Read(ct);
        if (!targets.Any(t => t.Kind == "team-filter-vault"))
        {
            await Click("fastrankup-filter", null, (_, scan) => scan().Targets.Any(t => t.Kind == "team-filter-vault"), "the food filter opens", ct, within: TimeSpan.FromSeconds(6));
            await Task.Delay(800, ct);
        }
        var changed = false;
        for (var i = 0; i < 4; i++)
        {
            var (_, now, _) = await Read(ct);
            var on = now.FirstOrDefault(t => t.Kind == "team-filter-vault" && t.Note == "on" && t.NotClickable is null);
            if (on is null) break;
            await Click("team-filter-vault", t => t.Path == on.Path, (_, scan) => scan().Targets.Any(t => t.Kind == "team-filter-vault" && t.Path == on.Path && t.Note == "off"),
                $"{on.Name} unticked", ct, within: TimeSpan.FromSeconds(5));
            log($"autoplay: unticked {on.Name}");
            changed = true;
        }
        log($"autoplay: Fast Rank Up's food filter: vaults {(changed ? "now left out" : "already left out")}");
        // The panel closed by its Apply (seen 2026-10-31) when it is open; it stays loaded when closed, so "open" is its Apply in view.
        static bool Open(UiTarget t) => t.Kind == "food-filter-apply" && t.NotClickable is null && t.FullyVisible
            && t.Rect is { } r && t.ClientWidth is { } w && r.X >= 0 && r.X + r.Width <= w;
        var (_, after, _) = await Read(ct);
        if (after.Any(Open))
        {
            await Click("food-filter-apply", Open, (_, _) => true, "the food filter closes", ct);
            await Task.Delay(1200, ct);
        }
    }

    /// <summary>The Tavern's Fast Rank Up on top (village → Tavern → Rank Up tab → Fast Rank Up).</summary>
    private async Task OpenFastRankUp(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "fastrankup-grade")) return;
            if (Has(s, PlayTargets.TavernView))
            {
                if (targets.FirstOrDefault(t => t.Kind == "tavern-rankup-tab") is { Note: "off" })
                {
                    await Click("tavern-rankup-tab", null, (_, scan) => scan().Targets.Any(t => t.Kind == "tavern-rankup-tab" && t.Note == "on"), "the Rank Up tab", ct);
                    continue;
                }
                await Click("tavern-fast-rankup", null, (_, scan) => scan().Targets.Any(t => t.Kind == "fastrankup-grade"), "Fast Rank Up opens", ct, within: TimeSpan.FromSeconds(10));
                continue;
            }
            // The champion screen's Tavern button (the village's Tavern building has no screen rect).
            if (targets.Any(t => t.Kind == "champion-tavern"))
            {
                await Click("champion-tavern", null, (s2, _) => Has(s2, PlayTargets.TavernView), "the Tavern opens", ct, within: TimeSpan.FromSeconds(10));
                continue;
            }
            if (Has(s, "HeroesInfoOverlay"))
                throw new PlayStopException($"The champion screen is open but its Tavern button is not recognised ("
                    + string.Join(" | ", (await Read(ct)).Play.Notes.Where(n => n.StartsWith("champion screen", StringComparison.Ordinal)))
                    + $"). Screen dump: {DumpScreen("champion tavern")}");
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            if (targets.Any(t => t.Kind == "village-tavern" && t.NotClickable is null))
                await Click("village-tavern", null, (s2, _) => Has(s2, PlayTargets.TavernView), "the Tavern opens", ct, within: TimeSpan.FromSeconds(10));
            else
                await Click("village-heroes", null, (s2, _) => Has(s2, "HeroesInfoOverlay"), "the champion screen opens", ct, within: TimeSpan.FromSeconds(10));
        }
        throw new PlayStopException($"Fast Rank Up not reached in 10 steps. Screen dump: {DumpScreen("fast rank up not reached")}");
    }

    private async Task SelectGrade(int grade, CancellationToken ct)
    {
        var (_, targets, _) = await Read(ct);
        var tab = targets.FirstOrDefault(t => t.Kind == "fastrankup-grade" && t.Id == grade)
            ?? throw new PlayStopException($"Fast Rank Up shows no grade {grade} ({string.Join(", ", targets.Where(t => t.Kind == "fastrankup-grade").Select(t => $"{t.Id}: {t.Note}"))}).");
        if (tab.Note?.StartsWith("selected", StringComparison.Ordinal) == true) return;
        await Click("fastrankup-grade", t => t.Id == grade, (_, scan) => scan().Targets.Any(t => t.Kind == "fastrankup-grade" && t.Id == grade
            && t.Note?.StartsWith("selected", StringComparison.Ordinal) == true), $"grade {grade} selected", ct);
        await Task.Delay(600, ct);
    }

    public async Task<int> SummonMysteryAsync(int count, bool dryRun, CancellationToken ct)
    {
        log($"autoplay: STEP opening up to {count} Mystery shard(s) for food");
        await OpenPortal(ct);
        const int Mystery = 1;
        var opened = 0;
        for (var k = 0; k < count; k++)
        {
            var (_, targets, play) = await Read(ct);
            if (play.SelectedShard != Mystery)
            {
                await Click("portal-shard", t => t.Id == Mystery, (_, scan) => scan().SelectedShard == Mystery, "Mystery shards selected", ct);
                (_, targets, play) = await Read(ct);
            }
            var left = targets.FirstOrDefault(t => t.Kind == "portal-shard" && t.Id == Mystery)?.Note is { } note
                && Regex.Match(note, @"count (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            if (left is not > 0) { log($"autoplay: no Mystery shard left ({left?.ToString(CultureInfo.InvariantCulture) ?? "count not read"})"); break; }
            var summon = targets.FirstOrDefault(t => t.Kind == "portal-summon1");
            if (dryRun)
            {
                if (summon?.Rect is { } r) Shot($"autoplay: WOULD CLICK Summon x1 with a Mystery shard ({left} left; dry run: NOT pressed)", r, always: true);
                else log($"autoplay: Summon x1 not identified: {summon?.Note ?? "no portal-summon1 target"}");
                break;
            }
            await Click("portal-summon1", null, (_, scan) => !scan().Targets.Any(t => t.Kind == "portal-shard" && t.Id == Mystery && t.Note == $"type {Mystery}, count {left}, selected"),
                "a champion summoned", ct, within: TimeSpan.FromSeconds(20), shot: true,
                before: p => p.SelectedShard == Mystery ? null : $"the selected shard is {p.SelectedShard}, not Mystery");
            opened++;
            await Task.Delay(2500, ct); // the summon's show starts after the click
            // The new champion's card (PortalHeroInfoDialog, seen 2026-10-31) slides in: its close is tried until it is on screen.
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(1500, ct);
                var (s2, t2, _) = await Read(ct);
                if (Top(s2) == "PortalDialog" && t2.Any(t => t.Kind == "portal-summon1" && t.NotClickable is null)) break;
                if (Top(s2) is { } top && top != "PortalDialog")
                {
                    try { await Back(top, ct); }
                    catch (PlayStopException e) { log($"autoplay: {top} not closed yet ({e.Message}); trying again"); }
                }
            }
        }
        // Home again; the last champion's card may still be sliding in: one more try after a pause.
        try { await _nav.GoHome(Action("from the Portal"), ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"autoplay: going home from the Portal failed once ({e.Message}); trying again");
            await Task.Delay(2500, ct);
            await _nav.GoHome(Action("from the Portal"), ct);
        }
        return opened;
    }

    private string GearBaselineFile => Path.Combine(ExtractorOutput, "autoplay", "campaign-gear-baseline.json");

    public async Task<int> SellCampaignGearAsync(GearSellSettings settings, bool dryRun, CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        // The farm's start: gear with a higher id dropped since (kept in a file so a restart does not sell older gear).
        int baseline;
        try { baseline = int.Parse(File.ReadAllText(GearBaselineFile).Trim(), CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            baseline = data.Artifacts.Count == 0 ? 0 : data.Artifacts.Max(a => a.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(GearBaselineFile)!);
            File.WriteAllText(GearBaselineFile, baseline.ToString(CultureInfo.InvariantCulture));
            log($"autoplay: Campaign gear from now on (artifact ids above {baseline}) may be sold");
            return 0;
        }
        var pieces = GearSale.Pick(data.Artifacts, baseline, settings);
        if (pieces.Count < settings.Every) return 0;
        log($"autoplay: STEP selling {pieces.Count} Campaign drop(s): {string.Join(", ", pieces.Select(a => $"#{a.Id} {a.Rank}* {a.Rarity} {a.Slot}"))}");
        var plan = RaidExtractor.Battle.SellPlan.Make(data, pieces.Select(a => (a, "Campaign drop (Auto Play)")), $"sell {pieces.Count} campaign drops");
        var (saved, path) = plan.SaveNew(ExtractorOutput, data);
        await RunCommand(["sell", path, "--confirm", saved.Code, .. (dryRun ? new[] { "--dry-run" } : [])], ct);
        return dryRun ? 0 : pieces.Count;
    }

    /// <summary>The Portal's summon tab on top (village → Portal).</summary>
    private async Task OpenPortal(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 6; hop++)
        {
            var s = await Settled(ct);
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "portal-shard")) return;
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            try
            {
                await Click("village-portal", null, (s2, scan) => !Input.NavigatingDriver.AtVillage(s2) || scan().Targets.Any(t => t.Kind == "portal-shard"), "the Portal opens", ct,
                    within: TimeSpan.FromSeconds(15), shot: true);
            }
            catch (PlayStopException) { }
            await Task.Delay(1500, ct);
            var (s3, t3, _) = await Read(ct);
            if (t3.Any(t => t.Kind == "portal-shard")) return;
            // The spot opened something else (the village was scrolled?): closed, and no more tries.
            if (!Input.NavigatingDriver.AtVillage(s3) && Top(s3) is { } other)
            {
                var dump = DumpScreen("portal spot opened something else");
                await Back(other, ct);
                throw new PlayStopException($"The Portal's spot opened {other}, not the Portal (closed). Screen dump: {dump}");
            }
            throw new PlayStopException($"A click on the Portal's spot opened nothing. Screen dump: {DumpScreen("portal spot")}");
        }
        throw new PlayStopException($"The Portal's shards were not reached. Screen dump: {DumpScreen("portal not reached")}");
    }
}
