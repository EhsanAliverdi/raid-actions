using RaidActions.App.Input;
using RaidActions.Game.Window;
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
        // Choosing a difficulty moves the map, so only when the bot left it on Hard; a miss (Hard left from elsewhere) chooses Normal and tries once more.
        if (_towerOnHard) await SelectTowerDifficulty("Normal", ct);
        try
        {
            await Click("dt-final-boss", null, (s2, _) => s2.Top?.Name is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView),
                $"{name} opens", ct, within: TimeSpan.FromSeconds(10));
        }
        catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal))
        {
            // The map opens on the floor last played (2026-11-02: floor 80 of Normal), so the top is dragged to, where the Final Boss has its spot.
            log("autoplay: the Final Boss's spot missed: choosing Normal, going to the top of the tower, trying again");
            await SelectTowerDifficulty("Normal", ct);
            var (w, why) = GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            var scale = w.Client.Width / 1298f;
            for (var i = 0; i < 12; i++)
            {
                if (MouseInput.Drag(w, 760 * scale, 130 * scale, 760 * scale, 640 * scale) is { } notSent) throw new PlayStopException(notSent);
                await Pause(500, ct);
            }
            await Pause(1500, ct);
            await Click("dt-final-boss", null, (s2, _) => s2.Top?.Name is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView),
                $"{name} opens", ct, within: TimeSpan.FromSeconds(10));
        }
        return await PlayTowerTeam(name, started, dryRun, ct);
    }

    private bool _towerOnHard;

    /// <summary>The Normal or Hard tower on the map; the game keeps the last one chosen.</summary>
    private async Task SelectTowerDifficulty(string name, CancellationToken ct)
    {
        await Click("dt-difficulty", null, (_, scan) => scan().Targets.Any(t => t.Kind == "dt-difficulty-item"), "the difficulty list opens", ct, within: TimeSpan.FromSeconds(6));
        await Pause(500, ct);
        await Click("dt-difficulty-item", t => t.Name.EndsWith(name, StringComparison.Ordinal), (_, scan) => !scan().Targets.Any(t => t.Kind == "dt-difficulty-item"), $"{name} chosen", ct, within: TimeSpan.FromSeconds(6));
        await Task.Delay(5000, ct); // the map redraws and moves to its current floor
        _towerOnHard = name == "Hard";
    }

    /// <summary>
    /// One floor of Hard: the gold banner found on the screen and tapped (the floors have no rectangles). A boss floor takes a gold key, and only for a win.
    /// </summary>
    public async Task<BattleOutcome> ClimbDoomTowerAsync(bool hard, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var tower = hard ? "Hard" : "Normal";
        log($"autoplay: STEP Doom Tower: climbing {tower}");
        await OpenDoomTower(ct);
        await SelectTowerDifficulty(tower, ct);
        var (w, why) = GameWindow.Find();
        if (w is null) throw new PlayStopException(why!);
        var (capture, problem) = WindowCapture.Take(w);
        if (capture is null) throw new PlayStopException($"The tower could not be captured: {problem}");
        (float X, float Y)? spot;
        using (capture)
        {
            spot = DoomTowerSpot.Find(capture.Image);
            if (spot is null) { var saved = Path.Combine(folder, $"tower-{tower.ToLowerInvariant()}-{DateTime.Now:HHmmss}.png"); OpenCvSharp.Cv2.ImWrite(saved, capture.Image); log($"autoplay: no gold floor found; the map as seen: {saved}"); }
        }
        if (spot is not { } at)
        {
            await SelectTowerDifficulty("Normal", ct);
            return new BattleOutcome(BattleResult.NotStarted, $"no gold floor on the {tower} map");
        }
        log($"autoplay: the {tower} floor to play at ({at.X:0},{at.Y:0})");
        if (MouseInput.Click(w, at.X, at.Y, new Random()) is { } notSent) throw new PlayStopException(notSent);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until && !(Top(await ReadState(ct)) is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView))) await Task.Delay(500, ct);
        var opened = Top(await ReadState(ct));
        if (!(opened is not null && (PlayTargets.IsTeamView(opened) || opened is PlayTargets.RefillView or UiState.MessageBoxView)))
        {
            await SelectTowerDifficulty("Normal", ct);
            return new BattleOutcome(BattleResult.NotStarted, $"the floor did not open ({opened})");
        }
        return await PlayTowerTeam($"{tower} floor", started, dryRun, ct);
    }

    /// <summary>From the stage's team screen to the result.</summary>
    private async Task<BattleOutcome> PlayTowerTeam(string name, DateTime started, bool dryRun, CancellationToken ct)
    {
        await Pause(800, ct);
        var after = await Settled(ct);
        if (Top(after) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "no key (the refill offer was closed)");
        }
        var teamView = Top(after) ?? "";
        if (!PlayTargets.IsTeamView(teamView)) await Refuse(after, $"Doom Tower {name}", ct);
        if (!await ApplyDefaultTeam("doom-tower", teamView, ct)) return new BattleOutcome(BattleResult.NotStarted, "the Teams card's team was refused here");
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
        try
        {
            await Click("team-start", t => t.View == teamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
                "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        }
        catch (PlayStopException) when (game.ReadUi().Item1 is { } now && Top(now) == teamView)
        {
            // Start with no key left does nothing (2026-11-12 05:57-07:38, BlackLord65: the count read 1, Start showed its key, four tries, each an error
            // that paused the tower): not an error; the tower waits for its keys.
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "Start did nothing (no key left?)");
        }
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
                try { await Click("mode-dt", null, (_, scan) => scan().Targets.Any(t => t.Kind == "dt-stage"), "the Doom Tower opens", ct, within: TimeSpan.FromSeconds(15)); }
                catch (PlayStopException)
                {
                    if (Top(await ReadState(ct)) != PlayTargets.BattleMenuView) throw;
                    // The card was pressed and the battle menu stayed (2026-11-05/06: KaelStormVex, a new account, every 4 minutes): the tower is not open
                    // on this account yet; the area rests (the engine's hold for a locked area), not an error each turn.
                    throw new AreaLockedException($"the Doom Tower card did not open the tower (locked on this account). Screen dump: {DumpScreen("doom tower locked")}");
                }
                await Pause(1200, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await HomeAsync(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"The Doom Tower was not reached in 10 steps. Screen dump: {DumpScreen("doom tower not reached")}");
    }
}
