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
}
