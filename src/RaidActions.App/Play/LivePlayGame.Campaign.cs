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
    /// <summary>The Campaign chapter's stage list on top (its region, "Region12"), from wherever the game is.</summary>
    private async Task OpenCampaign(string region, CancellationToken ct)
    {
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
                if (_openDungeon == key) return;
                _openDungeon = null;
                await Back(Top(s)!, ct); // a stage list this bot did not open (or another chapter's): back to the map
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
                await DragIntoView("campaign-region", ct, Mine);
                await Click("campaign-region", Mine, (_, scan) => scan().Targets.Any(t => t.Kind == "dungeon-stage"), $"{region}'s stages open", ct, within: TimeSpan.FromSeconds(15));
                await Task.Delay(800, ct);
                _openDungeon = key;
                continue;
            }
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-campaign", ct);
                await Click("mode-campaign", null, (_, scan) => scan().Targets.Any(t => t.Kind is "campaign-region" or "dungeon-stage"), "the campaign map opens", ct,
                    within: TimeSpan.FromSeconds(15));
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
    private async Task SetRunTeam(IReadOnlyList<int> team, string teamView, CancellationToken ct)
    {
        log($"autoplay: STEP team: {string.Join(", ", team.Select(Who))}");
        try { await SetTeam(team, ct, teamView); }
        catch (Exception e) when (e is not PlayStopException and not OperationCanceledException)
        {
            throw new PlayStopException($"The team could not be set: {e.Message}. Screen dump: {DumpScreen("team not set")}");
        }
    }

    public async Task<int> RankUpFoodAsync(IReadOnlyList<RankUpPlan> rankUps, IReadOnlySet<int> pool, bool dryRun, CancellationToken ct)
    {
        log($"autoplay: STEP food rank-ups: {string.Join(", ", rankUps.Select(r => $"{Who(r.HeroId)} {r.Rank}* with {string.Join("+", r.FoodIds.Select(Who))}"))}");
        await OpenFastRankUp(ct);
        var done = 0;
        foreach (var grade in rankUps.Select(r => r.Rank).Distinct().Order())
        {
            await SelectGrade(grade, ct);
            // Only food: every champion the game ticked, and every food it picked, must be in the pool; anything else is unticked.
            bool Fine(RankUpItemRead r) => pool.Contains(r.HeroId) && r.FoodIds.Count > 0 && r.FoodIds.All(pool.Contains);
            for (var i = 0; i < 12; i++)
            {
                var (_, _, play) = await Read(ct);
                var wrong = (play.RankUps ?? []).FirstOrDefault(r => r.Active == true && !Fine(r));
                if (wrong is null) break;
                log($"autoplay: {Who(wrong.HeroId)} (food {string.Join(", ", wrong.FoodIds.Select(Who))}) is not food: unticked");
                await Click("fastrankup-item", t => t.Id == wrong.HeroId, (_, scan) => scan().RankUps?.FirstOrDefault(r => r.HeroId == wrong.HeroId)?.Active != true,
                    $"{Who(wrong.HeroId)} unticked", ct);
            }
            var (_, targets, now) = await Read(ct);
            var ticked = (now.RankUps ?? []).Where(r => r.Active == true).ToList();
            if (ticked.Count == 0 || ticked.Any(r => !Fine(r)))
            {
                log($"autoplay: grade {grade}: nothing to rank up within the food ({(now.RankUps ?? []).Count} listed)");
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
            await Click("fastrankup-go", null, (s2, scan) => Top(s2) == UiState.MessageBoxView || !(scan().RankUps ?? []).Any(r => ids.Contains(r.HeroId) && r.Active == true),
                "the champions ranked up", ct, within: TimeSpan.FromSeconds(15), shot: true,
                before: p => (p.RankUps ?? []).Any(r => r.Active == true && !Fine(r)) ? "a champion that is not food is ticked" : null);
            var after = await Settled(ct);
            if (Top(after) == UiState.MessageBoxView)
            {
                // A confirmation the dump does not tell (artifacts to remove, missing silver, ...): not answered yet; closed, the step stops.
                var dump = DumpScreen("rank-up message box");
                await Back(UiState.MessageBoxView, ct);
                throw new PlayStopException($"Fast Rank Up asked something (closed, nothing ranked up). Screen dump: {dump}");
            }
            done += ticked.Count;
            log($"autoplay: ranked up {ticked.Count} champion(s) of grade {grade}");
            await Task.Delay(1500, ct);
        }
        await _nav.GoHome(Action("from the Tavern"), ct);
        return done;
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
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-tavern", null, (s2, _) => Has(s2, PlayTargets.TavernView), "the Tavern opens", ct, within: TimeSpan.FromSeconds(10));
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
            // The summon's show: taps and backs until the summon tab is back (a new champion's card, "summon again", ...).
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(1500, ct);
                var (s2, t2, _) = await Read(ct);
                if (t2.Any(t => t.Kind == "portal-summon1" && t.NotClickable is null)) break;
                if (Top(s2) is { } top) await Back(top, ct);
            }
        }
        await _nav.GoHome(Action("from the Portal"), ct);
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
            await Click("village-portal", null, (_, scan) => scan().Targets.Any(t => t.Kind == "portal-shard"), "the Portal opens", ct, within: TimeSpan.FromSeconds(15));
        }
        throw new PlayStopException($"The Portal's shards were not reached. Screen dump: {DumpScreen("portal not reached")}");
    }
}
