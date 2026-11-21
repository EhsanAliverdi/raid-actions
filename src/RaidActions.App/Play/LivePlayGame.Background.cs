namespace RaidActions.App.Play;

using RaidActions.Game.Memory;

/// <summary>
/// The game's background battles block every new battle until their results are collected (2026-11-21 BlackLord65: no battle 15:02-16:40).
/// On a stage list a finished run shows ResultBattleButton_h on its row (bg-result: free to press); a running one ActiveBackgroundBattle_h (bg-running).
/// What starts or stops a run (AutoBattleButton_h, StopBattleButton_h, auto-climb) is a forbidden target and never pressed.
/// </summary>
public sealed partial class LivePlayGame
{
    private const string BackgroundRewardView = "BackgroundBattleRewardListOverlay";

    /// <summary>Whether this view is the list of a background run's rewards (closed after collecting).</summary>
    public static bool IsBackgroundRewardView(string? view) => view is not null && view.StartsWith("BackgroundBattleRewardList", StringComparison.Ordinal);

    /// <summary>The running background runs on a scan (their rows' paths and what they are), for the log and the hold.</summary>
    public static List<string> BackgroundRunning(IEnumerable<UiTarget> targets) =>
        [.. targets.Where(t => t.Kind == "bg-running").Select(t => $"{t.Name} [{t.Note}]")];

    /// <summary>
    /// On a stage list (dungeons, Doom Tower): every finished background run's results collected (button pressed, reward list closed), running ones logged.
    /// Returns how many result lists were collected.
    /// </summary>
    private async Task<int> CollectBackgroundResults(string where, CancellationToken ct)
    {
        var collected = 0;
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            if (IsBackgroundRewardView(Top(s))) { log($"autoplay: background battle results collected on {where} ({Top(s)}): closing the list"); await Back(Top(s)!, ct); collected++; continue; }
            var (_, targets, _) = await Read(ct);
            foreach (var r in BackgroundRunning(targets)) log($"autoplay: a background battle is running on {where}: {r}; Start waits for it");
            var result = targets.FirstOrDefault(t => t.Kind == "bg-result" && t.NotClickable is null);
            if (result is null) break;
            log($"autoplay: background battle finished on {where}: pressing {result.Name}");
            var path = result.Path;
            await Click("bg-result", t => t.Path == path, (s2, scan) => IsBackgroundRewardView(Top(s2)) || scan().Targets.All(t => t.Path != path || t.Kind != "bg-result"),
                "the results open or the button is gone", ct, within: TimeSpan.FromSeconds(10));
        }
        return collected;
    }
}
