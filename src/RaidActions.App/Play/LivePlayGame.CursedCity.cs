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

    /// <summary>The team last set on a team screen (the game keeps it in the slots): the planner keeps it where it fits.</summary>
    private HashSet<int> _ccLastTeam = [];

    public async Task<CursedCityPlan> PlanAsync(int lossesToSkip, IReadOnlySet<int> skip, bool allowRisky, string? difficulty, CancellationToken ct)
    {
        var (data, _, ratings, locks) = await Account(ct);
        LoadLocked(data.Progress?.CursedCity?.RotationUpdatedUtc, difficulty ?? "Hard");
        // The readiness check (AoE control, survivability, damage, the boss's needs): NotReady levels are never played.
        _ccCtx ??= CcContext.Load(extractorRoot, ExtractorOutput);
        var plan = CursedCityPlanner.Plan(data, locks, ratings, lossesToSkip, skip, _ccLocked, _ccCtx, allowRisky, difficulty, _ccLastTeam);
        // The chosen level's own disabled champions: its team built again without them.
        if (plan.Next is { } next && _ccDisabled.TryGetValue(next.Stage.Id, out var off) && off.Count > 0)
        {
            var team = CursedCityPlanner.BuildTeam(next.Stage, [.. data.Champions.Where(c => (c.Level > 1 || c.Rank > 1) && !_ccLocked.Contains(c.Id) && !off.Contains(c.Id))], locks, ratings, _ccCtx, _ccLastTeam);
            var byId = data.Champions.ToDictionary(c => c.Id);
            plan = plan with { Next = next with { Team = team, Readiness = _ccCtx is null || team.Members.Count == 0 ? null
                : CursedCityReadiness.Assess(next.Stage, [.. team.Members.Select(m => byId[m.Id])], _ccCtx) } };
        }
        return plan;
    }

    private DateTime? _ccRotation;
    private string _ccLockDifficulty = "Hard";
    private CcContext? _ccCtx;
    private string LockedFile => CursedCityPlanner.LockedFile(ExtractorOutput, _ccLockDifficulty);

    /// <summary>The champions locked in the city's difficulty this rotation, kept in a file per difficulty (a restarted bot does not learn
    /// them again; whether the game shares them between difficulties is not known, so each difficulty learns its own); a new rotation clears them.</summary>
    private void LoadLocked(DateTime? rotation, string difficulty)
    {
        if (_ccRotation is not null && _ccRotation == rotation && _ccLockDifficulty == difficulty) return;
        _ccRotation = rotation;
        _ccLockDifficulty = difficulty;
        _ccLocked.Clear();
        try
        {
            if (File.Exists(LockedFile) && System.Text.Json.JsonSerializer.Deserialize<LockedFileData>(File.ReadAllText(LockedFile)) is { } saved && saved.Rotation == rotation)
                foreach (var id in saved.Ids) _ccLocked.Add(id);
            else _ccLocked.Clear();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockedFile)!);
            File.WriteAllText(LockedFile, System.Text.Json.JsonSerializer.Serialize(new LockedFileData(_ccRotation, [.. _ccLocked])));
        }
        catch (IOException) { }
    }

    private sealed record LockedFileData(DateTime? Rotation, List<int> Ids);

    /// <summary>A planned champion the team screen will not take: the team is built again without it.</summary>
    private sealed class LockedHeroException(int heroId, int state, IReadOnlyList<(int Id, int State)> shown) : Exception($"#{heroId} cannot be picked here (state {state})")
    {
        public int HeroId => heroId;
        public int State => state;

        /// <summary>Every champion the list shows not pickable at the same time, with its state (learnt in one go, not one per try).</summary>
        public IReadOnlyList<(int Id, int State)> Shown => shown;
    }

    /// <summary>Per champion, the levels whose team screen showed it locked with every slot empty.</summary>
    private readonly Dictionary<int, HashSet<int>> _ccLockedOn = [];

    /// <summary>Champions disabled on one level's team screen (state 2: not for this level), by the map's level id; not kept for other levels.</summary>
    private readonly Dictionary<int, HashSet<int>> _ccDisabled = [];

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
        log($"autoplay: STEP gearing for {CursedCityPlanner.Where(level.Stage)}: {string.Join(", ", picked.Select(m => $"{m.Name} ({m.Moves.Count} piece(s){(m.InVault ? ", out of the vault first" : "")})"))}");
        foreach (var args in commands) await RunCommand(args, ct);
        if (!dryRun) FromVault(picked.Where(m => m.InVault).Select(m => m.ChampionId), data);
        var pieces = picked.Sum(m => m.Moves.Count);
        return $"{pieces} piece{(pieces == 1 ? "" : "s")}" + (dryRun ? " (dry run)" : "");
    }

    public async Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct, bool cursedCity)
    {
        if (ids.Count == 0) return "nobody";
        // Faction Wars' champions go to the Reserve Vault. Cursed City's go back to where they were before the bot touched them (Ehsan,
        // 2026-10-30): the ones it took out of a vault for a level into that vault again, the others to the list.
        var fromVault = cursedCity ? LoadFromVault() : [];
        string Home(int id) => !cursedCity ? "reserve" : fromVault.GetValueOrDefault(id, "list");
        foreach (var group in ids.GroupBy(Home))
        {
            var then = group.Key;
            log($"autoplay: STEP taking the gear off {string.Join(", ", group.Select(Who))}, then {then switch { "reserve" => "back into the Reserve Vault", "master" => "back into the Master Vault", _ => "back to the champion list" }}");
            await StripGear([.. group], then, dryRun, ct);
            if (!dryRun && cursedCity && then != "list") { foreach (var id in group) fromVault.Remove(id); SaveFromVault(fromVault); }
        }
        return dryRun ? "(dry run)" : "done";
    }

    /// <summary>The champions the bot took out of the Reserve Vault for Cursed City (output/autoplay/cc-from-vault.json): they go back in when
    /// their gear comes off; kept in a file so a restarted bot still knows.</summary>
    private string FromVaultFile => Path.Combine(ExtractorOutput, "autoplay", "cc-from-vault.json");

    /// <summary>Champion id → the vault it came out of ("reserve" or "master").</summary>
    private Dictionary<int, string> LoadFromVault()
    {
        try { return File.Exists(FromVaultFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(FromVaultFile)) ?? [] : []; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return []; }
    }

    private void SaveFromVault(Dictionary<int, string> ids)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FromVaultFile)!); File.WriteAllText(FromVaultFile, System.Text.Json.JsonSerializer.Serialize(ids)); }
        catch (IOException) { }
    }

    /// <summary>Remembers the vault each champion was in before the bot took it out (InBathhouse is the Reserve Vault, InVault the Master Vault).</summary>
    private void FromVault(IEnumerable<int> ids, SavedExport data)
    {
        var all = LoadFromVault();
        var byId = data.Champions.ToDictionary(c => c.Id);
        foreach (var id in ids)
            if (!all.ContainsKey(id) && byId.TryGetValue(id, out var c) && (c.InBathhouse || c.InVault)) all[id] = c.InBathhouse ? "reserve" : "master";
        SaveFromVault(all);
    }

    Task<string> ICursedCityGame.RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct) => RemoveGearAsync(ids, dryRun, ct, cursedCity: true);

    public Task<BattleOutcome> CheckTeamAsync(CcLevel level, CancellationToken ct) => FightAsync(level, dryRun: true, ct, check: true);

    public Task<BattleOutcome> FightAsync(CcLevel level, bool dryRun, CancellationToken ct) => FightAsync(level, dryRun, ct, check: false);

    /// <param name="check">Read the level's team list end to end and stop (CheckTeamAsync, before gear is moved): every champion it shows
    /// locked is learnt for the level at once; nothing is clicked in the slots. Once per level and run.</param>
    private async Task<BattleOutcome> FightAsync(CcLevel level, bool dryRun, CancellationToken ct, bool check)
    {
        if (check && _ccScanned.Contains(level.Stage.Id)) return new BattleOutcome(BattleResult.NotStarted, "the level's list was read already");
        var started = DateTime.UtcNow;
        // The criterion's members first: the game locks champions that would leave its criterion impossible to meet with the slots left.
        // The leader (the aura that works in the city) first, so it takes the first slot; then the criterion's members.
        var (acct, gdata, _, _) = await Account(ct);
        var byId = acct.Champions.ToDictionary(c => c.Id);
        var leader = CursedCityPlanner.Leader([.. level.Team.Members.Where(m => byId.ContainsKey(m.Id)).Select(m => byId[m.Id])], gdata);
        var team = level.Team.Members.OrderByDescending(m => m.Id == leader).ThenByDescending(m => m.MatchesCriterion).Select(m => m.Id).ToList();
        if (leader is { } ld) log($"autoplay: leader {byId[ld].Name} (aura in every battle)");
        var inVault = level.Team.Members.Where(m => m.InVault).Select(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (inVault.Count > 0 && !check)
        {
            log($"autoplay: STEP going to the Reserve Vault to take out {string.Join(", ", level.Team.Members.Where(m => m.InVault).Select(m => m.Name))}");
            await RunCommand(["vault-withdraw", .. inVault, .. (dryRun ? new[] { "--dry-run" } : [])], ct);
            if (!dryRun) FromVault(level.Team.Members.Where(m => m.InVault).Select(m => m.Id), acct);
        }

        var where = CursedCityPlanner.Where(level.Stage);
        log($"autoplay: STEP going to {where} ({level.Criterion}) with {string.Join(", ", level.Team.Members.Select(m => m.Name))}");
        var map = await OpenCursedCity(level, ct);
        if (map.State is 0) return new BattleOutcome(BattleResult.NotStarted, "closed on the map (not reachable yet)");
        var mapId = MapId(level.Stage);
        await Click("cc-level", t => t.Id == mapId, (s2, _) => s2.Top?.Name is PlayTargets.CcTeamView or UiState.MessageBoxView || s2.Active.Any(v => v.Name == PlayTargets.CcTeamView),
            $"the team screen for {where}", ct);
        var after = await Settled(ct);
        if (Top(after) != PlayTargets.CcTeamView) await Refuse(after, where, ct);

        // The game locks some champions in Cursed City (used elsewhere this rotation): learnt from the list, then the plan is made again
        // without them (Retry), so their stand-ins get gear before they fight.
        if (check)
        {
            log($"autoplay: STEP reading {where}'s champion list (who can play it) before moving any gear");
            if (!_vaultFilterChecked) await VaultFilterOff(ct);
            await EmptySlots(ct);
            var shownLocked = await ScanList(ct);
            _ccScanned.Add(level.Stage.Id);
            var disabledHere = _ccDisabled.TryGetValue(level.Stage.Id, out var dh) ? dh : _ccDisabled[level.Stage.Id] = [];
            foreach (var id in shownLocked) disabledHere.Add(id);
            await CloseTeamScreen(ct);
            var lockedPlanned = team.Where(shownLocked.Contains).ToList();
            log($"autoplay: {where}: the list shows {shownLocked.Count} locked champion(s){(lockedPlanned.Count > 0 ? $", {lockedPlanned.Count} of the planned team" : "")}");
            return lockedPlanned.Count > 0
                ? new BattleOutcome(BattleResult.Retry, $"{lockedPlanned.Count} planned champion(s) are locked on this level ({shownLocked.Count} in all): planning it again without them")
                : new BattleOutcome(BattleResult.NotStarted, "the game takes the whole team");
        }
        try { await SetTeam(team, ct); }
        catch (LockedHeroException e)
        {
            // Locked with the slots emptied and the criterion's members placed first: really not for this level (kept for this level only;
            // the game's locks follow the level's criterion and the slots, so nothing is kept for the rotation).
            // With the slots emptied the criterion locks nobody: every champion the list shows locked then is out for this level (all at once).
            var disabled = _ccDisabled.TryGetValue(level.Stage.Id, out var d) ? d : _ccDisabled[level.Stage.Id] = [];
            var before = disabled.Count;
            disabled.Add(e.HeroId);
            foreach (var (id, _) in e.Shown) disabled.Add(id);
            // Locked with every slot empty on two different levels: used in the city this rotation (seen 2026-10-30): out for every level.
            foreach (var (id, st) in e.Shown.Append((e.HeroId, e.State)).Where(x => x.Item2 is >= 2 and < 99 and not 7))
            {
                var seen = _ccLockedOn.TryGetValue(id, out var levels) ? levels : _ccLockedOn[id] = [];
                seen.Add(level.Stage.Id);
                if (seen.Count >= 2) _ccLocked.Add(id);
            }
            SaveLocked();
            log($"autoplay: not pickable for this level with the slots emptied: {string.Join(", ", e.Shown.Append((e.HeroId, e.State)).Distinct().Select(x => $"#{x.Item1}:{x.Item2}"))}");
            await CloseTeamScreen(ct);
            return new BattleOutcome(BattleResult.Retry, $"{disabled.Count - before} champion(s) cannot join this level: planning it again without them");
        }
        if (dryRun)
        {
            var (_, targets, _) = await Read(ct);
            var start = targets.FirstOrDefault(t => t.Kind == "team-start" && t.View == PlayTargets.CcTeamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {where} (dry run: NOT pressed)", r, always: true);
            await CloseTeamScreen(ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null ? "team set, Start shown, not pressed"
                : $"team set, Start not identified: {start?.Note ?? "no team-start target"}; screen dump: {DumpScreen("cc team-start not found")}");
        }
        string? refused = null;
        try
        {
            await Click("team-start", t => t.View == PlayTargets.CcTeamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
                "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        }
        catch (PlayStopException e) { refused = e.Message; }
        if (refused is not null)
        {
            if (Top(await ReadState(ct)) != PlayTargets.CcTeamView) throw new PlayStopException(refused);
            // Start pressed, the team screen stays (seen 2026-10-30 on Hard level 23, twice, reason not known yet): the level is skipped
            // for this run so the keys go to other levels, instead of the same refusal every turn.
            var dump = DumpScreen("cc start refused");
            await CloseTeamScreen(ct);
            return new BattleOutcome(BattleResult.NotStarted, $"the game did not start the battle (Start pressed, team screen stayed); screen dump: {dump}");
        }
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
        // A win does not lock its champions for every level (2026-10-30: Normal level 1's winners were all pickable on level 2): only what
        // a team screen shows locked is learnt (above).
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    /// <summary>The Cursed City map on top with the level in view (dragged there when off screen); its map entry (state 0 closed).</summary>
    private async Task<CcMapRead> OpenCursedCity(CcLevel level, CancellationToken ct)
    {
        _nav.FitWindow();
        var switches = 0;
        for (var hop = 0; hop < 16; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.CcResultView)) { await WaitBattle(ct, PlayTargets.CcResultView); continue; }
            if (Has(s, PlayTargets.CcResultView)) { await LeaveResult(ct, PlayTargets.CcResultView); continue; }
            if (Top(s) == PlayTargets.CcTeamView) { await CloseTeamScreen(ct); continue; }
            if (Has(s, PlayTargets.CcMapView))
            {
                var (_, targets, play) = await Read(ct);
                // The map shows one difficulty (its levels share their map ids with the other's): switched with the HUD's dropdown first.
                var want = level.Stage.Difficulty switch { "Normal" => 1, "Hard" => 2, "Brutal" => 3, _ => 0 };
                if (want > 0 && play.CcDifficulty != want)
                {
                    if (switches++ >= 3)
                        throw new PlayStopException($"The map stays on difficulty {play.CcDifficulty?.ToString() ?? "?"}, not {level.Stage.Difficulty}. Screen dump: {DumpScreen("cc difficulty")}");
                    var item = targets.FirstOrDefault(x => x.Kind == "cc-difficulty-item" && x.NotClickable is null
                        && string.Equals(x.Note?.Trim(), level.Stage.Difficulty, StringComparison.OrdinalIgnoreCase));
                    if (item is not null)
                        await Click("cc-difficulty-item", x => x == item || x.Name == item.Name, (_, scan) => scan().CcDifficulty == want,
                            $"the map shows {level.Stage.Difficulty}", ct, within: TimeSpan.FromSeconds(10));
                    else
                    {
                        await Click("cc-difficulty", null, (_, scan) => scan().Targets.Any(x => x.Kind == "cc-difficulty-item"), "the difficulty list opens", ct,
                            within: TimeSpan.FromSeconds(8));
                        log($"autoplay: difficulty list open: {DumpScreen("cc difficulty list")}");
                    }
                    await Task.Delay(800, ct);
                    continue;
                }
                var mine = (play.CcLevels ?? []).FirstOrDefault(x => x.StageId == MapId(level.Stage));
                var (gw, gwhy) = Game.Window.GameWindow.Find();
                if (gw is null) throw new PlayStopException(gwhy!);
                // Clickable but under the HUD at an edge (seen 2026-10-30: level 5 at y 39 of 486, the click hit the HUD): dragged in first.
                if (mine?.Open is { } t && t.NotClickable is null && t.Rect is { } tr
                    && tr.Center.X > gw.Client.Width * 0.1 && tr.Center.X < gw.Client.Width * 0.9
                    && tr.Center.Y > gw.Client.Height * 0.15 && tr.Center.Y < gw.Client.Height * 0.85) return mine;
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
    private async Task DragIntoView(string kind, CancellationToken ct, Func<UiTarget, bool>? which = null)
    {
        for (var i = 0; i < 4; i++)
        {
            var (_, targets, _) = await Read(ct);
            var t = targets.FirstOrDefault(x => x.Kind == kind && x.Rect is not null && (which is null || which(x)));
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

    private bool _vaultFilterChecked;

    /// <summary>Every slot of the team screen emptied (the game keeps the last team in them): only then does Locked mean "not for this level".</summary>
    private async Task EmptySlots(CancellationToken ct)
    {
        for (var i = 0; i < 6; i++)
        {
            var (_, _, play) = await Read(ct);
            var id = (play.TeamIds ?? []).FirstOrDefault();
            if (id == 0) return;
            await Click("team-slot", x => x.Id == id && x.View == PlayTargets.CcTeamView, (_, lazy) => !(lazy().TeamIds ?? []).Contains(id), $"{Who(id)} leaves the team", ct);
        }
    }

    /// <summary>Levels whose team list was read end to end this run (CheckTeamAsync).</summary>
    private readonly HashSet<int> _ccScanned = [];

    /// <summary>
    /// The team list scrolled from one end to the other in big steps (it builds only part of itself at a time), every champion it shows
    /// Locked (3) collected (Ehsan, 2026-10-30: gear moved to champions the level then refused).
    /// </summary>
    private async Task<HashSet<int>> ScanList(CancellationToken ct)
    {
        var locked = new HashSet<int>();
        foreach (var dir in new[] { 8, -8 })
        {
            string? last = null;
            var same = 0;
            for (var i = 0; i < 40 && same < 2; i++)
            {
                var (_, _, play) = await Read(ct);
                foreach (var p in play.Picks ?? []) if (p.State is 3) locked.Add(p.HeroId);
                var shown = (play.Picks ?? []).Where(p => p.Pick is { Rect: not null, Active: true, FullyVisible: true }).ToList();
                if (shown.Count == 0) break;
                var now = string.Join(",", shown.Select(p => p.HeroId));
                same = now == last ? same + 1 : 0;
                last = now;
                var (w, why) = Game.Window.GameWindow.Find();
                if (w is null) throw new PlayStopException(why!);
                if (MouseInput.Wheel(w, shown.Average(p => p.Pick!.Rect!.Value.Center.X), shown.Average(p => p.Pick!.Rect!.Value.Center.Y), dir) is { } notSent)
                    throw new PlayStopException("Not scrolled: " + notSent);
                await Task.Delay(300, ct);
            }
        }
        return locked;
    }

    /// <summary>
    /// The team list without vault champions (Ehsan, 2026-10-30: "make sure those are unchecked", the list is much shorter): the filter
    /// opened once per run, each ticked vault box unticked, the filter closed. The game keeps the filter, so later screens need nothing.
    /// </summary>
    private async Task VaultFilterOff(CancellationToken ct)
    {
        // The filter panel stays loaded when closed (2026-10-30), so its boxes are read without opening it: opened only when one is ticked.
        var (_, targets, _) = await Read(ct);
        _vaultFilterChecked = true;
        if (!targets.Any(t => t.Kind == "team-filter-vault" && t.Note == "on")) return;
        await Click("team-filter", null, (_, _) => true, "the filter opens", ct);
        await Task.Delay(800, ct);
        for (var i = 0; i < 4; i++)
        {
            var (_, now, _) = await Read(ct);
            var on = now.FirstOrDefault(t => t.Kind == "team-filter-vault" && t.Note == "on" && t.NotClickable is null);
            if (on is null) break;
            await Click("team-filter-vault", t => t.Path == on.Path, (_, scan) => scan().Targets.Any(t => t.Kind == "team-filter-vault" && t.Path == on.Path && t.Note == "off"),
                $"{on.Name} unticked", ct, within: TimeSpan.FromSeconds(5));
            log($"autoplay: unticked {on.Name}");
        }
        await Click("filter-hide", null, (_, _) => true, "the filter closes", ct);
        await Task.Delay(800, ct);
    }

    /// <summary>The team screen left with its own Close (the filter panel's Hide counts as a second "back" while it is loaded).</summary>
    private async Task CloseTeamScreen(CancellationToken ct) =>
        await Click("back", t => t.View == PlayTargets.CcTeamView && t.Path.EndsWith("/CloseButton", StringComparison.Ordinal), (s2, _) => Top(s2) != PlayTargets.CcTeamView,
            "the team screen closes", ct);

    /// <summary>
    /// The team screen's squad set to exactly <paramref name="team"/>: champions not in it taken out (a click on their slot), the missing ones
    /// picked from the list (scrolled with the wheel when not shown). Checked from memory after each click.
    /// </summary>
    private async Task SetTeam(IReadOnlyList<int> team, CancellationToken ct, string view = PlayTargets.CcTeamView)
    {
        if (!_vaultFilterChecked) await VaultFilterOff(ct);
        var wanted = team.ToHashSet();
        var emptied = 0;
        var down = true;
        string? lastShown = null;
        var looking = 0;
        // Scrolling to a champion the list has built off view: pixels per wheel notch (signed) learnt from the first scroll, then one
        // scroll of the right size (not a search; Ehsan 2026-10-30: "you are scrolling too much").
        float? pxPerNotch = null, lastX = null;
        var lastNotches = 0;
        var lastLooking = 0;
        // Every champion the list showed Locked (3) while scrolling: all learnt at once when a member is not found (not one per try).
        var seenLocked = new Dictionary<int, int>();
        for (var round = 0; round < 60; round++)
        {
            var (_, targets, play) = await Read(ct);
            var inSlots = (play.TeamIds ?? []).ToHashSet();
            // Locked only means "not for this level" with every slot empty (else the full team or the criterion locks them).
            if (inSlots.Count == 0) foreach (var p in play.Picks ?? []) if (p.State is 3) seenLocked[p.HeroId] = 3;
            // The list marks a champion in the team Selected (1) or LockedSelected (7) (SelectableHeroState): counted in even when the
            // slots read late, so a selected champion is never clicked again (a second click takes it out; 2026-10-30, Zelotah).
            foreach (var p in play.Picks ?? []) if (p.State is 1 or 7) inSlots.Add(p.HeroId);
            var extra = inSlots.Except(wanted).FirstOrDefault();
            if (extra != 0)
            {
                await Click("team-slot", t => t.Id == extra && t.View == view, (_, lazy) => !(lazy().TeamIds ?? []).Contains(extra),
                    $"#{extra} leaves the team", ct);
                continue;
            }
            var missing = team.FirstOrDefault(id => !inSlots.Contains(id));
            if (missing != 0 && missing != looking) log($"autoplay: STEP looking for {Who(missing)} in the team list");
            looking = missing;
            if (missing == 0)
            {
                log($"autoplay: team set: {string.Join(", ", team.Select(i => $"#{i}"))}");
                _ccLastTeam = [.. team];
                return;
            }
            var pick = (play.Picks ?? []).FirstOrDefault(p => p.HeroId == missing);
            if (pick?.Pick is { } t && t.NotClickable is null)
            {
                if (pick.State is >= 2 and not 7)
                {
                    // The champions in the slots may be what locks it (the criterion must stay possible): empty every slot once, then pick again.
                    // A lock only counts with every slot empty (the criterion locks nobody then); else empty them and pick again.
                    // Still locked after one emptying, with the earlier members back in: out for this level (not a rotation lock: state 2).
                    // Disabled (2) is not the criterion (seen 2026-10-30: champions that fought in this rotation's earlier battles): every
                    // disabled champion shown is out for this level at once, and for the rotation when disabled on two levels.
                    // Locked (3) also shows for every champion when the team is full, and for the ones the criterion leaves no room for
                    // (2026-10-30: 113 "locked" on level 3, read with the old team in the slots, while Ehsan won it with Athel, Dorothy, ...):
                    // it counts only with the slots emptied.
                    if (inSlots.Count > 0 && emptied >= 1)
                        throw new LockedHeroException(missing, pick.State.Value, [.. (play.Picks ?? []).Where(p => p.State is >= 2 and not 7).Select(p => (p.HeroId, p.State!.Value))]);
                    if (inSlots.Count > 0)
                    {
                        emptied++;
                        foreach (var id in inSlots)
                            await Click("team-slot", x => x.Id == id && x.View == view, (_, lazy) => !(lazy().TeamIds ?? []).Contains(id), $"#{id} leaves the team", ct);
                        log("autoplay: emptied the team slots (a champion was locked by the criterion with the slots as they were)");
                        continue;
                    }
                    throw new LockedHeroException(missing, pick.State.Value, [.. (play.Picks ?? []).Where(p => p.State is >= 2 and not 7).Select(p => (p.HeroId, p.State!.Value))]);
                }
                await Click("team-pick", x => x.Id == missing && x.View == view, (_, lazy) => (lazy().TeamIds ?? []).Contains(missing),
                    $"#{missing} joins the team", ct);
                continue;
            }
            // Not in view: scroll the list (the shown picks' area) down, then up from the end.
            var shown = (play.Picks ?? []).Where(p => p.Pick is { Rect: not null, Active: true, FullyVisible: true }).Select(p => p.Pick!.Rect!.Value).ToList();
            if (shown.Count == 0) throw new PlayStopException($"The team screen shows no champion list. Screen dump: {DumpScreen("team list empty")}");
            var (w, why) = Game.Window.GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            var viewX = shown.Average(r => r.Center.X);
            int notches;
            if (missing != lastLooking) { pxPerNotch = null; lastX = null; lastLooking = missing; }
            if (pick?.Pick?.Rect is { } off)
            {
                // Built off view: learn how far a notch moves it, then scroll it to the middle of the shown part in one go.
                if (lastX is { } x0 && lastNotches != 0 && Math.Abs(off.Center.X - x0) > 2) pxPerNotch = (off.Center.X - x0) / lastNotches;
                lastX = off.Center.X;
                notches = pxPerNotch is { } ppn ? (int)Math.Clamp(Math.Round((viewX - off.Center.X) / ppn), -25, 25) : -3;
                if (notches == 0) notches = viewX > off.Center.X ? 1 : -1;
            }
            else
            {
                // Not built: big steps one way, turning around at an end of the list (the same champions shown twice).
                var now = string.Join(",", (play.Picks ?? []).Where(p => p.Pick is { FullyVisible: true }).Select(p => p.HeroId));
                if (now == lastShown) down = !down;
                lastShown = now;
                lastX = null;
                notches = down ? -8 : 8;
            }
            lastNotches = notches;
            if (MouseInput.Wheel(w, viewX, shown.Average(r => r.Center.Y), notches) is { } notSent) throw new PlayStopException("Not scrolled: " + notSent);
            await Task.Delay(350, ct);
        }
        // A champion the list never showed (scrolled both ways): not on offer for this level; the level is planned again without it.
        var notShown = looking;
        log($"autoplay: #{notShown} not found in the team list; screen dump: {DumpScreen("team member not in the list")}");
        throw new LockedHeroException(notShown, 99, [.. seenLocked.Select(kv => (kv.Key, kv.Value))]);
    }
}
