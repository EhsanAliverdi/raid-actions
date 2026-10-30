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
    private bool _clanDumped;

    public async Task<ClanBossAccount> ReadClanBossAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        var clan = data.Progress?.Clan;
        return new ClanBossAccount(data.Account.Resources.GetValueOrDefault(ClanBossRules.Key), clan?.ClanBossBestDamage ?? [], clan?.ClanBossMaxQuickBattle);
    }

    public async Task<BattleOutcome> FightClanBossAsync(string difficulty, bool quick, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        log($"autoplay: STEP Clan Boss {difficulty}{(quick ? " (Quick Battle asked: not explored yet, a normal battle)" : "")}");
        await OpenDemonLord(ct);

        // The difficulty: its row in the Demon Lord's list, selected (the list may need dragging).
        bool Mine(UiTarget t) => t.Kind == "clan-difficulty" && ClanBossRules.Same(difficulty, t.Name.Replace("clan boss: ", "", StringComparison.Ordinal));
        var (_, targets, _) = await Read(ct);
        if (!_clanDumped) { _clanDumped = true; log($"autoplay: the Demon Lord's list. Screen dump: {DumpScreen("clan boss list")}"); }
        var row = targets.FirstOrDefault(Mine);
        if (row is null)
            throw new PlayStopException($"No Clan Boss row named {difficulty} ({string.Join(", ", targets.Where(t => t.Kind == "clan-difficulty").Select(t => t.Name))}). Screen dump: {DumpScreen("clan difficulty")}");
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
            return new BattleOutcome(BattleResult.NotStarted, "no Clan Boss key (the refill offer was closed)");
        }
        var teamView = Top(after) ?? "";
        if (!PlayTargets.IsTeamView(teamView)) await Refuse(after, $"Clan Boss {difficulty}", ct);
        if (dryRun)
        {
            var (_, t2, _) = await Read(ct);
            var start = t2.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, Clan Boss {difficulty} (dry run: NOT pressed)", r, always: true);
            else log($"autoplay: Start not identified on {teamView}. Screen dump: {DumpScreen("clan team start")}");
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
            return new BattleOutcome(BattleResult.NotStarted, "no Clan Boss key (the refill offer was closed)");
        }
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start Clan Boss {difficulty}", ct);
        log($"autoplay: Clan Boss {difficulty} started");
        var result = await WaitBattle(ct, PlayTargets.ClanResultView);
        var damage = result?.ClanDamage;
        await LeaveResult(ct, PlayTargets.ClanResultView);
        // The Clan Boss is never "won": the battle ends when the team falls or the turns run out; its result is the damage.
        return new BattleOutcome(BattleResult.Victory, $"damage {damage?.Replace(" Damage Dealt", "", StringComparison.OrdinalIgnoreCase) ?? "?"}, {(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    /// <summary>The Demon Lord's difficulty list on top, from wherever the game is.</summary>
    private async Task OpenDemonLord(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.ClanResultView)) { await WaitBattle(ct, PlayTargets.ClanResultView); continue; }
            if (Has(s, PlayTargets.ClanResultView)) { await LeaveResult(ct, PlayTargets.ClanResultView); continue; }
            if (Top(s) is { } top && PlayTargets.IsTeamView(top)) { await Back(top, ct); continue; }
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "clan-difficulty")) return;
            if (targets.Any(t => t.Kind == "clan-demonlord"))
            {
                await Click("clan-demonlord", null, (_, scan) => scan().Targets.Any(t => t.Kind == "clan-difficulty"), "the Demon Lord opens", ct, within: TimeSpan.FromSeconds(12));
                await Task.Delay(800, ct);
                continue;
            }
            if (Has(s, PlayTargets.ClanMapView))
                throw new PlayStopException($"The alliance map is open but the Demon Lord is not recognised. Screen dump: {DumpScreen("demon lord")}");
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await DragIntoView("mode-clan", ct);
                await Click("mode-clan", null, (s2, scan) => Has(s2, PlayTargets.ClanMapView) || scan().Targets.Any(t => t.Kind is "clan-demonlord" or "clan-difficulty"),
                    "the alliance map opens", ct, within: TimeSpan.FromSeconds(12));
                await Task.Delay(800, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"The Demon Lord was not reached in 10 steps. Screen dump: {DumpScreen("clan boss not reached")}");
    }
}
