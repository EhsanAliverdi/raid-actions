using RaidActions.App.Input;
using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor;
using RaidExtractor.Autoplay;
using RaidExtractor.Battle;

namespace RaidActions.App.Play;

/// <summary>
/// Cursed City on the live game (plan/cursed-city.md): the plan from a fresh account read, the gear with Manage team's own commands, and
/// the screens: village → Battle → Cursed City → the level on the map (dragged into view when needed; it must show opened or completed)
/// → the team screen, where the bot sets exactly the planned team (slots emptied, champions picked from the list, scrolled when
/// needed) → Start → result.
/// </summary>
public sealed partial class LivePlayGame : ICursedCityGame
{
    public async Task<CursedCityPlan> PlanAsync(int lossesToSkip, IReadOnlySet<int> skip, CancellationToken ct)
    {
        var (data, _, ratings, locks) = await Account(ct);
        return CursedCityPlanner.Plan(data, locks, ratings, lossesToSkip, skip);
    }

    public async Task<string> GearAsync(CcLevel level, IReadOnlyList<int> ids, bool dryRun, CancellationToken ct)
    {
        var (data, gd, ratings, locks) = await Account(ct);
        var fight = $"Cursed City: {CursedCityPlanner.Where(level.Stage)}";
        var made = Prepare.Make(data, gd, ratings, ids, fight, locks: locks);
        var picked = made.Members.Where(m => m.Changes || m.InVault).ToList();
        if (picked.Count == 0) return "nothing to equip from the inventory";
        var (commands, _, problem) = Prepare.BotCommands(data, gd, fight, picked, ExtractorOutput, dryRun);
        if (commands is null) return problem ?? "nothing to equip";
        foreach (var args in commands) await RunCommand(args, ct);
        var pieces = picked.Sum(m => m.Moves.Count);
        return $"{pieces} piece{(pieces == 1 ? "" : "s")}" + (dryRun ? " (dry run)" : "");
    }

    public async Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct, bool cursedCity)
    {
        if (ids.Count == 0) return "nobody";
        // Cursed City's champions go back to the list (they may play the next levels); Faction Wars' to the Reserve Vault.
        await RunCommand(["remove-gear", .. ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "--then", cursedCity ? "list" : "reserve",
            .. (dryRun ? new[] { "--dry-run" } : [])], ct);
        return dryRun ? "(dry run)" : "done";
    }

    Task<string> ICursedCityGame.RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct) => RemoveGearAsync(ids, dryRun, ct, cursedCity: true);

    public async Task<BattleOutcome> FightAsync(CcLevel level, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var team = level.Team.Members.Select(m => m.Id).ToList();
        var inVault = level.Team.Members.Where(m => m.InVault).Select(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (inVault.Count > 0) await RunCommand(["vault-withdraw", .. inVault, .. (dryRun ? new[] { "--dry-run" } : [])], ct);

        var where = CursedCityPlanner.Where(level.Stage);
        var map = await OpenCursedCity(level, ct);
        if (map.State is 0) return new BattleOutcome(BattleResult.NotStarted, "closed on the map (not reachable yet)");
        await Click("cc-level", t => t.Id == level.Stage.Id, (s2, _) => s2.Top?.Name is PlayTargets.CcTeamView or UiState.MessageBoxView || s2.Active.Any(v => v.Name == PlayTargets.CcTeamView),
            $"the team screen for {where}", ct);
        var after = await Settled(ct);
        if (Top(after) != PlayTargets.CcTeamView) await Refuse(after, where, ct);

        await SetTeam(team, ct);
        if (dryRun)
        {
            var (_, targets, _) = await Read(ct);
            var start = targets.FirstOrDefault(t => t.Kind == "team-start" && t.View == PlayTargets.CcTeamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {where} (dry run: NOT pressed)", r, always: true);
            await Back(PlayTargets.CcTeamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null ? "team set, Start shown, not pressed"
                : $"team set, Start not identified: {start?.Note ?? "no team-start target"}; screen dump: {DumpScreen("cc team-start not found")}");
        }
        await Click("team-start", t => t.View == PlayTargets.CcTeamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        var inBattle = await ReadState(ct);
        if (Top(inBattle) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "no key for this level (the offer to buy keys was closed)");
        }
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start {where}", ct);
        log($"autoplay: {where} started with {string.Join(", ", level.Team.Members.Select(m => m.Name))}");
        var result = await WaitBattle(ct, PlayTargets.CcResultView);
        await LeaveResult(ct, PlayTargets.CcResultView);
        var outcome = result?.Pve?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    /// <summary>The Cursed City map on top with the level in view (dragged there when off screen); its map entry (state 0 closed).</summary>
    private async Task<CcMapRead> OpenCursedCity(CcLevel level, CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 16; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.CcResultView)) { await WaitBattle(ct, PlayTargets.CcResultView); continue; }
            if (Has(s, PlayTargets.CcResultView)) { await LeaveResult(ct, PlayTargets.CcResultView); continue; }
            if (Top(s) == PlayTargets.CcTeamView) { await Back(PlayTargets.CcTeamView, ct); continue; }
            if (Has(s, PlayTargets.CcMapView))
            {
                var (_, targets, play) = await Read(ct);
                var mine = (play.CcLevels ?? []).FirstOrDefault(x => x.StageId == level.Stage.Id);
                if (mine?.Open is { } t && t.NotClickable is null) return mine;
                // Off screen (or not built yet): drag the map towards it, by the shown levels' numbers (a district's levels run in order).
                var shown = (play.CcLevels ?? []).Where(x => x.Open is { Rect: not null, Active: true }).ToList();
                if (shown.Count == 0) throw new PlayStopException($"No Cursed City level is shown on the map. Screen dump: {DumpScreen("cc map empty")}");
                var (w, why) = Game.Window.GameWindow.Find();
                if (w is null) throw new PlayStopException(why!);
                var cx = w.Client.Width / 2f;
                var cy = w.Client.Height / 2f;
                float dx, dy;
                if (mine?.Open?.Rect is { } r) { dx = cx - r.Center.X; dy = cy - r.Center.Y; }
                else
                {
                    // Not built: towards higher numbers (further along the district) or lower ones.
                    var avg = shown.Average(x => x.StageId % 1000);
                    var further = level.Stage.Number > avg;
                    var far = shown.OrderBy(x => x.StageId % 1000).Last().Open!.Rect!.Value;
                    var near = shown.OrderBy(x => x.StageId % 1000).First().Open!.Rect!.Value;
                    var dir = further ? (far.Center.X - near.Center.X, far.Center.Y - near.Center.Y) : (near.Center.X - far.Center.X, near.Center.Y - far.Center.Y);
                    var len = Math.Max(1, Math.Sqrt(dir.Item1 * dir.Item1 + dir.Item2 * dir.Item2));
                    dx = -(float)(dir.Item1 / len) * w.Client.Width * 0.35f;
                    dy = -(float)(dir.Item2 / len) * w.Client.Height * 0.35f;
                }
                var (x1, y1) = (cx - dx / 2, cy - dy / 2);
                var (x2, y2) = (cx + dx / 2, cy + dy / 2);
                if (MouseInput.Drag(w, Math.Clamp(x1, 40, w.Client.Width - 40), Math.Clamp(y1, 60, w.Client.Height - 60),
                    Math.Clamp(x2, 40, w.Client.Width - 40), Math.Clamp(y2, 60, w.Client.Height - 60)) is { } notSent)
                    throw new PlayStopException("Map not dragged: " + notSent);
                log($"autoplay: dragged the Cursed City map towards level {level.Stage.Number}");
                await Task.Delay(700, ct);
                continue;
            }
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await Click("mode-cc", null, (s2, _) => Has(s2, PlayTargets.CcMapView), "the Cursed City map opens", ct, within: TimeSpan.FromSeconds(15));
                continue;
            }
            if (!NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not bring {CursedCityPlanner.Where(level.Stage)} into view in 16 steps. Screen dump: {DumpScreen("cc level not reached")}");
    }

    /// <summary>
    /// The team screen's squad set to exactly <paramref name="team"/>: champions not in it taken out (a click on their slot), the missing ones
    /// picked from the list (scrolled with the wheel when not shown). Checked from memory after each click.
    /// </summary>
    private async Task SetTeam(IReadOnlyList<int> team, CancellationToken ct)
    {
        var wanted = team.ToHashSet();
        for (var round = 0; round < 40; round++)
        {
            var (_, targets, play) = await Read(ct);
            var inSlots = (play.TeamIds ?? []).ToHashSet();
            var extra = inSlots.Except(wanted).FirstOrDefault();
            if (extra != 0)
            {
                await Click("team-slot", t => t.Id == extra && t.View == PlayTargets.CcTeamView, (_, lazy) => !(lazy().TeamIds ?? []).Contains(extra),
                    $"#{extra} leaves the team", ct);
                continue;
            }
            var missing = wanted.Except(inSlots).FirstOrDefault();
            if (missing == 0)
            {
                log($"autoplay: team set: {string.Join(", ", team.Select(i => $"#{i}"))}");
                return;
            }
            var pick = (play.Picks ?? []).FirstOrDefault(p => p.HeroId == missing);
            if (pick?.Pick is { } t && t.NotClickable is null)
            {
                if (pick.State is >= 2 and not 7) throw new PlayStopException($"#{missing} cannot be picked here (state {pick.State}: disabled or locked by the level's criterion). Screen dump: {DumpScreen("team pick locked")}");
                await Click("team-pick", x => x.Id == missing && x.View == PlayTargets.CcTeamView, (_, lazy) => (lazy().TeamIds ?? []).Contains(missing),
                    $"#{missing} joins the team", ct);
                continue;
            }
            // Not in view: scroll the list (the shown picks' area) down, then up from the end.
            var shown = (play.Picks ?? []).Where(p => p.Pick is { Rect: not null, Active: true, FullyVisible: true }).Select(p => p.Pick!.Rect!.Value).ToList();
            if (shown.Count == 0) throw new PlayStopException($"The team screen shows no champion list. Screen dump: {DumpScreen("team list empty")}");
            var (w, why) = Game.Window.GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            var down = round < 20;
            if (MouseInput.Wheel(w, shown.Average(r => r.Center.X), shown.Average(r => r.Center.Y), down ? -3 : 3) is { } notSent) throw new PlayStopException("Not scrolled: " + notSent);
            await Task.Delay(350, ct);
        }
        throw new PlayStopException($"Could not set the team ({string.Join(", ", team.Select(i => $"#{i}"))}). Screen dump: {DumpScreen("team not set")}");
    }
}
