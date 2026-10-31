using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Doom Tower on the live game (raid-extractor plan/doom-tower.md): village → Battle → the Doom Tower card → the tower map (its floors,
/// rooms and bosses read from DoomTowerMapItemContext) → the stage → its Battle (when a panel opens first) → team screen (the team the
/// game shows) → Start → the battle → result. Built from the dump on 2026-10-31; every rule is to confirm with a dry run.
/// </summary>
public sealed partial class LivePlayGame : IDoomTowerGame
{
    private bool _dtDumped;

    /// <summary>
    /// The top boss: the only stage the bot reaches. The tower is a 3D scene with no screen rectangles; with every floor beaten the map opens
    /// on this boss (seen 2026-10-31), a fixed spot, and a click opens its team screen.
    /// </summary>
    private const int FinalBoss = 120;

    public async Task<DoomTowerMap> ReadDoomTowerAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        await OpenDoomTower(ct);
        var (_, targets, _) = await Read(ct);
        if (!_dtDumped) { _dtDumped = true; log($"autoplay: the Doom Tower map. Screen dump: {DumpScreen("doom tower map")}"); }
        var stages = targets.Where(t => t.Kind == "dt-stage" && t.Id is > 0).GroupBy(t => t.Id!.Value).Select(g =>
        {
            var t = g.First();
            var status = t.Note is { } n && System.Text.RegularExpressions.Regex.Match(n, @"status (\d)") is { Success: true } m ? m.Groups[1].Value[0] - '0' : 0;
            var id = g.Key;
            return new DtStage(id, DoomTowerRules.KindOf(id, false), status, DoomTowerRules.FloorOf(id), false);
        }).Where(s => s.Kind == DtKind.Boss && s.Floor == FinalBoss).ToList();
        log($"autoplay: the tower: floor {FinalBoss} " + (stages.Count == 0 ? "not read" : $"status {stages[0].Status}"));
        var r = data.Account.Resources;
        return new DoomTowerMap(stages, r.GetValueOrDefault(DoomTowerRules.Gold), r.GetValueOrDefault(DoomTowerRules.Silver));
    }

    public async Task<BattleOutcome> FightDoomTowerAsync(DtStage stage, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var name = DoomTowerRules.Name(stage);
        log($"autoplay: STEP Doom Tower {name}");
        await OpenDoomTower(ct);
        await Click("dt-final-boss", null, (s2, _) => s2.Top?.Name is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView),
            $"{name} opens", ct, within: TimeSpan.FromSeconds(10));
        await Task.Delay(800, ct);
        var after = await Settled(ct);
        if (Top(after) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "no key (the refill offer was closed)");
        }
        var teamView = Top(after) ?? "";
        if (!PlayTargets.IsTeamView(teamView)) await Refuse(after, $"Doom Tower {name}", ct);
        if (((await Read(ct)).Play.TeamIds ?? []).Count == 0)
        {
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "no team saved there: set one in the game once");
        }
        if (dryRun)
        {
            var (_, t2, _) = await Read(ct);
            var start = t2.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
            if (start?.Rect is { } rect) Shot($"autoplay: WOULD CLICK Start, Doom Tower {name} (dry run: NOT pressed)", rect, always: true);
            else log($"autoplay: Start not identified on {teamView}. Screen dump: {DumpScreen("doom tower start")}");
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null ? "Start shown, not pressed" : $"Start not identified: {start?.Note ?? "no team-start target"}");
        }
        await Click("team-start", t => t.View == teamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        var inBattle = await ReadState(ct);
        if (Top(inBattle) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "no key (the refill offer was closed)");
        }
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start Doom Tower {name}", ct);
        log($"autoplay: Doom Tower {name} started");
        var result = await WaitBattle(ct, PlayTargets.DtResultView);
        await LeaveResult(ct, PlayTargets.DtResultView);
        var outcome = result?.Pve?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    /// <summary>The tower map on top (its stages read), from wherever the game is.</summary>
    private async Task OpenDoomTower(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.DtResultView)) { await WaitBattle(ct, PlayTargets.DtResultView); continue; }
            if (Top(s) is { } fin && fin.StartsWith("BattleFinish", StringComparison.Ordinal)) { await LeaveResult(ct, fin); continue; }
            if (Top(s) is { } top && PlayTargets.IsTeamView(top)) { await Back(top, ct); continue; }
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "dt-stage")) return;
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-dt", ct);
                if (!(await Read(ct)).Targets.Any(t => t.Kind == "mode-dt"))
                    throw new PlayStopException($"The battle menu shows no Doom Tower card. Screen dump: {DumpScreen("doom tower card")}");
                await Click("mode-dt", null, (_, scan) => scan().Targets.Any(t => t.Kind == "dt-stage"), "the Doom Tower opens", ct, within: TimeSpan.FromSeconds(15));
                await Task.Delay(1200, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"The Doom Tower was not reached in 10 steps. Screen dump: {DumpScreen("doom tower not reached")}");
    }
}
