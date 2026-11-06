using System.Globalization;

namespace RaidActions.Autoplay;

/// <summary>What the account holds, for the rewards log: resources and items by name, gear and champions by id with a label.</summary>
public sealed record Holdings(IReadOnlyDictionary<string, double> Items, IReadOnlyDictionary<int, string> Gear, IReadOnlyDictionary<int, string> Champions);

/// <summary>
/// What a turn brought in (Ehsan 2026-11-06: "log whatever rewards are collected and from where, e.g. 100 energy from daily activities or gear xxx
/// from ..."): every resource or item that went up, the new gear pieces and the new champions. Spending is not a reward and is left out.
/// </summary>
public static class Gains
{
    /// <summary>Resources that regenerate by themselves: a rise this small over a turn is the clock, not a reward.</summary>
    private static readonly Dictionary<string, double> Regenerating = new(StringComparer.OrdinalIgnoreCase) { ["Energy"] = 3, ["Token"] = 1, ["LiveArenaToken"] = 1 };

    /// <summary>"+100 Energy, +2 Mystery shard, gear: Epic 5★ Boots, champion: Warmaiden (Rare)"; null when nothing came in.</summary>
    public static string? Describe(Holdings before, Holdings after)
    {
        var parts = new List<string>();
        foreach (var (name, now) in after.Items.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var was = before.Items.GetValueOrDefault(name);
            var up = now - was;
            if (up < 1 || Regenerating.TryGetValue(name, out var noise) && up <= noise) continue;
            parts.Add($"+{Math.Round(up).ToString("N0", CultureInfo.InvariantCulture)} {name}");
        }
        var gear = after.Gear.Where(g => !before.Gear.ContainsKey(g.Key)).Select(g => g.Value).ToList();
        if (gear.Count > 0) parts.Add($"gear: {Group(gear)}");
        var heroes = after.Champions.Where(c => !before.Champions.ContainsKey(c.Key)).Select(c => c.Value).ToList();
        if (heroes.Count > 0) parts.Add($"champion{(heroes.Count == 1 ? "" : "s")}: {Group(heroes)}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string Group(IEnumerable<string> labels) =>
        string.Join(", ", labels.GroupBy(l => l).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()}× {g.Key}"));
}
