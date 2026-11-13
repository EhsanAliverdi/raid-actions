using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Clan Boss on the live game (raid-extractor plan/clan-boss.md): village → Battle → the Clan Boss card → the alliance map's Demon Lord →
/// the difficulty → Battle → team screen (the team the game shows) → Start → the battle → result (damage). Built from the dump on
/// 2026-10-31; every rule is to confirm with a dry run.
/// </summary>
public sealed partial class LivePlayGame : IClanBossGame
{
    private readonly HashSet<string> _clanDumped = [];
    private const string QuickResult = "AllianceBossQuickBattleOverlay", ChimeraQuickResult = "ChimeraQuickBattleOverlay";

    public async Task<ClanBossAccount> ReadClanBossAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        var clan = data.Progress?.Clan;
        return new ClanBossAccount(data.Account.Resources.GetValueOrDefault(ClanBossRules.Key), clan?.ClanBossBestDamage ?? [], clan?.ClanBossMaxQuickBattle);
    }

    public Task<BattleOutcome> FightClanBossAsync(string difficulty, bool quick, bool dryRun, CancellationToken ct)
    {
        if (quick) log("autoplay: Quick Battle asked: not explored yet, a normal battle");
        return FightAllianceAsync("Clan Boss", "clan-demonlord", 1, PlayTargets.ClanResultView, difficulty, dryRun, ct);
    }

    public async Task<ClanStanding?> ReadClanStandingAsync(string difficulty, CancellationToken ct)
    {
        var me = (await Account(ct)).Data.Account.Name;
        await OpenAllianceEnemy("clan-demonlord", PlayTargets.ClanResultView, ct);
        bool Mine(UiTarget t) => t.Kind == "clan-difficulty" && t.Note?.Contains("type 1", StringComparison.Ordinal) == true
            && ClanBossRules.Same(difficulty, t.Name.Replace("clan boss: ", "", StringComparison.Ordinal));
        var row = (await Read(ct)).Targets.FirstOrDefault(Mine);
        if (row is null || row.Note?.Contains("; locked", StringComparison.Ordinal) == true) return null;
        if (row.Note?.Contains("; selected", StringComparison.Ordinal) != true)
        {
            await DragIntoView("clan-difficulty", ct, Mine);
            await Click("clan-difficulty", Mine, (_, scan) => scan().Targets.Any(t => Mine(t) && t.Note?.Contains("; selected", StringComparison.Ordinal) == true), $"{difficulty} selected", ct);
            await Pause(1000, ct);
        }
        var (_, targets, _) = await Read(ct);
        var line = targets.FirstOrDefault(t => t.Kind == "clan-score" && t.Name == me)?.Note?.Split('|');
        var damage = ClanBossRules.Damage(line?.ElementAtOrDefault(0)) ?? 0;
        var keys = int.TryParse(line?.ElementAtOrDefault(1), out var spent) ? spent : 0;
        if (!_clanTop.TryGetValue(difficulty, out var top))
        {
            // The difficulty's own chest button: same stage id as the row, dragged into view with it.
            var (_, now, _) = await Read(ct);
            var box = now.FirstOrDefault(t => t.Kind == "clan-chests" && t.Name == "clan chests: " + row.Name.Replace("clan boss: ", "", StringComparison.Ordinal) && t.NotClickable is null);
            if (box is not null)
            {
                await Click("clan-chests", t => t.Path == box.Path, (_, scan) => scan().Targets.Any(t => t.Kind == "clan-chest"), "the chest list opens", ct, within: TimeSpan.FromSeconds(8));
                await Pause(800, ct);
                var (s2, list, _) = await Read(ct);
                top = list.Where(t => t.Kind == "clan-chest").Select(t => ClanBossRules.Damage(t.Note)).Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(0).Max();
                if (top > 0) _clanTop[difficulty] = top;
                await Back(Top(s2)!, ct);
                await Pause(800, ct);
            }
        }
        log($"autoplay: {difficulty}: {me} {damage:N0} damage, {keys} key(s) today; top chest {top:N0}");
        return new ClanStanding(damage, keys, top > 0 ? top : null);
    }

    /// <summary>The top chest's damage per difficulty (the game's list; it does not change in a run).</summary>
    private readonly Dictionary<string, long> _clanTop = [];

    public async Task<ClanBossAccount> ReadHydraAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        var clan = data.Progress?.Clan;
        return new ClanBossAccount(data.Account.Resources.GetValueOrDefault(HydraRules.Key), clan?.HydraBestDamage ?? [], clan?.HydraMaxQuickBattle);
    }

    public async Task<ClanBossAccount> ReadChimeraAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        var clan = data.Progress?.Clan;
        return new ClanBossAccount(data.Account.Resources.GetValueOrDefault(ChimeraRules.Key), clan?.ChimeraBestDamage ?? [], clan?.ChimeraMaxQuickBattle);
    }

    public Task<BattleOutcome> FightChimeraAsync(string difficulty, bool dryRun, CancellationToken ct) =>
        FightAllianceAsync("Chimera", "clan-chimera", 3, PlayTargets.ChimeraResultView, difficulty, dryRun, ct);

    public Task<BattleOutcome> FightHydraAsync(string difficulty, bool dryRun, CancellationToken ct) =>
        FightAllianceAsync("Hydra", "clan-hydra", 2, PlayTargets.HydraResultView, difficulty, dryRun, ct);

    /// <summary>One alliance boss battle: the map item (Demon Lord, Hydra), the row of its enemy type and difficulty, Battle, Start, result.</summary>
    private async Task<BattleOutcome> FightAllianceAsync(string label, string mapKind, int enemyType, string resultView, string difficulty, bool dryRun, CancellationToken ct)
    {
        // The boss's team geared first when it asks for it (Ehsan 2026-11-13).
        var gearArea = enemyType == 3 ? "chimera" : enemyType == 2 ? "hydra" : "clan-boss";
        var gearChoice = TeamsOf(gearArea)?.Default;
        await PrepareTeam(label, gearArea, BotTeams.ContentArea(gearArea), gearArea == "hydra" ? 6 : 5, gearChoice, dryRun, ct);
        var started = DateTime.UtcNow;
        log($"autoplay: STEP {label} {difficulty}");
        await OpenAllianceEnemy(mapKind, resultView, ct);

        // The difficulty: its row in the Demon Lord's list, selected (the list may need dragging).
        bool Mine(UiTarget t) => t.Kind == "clan-difficulty" && t.Note?.Contains($"type {enemyType}", StringComparison.Ordinal) == true
            && ClanBossRules.Same(difficulty, t.Name.Replace("clan boss: ", "", StringComparison.Ordinal));
        var (_, targets, _) = await Read(ct);
        if (!_clanDumped.Contains(mapKind)) { _clanDumped.Add(mapKind); log($"autoplay: the {label} list. Screen dump: {DumpScreen($"{label} list")}"); }
        var row = targets.FirstOrDefault(Mine);
        if (row is null)
            throw new PlayStopException($"No {label} row named {difficulty} ({string.Join(", ", targets.Where(t => t.Kind == "clan-difficulty").Select(t => t.Name))}). Screen dump: {DumpScreen("clan difficulty")}");
        if (row.Note?.Contains("locked", StringComparison.Ordinal) == true && row.Note.Contains("; locked", StringComparison.Ordinal))
            return new BattleOutcome(BattleResult.NotStarted, $"{difficulty} is locked in the game");
        if (row.Note?.Contains("; selected", StringComparison.Ordinal) != true)
        {
            await DragIntoView("clan-difficulty", ct, Mine);
            await Click("clan-difficulty", Mine, (_, scan) => scan().Targets.Any(t => Mine(t) && t.Note?.Contains("; selected", StringComparison.Ordinal) == true),
                $"{difficulty} selected", ct);
            await Pause(600, ct);
        }
        // Start greyed out (2026-11-06 02:56: BlackLord65 right after the boss's reset, "0 damage today", twice): not now; the activity tries again in an hour.
        if ((await Read(ct)).Targets.FirstOrDefault(t => t.Kind == "clan-battle") is { NotClickable: { } why })
        {
            await HomeAsync(Action("to the village"), ct);
            return new BattleOutcome(BattleResult.NotStarted, $"{label} {difficulty}: Start is greyed out ({why}); the boss may be resetting");
        }
        await Click("clan-battle", null, (s2, _) => s2.Top?.Name is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView),
            "the team screen", ct, within: TimeSpan.FromSeconds(10));
        var after = await Settled(ct);
        if (Top(after) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, $"no {label} key (the refill offer was closed)");
        }
        var teamView = Top(after) ?? "";
        if (!PlayTargets.IsTeamView(teamView)) await Refuse(after, $"{label} {difficulty}", ct);
        if (!await ApplyDefaultTeam(enemyType == 3 ? "chimera" : enemyType == 2 ? "hydra" : "clan-boss", teamView, ct))
            return new BattleOutcome(BattleResult.NotStarted, "the Teams card's team was refused here");
        // An empty team screen (no team ever saved there; seen 2026-10-31 on the Hydra): nothing to start with, the row waits.
        if (((await Read(ct)).Play.TeamIds ?? []).Count == 0)
        {
            await Pause(1000, ct);
            if (((await Read(ct)).Play.TeamIds ?? []).Count == 0)
            {
                // No team saved (Ehsan 2026-11-01: "make the best team yourself"): the strongest geared champions, one of each type.
                var (data, _, ratings, _) = await Account(ct);
                // A champion the screen refuses is left out and the next best takes the place (2026-11-01: the Hydra locks those it has used, and the same six
                // were tried for hours with 2 keys waiting). Known only for this battle: a lock ends with the rotation, and these are no food.
                var locked = new HashSet<int>();
                List<int>? team = null;
                for (var attempt = 0; attempt < 5 && team is null; attempt++)
                {
                    var next = data.Champions.Where(c => !c.InVault && !c.InBathhouse && c.Artifacts.Count > 0 && c.Level >= AutoplaySettings.Load(ExtractorOutput).Rules.BuiltTeamMinLevel && !locked.Contains(c.Id))
                        .OrderByDescending(c => CursedCityPlanner.Score(c, ratings)).DistinctBy(c => c.BaseTypeId).Take(6).Select(c => c.Id).ToList();
                    if (next.Count < 6) break;
                    log($"autoplay: STEP team: {string.Join(", ", next.Select(Who))}");
                    try
                    {
                        await SetTeam(next, ct, teamView);
                        team = next;
                    }
                    catch (LockedHeroException e)
                    {
                        locked.Add(e.HeroId);
                        foreach (var (id, _) in e.Shown) locked.Add(id);
                        log($"autoplay: {label}: locked here: {string.Join(", ", locked.Select(Who))}");
                    }
                }
                if (team is null)
                {
                    await Back(teamView, ct);
                    return new BattleOutcome(BattleResult.NotStarted, "no team could be set there");
                }
                log($"autoplay: {label}: no saved team, the bot built {string.Join(", ", team.Select(Who))}");
                await Pause(800, ct);
            }
        }
        if (dryRun)
        {
            var (_, t2, _) = await Read(ct);
            var start = t2.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {label} {difficulty} (dry run: NOT pressed)", r, always: true);
            else log($"autoplay: Start not identified on {teamView}. Screen dump: {DumpScreen("clan team start")}");
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null ? "Start shown, not pressed" : $"Start not identified: {start?.Note ?? "no team-start target"}");
        }
        await Click("team-start", t => t.View == teamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView or "AllianceBossQuickBattleConfirmOverlay" or "ChimeraQuickBattleConfirmOverlay" or QuickResult or ChimeraQuickResult,
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        // The game asks before a normal battle where Quick Battle is open (seen 2026-11-01): its one action is Continue.
        if (Top(await ReadState(ct)) is "AllianceBossQuickBattleConfirmOverlay" or "ChimeraQuickBattleConfirmOverlay")
        {
            await Pause(800, ct);
            log($"autoplay: the Quick Battle question. Screen dump: {DumpScreen("clan confirm")}");
            await Click("clan-continue", null, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView or QuickResult,
                "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        }
        // Quick Battle on (the team screen's switch, set in the game): no fight, the result at once (seen 2026-11-01: 23.03M on Brutal, a key spent).
        if (Top(await ReadState(ct)) is QuickResult or ChimeraQuickResult)
        {
            await Pause(1000, ct);
            await LeaveResult(ct, Top(await ReadState(ct)) ?? QuickResult);
            log($"autoplay: {label} {difficulty}: Quick Battle done");
            return new BattleOutcome(BattleResult.Victory, $"Quick Battle, {(DateTime.UtcNow - started).TotalSeconds:0} s");
        }
        var inBattle = await ReadState(ct);
        if (Top(inBattle) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, $"no {label} key (the refill offer was closed)");
        }
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start {label} {difficulty}", ct);
        log($"autoplay: {label} {difficulty} started");
        var result = await WaitBattle(ct, resultView);
        var damage = result?.ClanDamage;
        await LeaveResult(ct, resultView);
        // An alliance boss is never "won": the battle ends when the team falls or the turns run out; its result is the damage.
        return new BattleOutcome(BattleResult.Victory, $"damage {damage?.Replace(" Damage Dealt", "", StringComparison.OrdinalIgnoreCase) ?? "?"}, {(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    /// <summary>An alliance boss's difficulty list on top (the map item <paramref name="mapKind"/>), from wherever the game is.</summary>
    public async Task<int> RefillClanBossKeysAsync(bool dryRun, CancellationToken ct)
    {
        await OpenAllianceEnemy("clan-demonlord", PlayTargets.ClanResultView, ct);
        return await RefillByPlus("AllianceBossKey", "Clan Boss keys", dryRun, ct);
    }

    private readonly Dictionary<string, DateTime> _chestSweep = [];

    /// <summary>
    /// Yesterday's alliance boss chests (Ehsan 2026-11-13: "you are not collecting rewards for Demon Lord, it is on the same screen as you play"): the
    /// selected row's Collect Chest whenever it shows, and every row of the boss looked at every 6 hours (a chest left there greys Start out).
    /// </summary>
    private async Task CollectAllianceChests(string type, CancellationToken ct)
    {
        await TakeClanChest(ct);
        var key = $"{(await Account(ct)).Data.Account.Name}|{type}";
        if (_chestSweep.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(6)) return;
        _chestSweep[key] = DateTime.UtcNow;
        var names = (await Read(ct)).Targets.Where(t => t.Kind == "clan-difficulty" && t.Note?.Contains(type, StringComparison.Ordinal) == true
            && t.Note.Contains("; locked", StringComparison.Ordinal) != true).Select(t => t.Name).Distinct().ToList();
        foreach (var name in names)
        {
            bool Row(UiTarget t) => t.Kind == "clan-difficulty" && t.Name == name && t.Note?.Contains(type, StringComparison.Ordinal) == true;
            var row = (await Read(ct)).Targets.FirstOrDefault(Row);
            if (row is null) continue;
            if (row.Note?.Contains("; selected", StringComparison.Ordinal) != true)
            {
                try
                {
                    await DragIntoView("clan-difficulty", ct, Row);
                    await Click("clan-difficulty", Row, (_, scan) => scan().Targets.Any(t => Row(t) && t.Note?.Contains("; selected", StringComparison.Ordinal) == true), $"{name} selected", ct);
                }
                catch (PlayStopException e) { log($"autoplay: {name}: not selected to look for its chest ({e.Message})"); continue; }
                await Pause(600, ct);
            }
            await TakeClanChest(ct);
        }
    }

    /// <summary>The selected row's Collect Chest when it shows: clicked, the reward window closed.</summary>
    private async Task TakeClanChest(CancellationToken ct)
    {
        var (_, targets, _) = await Read(ct);
        if (targets.FirstOrDefault(t => t.Kind == "clan-collect" && t.NotClickable is null) is not { } box) return;
        var row = targets.FirstOrDefault(t => t.Kind == "clan-difficulty" && t.Note?.Contains("; selected", StringComparison.Ordinal) == true)?.Name ?? "the boss";
        try
        {
            await Click("clan-collect", t => t.Path == box.Path, (s2, scan) => Top(s2) != PlayTargets.ClanDialogView || !scan().Targets.Any(t => t.Kind == "clan-collect" && t.NotClickable is null),
                $"{row}: chest taken", ct, within: TimeSpan.FromSeconds(8));
            await Pause(1500, ct);
            var top = Top(await Settled(ct));
            log($"autoplay: {row}: chest collected" + (top != PlayTargets.ClanDialogView ? $" (reward window {top}). Screen dump: {DumpScreen("clan chest reward")}" : ""));
            // The chest's window (GachaChestOverlay, seen 2026-11-13) has no close: it waits, then Esc; any other window its own close.
            for (var k = 0; k < 6 && Top(await Settled(ct)) is { } t2 && t2 != PlayTargets.ClanDialogView; k++)
            {
                if (t2 == "GachaChestOverlay")
                {
                    await Pause(1500, ct);
                    if (Top(await Settled(ct)) != t2) continue;
                    if (Game.Window.GameWindow.Find().Window is { } w && Input.MouseInput.SendEscape(w) is null) log("autoplay: the chest's window: Esc sent");
                    await Pause(1200, ct);
                    continue;
                }
                await Back(t2, ct);
                await Pause(800, ct);
            }
        }
        catch (PlayStopException e) { log($"autoplay: {row}: chest not taken ({e.Message}). Screen dump: {DumpScreen("clan chest")}"); }
    }

    private async Task OpenAllianceEnemy(string mapKind, string resultView, CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, resultView)) { await WaitBattle(ct, resultView); continue; }
            if (Has(s, resultView)) { await LeaveResult(ct, resultView); continue; }
            if (Has(s, QuickResult)) { await LeaveResult(ct, QuickResult); continue; }
            if (Top(s) is { } top && PlayTargets.IsTeamView(top)) { await Back(top, ct); continue; }
            var (_, targets, _) = await Read(ct);
            // The list of the wanted boss: its rows' enemy type (a list of the other boss is left).
            var want = mapKind == "clan-chimera" ? "type 3" : mapKind == "clan-hydra" ? "type 2" : "type 1";
            if (targets.Any(t => t.Kind == "clan-difficulty" && t.Note?.Contains(want, StringComparison.Ordinal) == true)) { await CollectAllianceChests(want, ct); return; }
            if (targets.Any(t => t.Kind == "clan-difficulty")) { await Back(Top(s)!, ct); continue; }
            if (targets.Any(t => t.Kind == mapKind))
            {
                await Click(mapKind, null, (_, scan) => scan().Targets.Any(t => t.Kind == "clan-difficulty"), $"{mapKind} opens", ct, within: TimeSpan.FromSeconds(12));
                await Pause(800, ct);
                continue;
            }
            if (Has(s, PlayTargets.ClanMapView))
                throw new PlayStopException($"The alliance map is open but {mapKind} is not recognised. Screen dump: {DumpScreen(mapKind)}");
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-clan", ct);
                await Click("mode-clan", null, (s2, scan) => Has(s2, PlayTargets.ClanMapView) || scan().Targets.Any(t => t.Kind is "clan-demonlord" or "clan-hydra" or "clan-difficulty"),
                    "the alliance map opens", ct, within: TimeSpan.FromSeconds(12));
                await Pause(800, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await TowardsBattleMenu(s, ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"{mapKind} was not reached in 10 steps. Screen dump: {DumpScreen("clan boss not reached")}");
    }
}
