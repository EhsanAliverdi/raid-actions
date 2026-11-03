using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

/// <summary>
/// Live Arena on the live game (raid-extractor plan/live-arena.md): the battle menu's Live Arena button → LiveArenaDialog → Find Opponent → the
/// draft (LiveArenaDraftDialog, its phases HeroPick, HeroBan, LeaderSlotSelection) → Start → auto → the result. The dialog is seen live; the draft
/// is from the dump, a match needs a token and the arena's hours: each new phase leaves a screen dump for the first live match.
/// </summary>
public sealed partial class LivePlayGame : ILiveArenaGame
{
    private const string LiveDraftView = "LiveArenaDraftDialog", LiveResultView = "BattleFinishLiveArenaDialog";

    public async Task<LiveArenaState> ReadLiveArenaAsync(CancellationToken ct)
    {
        // From the village its badge tells, without opening anything.
        var (s0, t0, _) = await Read(ct);
        if (Input.NavigatingDriver.AtVillage(s0) && !t0.Any(t => t.Kind == "live-badge")) return new LiveArenaState(false, false);
        // Out of its hours the battle menu has no Live Arena button (seen 2026-11-01): closed, not an error.
        if (!await OpenLiveArena(ct)) return new LiveArenaState(false, false);
        var (_, targets, _) = await Read(ct);
        var can = targets.Any(t => t.Kind == "live-find" && t.NotClickable is null && t.Note == "enabled");
        // The hours' context is not always read (2026-11-03: open for 2h, read as closed every time); an enabled Find Opponent is the game saying it is open.
        var active = can || targets.FirstOrDefault(t => t.Kind == "live-state")?.Note == "active";
        return new LiveArenaState(active, can);
    }

    private async Task<bool> OpenLiveArena(CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 8; hop++)
        {
            var s = await Settled(ct);
            var (_, targets, _) = await Read(ct);
            if (targets.Any(t => t.Kind == "live-find")) return true;
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                if (!targets.Any(t => t.Kind == "live-open")) return false;
                await DragIntoView("live-open", ct);
                await Click("live-open", null, (_, scan) => scan().Targets.Any(t => t.Kind == "live-find"), "Live Arena opens", ct, within: TimeSpan.FromSeconds(10));
                await Task.Delay(1000, ct);
                continue;
            }
            if (!Input.NavigatingDriver.AtVillage(s)) { await HomeAsync(Action("to the village"), ct); await ToVillage(ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Live Arena was not reached. Screen dump: {DumpScreen("live arena not reached")}");
    }

    public async Task<BattleOutcome> PlayLiveArenaAsync(LiveArenaSettings s, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        if (InLiveMatch(await ReadState(ct))) return await FinishLiveMatchAsync(s, started, ct);
        if (!await OpenLiveArena(ct)) return new BattleOutcome(BattleResult.NotStarted, "closed now");
        if (dryRun)
        {
            var (_, t0, _) = await Read(ct);
            if (t0.FirstOrDefault(t => t.Kind == "live-find")?.Rect is { } r) Shot("autoplay: WOULD CLICK Find Opponent (dry run: NOT pressed)", r, always: true);
            return new BattleOutcome(BattleResult.NotStarted, "Find Opponent shown, not pressed");
        }
        await Click("live-find", null, (s2, _) => Has(s2, LiveDraftView) || Top(s2) == "LiveArenaMatchMakingDialog" || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView, "the match is searched", ct, within: TimeSpan.FromSeconds(5));
        // Out of tokens the game offers to buy some (2026-11-03: Find Opponent stays enabled at 0): closed, nothing bought, no match.
        var (after, _, _) = await Read(ct);
        if (Top(after) is PlayTargets.RefillView or UiState.MessageBoxView)
        {
            await HomeAsync(Action("from the token offer"), ct);
            return new BattleOutcome(BattleResult.NotStarted, "out of Live Arena tokens (the game offered to buy some; closed, nothing bought)");
        }
        log("autoplay: looking for a Live Arena opponent");
        return await FinishLiveMatchAsync(s, started, ct);
    }

    private bool InLiveMatch(UiState state) => Has(state, LiveDraftView) || Has(state, "LiveArenaMatchMakingDialog") || Has(state, LiveResultView);

    /// <summary>From the search on: the draft, the battle and its result.</summary>
    private async Task<BattleOutcome> FinishLiveMatchAsync(LiveArenaSettings s, DateTime started, CancellationToken ct)
    {
        var (data, _, _, _) = await Account(ct);
        var phasesSeen = new HashSet<string>();
        var leaders = new List<int>();
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(6);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(700, ct);
            var state = await ReadState(ct);
            if (state.Active.Any(v => BattleViews.Contains(v.Name)) || Has(state, LiveResultView)) break;
            if (!Has(state, LiveDraftView)) continue; // still searching
            var (_, targets, play) = await Read(ct);
            var phase = targets.FirstOrDefault(t => t.Kind == "live-phase");
            var parts = (phase?.Note ?? "").Split('|');
            var name = parts.ElementAtOrDefault(0);
            if (phasesSeen.Add(name ?? "?")) log($"autoplay: Live Arena phase '{name}' ({phase?.Note}). Screen dump: {DumpScreen($"live arena {name}")}");
            if (parts.ElementAtOrDefault(1) != "mine") continue;
            // Confirm lit means the turn's choices are made (first in the draft: 1 pick; second: 2): never a choice more.
            if (targets.Any(t => t.Kind == "live-confirm" && t.NotClickable is null))
            {
                await Click("live-confirm", null, (_, _) => true, "the turn confirmed", ct);
                continue;
            }
            switch (LiveArenaDraft.Kind(name))
            {
                case "leader":
                {
                    var squad = targets.Where(t => t.Kind == "live-slot" && t.Note == "mine" && t.Id is > 0).Select(t => t.Id!.Value).ToList();
                    var want = LiveArenaDraft.Leader(leaders.Count == 0 ? s.PrimaryLeader : s.SecondaryLeader, squad, leaders, data.Champions);
                    if (want is { } id)
                    {
                        await Click("live-slot", t => t.Id == id && t.Note == "mine", (_, _) => true, $"leader {Who(id)}", ct);
                        leaders.Add(id);
                        log($"autoplay: Live Arena leader {leaders.Count}: {Who(id)}");
                    }
                    break;
                }
                case "ban":
                {
                    var enemy = targets.Where(t => t.Kind == "live-slot" && t.Note == "enemy" && t.NotClickable is null && t.Id is not null)
                        .Select(t => (Slot: t.Id!.Value, Name: t.Name.Split(": ", 2).ElementAtOrDefault(1))).ToList();
                    if (LiveArenaDraft.Ban(s.BanList, enemy) is { } slot)
                    {
                        await Click("live-slot", t => t.Id == slot && t.Note == "enemy", (_, _) => true, "the slot to ban", ct);
                        log($"autoplay: Live Arena: banned slot {slot} ({enemy.First(e => e.Slot == slot).Name}) of {string.Join(", ", enemy.Select(e => e.Name))}");
                    }
                    break;
                }
                default:
                {
                    var offered = (play.Picks ?? []).Where(p => p.State is null or 0 && p.Pick is { NotClickable: null }).Select(p => p.HeroId).ToList();
                    if (LiveArenaDraft.Pick(s, offered, data.Champions) is { } id)
                    {
                        await Click("team-pick", t => t.Id == id, (_, _) => true, $"{Who(id)} picked", ct);
                        log($"autoplay: Live Arena pick: {Who(id)}");
                    }
                    break;
                }
            }
        }
        var result = await WaitBattle(ct, LiveResultView);
        var (_, _, last) = await Read(ct);
        var outcome = (result ?? last).Result?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        await LeaveResult(ct, LiveResultView);
        return new BattleOutcome(outcome, $"{(DateTime.UtcNow - started).TotalSeconds:0} s");
    }
}
