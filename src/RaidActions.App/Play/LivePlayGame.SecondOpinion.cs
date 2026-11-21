using RaidActions.Autoplay;
using RaidExtractor;
using RaidExtractor.Battle;
using RaidExtractor.Core.GameBattle;

namespace RaidActions.App.Play;

public sealed partial class LivePlayGame
{
    /// <summary>Runs of the new engine per second opinion, and its time box (seconds): the bot's gates wait for it, so it stays short.</summary>
    public const int SecondOpinionRuns = 50;
    public const double SecondOpinionSeconds = 10;

    /// <summary>
    /// The second opinion of a simulating gate (Ehsan 2026-11-21): the same stage, team and gear plan through the ported engine (GameSimBuilder.WinRate), logged as `sim2: ...` and appended to
    /// output/sim-compare.csv with the old number. READ ONLY: nothing the gate decides depends on it, and nothing it does can throw into the gate (every failure is logged and swallowed).
    /// </summary>
    private void SecondOpinion(string area, int stageId, IReadOnlyList<int> team, PrepareResult? plan, double oldRate, SavedExport d, GameDataLookup g)
    {
        try
        {
            var (rate, runs, errors, _, firstError) = GameSimBuilder.WinRate(d, g, ExtractorOutput, stageId, team, SecondOpinionRuns, 1, plan, SecondOpinionSeconds);
            double? @new = runs > 0 ? rate : null;
            if (runs == 0) log("autoplay: " + SimCompare.NotPortedLine(firstError ?? "no run finished"));
            log("autoplay: " + SimCompare.EventLine(area, stageId, oldRate, @new, runs));
            var note = errors > 0 ? $"{errors} runs stopped: {firstError}" : null;
            SimCompare.Append(string.IsNullOrEmpty(outDir) ? ExtractorOutput : outDir, SimCompare.Row(DateTime.UtcNow, d.Account.Id, area, stageId, team, oldRate, @new, runs, note));
        }
        catch (Exception e)
        {
            log($"autoplay: new engine: second opinion failed ({e.GetType().Name}: {e.Message})");
        }
    }
}
