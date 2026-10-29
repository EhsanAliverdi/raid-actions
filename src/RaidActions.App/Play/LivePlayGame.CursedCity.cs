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
    /// <summary>Champions the team screen showed locked for Cursed City (used elsewhere in the city this rotation): left out of every plan.</summary>
    private readonly HashSet<int> _ccLocked = [];

    public async Task<CursedCityPlan> PlanAsync(int lossesToSkip, IReadOnlySet<int> skip, CancellationToken ct)
    {
        var (data, _, ratings, locks) = await Account(ct);
        return CursedCityPlanner.Plan(data, locks, ratings, lossesToSkip, skip, _ccLocked);
    }

    /// <summary>A planned champion the team screen will not take: the team is built again without it.</summary>
    private sealed class LockedHeroException(int heroId, IReadOnlyList<int> alsoLocked) : Exception($"#{heroId} is locked here")
    {
        public int HeroId => heroId;

        /// <summary>Every champion the list shows locked at the same time (learnt in one go, not one per try).</summary>
        public IReadOnlyList<int> AlsoLocked => alsoLocked;
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
        // A dry run does not take champions out of a vault, so the gear walk cannot find them in the list: only the vault step is shown.
        if (dryRun && picked.Any(m => m.InVault)) commands = commands.Where(c => c[0] == "vault-withdraw").ToList();
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
        var mapId = MapId(level.Stage);
        await Click("cc-level", t => t.Id == mapId, (s2, _) => s2.Top?.Name is PlayTargets.CcTeamView or UiState.MessageBoxView || s2.Active.Any(v => v.Name == PlayTargets.CcTeamView),
            $"the team screen for {where}", ct);
        var after = await Settled(ct);
        if (Top(after) != PlayTargets.CcTeamView) await Refuse(after, where, ct);

        // The game locks some champions in Cursed City (used elsewhere this rotation): the team is built again without them.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SetTeam(team, ct);
                break;
            }
            catch (LockedHeroException e) when (attempt < 8)
            {
                _ccLocked.Add(e.HeroId);
                foreach (var id in e.AlsoLocked) _ccLocked.Add(id);
                var (data, _, ratings, locks) = await Account(ct);
                var rebuilt = CursedCityPlanner.BuildTeam(level.Stage, data.Champions.Where(c => (c.Level > 1 || c.Rank > 1) && !_ccLocked.Contains(c.Id)).ToList(), locks, ratings);
                if (!rebuilt.Feasible)
                {
                    await Back(PlayTargets.CcTeamView, ct);
                    return new BattleOutcome(BattleResult.NotStarted, $"no team without the locked champions ({string.Join(", ", _ccLocked.Select(i => $"#{i}"))}): {rebuilt.Why}");
                }
                team = rebuilt.Members.Select(m => m.Id).ToList();
                log($"autoplay: #{e.HeroId} is locked in Cursed City; team built again: {string.Join(", ", rebuilt.Members.Select(m => m.Name))}");
            }
        }
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
                var mine = (play.CcLevels ?? []).FirstOrDefault(x => x.StageId == MapId(level.Stage));
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
                // The battle menu's cards run sideways past the window (seen 2026-10-29: Cursed City at x 1263 of 864): drag it into view.
                await DragIntoView("mode-cc", ct);
                await Click("mode-cc", null, (s2, _) => Has(s2, PlayTargets.CcMapView), "the Cursed City map opens", ct, within: TimeSpan.FromSeconds(15));
                continue;
            }
            if (!NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not bring {CursedCityPlanner.Where(level.Stage)} into view in 16 steps. Screen dump: {DumpScreen("cc level not reached")}");
    }

    /// <summary>
    /// The id the map gives a level (seen 2026-10-29: district × 1000 + number, e.g. Cobblemarket 21 = 1021; the difficulty is the map's own).
    /// </summary>
    private static int MapId(CcStage s) =>
        (int.TryParse(System.Text.RegularExpressions.Regex.Match(s.District, @"\d+$").Value, out var d) ? d : 0) * 1000 + s.Number;

    /// <summary>A target whose middle is outside the window (a list that runs past it): the view dragged until it is inside (at most 4 drags).</summary>
    private async Task DragIntoView(string kind, CancellationToken ct)
    {
        for (var i = 0; i < 4; i++)
        {
            var (_, targets, _) = await Read(ct);
            var t = targets.FirstOrDefault(x => x.Kind == kind && x.Rect is not null);
            var (w, why) = Game.Window.GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            if (t?.Rect is not { } r) return;
            var (cx, cy) = r.Center;
            var inside = cx > w.Client.Width * 0.08 && cx < w.Client.Width * 0.92 && cy > w.Client.Height * 0.1 && cy < w.Client.Height * 0.9;
            if (inside) return;
            var dx = Math.Clamp(w.Client.Width / 2f - cx, -w.Client.Width * 0.6f, w.Client.Width * 0.6f);
            var dy = Math.Clamp(w.Client.Height / 2f - cy, -w.Client.Height * 0.5f, w.Client.Height * 0.5f);
            var (x1, y1) = (w.Client.Width / 2f - dx / 2, w.Client.Height / 2f - dy / 2);
            if (MouseInput.Drag(w, x1, y1, x1 + dx, y1 + dy) is { } notSent) throw new PlayStopException("Not dragged: " + notSent);
            log($"autoplay: dragged the view {dx:0},{dy:0} to bring {t.Name} into view");
            await Task.Delay(700, ct);
        }
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
                if (pick.State is >= 2 and not 7)
                    throw new LockedHeroException(missing, [.. (play.Picks ?? []).Where(p => p.State is >= 2 and not 7).Select(p => p.HeroId)]);
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
