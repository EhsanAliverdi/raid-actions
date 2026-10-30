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
        return new EnergyAccount(data.Account.Resources, data.Progress?.StageCosts ?? [], data.Progress?.Areas ?? []);
    }

    /// <summary>The dungeon whose stage list this bot opened (as Faction Wars' crypt: a list it did not open is never trusted).</summary>
    private string? _openDungeon;

    public async Task<BattleOutcome> RunStageAsync(EnergyRun run, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var where = EnergyPlanner.Describe(run);
        log($"autoplay: STEP going to {where}");
        await OpenDungeon(run.Target.Region, ct);
        await SetDifficulty(run.Target.Difficulty, ct);

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

        await SetSuperRaid(run.Target.SuperRaid, teamView, ct);
        if (dryRun)
        {
            var (_, targets, _) = await Read(ct);
            var start = targets.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {where} (dry run: NOT pressed)", r, always: true);
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
        var result = await WaitBattle(ct, PlayTargets.DungeonResultView);
        await LeaveResult(ct, PlayTargets.DungeonResultView);
        var pve = result?.Pve;
        var outcome = pve?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

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
            // The dungeon's tile: its note names the region ("region 206 (DragonsLair)").
            bool Mine(UiTarget t) => t.Note?.Contains($"({region})", StringComparison.Ordinal) == true;
            if (targets.Any(t => t.Kind == "dungeon-region"))
            {
                if (!targets.Any(t => t.Kind == "dungeon-region" && Mine(t)))
                    throw new PlayStopException($"{region}: no such dungeon tile ({string.Join(", ", targets.Where(t => t.Kind == "dungeon-region").Select(t => t.Name))}). Screen dump: {DumpScreen("dungeon-region not found")}");
                await DragIntoView("dungeon-region", ct, Mine);
                await Click("dungeon-region", Mine, (_, scan) => scan().Targets.Any(t => t.Kind == "dungeon-stage"), $"{region}'s stages open", ct, within: TimeSpan.FromSeconds(15));
                _openDungeon = region;
                continue;
            }
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-dungeon", ct);
                await Click("mode-dungeon", null, (_, scan) => scan().Targets.Any(t => t.Kind == "dungeon-region"), "the dungeons open", ct, within: TimeSpan.FromSeconds(15));
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not open {region}'s stages in 12 steps. Screen dump: {DumpScreen("dungeon not reached")}");
    }

    /// <summary>The dungeon's difficulty switch set (a toggle whose note starts with the difficulty); none shown and Normal wanted: fine.</summary>
    private async Task SetDifficulty(string difficulty, CancellationToken ct)
    {
        var (_, targets, _) = await Read(ct);
        var mine = targets.FirstOrDefault(t => t.Kind == "dungeon-difficulty" && t.Note?.StartsWith(difficulty, StringComparison.OrdinalIgnoreCase) == true);
        if (mine is null)
        {
            if (difficulty == "Normal") return;
            throw new PlayStopException($"The dungeon's {difficulty} switch is not found. Screen dump: {DumpScreen("dungeon difficulty")}");
        }
        if (mine.Note!.EndsWith("on", StringComparison.Ordinal)) return;
        log($"autoplay: STEP switching the dungeon to {difficulty}");
        await Click("dungeon-difficulty", t => t.Path == mine.Path, (_, scan) => scan().Targets.Any(t => t.Kind == "dungeon-difficulty" && t.Path == mine.Path && t.Note!.EndsWith("on", StringComparison.Ordinal)),
            $"{difficulty} is on", ct);
        await Task.Delay(800, ct);
    }

    /// <summary>The team screen's Super Raid box set as the row says (none shown: fine only when the row does not want it).</summary>
    private async Task SetSuperRaid(bool wanted, string teamView, CancellationToken ct)
    {
        var (_, targets, _) = await Read(ct);
        var box = targets.FirstOrDefault(t => t.Kind == "super-raid" && t.View == teamView);
        if (box is null)
        {
            if (!wanted) return;
            throw new PlayStopException($"The Super Raid box is not found on the team screen. Screen dump: {DumpScreen("super raid")}");
        }
        if (box.Note == (wanted ? "on" : "off")) return;
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
