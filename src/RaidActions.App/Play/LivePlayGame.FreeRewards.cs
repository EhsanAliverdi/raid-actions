using RaidExtractor.Autoplay;
using RaidActions.Autoplay;
using RaidActions.Game.Memory;

namespace RaidActions.App.Play;

/// <summary>
/// Free rewards on the live game (raid-extractor plan/free-rewards.md): the village's playtime gifts (HourlyGifts button → the gifts
/// overlay → each gift ready to collect clicked). Built from the dump on 2026-10-31.
/// </summary>
public sealed partial class LivePlayGame : IFreeRewardsGame
{
    public async Task<int> CollectPlaytimeGiftsAsync(bool dryRun, CancellationToken ct)
    {
        _nav.FitWindow();
        var s = await Settled(ct);
        if (!Input.NavigatingDriver.AtVillage(s)) await _nav.GoHome(Action("to the village"), ct);
        var (_, targets, _) = await Read(ct);
        if (!targets.Any(t => t.Kind == "village-gifts" && t.NotClickable is null))
            throw new PlayStopException($"The village's playtime gifts button is not recognised. Screen dump: {DumpScreen("village gifts")}");
        await Click("village-gifts", null, (_, scan) => scan().Targets.Any(t => t.Kind == "gift-prize"), "the playtime gifts open", ct, within: TimeSpan.FromSeconds(8));
        await Task.Delay(800, ct);
        var got = 0;
        for (var i = 0; i < 12; i++)
        {
            var (s2, now, _) = await Read(ct);
            var ready = now.FirstOrDefault(t => t.Kind == "gift-prize" && t.Note == "state 1" && t.NotClickable is null);
            if (ready is null) break;
            if (dryRun)
            {
                if (ready.Rect is { } r) Shot($"autoplay: WOULD CLICK {ready.Name} (dry run: NOT pressed)", r, always: true);
                break;
            }
            var top = Top(s2);
            await Click("gift-prize", t => t.Path == ready.Path, (s3, scan) => Top(s3) != top || scan().Targets.Any(t => t.Path == ready.Path && t.Note != "state 1"),
                $"{ready.Name} collected", ct, within: TimeSpan.FromSeconds(8));
            got++;
            log($"autoplay: collected {ready.Name}");
            await Task.Delay(1200, ct);
            // A reward window over the gifts (the prize shown): closed by its own close.
            var after = await Settled(ct);
            if (Top(after) is { } t3 && t3 != top) await Back(t3, ct);
        }
        await _nav.GoHome(Action("from the playtime gifts"), ct);
        return got;
    }

    public async Task<QuestPass> QuestPassAsync(bool claim, bool dryRun, CancellationToken ct)
    {
        var read = new Dictionary<(string, int, string), QuestRead>();
        _nav.FitWindow();
        var s = await Settled(ct);
        if (!Input.NavigatingDriver.AtVillage(s)) await _nav.GoHome(Action("to the village"), ct);
        var (_, targets, _) = await Read(ct);
        if (!targets.Any(t => t.Kind == "village-quests" && t.NotClickable is null))
            throw new PlayStopException($"The village's Quests button is not recognised. Screen dump: {DumpScreen("village quests")}");
        await Click("village-quests", null, (_, scan) => scan().Targets.Any(t => t.Kind == "quest-tab"), "the quests open", ct, within: TimeSpan.FromSeconds(8));
        await Task.Delay(800, ct);
        var claimed = 0;
        var tabs = (await Read(ct)).Item2.Where(t => t.Kind == "quest-tab").Select(t => t.Id ?? -1).Where(k => k >= 0).Distinct().Order().ToList();
        foreach (var k in tabs)
        {
            var (_, now, _) = await Read(ct);
            var tab = now.FirstOrDefault(t => t.Kind == "quest-tab" && t.Id == k);
            if (tab is null || tab.NotClickable is not null) continue;
            if (tab.Note != "on")
            {
                await Click("quest-tab", t => t.Id == k, (_, scan) => scan().Targets.Any(t => t.Kind == "quest-tab" && t.Id == k && t.Note == "on"), $"quest tab {k}", ct);
                await Task.Delay(800, ct);
            }
            var (s2, after, play) = await Read(ct);
            foreach (var q in play.Quests ?? []) read[(q.Tab, q.Id, q.Text)] = q;
            if (!claim) continue;
            var claimAll = after.FirstOrDefault(t => t.Kind == "quest-claim-all" && t.Note == "available" && t.NotClickable is null);
            var claimable = (play.Quests ?? []).Count(q => q.Claimable);
            var seen = after.Where(t => t.Kind == "quest-claim-all").Select(t => $"{t.Note}{(t.NotClickable is { } why ? $" ({why})" : "")}").ToList();
            if (claimAll is null && (claimable > 0 || (play.Quests ?? []).Any(q => q.Completed)))
                log($"autoplay: quest tab {k}: {claimable} claimable, {(play.Quests ?? []).Count(q => q.Completed)} completed, Claim all: {(seen.Count == 0 ? "not found" : string.Join(", ", seen))}. Screen dump: {DumpScreen($"quest tab {k}")}");
            if (claimAll is null) continue;
            if (dryRun)
            {
                if (claimAll.Rect is { } r) Shot($"autoplay: WOULD CLICK Claim all on quest tab {k} (dry run: NOT pressed)", r, always: true);
                continue;
            }
            var top = Top(s2);
            try { await Click("quest-claim-all", t => t.Path == claimAll.Path, (s3, scan) => Top(s3) != top || !scan().Targets.Any(t => t.Kind == "quest-claim-all" && t.Note == "available"),
                $"quest tab {k}'s rewards claimed", ct, within: TimeSpan.FromSeconds(6)); }
            // The button stays lit after a claim on some tabs: not an error.
            catch (PlayStopException e) when (e.Message.StartsWith("After clicking", StringComparison.Ordinal)) { }
            claimed++;
            log($"autoplay: claimed the rewards of quest tab {k}");
            await Task.Delay(1500, ct);
            // The rewards shown over the quests: closed by their own close.
            for (var i = 0; i < 3; i++)
            {
                var s4 = await Settled(ct);
                if (Top(s4) is not { } t4 || t4 == top) break;
                await Back(t4, ct);
            }
        }
        await _nav.GoHome(Action("from the quests"), ct);
        // The village's Missions: read, and each completed one collected (a click on it) when claiming.
        try
        {
            var (_, vt, _) = await Read(ct);
            if (vt.Any(t => t.Kind == "village-missions" && t.NotClickable is null))
            {
                await Click("village-missions", null, (_, scan) => scan().Targets.Any(t => t.Kind == "mission-item"), "the missions open", ct, within: TimeSpan.FromSeconds(8));
                await Task.Delay(800, ct);
                for (var i = 0; i < 8; i++)
                {
                    var (sm, mt, mp) = await Read(ct);
                    foreach (var q in mp.Quests ?? []) read[(q.Tab, q.Id, q.Text)] = q;
                    var done = mt.FirstOrDefault(t => t.Kind == "mission-item" && t.Note == "state 2" && t.NotClickable is null);
                    if (!claim || done is null) break;
                    if (dryRun)
                    {
                        if (done.Rect is { } r) Shot($"autoplay: WOULD CLICK {done.Name} (dry run: NOT pressed)", r, always: true);
                        break;
                    }
                    var topM = Top(sm);
                    await Click("mission-item", t => t.Path == done.Path, (s3, scan) => Top(s3) != topM || !scan().Targets.Any(t => t.Path == done.Path && t.Note == "state 2"),
                        $"{done.Name} collected", ct, within: TimeSpan.FromSeconds(8));
                    claimed++;
                    log($"autoplay: collected {done.Name}");
                    await Task.Delay(1500, ct);
                    var s4 = await Settled(ct);
                    if (Top(s4) is { } t4 && t4 != topM) await Back(t4, ct);
                }
                await _nav.GoHome(Action("from the missions"), ct);
            }
        }
        catch (PlayStopException e) { log($"autoplay: missions not read: {e.Message}"); }
        log($"autoplay: quests read: {read.Count} ({string.Join("; ", read.Values.Select(q => $"{q.Tab} {q.Type}: {q.Text} {q.Progress}{(q.Completed ? " done" : "")}"))})");
        return new QuestPass(claimed, [.. read.Values]);
    }
}
