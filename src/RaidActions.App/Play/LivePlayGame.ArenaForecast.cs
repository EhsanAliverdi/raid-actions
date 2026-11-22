using RaidActions.Autoplay;
using RaidExtractor.Autoplay;

namespace RaidActions.App.Play;

public sealed partial class LivePlayGame
{
    /// <summary>Seconds one opponent's forecast may take (the runs done by then count).</summary>
    private const double ArenaForecastSeconds = 20;

    /// <summary>
    /// Classic Arena forecast (Ehsan 2026-11-23): the attack team (the Teams card's, else the one last seen on the arena's team screen) against the opponent's
    /// defence team from the board, on the new engine. Reads the account from memory (as every other read); no game input.
    /// </summary>
    public async Task<ArenaForecast> ForecastArenaAsync(ArenaEnemyTeam enemy, int runs, CancellationToken ct)
    {
        var (data, gd, _, _) = await Account(ct);
        var seen = TeamsSeen.Load(ExtractorOutput).GetValueOrDefault(ClassicArenaAreaId) ?? [];
        var pick = TeamRules.Resolve(TeamsOf(ClassicArenaAreaId), TeamVariant.Default, seen, data.Champions, fillEmpty: true);
        var team = pick.Team is { Count: > 0 } t ? t : seen;
        var ours = team.Select(id => data.Champions.FirstOrDefault(c => c.Id == id)?.Power).ToList();
        double? ourPower = ours.Count > 0 && ours.All(p => p is > 0) ? ours.Sum(p => p!.Value) : null;
        return await Task.Run(() => ArenaForecaster.WinRate(data, gd, ExtractorOutput, team, enemy, runs, 1, ArenaForecastSeconds, ourPower), ct);
    }
}
