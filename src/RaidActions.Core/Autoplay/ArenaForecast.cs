using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>One hero of an opponent's defence as the board shows it (type id, level, grade = stars, double ascension): all the forecast needs to build the enemy.</summary>
public sealed record ArenaEnemyHero(int Slot, int TypeId, int Level, int Grade, int? DoubleAscend = null);

/// <summary>The simulated win rate of our attack team against one opponent; Rate null = it could not be computed (Why says why).</summary>
public sealed record ArenaForecast(double? Rate, int Runs, string? Why = null);

/// <summary>
/// The switch for the Classic Arena forecast (Ehsan 2026-11-23: "the simulator can predict the outcome, then we use that for deciding whether to fight an
/// enemy or not"). OFF by default until he tries it live. On with the command line flag <c>--arena-forecast</c> or the environment variable
/// <c>RAID_ARENA_FORECAST=1</c>. It never touches the power cap: it only chooses among the opponents already under it.
/// </summary>
public static class ArenaForecastSwitch
{
    public const string Flag = "--arena-forecast", EnvVar = "RAID_ARENA_FORECAST";
    public const int Runs = 30;
    /// <summary>An opponent whose forecast is under this is not fought.</summary>
    public const double MinRate = 0.5;

    public static bool Enabled { get; set; } = FromEnvironment();

    public static bool FromEnvironment() => Environment.GetEnvironmentVariable(EnvVar) is "1" or "true" or "on";
}

/// <summary>Pure pick logic of the forecast: who of the opponents already under the power cap is fought, in which order, and what is logged.</summary>
public static class ArenaForecastPick
{
    public sealed record Result(List<ArenaOpponentInfo> Order, List<ArenaOpponentInfo> Skipped, List<string> Lines);

    /// <summary>
    /// <paramref name="eligible"/> is what <see cref="ArenaRules.Eligible"/> returned (the cap already applied, in today's order); the result is always a
    /// subset of it, so the cap can never be exceeded. Forecast >= <paramref name="minRate"/>: fought, highest first (ties keep today's order). Under it:
    /// skipped. No forecast for an opponent: it keeps today's pick (after the forecast ones) and the line says why.
    /// </summary>
    public static Result Choose(IReadOnlyList<ArenaOpponentInfo> eligible, Func<ArenaOpponentInfo, ArenaForecast?> forecast, double minRate = ArenaForecastSwitch.MinRate)
    {
        var lines = new List<string>();
        var known = new List<(ArenaOpponentInfo O, double Rate, int Index)>();
        var unknown = new List<ArenaOpponentInfo>();
        var skipped = new List<ArenaOpponentInfo>();
        for (var i = 0; i < eligible.Count; i++)
        {
            var o = eligible[i];
            var who = $"{o.Name ?? "?"} ({o.Power:N0})";
            var f = forecast(o);
            if (f?.Rate is not { } rate || f.Runs <= 0)
            {
                lines.Add($"forecast {who}: not computed ({f?.Why ?? "no forecast"}); today's pick");
                unknown.Add(o);
                continue;
            }
            lines.Add($"forecast {who}: {rate:P0} over {f.Runs} runs" + (rate < minRate ? $", under {minRate:P0}: skipped" : ""));
            if (rate < minRate) skipped.Add(o); else known.Add((o, rate, i));
        }
        var order = known.OrderByDescending(k => k.Rate).ThenBy(k => k.Index).Select(k => k.O).Concat(unknown).ToList();
        return new Result(order, skipped, lines);
    }
}
