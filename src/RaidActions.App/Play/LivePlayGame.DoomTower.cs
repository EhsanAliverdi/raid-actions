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
        }).Where(s => s.Kind == DtKind.Boss && (s.Floor == FinalBoss || s.Beaten)).ToList();
        // The beaten bosses too (Ehsan 2026-11-17: silver keys on the last boss beaten, with Super Raid; BlackLord65 sat on 9 silver keys): the map's
        // floors have no rectangles, so they are found on the screen (FindTowerFloor).
        log($"autoplay: the tower: floor {FinalBoss} {stages.FirstOrDefault(x => x.Floor == FinalBoss)?.Status.ToString() ?? "not read"}; beaten bosses: {string.Join(", ", stages.Where(x => x.Beaten && x.Floor != FinalBoss).Select(x => x.Floor))}");
        var r = data.Account.Resources;
        return new DoomTowerMap(stages, r.GetValueOrDefault(DoomTowerRules.Gold), r.GetValueOrDefault(DoomTowerRules.Silver));
    }

    public async Task<BattleOutcome> FightDoomTowerAsync(DtStage stage, bool dryRun, CancellationToken ct, bool superRaid = false)
    {
        var started = DateTime.UtcNow;
        var name = DoomTowerRules.Name(stage);
        // The boss's team (or the bosses' / the tower's) geared first when it asks for it (Ehsan 2026-11-13).
        var bossFirst = RaidExtractor.GdTowerBoss.Load(Path.Combine(extractorRoot, "output", "gamedata")).GetValueOrDefault(stage.Id)?.Boss;
        var diffName = stage.Hard ? "Hard" : "Normal";
        var gearChoice = (bossFirst is null ? null : TeamsOf($"doom-tower:{diffName}:boss:{bossFirst}")) ?? TeamsOf($"doom-tower:{diffName}:bosses") ?? TeamsOf("doom-tower");
        await PrepareTeam($"Doom Tower {diffName} {bossFirst ?? "boss"}", "doom-tower",
            bossFirst is null ? "dt-floors" : "dt-" + new string([.. bossFirst.ToLowerInvariant().Where(char.IsLetter)]).Replace("celestial", "", StringComparison.Ordinal),
            5, gearChoice?.Default, dryRun, ct);
        log($"autoplay: STEP Doom Tower {name}");
        await OpenDoomTower(ct);
        if (stage.Floor != FinalBoss)
        {
            // A beaten boss replayed with silver keys: found on the map (its stage id checked on the team screen), Super Raid on.
            await SelectTowerDifficulty(diffName, ct);
            var (fd, _, _, _) = await Account(ct);
            var raw = fd.Progress?.DoomTower?.StageIndicatorRaw.Values.FirstOrDefault() ?? 0;
            var want = raw > 0 ? raw / 10_000 * 10_000 + (stage.Hard ? 2 : 1) * 1000 + stage.Floor : 0;
            if (await FindTowerFloor(diffName, stage.Floor, want, ct) is { } notFound) return new BattleOutcome(BattleResult.NotStarted, notFound);
            var teamView = Top(await Settled(ct)) ?? "";
            // Super Raid (x2) when the keys cover it (the activity decides); the box is the 'MultiRun' block of the Doom Tower team screen.
            var superOn = false;
            try { superOn = await SetSuperRaid(superRaid && !dryRun, teamView, ct); }
            catch (PlayStopException e) { log($"autoplay: Doom Tower {name}: Super Raid not set ({e.Message}); a normal run"); }
            _dtSuper = superOn;
            var replayBoss = RaidExtractor.GdTowerBoss.Load(Path.Combine(extractorRoot, "output", "gamedata")).GetValueOrDefault(want)?.Boss;
            var played = await PlayTowerTeam(name, started, dryRun, ct, replayBoss);
            return superRaid && !superOn && played.Result is BattleResult.Victory or BattleResult.Defeat ? played with { Text = played.Text + NormalRunMark } : played;
        }
        _dtSuper = false;
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
        // The boss on this floor (output/gamedata/doomtower.json, read from the game's stage data): its own team on the page, when set.
        var boss = RaidExtractor.GdTowerBoss.Load(Path.Combine(extractorRoot, "output", "gamedata")).GetValueOrDefault(stage.Id)?.Boss;
        if (boss is not null) log($"autoplay: Doom Tower {name}: {boss}");
        var final = await PlayTowerTeam(name, started, dryRun, ct, boss);
        // The Final Boss's team screen is not given Super Raid here: a normal run (the activity counts one battle of keys).
        return superRaid && final.Result is BattleResult.Victory or BattleResult.Defeat ? final with { Text = final.Text + NormalRunMark } : final;
    }

    private bool _towerOnHard;

    /// <summary>The last battle's result screen is still open (for Replay), and whether that battle ran with Super Raid.</summary>
    private bool _dtResultOpen, _dtSuper;

    public async Task LeaveDoomTowerResultAsync(CancellationToken ct)
    {
        if (!_dtResultOpen) return;
        _dtResultOpen = false;
        if (Top(await ReadState(ct)) is { } top && top == PlayTargets.DtResultView) await LeaveResult(ct, PlayTargets.DtResultView);
    }

    /// <summary>
    /// Replay from the Doom Tower result screen (Ehsan 2026-11-20: "why are you going back to the village between boss floor 30 farms"): the same stage, the
    /// same team, the same Super Raid. Null (the result left, the long way taken) when the screen is gone, Replay is missing or Super Raid would change.
    /// </summary>
    public async Task<BattleOutcome?> ReplayDoomTowerAsync(DtStage stage, bool superRaid, CancellationToken ct)
    {
        if (!_dtResultOpen) return null;
        _dtResultOpen = false;
        var started = DateTime.UtcNow;
        if (Top(await ReadState(ct)) != PlayTargets.DtResultView) return null;
        var (_, targets, _) = await Read(ct);
        if (superRaid != _dtSuper || !targets.Any(t => t.Kind == "result-replay" && t.View == PlayTargets.DtResultView && t.NotClickable is null))
        {
            if (superRaid == _dtSuper) log($"autoplay: no Replay on the Doom Tower result: the long way. Screen dump: {DumpScreen("doom tower result")}");
            await LeaveResult(ct, PlayTargets.DtResultView);
            return null;
        }
        try
        {
            await Click("result-replay", t => t.View == PlayTargets.DtResultView, (s2, _) => Top(s2) is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView)
                || s2.Active.Any(v => BattleViews.Contains(v.Name)) && !Has(s2, PlayTargets.DtResultView), "Replay leads on", ct, within: TimeSpan.FromSeconds(15));
        }
        catch (PlayStopException e)
        {
            log($"autoplay: Replay did not lead on ({e.Message}): the long way");
            return null;
        }
        var after = await Settled(ct);
        if (!after.Active.Any(v => BattleViews.Contains(v.Name)) || Has(after, PlayTargets.DtResultView))
        {
            // No key for it (the refill offer) or its team screen: closed, and the map's way taken.
            if (Top(after) is { } over && over != PlayTargets.DtResultView) await Back(over, ct);
            return null;
        }
        log($"autoplay: Doom Tower {DoomTowerRules.Name(stage)} replayed from the result screen{(_dtSuper ? " (Super Raid)" : "")}");
        return await FinishTowerBattle(started, ct);
    }

    /// <summary>A started battle to its end; its result screen is left open after a win or a loss, for Replay (<see cref="LeaveDoomTowerResultAsync"/> closes it).</summary>
    private async Task<BattleOutcome> FinishTowerBattle(DateTime started, CancellationToken ct)
    {
        var result = await WaitBattle(ct, PlayTargets.DtResultView);
        await ClearOverResult(ct, PlayTargets.DtResultView);
        var outcome = result?.Pve?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        _dtResultOpen = outcome is BattleResult.Victory or BattleResult.Defeat && Top(await ReadState(ct)) == PlayTargets.DtResultView;
        if (!_dtResultOpen) await LeaveResult(ct, PlayTargets.DtResultView);
        RecordTry(outcome);
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s", _fielded);
    }

    /// <summary>
    /// A floor's team screen opened from the map (the floors have no rectangles): the spots seen on the screen (boss portraits, gold banners) tapped and the
    /// team screen's stage id checked (<paramref name="stageId"/>; 0: not checked); the map dragged towards the floor when a spot opens a lower or higher one.
    /// Null when its team screen is open; else why not.
    /// </summary>
    private async Task<string?> FindTowerFloor(string tower, int floor, int stageId, CancellationToken ct)
    {
        var (w, why) = GameWindow.Find();
        if (w is null) throw new PlayStopException(why!);
        var sc = w.Client.Width / 1298f;
        var up = true;   // up the tower first, as the map opens on the current (higher) floors... unless a lower one is seen
        var tried = 0;
        for (var view = 0; view < 10; view++)
        {
            if (view > 0)
            {
                // Up the tower: the finger moves down; down the tower: up.
                var (y1, y2) = up ? (120 * sc, 640 * sc) : (640 * sc, 120 * sc);
                if (MouseInput.Drag(w, 650 * sc, y1, 650 * sc, y2) is { } dragFailed) throw new PlayStopException(dragFailed);
                await Pause(1500, ct);
            }
            var (capture, problem) = WindowCapture.Take(w);
            if (capture is null) throw new PlayStopException($"The tower could not be captured: {problem}");
            List<(float X, float Y)> spots;
            using (capture) spots = DoomTowerSpot.Candidates(capture.Image);
            log($"autoplay: the {tower} map, looking for floor {floor}: {spots.Count} spot(s)");
            var moved = false;
            foreach (var at in spots.Take(6))
            {
                tried++;
                if (MouseInput.Click(w, at.X, at.Y, new Random()) is { } notSent) throw new PlayStopException(notSent);
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                while (DateTime.UtcNow < until && !(Top(await ReadState(ct)) is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView))) await Task.Delay(400, ct);
                var opened = Top(await ReadState(ct));
                if (opened is null || !PlayTargets.IsTeamView(opened))
                {
                    if (opened is not null && opened != "DoomTowerHUD" && opened != "DoomTowerMap") await Back(opened, ct);
                    if (Top(await ReadState(ct)) is { } now && now != "DoomTowerHUD" && now != "DoomTowerMap") return $"a tap on the {tower} map left the tower ({now})";
                    continue;
                }
                var got = (await Read(ct)).Play.TowerStage;
                if (stageId > 0 && got is { } g && g != stageId)
                {
                    log($"autoplay: ({at.X:0},{at.Y:0}) opened floor {g % 1000}, not {floor}: back");
                    await Back(opened, ct);
                    await Pause(800, ct);
                    up = g % 1000 < floor;
                    moved = true;
                    break;
                }
                log($"autoplay: the {tower} floor {floor} opened at ({at.X:0},{at.Y:0})");
                return null;
            }
            _ = moved;
        }
        return $"floor {floor} not found on the {tower} map ({tried} spot(s) tried)";
    }

    /// <summary>A boss's Content teams fight ("Dark Fae" → dt-darkfae).</summary>
    private static string TowerContent(string boss) => "dt-" + new string([.. boss.ToLowerInvariant().Where(char.IsLetter)]).Replace("celestial", "", StringComparison.Ordinal);

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
        // The floor to play (the account's progress: its stage id) and, on a boss floor, its boss (Ehsan 2026-11-13: BlackLord65 stuck at the Dark Fae):
        // the boss's team on the page, prepared first (gear up) when it asks for it.
        log($"autoplay: STEP Doom Tower: climbing {tower}");
        await OpenDoomTower(ct);
        await SelectTowerDifficulty(tower, ct);
        // The floor to play: the map's open one (2026-11-13: the account's progress said 30 while 31-39 were beaten and 40 was open), its stage id from
        // the tower's rotation (the progress's stage id: 703 1 030), and on a boss floor its boss: that boss's team, prepared first when it asks for it.
        var (climbData, _, _, _) = await Account(ct);
        var raw = climbData.Progress?.DoomTower?.StageIndicatorRaw.Values.FirstOrDefault() ?? 0;
        var openFloor = (await Read(ct)).Targets.Where(t => t.Kind == "dt-stage" && t.Id is > 0 and < 800 && t.Note?.Contains("status 1", StringComparison.Ordinal) == true)
            .Select(t => t.Id!.Value).DefaultIfEmpty(0).Min();
        var current = raw > 0 && openFloor > 0 ? raw / 10_000 * 10_000 + (hard ? 2 : 1) * 1000 + openFloor : 0;
        var floorBoss = openFloor % 10 == 0 && current > 0 ? RaidExtractor.GdTowerBoss.Load(Path.Combine(extractorRoot, "output", "gamedata")).GetValueOrDefault(current)?.Boss : null;
        log($"autoplay: Doom Tower {tower}: floor {openFloor} open{(floorBoss is null ? "" : $": {floorBoss}")}");
        if (floorBoss is not null)
        {
            var bossChoice = TeamsOf($"doom-tower:{tower}:boss:{floorBoss}") ?? TeamsOf($"doom-tower:{tower}:bosses") ?? TeamsOf("doom-tower");
            // Simulate, don't guess (Ehsan 2026-11-19: "the team you suggest must be able to pass ... try the next level only above 50%"): the best team
            // the simulator finds for this boss, simulated with the gear the bot will put on it; at 50% or less the floor is not tried.
            List<int>? nextTry;
            {
                var (sd, sgd, sr, sl) = await Account(ct);
                log($"autoplay: STEP Doom Tower {tower} floor {openFloor} ({floorBoss}): simulating teams for stage {current}");
                var pick = RaidExtractor.Battle.StageTeam.Best(sd, sgd, ExtractorOutput, current, sr, sl);
                if (pick is null)
                    return new BattleOutcome(BattleResult.NotStarted, $"floor {openFloor} ({floorBoss}): no team could be simulated for stage {current}");
                var plan = pick.Gear ?? RaidExtractor.Battle.Prepare.Make(sd, sgd, sr, pick.Ids, $"Auto Play: Doom Tower {tower} {floorBoss}", locks: sl);
                var (rate, simWhy) = RaidExtractor.Battle.StageSim.WinRate(sd, sgd, ExtractorOutput, current, pick.Ids, gear: plan);
                log($"autoplay: Doom Tower {tower} floor {openFloor}: best team {string.Join(", ", pick.Names)}: simulated win {rate:P0} after gear-up ({simWhy})");
                SecondOpinion(DoomTowerActivity.AreaId, current, pick.Ids, plan, rate, sd, sgd);
                if (!RaidExtractor.Battle.StageTeam.ShouldPush(rate))
                    return new BattleOutcome(BattleResult.NotStarted, $"floor {openFloor} ({floorBoss}): best team {string.Join(", ", pick.Names)} wins {rate:P0} in simulation (needs over 50%): not tried");
                nextTry = pick.Ids;
            }
            if ((await PrepareTeam($"Doom Tower {tower} {floorBoss}", "doom-tower", TowerContent(floorBoss), 5, bossChoice?.Default, dryRun, ct, team: nextTry)).Length > 0)
            {
                await OpenDoomTower(ct);
                await SelectTowerDifficulty(tower, ct);
            }
        }
        // The floor's spot: the candidates on the map (a glowing boss portrait, the gold banners), each tapped and checked in memory (the team screen's
        // stage) until the open floor's one opens; a wrong one is backed out of; none: the map scrolled up a screen and looked at again (2026-11-13: the
        // Normal map opened on floors 26-39 and the bot played the beaten floor-30 boss instead of floor 40's Dark Fae).
        var (w, why) = GameWindow.Find();
        if (w is null) throw new PlayStopException(why!);
        var tried = 0;
        for (var view = 0; view < 8; view++)
        {
            if (view > 0)
            {
                // Up the tower: the finger moves down (2026-11-15: a 320-pixel drag moved ~7 floors; the map opened at floor 10 and floor 40 was never seen).
                var sc = w.Client.Width / 1298f;
                if (MouseInput.Drag(w, 650 * sc, 120 * sc, 650 * sc, 640 * sc) is { } dragFailed) throw new PlayStopException(dragFailed);
                await Pause(1500, ct);
            }
            var (capture, problem) = WindowCapture.Take(w);
            if (capture is null) throw new PlayStopException($"The tower could not be captured: {problem}");
            List<(float X, float Y)> spots;
            using (capture)
            {
                spots = DoomTowerSpot.Candidates(capture.Image);
                var saved = Path.Combine(folder, $"tower-{tower.ToLowerInvariant()}-{DateTime.Now:HHmmss}-{view}.png");
                OpenCvSharp.Cv2.ImWrite(saved, capture.Image);
                log($"autoplay: the {tower} map ({saved}): {spots.Count} spot(s) {string.Join(", ", spots.Select(p => $"({p.X:0},{p.Y:0})"))}");
            }
            foreach (var at in spots.Take(5))
            {
                tried++;
                if (MouseInput.Click(w, at.X, at.Y, new Random()) is { } notSent) throw new PlayStopException(notSent);
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                while (DateTime.UtcNow < until && !(Top(await ReadState(ct)) is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView))) await Task.Delay(400, ct);
                var opened = Top(await ReadState(ct));
                if (opened is null || !PlayTargets.IsTeamView(opened))
                {
                    if (opened is PlayTargets.RefillView or UiState.MessageBoxView) await Back(opened, ct);
                    else if (opened is not null && opened != "DoomTowerHUD" && opened != "DoomTowerMap") await Back(opened, ct);
                    // Still on the tower? (2026-11-15: a tap on a button left it and the next look was the village's.)
                    if (Top(await ReadState(ct)) is { } now && now != "DoomTowerHUD" && now != "DoomTowerMap")
                        return new BattleOutcome(BattleResult.NotStarted, $"a tap on the {tower} map left the tower ({now}); floor {openFloor} not found");
                    continue;
                }
                var (_, _, play) = await Read(ct);
                var stage = play.TowerStage;
                if (current > 0 && stage is { } got && got != current)
                {
                    log($"autoplay: ({at.X:0},{at.Y:0}) opened stage {got} (floor {got % 1000}), not floor {openFloor}: back");
                    await Back(opened, ct);
                    await Pause(800, ct);
                    // A lower floor: the open one is further up, past this screen (its spots are lower floors too): scroll on.
                    if (got % 1000 < openFloor) break;
                    continue;
                }
                log($"autoplay: the {tower} floor {openFloor} opened at ({at.X:0},{at.Y:0}) (stage {stage?.ToString() ?? "not read"})");
                return await PlayTowerTeam(floorBoss is null ? $"{tower} floor {openFloor}" : $"{tower} boss floor {openFloor}", started, dryRun, ct, floorBoss);
            }
        }
        try { await SelectTowerDifficulty("Normal", ct); }
        catch (PlayStopException e) { log($"autoplay: the tower left as it is ({e.Message})"); }
        return new BattleOutcome(BattleResult.NotStarted, tried == 0 ? $"no gold floor on the {tower} map" : $"floor {openFloor} not found on the {tower} map ({tried} spot(s) tried)");
    }

    /// <summary>From the stage's team screen to the result.</summary>
    private async Task<BattleOutcome> PlayTowerTeam(string name, DateTime started, bool dryRun, CancellationToken ct, string? boss = null)
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
        // The page's team for this difficulty and kind (Ehsan 2026-11-13: floors and bosses, Normal and Hard), else the area's.
        var difficulty = name.StartsWith("Hard", StringComparison.Ordinal) ? "Hard" : "Normal";
        var towerKey = $"doom-tower:{difficulty}:{(name.Contains("boss", StringComparison.Ordinal) ? "bosses" : "floors")}";
        // A boss's own team first (Ehsan 2026-11-13: "a team for each boss"), else the difficulty's bosses team, else the tower's.
        if (boss is not null && TeamsOf($"doom-tower:{difficulty}:boss:{boss}") is not null) towerKey = $"doom-tower:{difficulty}:boss:{boss}";
        var bossArea = boss is null ? "dt-floors" : "dt-" + new string([.. boss.ToLowerInvariant().Where(char.IsLetter)]).Replace("celestial", "", StringComparison.Ordinal);
        if (!await ApplyDefaultTeam("doom-tower", teamView, ct, name, key: towerKey, contentArea: bossArea)) return new BattleOutcome(BattleResult.NotStarted, "the Teams card's team was refused here (or every team tried lost here)");
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
        // 2026-11-22: the tower's Start launched the 11.95 background AUTO-CLIMB twice (04:07 and 08:10, KaelStormVex). The team screen's tree
        // from before 11.95 is all we have: save it once per run, to find the auto-climb control and keep it off.
        if (!_towerTeamDumped) { _towerTeamDumped = true; log($"autoplay: the tower team screen before Start. Screen dump: {DumpScreen("doom tower team screen (auto-climb check)")}"); }
        // Ehsan: never start an auto-climb. The checkbox above Start is read from the account (AutoClimbMode) and turned off by one tap if on;
        // unknown or still on: Start is not pressed.
        if (teamView == PlayTargets.DoomTowerTeamView)
        {
            // The checkbox itself (the account's AutoClimbMode follows it only at Start: 20:17, one tap, the stored value still on).
            bool? CheckboxOn() => ReadSync().Targets.FirstOrDefault(t => t.Kind == "autoclimb-state")?.Note switch { "on" => true, "off" => false, _ => null };
            var guard = await AutoClimbGuard.EnsureOffAsync(CheckboxOn,
                () => Click("autoclimb-checkbox", t => t.View == teamView, (_, _) => true, "the checkbox answers", ct),
                ms => Pause(ms, ct), log);
            if (!guard.CanStart)
            {
                await Back(teamView, ct);
                return new BattleOutcome(BattleResult.NotStarted, guard.Message);
            }
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
            await ThrowIfBackgroundBattle("the Doom Tower", ct);
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
        return await FinishTowerBattle(started, ct);
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
            if (targets.Any(t => t.Kind == "dt-stage")) { await CollectBackgroundResults("the Doom Tower", ct); return; }
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
            if (!Input.NavigatingDriver.AtVillage(s)) { await TowardsBattleMenu(s, ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"The Doom Tower was not reached in 10 steps. Screen dump: {DumpScreen("doom tower not reached")}");
    }
}
