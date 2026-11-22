using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>
/// Second opinion of the bot's simulating gates (Ehsan 2026-11-21, simulator port phase 6): the old StageSim decides, the ported engine (GameSimBuilder.WinRate) only reports.
/// Every pair is logged as one event line and appended to output/sim-compare.csv (time, account, area, stage, team, old, new, runs, note, later real outcome). The real outcome is filled in
/// when the area's next battle event arrives: the row is joined by area + stage + team (the pending row of that area, newest first, whose time is before the event's).
/// </summary>
public static class SimCompare
{
    public const string FileName = "sim-compare.csv";
    public const string Header = "time,account,area,stage,team,old,new,runs,note,real";

    /// <summary>How long a row waits for its battle's end: a fight comes right after its simulation (minutes), a day at most.</summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromHours(6);

    public static string PathIn(string outDir) => System.IO.Path.Combine(outDir, FileName);

    /// <summary>`sim2: faction-wars 5011009: old 83% new 90% (50 runs)`; the new engine's number is "n/a" when it did not finish a run.</summary>
    public static string EventLine(string area, int stage, double old, double? @new, int runs) =>
        string.Create(CultureInfo.InvariantCulture, $"sim2: {area} {stage}: old {old * 100:0}% new {(@new is { } n ? $"{n * 100:0}%" : "n/a")} ({runs} runs)");

    /// <summary>The log line for a new engine that could not run: the first not-ported piece it hit (the engine's error text starts "not ported: ").</summary>
    public static string NotPortedLine(string error) => "new engine: " + (error.StartsWith("not ported:", StringComparison.Ordinal) ? error : "not ported: " + error);

    private static string Q(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Row(DateTime utc, long? account, string area, int stage, IEnumerable<int> team, double old, double? @new, int runs, string? note, string? real = null) =>
        string.Join(',', utc.ToString("O", CultureInfo.InvariantCulture), account?.ToString(CultureInfo.InvariantCulture) ?? "", Q(area), stage.ToString(CultureInfo.InvariantCulture),
            Q(string.Join(' ', team)), old.ToString("0.###", CultureInfo.InvariantCulture), @new is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture) : "",
            runs.ToString(CultureInfo.InvariantCulture), Q(note ?? ""), Q(real ?? ""));

    private static readonly object Gate = new();

    /// <summary>Appends a row (the header first when the file is new). Never throws: the second opinion must not hurt a gate.</summary>
    public static void Append(string outDir, string row)
    {
        try
        {
            lock (Gate)
            {
                var path = PathIn(outDir);
                Directory.CreateDirectory(outDir);
                if (!File.Exists(path)) File.WriteAllText(path, Header + Environment.NewLine, new UTF8Encoding(false));
                File.AppendAllText(path, row + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Splits one CSV line (quotes, doubled quotes).</summary>
    public static List<string> Fields(string line)
    {
        var f = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (c == '"') quoted = false; else sb.Append(c); }
            else if (c == '"') quoted = true;
            else if (c == ',') { f.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        f.Add(sb.ToString());
        return f;
    }

    /// <summary>The result word of a battle event's text ("Victory Doom Tower ..." / "Defeat ..."): its first word, when it is a result.</summary>
    public static string? ResultOf(string text)
    {
        var w = text.Split(' ', 2)[0].TrimEnd(':', '.', ',');
        return w is "Victory" or "Defeat" or "Won" or "Lost" ? (w is "Victory" or "Won" ? "win" : "loss") : null;
    }

    /// <summary>
    /// A battle event arrived: the newest row of its area without a real outcome and no later than the event (within <see cref="JoinWindow"/>) gets it. A row's team is matched against
    /// the event text when the text names champions (Cursed City writes "team A, B"): a row whose team names do not appear is left for its own battle. Returns true when a row was filled.
    /// </summary>
    /// <summary>
    /// The row's area against the battle's: the same name, or an Energy push (rows "energy-push", battles "energy"; 2026-11-23: 64 push forecasts never got
    /// their result) when the battle came from a push row ("[row push-keep-void]", "[row brutal-push]"), not a farm row whose battle would answer for it.
    /// </summary>
    private static bool SameArea(string rowArea, AutoplayEvent e) =>
        rowArea == e.Area || rowArea == "energy-push" && e.Area == "energy" && Regex.IsMatch(e.Text, @"\[row [^\]]*push[^\]]*\]");

    public static bool ResolveFromEvent(string outDir, AutoplayEvent e, IReadOnlyDictionary<string, string>? teamNames = null)
    {
        if (e.Kind != "battle" || ResultOf(e.Text) is not { } result) return false;
        try
        {
            lock (Gate)
            {
                var path = PathIn(outDir);
                if (!File.Exists(path)) return false;
                var lines = File.ReadAllLines(path);
                for (var i = lines.Length - 1; i >= 1; i--)
                {
                    var f = Fields(lines[i]);
                    if (f.Count < 10 || !SameArea(f[2], e) || f[9].Length > 0) continue;
                    if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || at > e.Utc || e.Utc - at > JoinWindow) continue;
                    if (teamNames is not null && e.Text.Contains("team ", StringComparison.Ordinal))
                    {
                        var names = f[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => teamNames.GetValueOrDefault(id)).OfType<string>().ToList();
                        if (names.Count > 0 && !names.All(n => e.Text.Contains(n, StringComparison.Ordinal))) continue;
                    }
                    f[9] = result;
                    lines[i] = string.Join(',', f.Select(Q));
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
                    return true;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
}
