using RaidActions.Autoplay;
using RaidExtractor;
using RaidExtractor.Autoplay;
using RaidExtractor.Battle;
using RaidExtractor.Core.GameBattle;

namespace RaidActions.App.Play;

public sealed partial class LivePlayGame
{
    /// <summary>
    /// The forecast of an unbeaten push stage (Ehsan 2026-11-21): the team the bot will field (the place's or the row's saved team, else the bot's pick topped up),
    /// geared as the row gears it, through the old engine (StageSim) and the new one (GameSimBuilder); both logged as `sim2:` and in sim-compare.csv. The
    /// activity decides on the engine trusted for the area (<see cref="PushGate"/>). Null: no team known (the game's current team) or no stage id.
    /// </summary>
    public async Task<PushForecast?> ForecastPushAsync(EnergyRun run, CancellationToken ct)
    {
        if (run.StageId is not { } stageId) return null;
        var (d, g, r, l) = await Account(ct);
        var placeChoice = run.Target.RegionTeams.GetValueOrDefault(run.Region);
        List<int> team;
        if (placeChoice is { Mode: TeamMode.Saved, Ids.Count: > 0 }) team = [.. placeChoice.Ids];
        else if (placeChoice is { Mode: TeamMode.BotChooses } || placeChoice is null && run.Target is { Team.Count: 0 } && (run.Target.BotTeam || !run.Target.Food))
        {
            var pick = BotTeams.Pick(d, g, extractorRoot, BotTeams.ContentArea(run.Target.Area, run.Region, run.Difficulty), EnergyPlaces.Slots(run.Target.Area));
            team = TopUpTeam(d, pick, EnergyPlaces.Slots(run.Target.Area), [], "energy");
        }
        else if (placeChoice is null && run.Target.Team.Count > 0) team = [.. run.Target.Team];
        else return null; // the game's current team: not known without the screen
        if (team.Count == 0) return null;
        var plan = run.Target.GearUp ? Prepare.Make(d, g, r, team, $"Push: {EnergyPlanner.Describe(run)}", locks: l) : null;
        double? oldRate = null, newRate = null;
        string why = "";
        try
        {
            var (rate, w) = StageSim.WinRate(d, g, ExtractorOutput, stageId, team, gear: plan);
            if (!w.Contains("not in the static data", StringComparison.Ordinal)) { oldRate = rate; why = w; }
        }
        catch (Exception e) when (e is not OperationCanceledException) { log($"autoplay: old engine forecast failed ({e.GetType().Name}: {e.Message})"); }
        try
        {
            var (rate, runs, errors, _, firstError) = GameSimBuilder.WinRate(d, g, ExtractorOutput, stageId, team, SecondOpinionRuns, 1, plan, SecondOpinionSeconds);
            if (runs > 0) newRate = rate; else log("autoplay: " + SimCompare.NotPortedLine(firstError ?? "no run finished"));
            log("autoplay: " + SimCompare.EventLine("energy-push", stageId, oldRate ?? 0, newRate, runs));
            SimCompare.Append(string.IsNullOrEmpty(outDir) ? ExtractorOutput : outDir,
                SimCompare.Row(DateTime.UtcNow, d.Account.Id, "energy-push", stageId, team, oldRate ?? 0, newRate, runs, errors > 0 ? $"{errors} runs stopped: {firstError}" : null));
        }
        catch (Exception e) when (e is not OperationCanceledException) { log($"autoplay: new engine: push forecast failed ({e.GetType().Name}: {e.Message})"); }
        return new PushForecast(oldRate, newRate, why);
    }
}
