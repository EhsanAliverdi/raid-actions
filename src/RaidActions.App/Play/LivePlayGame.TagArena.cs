using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Tag Team Arena on the live game (raid-extractor plan/tag-arena.md): village → Battle → Arena → the arena map's Tag Team Arena
/// (`ArenaMapContext._arena3x3`) → its battle tab (`Arena3x3BattleTabContext`: opponents, the free refresh) → Battle → the three teams
/// the game has set → Start → three rounds → result. Built from the dump on 2026-10-31; the screens' names are to confirm live.
/// </summary>
public sealed partial class LivePlayGame : ITagArenaGame
{
    public async Task<double> ReadTagTokensAsync(CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        return data.Account.Resources.GetValueOrDefault("Arena3x3Token");
    }

    public async Task<ArenaBoard> OpenTagArenaAsync(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle) { log("autoplay: a battle is running; waiting for it to end (no input)"); await WaitBattle(ct, "BattleFinishArena3x3Dialog"); continue; }
            if (Top(s) is { } r && r.StartsWith("BattleFinish", StringComparison.Ordinal)) { await LeaveResult(ct, r); continue; }
            var (_, targets, play) = await Read(ct);
            if (play.Tag is { } board && targets.Any(t => t.Kind is "tag-opponent" or "tag-refresh")) return TagBoard(board);
            if (Top(s) is { } top && PlayTargets.IsTeamView(top)) { await Back(top, ct); continue; }
            if (targets.Any(t => t.Kind == "arena-tag"))
            {
                await Click("arena-tag", null, (_, scan) => scan().Targets.Any(t => t.Kind is "tag-opponent" or "tag-refresh"), "the Tag Team Arena opens", ct, within: TimeSpan.FromSeconds(12));
                await Task.Delay(1000, ct);
                continue;
            }
            if (Has(s, PlayTargets.ArenaMapView)) throw new PlayStopException($"The arena map is open but its Tag Team Arena is not recognised. Screen dump: {DumpScreen("tag arena")}");
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await Click("mode-arena", null, (s2, _) => Has(s2, PlayTargets.ArenaMapView), "the arena map opens", ct);
                continue;
            }
            if (Top(s) is { } other && other != PlayTargets.VillageHudView && !Input.NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView) || Has(s2, PlayTargets.ArenaMapView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not reach the Tag Team Arena in 10 steps. Screen dump: {DumpScreen("tag arena not reached")}");
    }

    private static ArenaBoard TagBoard(ArenaBoardRead b) => new(DateTime.UtcNow,
        [.. b.Rows.Select(r => new ArenaOpponentInfo(r.UserId, r.Name, r.Power, (OpponentStatus)Math.Max(0, r.Status), r.Playable && r.Status is 0))],
        b.FreeRefreshAvailable == true, GameText.TimeLeft(b.FreeRefreshText));

    public async Task<BattleOutcome> FightTagOpponentAsync(long userId, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var (_, targets, _) = await Read(ct);
        var row = targets.FirstOrDefault(t => t.Kind == "tag-opponent" && t.Key == userId)
            ?? throw new PlayStopException($"Tag Team Arena: opponent {userId} is not on the board.");
        log($"autoplay: STEP Tag Team Arena: {row.Name}");
        await Click("tag-opponent", t => t.Key == userId, (s2, _) => s2.Top?.Name is { } top && (PlayTargets.IsTeamView(top) || top is PlayTargets.RefillView or UiState.MessageBoxView),
            "the team screen", ct, within: TimeSpan.FromSeconds(10));
        var after = await Settled(ct);
        if (Top(after) == PlayTargets.RefillView)
        {
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "no Tag Team token (the refill offer was closed)");
        }
        var teamView = Top(after) ?? "";
        if (!PlayTargets.IsTeamView(teamView)) await Refuse(after, "Tag Team Arena battle", ct);
        var (_, t2, _) = await Read(ct);
        var start = t2.FirstOrDefault(t => t.Kind == "team-start" && t.View == teamView);
        if (dryRun || start is null)
        {
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, Tag Team Arena (dry run: NOT pressed)", r, always: true);
            else log($"autoplay: Start not identified on {teamView}. Screen dump: {DumpScreen("tag team start")}");
            await Back(teamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, start is null ? "Start not identified" : "Start shown, not pressed");
        }
        await Click("team-start", t => t.View == teamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        // Three rounds: each a battle; the result screens in between and at the end are left until the arena's board is back.
        PlayScan? last = null;
        for (var round = 0; round < 4; round++)
        {
            var s = await ReadState(ct);
            if (!s.InBattle && !(Top(s)?.StartsWith("BattleFinish", StringComparison.Ordinal) ?? false)) break;
            last = await WaitBattle(ct, "BattleFinishArena3x3Dialog") ?? last;
            var r = await ReadState(ct);
            if (Top(r) is { } fin && fin.StartsWith("BattleFinish", StringComparison.Ordinal)) await LeaveResult(ct, fin);
            await Task.Delay(1500, ct);
        }
        var outcome = last?.Result?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => (last?.Pve?.ResultType) switch
        {
            1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown,
        } };
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }

    public async Task<ArenaBoard?> RefreshTagFreeAsync(bool dryRun, CancellationToken ct)
    {
        var (_, targets, play) = await Read(ct);
        var refresh = targets.FirstOrDefault(t => t.Kind == "tag-refresh");
        if (refresh is null || play.Tag?.FreeRefreshAvailable != true) return null;
        if (dryRun)
        {
            if (refresh.Rect is { } r) Shot("autoplay: WOULD CLICK the Tag Team Arena's free refresh (dry run: NOT pressed)", r, always: true);
            return null;
        }
        var before = string.Join(",", (play.Tag?.Rows ?? []).Select(x => x.UserId));
        await Click("tag-refresh", null, (_, scan) => string.Join(",", (scan().Tag?.Rows ?? []).Select(x => x.UserId)) != before, "a new board", ct,
            within: TimeSpan.FromSeconds(10), before: p => p.Tag?.FreeRefreshAvailable == true ? null : "the refresh is not free now");
        await Task.Delay(800, ct);
        var (_, _, now) = await Read(ct);
        return now.Tag is { } b ? TagBoard(b) : null;
    }
}
