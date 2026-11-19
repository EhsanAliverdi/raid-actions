using System.Globalization;
using System.Text.RegularExpressions;

namespace RaidActions.Autoplay;

/// <summary>Clan vs Clan's phase as the village button shows it (CvcTournamentButtonContext._state: None 0, Teaser 1 = "starts in ...", Started 2, Finished 3).</summary>
public enum CvcPhase { None, NotStarted, Started, Finished }

/// <summary>What the village's Clan vs Clan button said at <see cref="ReadUtc"/>; <see cref="StartUtc"/> only while it counts down to the start.</summary>
public sealed record CvcStatus(CvcPhase Phase, DateTime ReadUtc, DateTime? StartUtc = null)
{
    /// <summary>From the button's note "state|timer|title" (state may be empty when not read).</summary>
    public static CvcStatus? FromNote(string? note, DateTime utc)
    {
        if (note is null) return null;
        var p = note.Split('|', 3);
        var timer = p.Length > 1 ? ParseSpan(p[1]) : null;
        var title = p.Length > 2 ? p[2] : "";
        CvcPhase? phase = int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is >= 0 and <= 3 ? (CvcPhase)s : null;
        // The state not read: the title says it ("Starts in", "Ends in") with a countdown.
        if (phase is null && timer is not null) phase = title.Contains("start", StringComparison.OrdinalIgnoreCase) ? CvcPhase.NotStarted : CvcPhase.Started;
        if (phase is null) return null;
        return new CvcStatus(phase.Value, utc, phase == CvcPhase.NotStarted && timer is { } t ? utc + t : null);
    }

    /// <summary>"1d 5h", "5h 12m", "05:12:33", "2d 04:10:00", "12m 30s" (the game's countdown texts).</summary>
    public static TimeSpan? ParseSpan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var d = Regex.Match(text, @"(\d+)\s*d", RegexOptions.IgnoreCase);
        var colon = Regex.Match(text, @"(\d+):(\d{2})(?::(\d{2}))?");
        var total = TimeSpan.Zero;
        var any = false;
        if (d.Success) { total += TimeSpan.FromDays(int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture)); any = true; }
        if (colon.Success)
        {
            var a = int.Parse(colon.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(colon.Groups[2].Value, CultureInfo.InvariantCulture);
            total += colon.Groups[3].Success ? new TimeSpan(a, b, int.Parse(colon.Groups[3].Value, CultureInfo.InvariantCulture)) : new TimeSpan(0, a, b);
            return total;
        }
        foreach (Match m in Regex.Matches(text, @"(\d+)\s*([hms])\b", RegexOptions.IgnoreCase))
        {
            var v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 'h' => TimeSpan.FromHours(v), 'm' => TimeSpan.FromMinutes(v), _ => TimeSpan.FromSeconds(v) };
            any = true;
        }
        return any ? total : null;
    }

    /// <summary>When the cached read should be taken again: a countdown at its start (or half-hourly), anything else every 3 hours.</summary>
    public bool Stale(DateTime utc) =>
        Phase == CvcPhase.NotStarted ? utc - ReadUtc > TimeSpan.FromMinutes(30) || StartUtc is { } s && utc >= s : utc - ReadUtc > TimeSpan.FromHours(3);
}

/// <summary>Faction Wars keys held for Clan vs Clan (Ehsan 2026-11-20: Faction Wars gives good points while Clan vs Clan runs).</summary>
public static class CvcKeyHold
{
    /// <summary>A crypt's keys at their cap (assumed; the game's own cap is to be confirmed live) and the time one key takes to come back.</summary>
    // Seen on the accounts (2026-11-20): +6 keys per crypt at the daily reset (12 -> 18 on KaelStormVex), three crypts sitting at exactly 18: the cap.
    // 6 a day is one key per 4 hours on average.
    public static int KeyCap { get; set; } = 18;
    public static TimeSpan KeyRegen { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// The keys to leave in a crypt now. While Clan vs Clan is not started and starts before the crypt's keys would reach the cap, everything below the cap
    /// is kept (a crypt at the cap spends down to one below it, so no key is ever lost). Not held when it starts later than the cap (play on, the keys
    /// come back), when a Faction Wars quest would expire first (quests reset at the UTC day), or when nothing is known.
    /// </summary>
    public static int Keep(CvcStatus? cvc, DateTime now, int keys, int baseKeep, bool questNeedsKeys, DateTime nextReset, out TimeSpan? startsIn)
    {
        startsIn = null;
        if (cvc is not { Phase: CvcPhase.NotStarted, StartUtc: { } start } || start <= now) return baseKeep;
        startsIn = start - now;
        if (questNeedsKeys && start > nextReset) return baseKeep;
        var toCap = TimeSpan.FromTicks(KeyRegen.Ticks * Math.Max(0, KeyCap - keys));
        return start - now <= toCap ? Math.Max(baseKeep, KeyCap - 1) : baseKeep;
    }

    public static string Hours(TimeSpan t) => t.TotalHours >= 10 ? $"{t.TotalHours:0}h" : $"{t.TotalHours:0.#}h";
}
