using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Energy on the live game (plan/energy.md): the account read (resources, stage costs, beaten stages), a dungeon run (village → Battle →
/// Dungeons → the dungeon's tile → difficulty → the stage → team screen with Super Raid as the row says → Start → result), the inbox's
/// energy. Built from the dump on 2026-10-30 with the game closed: **every target rule is to confirm with autoplay-explore** before
/// a dry run (Ehsan: no game use until he says so). Gem refills are not done live until the refill window is explored.
/// </summary>
public sealed partial class LivePlayGame : IEnergyGame
{
    public async Task<EnergyAccount> ReadAccountAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        // Champions a team screen refused count as locked (never food) for this run.
        var champions = data.Champions.Select(c => _notPickable.Contains(c.Id) ? c with { Locked = true } : c).ToList();
        return new EnergyAccount(data.Account.Resources, data.Progress?.StageCosts ?? [], data.Progress?.Areas ?? [], 2, champions,
            data.Inventory?.Shards.GetValueOrDefault("Mystery"));
    }

    /// <summary>The dungeon whose stage list this bot opened (as Faction Wars' crypt: a list it did not open is never trusted).</summary>
    private string? _openDungeon;

    /// <summary>A dungeon closed today offers to open it for gems (never paid): the window is closed and the row waits for another day.</summary>
    private const string PaidLocationView = "PaidLocationOpeningOverlay";

    private sealed class ClosedTodayException(string region) : Exception($"{region} is closed today")
    {
        public string Region => region;
    }

    /// <summary>The dungeons screen the battle menu's Dungeons card opens (seen live 2026-10-30).</summary>
    private const string DungeonsView = "DungeonsHUD";

    public async Task<BattleOutcome> RunStageAsync(EnergyRun run, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var where = EnergyPlanner.Describe(run);
        log($"autoplay: STEP going to {where}");
        var story = run.Target.Area == "Story";
        var resultView = story ? PlayTargets.StoryResultView : PlayTargets.DungeonResultView;
        if (story) await OpenCampaign(run.Region, run.Difficulty, run.StageId, ct);
        else
        {
            try { await OpenDungeon(run.Region, ct); }
            catch (ClosedTodayException) { return new BattleOutcome(BattleResult.NotStarted, "closed today (the game offers to open it for gems: never)"); }
            try { await SetDifficulty(run.Difficulty, run.StageId, ct); }
            catch (ClosedTodayException)
            {
                await Back(Top(await ReadState(ct))!, ct);
                _openDungeon = null;
                return new BattleOutcome(BattleResult.NotStarted, "not open today (the screen shows another day's stages)");
            }
        }

        var stage = await FindStage(run.Number, ct, "dungeon-stage", run.StageId);
        await Click("dungeon-stage", t => t.Id == stage.Id, (s2, _) => s2.Top?.Name is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView),
            $"the team screen for {where}", ct);
        var after = await Settled(ct);
        if (Top(after) == PlayTargets.RefillView)
        {
            // Not enough energy: the game offers a refill. Closed (refills are the planner's, not this click's).
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "not enough energy for this stage (the refill offer was closed)");
        }
        var teamView = Top(after) ?? "";
        if (!PlayTargets.IsTeamView(teamView)) await Refuse(after, where, ct);

        if (run.Team is { Count: > 0 } team && !await SetRunTeam(team, teamView, ct))
            return new BattleOutcome(BattleResult.Retry, $"food not pickable here, left out from now on: {string.Join(", ", _notPickable.Select(Who))}");
        await SetSuperRaid(run.Target.SuperRaid, teamView, ct);
        // An empty team screen (no team ever saved there; seen 2026-10-31 on the Hydra): nothing to start with, the row waits.
        if (((await Read(ct)).Play.TeamIds ?? []).Count == 0)
        {
            await Task.Delay(1000, ct);
            if (((await Read(ct)).Play.TeamIds ?? []).Count == 0)
            {
                await Back(teamView, ct);
                return new BattleOutcome(BattleResult.NotStarted, "no team saved there: set one in the game once");
            }
        }
        if (dryRun)
        {
            var (_, targets, _) = await Read(ct);
            var start = targets.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {where} (dry run: NOT pressed)", r, always: true);
            else log($"autoplay: Start not identified on {teamView}. Screen dump: {DumpScreen("team start")}");
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null ? "Start shown, not pressed" : $"Start not identified: {start?.Note ?? "no team-start target"}");
        }
        try
        {
            await Click("team-start", t => t.View == teamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
                "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        }
        catch (PlayStopException)
        {
            if (Top(await ReadState(ct)) != teamView) throw;
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "Start did nothing (not enough energy?)");
        }
        var inBattle = await ReadState(ct);
        if (Top(inBattle) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "not enough energy (the refill offer was closed)");
        }
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start {where}", ct);
        log($"autoplay: {where} started");
        var result = await WaitBattle(ct, resultView);
        await LeaveResult(ct, resultView);
        var pve = result?.Pve;
        var outcome = pve?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    /// <summary>The dungeon map's point name for a region (RegionTypeId name) where they differ (seen live 2026-10-31).</summary>
    private static string MapPoint(string region) => region switch
    {
        "IceGolemCave" => "IceGolemsPeak",
        "FireGolemCave" => "FireKnightsCastle",
        "SpiderCave" => "SpidersKeep",
        "ArtifactAscendDungeon" => "ArtifactAscend",
        "AccessoryAscendDungeon" => "AccessoryAscend",
        _ when region.EndsWith("DoubleAscendDungeon", StringComparison.Ordinal) => "DoubleAscend",
        _ => region,
    };

    /// <summary>The dungeon's stage list on top, from wherever the game is (village → Battle → Dungeons → the dungeon's tile).</summary>
    private async Task OpenDungeon(string region, CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 12; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.DungeonResultView)) { await WaitBattle(ct, PlayTargets.DungeonResultView); continue; }
            if (Has(s, PlayTargets.DungeonResultView)) { await LeaveResult(ct, PlayTargets.DungeonResultView); continue; }
            if (Top(s) is { } top && PlayTargets.IsTeamView(top)) { await Back(top, ct); continue; }
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "dungeon-stage"))
            {
                if (_openDungeon == region) return;
                _openDungeon = null;
                await Back(Top(s)!, ct); // a stage list this bot did not open (or another dungeon's): back to the dungeons
                continue;
            }
            // The dungeon's tile: its note names the map point ("region (DragonsLair)"); some points have their own names (seen 2026-10-31).
            var point = MapPoint(region);
            bool Mine(UiTarget t) => t.Note?.Contains($"({point})", StringComparison.Ordinal) == true;
            if (targets.Any(t => t.Kind == "dungeon-region"))
            {
                if (!targets.Any(t => t.Kind == "dungeon-region" && Mine(t)))
                    throw new PlayStopException($"{region}: no such dungeon tile ({string.Join(", ", targets.Where(t => t.Kind == "dungeon-region").Select(t => t.Name))}). Screen dump: {DumpScreen("dungeon-region not found")}");
                await DragIntoView("dungeon-region", ct, Mine);
                await Click("dungeon-region", Mine, (s2, scan) => !Has(s2, DungeonsView) || Top(s2) == PaidLocationView || scan().Targets.Any(t => t.Kind == "dungeon-stage"),
                    $"{region}'s stages open", ct, within: TimeSpan.FromSeconds(15));
                await Task.Delay(800, ct);
                if (Top(await ReadState(ct)) == PaidLocationView)
                {
                    await Back(PaidLocationView, ct);
                    throw new ClosedTodayException(region);
                }
                if (!(await Read(ct)).Item2.Any(t => t.Kind == "dungeon-stage"))
                    throw new PlayStopException($"{region} opened but no stage is recognised. Screen dump: {DumpScreen("dungeon stages not recognised")}");
                _openDungeon = region;
                continue;
            }
            // The dungeons screen (DungeonsHUD, seen live 2026-10-30) open but no tile recognised: the rule is to fix from the dump.
            if (Has(s, DungeonsView))
                throw new PlayStopException($"The dungeons screen is open but no dungeon tile is recognised. Screen dump: {DumpScreen("dungeon tiles not recognised")}");
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-dungeon", ct);
                await Click("mode-dungeon", null, (s2, scan) => Has(s2, DungeonsView) || scan().Targets.Any(t => t.Kind == "dungeon-region"), "the dungeons open", ct, within: TimeSpan.FromSeconds(15));
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not open {region}'s stages in 12 steps. Screen dump: {DumpScreen("dungeon not reached")}");
    }

    /// <summary>
    /// The dungeon's difficulty shown: the list shows one difficulty, whose stage ids are its own, so the wanted stage's id in the list
    /// means the right one. Otherwise the dropdown (bottom left, seen live 2026-10-30) is opened and the item named for it clicked.
    /// </summary>
    private async Task SetDifficulty(string difficulty, int? stageId, CancellationToken ct)
    {
        if (stageId is not { } sid) throw new PlayStopException($"No stage id for {difficulty}: the export has no stage costs for this dungeon.");
        for (var tries = 0; tries < 4; tries++)
        {
            var (_, targets, _) = await Read(ct);
            // A stage of the same difficulty in view is enough (stage ids are region, difficulty, number: 2062004 is Hard 4, 2069004 Normal 4); the wanted
            // stage may be further down the list (seen 2026-10-31: Void Keep 20, no difficulty switch at all).
            if (targets.Any(t => t.Kind == "dungeon-stage" && t.Id is { } id && id / 1000 == sid / 1000)) return;
            var item = targets.FirstOrDefault(t => t.Kind == "cc-difficulty-item" && t.NotClickable is null && SameName(t.Note, difficulty));
            if (item is not null)
            {
                log($"autoplay: STEP switching the dungeon to {difficulty}");
                await Click("cc-difficulty-item", t => t == item || t.Name == item.Name, (_, scan) => scan().Targets.Any(t => t.Kind == "dungeon-stage" && t.Id == sid),
                    $"the dungeon shows {difficulty}", ct, within: TimeSpan.FromSeconds(10));
                await Task.Delay(800, ct);
                continue;
            }
            // No difficulty switch and other stages shown: the screen is another dungeon's day (Iron Twins: one fortress a day; seen
            // 2026-10-31), so this one is not open today.
            if (!targets.Any(t => t.Kind == "dungeon-dropdown" && t.NotClickable is null) && targets.Any(t => t.Kind == "dungeon-stage"))
                throw new ClosedTodayException($"stage id {sid}");
            await Click("dungeon-dropdown", null, (_, scan) => scan().Targets.Any(t => t.Kind == "cc-difficulty-item"), "the difficulty list opens", ct, within: TimeSpan.FromSeconds(8));
            await Task.Delay(500, ct);
        }
        throw new PlayStopException($"The dungeon does not show {difficulty} (stage id {sid} not in the list). Screen dump: {DumpScreen("dungeon difficulty")}");

        // The export says "None" for Normal; the dropdown's text may carry more than the name.
        static bool SameName(string? text, string want) => text is { } x && (x.Trim().Contains(want, StringComparison.OrdinalIgnoreCase)
            || want == "Normal" && x.Trim().Contains("None", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The team screen's Super Raid box set as the row says (none shown: fine only when the row does not want it).</summary>
    private async Task SetSuperRaid(bool wanted, string teamView, CancellationToken ct)
    {
        // The box's state settles after the team screen opens (2026-10-30, Campaign: read "on", then "unavailable"): read after a pause.
        await Task.Delay(1200, ct);
        var (_, targets, _) = await Read(ct);
        var box = targets.FirstOrDefault(t => t.Kind == "super-raid" && t.View == teamView);
        if (box is null)
        {
            if (!wanted) return;
            throw new PlayStopException($"The Super Raid box is not found on the team screen. Screen dump: {DumpScreen("super raid")}");
        }
        if (box.Note == (wanted ? "on" : "off")) return;
        if (box.Note is not ("on" or "off"))
        {
            // Not offered now (seen 2026-10-31 on Campaign and Dragon's Lair: "unavailable", Super Raid seems to come and go with events):
            // a normal run instead of no run at all.
            if (wanted) log($"autoplay: Super Raid is {box.Note} here now: a normal run");
            return;
        }
        log($"autoplay: STEP Super Raid {(wanted ? "on" : "off")}");
        await Click("super-raid", t => t.Path == box.Path, (_, scan) => scan().Targets.Any(t => t.Kind == "super-raid" && t.Path == box.Path && t.Note == (wanted ? "on" : "off")),
            $"Super Raid {(wanted ? "on" : "off")}", ct);
    }

    public Task<double> CollectInboxAsync(string resource, bool dryRun, CancellationToken ct) =>
        resource == "Energy" ? CollectInbox(PlayTargets.IsEnergy, "Energy", "energy", dryRun, ct) : Task.FromResult(0d);

    /// <summary>Not done in the live game yet: the refill window's price must be explored first (plan/energy.md step 7).</summary>
    public Task<RefillResult> BuyRefillAsync(string resource, int maxGems, bool dryRun, CancellationToken ct) =>
        Task.FromResult(new RefillResult(false, 0, 0, "gem refills are not explored in the game yet (plan/energy.md step 7)"));
}
