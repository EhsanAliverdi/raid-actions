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
    private const string QuickResult = "AllianceBossQuickBattleOverlay";

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

    public async Task<ClanBossAccount> ReadHydraAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        var clan = data.Progress?.Clan;
        return new ClanBossAccount(data.Account.Resources.GetValueOrDefault(HydraRules.Key), clan?.HydraBestDamage ?? [], clan?.HydraMaxQuickBattle);
    }

    public Task<BattleOutcome> FightHydraAsync(string difficulty, bool dryRun, CancellationToken ct) =>
        FightAllianceAsync("Hydra", "clan-hydra", 2, PlayTargets.HydraResultView, difficulty, dryRun, ct);

    /// <summary>One alliance boss battle: the map item (Demon Lord, Hydra), the row of its enemy type and difficulty, Battle, Start, result.</summary>
    private async Task<BattleOutcome> FightAllianceAsync(string label, string mapKind, int enemyType, string resultView, string difficulty, bool dryRun, CancellationToken ct)
    {
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
            await Task.Delay(600, ct);
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
            var (_, t2, _) = await Read(ct);
            var start = t2.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {label} {difficulty} (dry run: NOT pressed)", r, always: true);
            else log($"autoplay: Start not identified on {teamView}. Screen dump: {DumpScreen("clan team start")}");
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start?.NotClickable is null && start is not null ? "Start shown, not pressed" : $"Start not identified: {start?.Note ?? "no team-start target"}");
        }
        await Click("team-start", t => t.View == teamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView or "AllianceBossQuickBattleConfirmOverlay",
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        // The game asks before a normal battle where Quick Battle is open (seen 2026-11-01): its one action is Continue.
        if (Top(await ReadState(ct)) == "AllianceBossQuickBattleConfirmOverlay")
        {
            await Task.Delay(800, ct);
            log($"autoplay: the Quick Battle question. Screen dump: {DumpScreen("clan confirm")}");
            await Click("clan-continue", null, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView or QuickResult,
                "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        }
        // Quick Battle on (the team screen's switch, set in the game): no fight, the result at once (seen 2026-11-01: 23.03M on Brutal, a key spent).
        if (Top(await ReadState(ct)) == QuickResult)
        {
            await Task.Delay(1000, ct);
            await LeaveResult(ct, QuickResult);
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
            var want = mapKind == "clan-hydra" ? "type 2" : "type 1";
            if (targets.Any(t => t.Kind == "clan-difficulty" && t.Note?.Contains(want, StringComparison.Ordinal) == true)) return;
            if (targets.Any(t => t.Kind == "clan-difficulty")) { await Back(Top(s)!, ct); continue; }
            if (targets.Any(t => t.Kind == mapKind))
            {
                await Click(mapKind, null, (_, scan) => scan().Targets.Any(t => t.Kind == "clan-difficulty"), $"{mapKind} opens", ct, within: TimeSpan.FromSeconds(12));
                await Task.Delay(800, ct);
                continue;
            }
            if (Has(s, PlayTargets.ClanMapView))
                throw new PlayStopException($"The alliance map is open but {mapKind} is not recognised. Screen dump: {DumpScreen(mapKind)}");
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-clan", ct);
                await Click("mode-clan", null, (s2, scan) => Has(s2, PlayTargets.ClanMapView) || scan().Targets.Any(t => t.Kind is "clan-demonlord" or "clan-hydra" or "clan-difficulty"),
                    "the alliance map opens", ct, within: TimeSpan.FromSeconds(12));
                await Task.Delay(800, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"{mapKind} was not reached in 10 steps. Screen dump: {DumpScreen("clan boss not reached")}");
    }
}
