using RaidExtractor.Autoplay;

namespace RaidActions.Autoplay;

/// <summary>What an activity wants now: to act, to wait until a time (and why), or nothing (off, done for today).</summary>
public sealed record Decision(bool Act, DateTime? UntilUtc, string Why)
{
    public static Decision Now(string why) => new(true, null, why);
    public static Decision Wait(DateTime untilUtc, string why) => new(false, untilUtc, why);
    public static Decision Off(string why) => new(false, null, why);
}

/// <summary>
/// One area of the game the bot plays (Classic Arena now; energy, quests, inbox, ... later). <see cref="Decide"/> is quick and reads
/// nothing from the game (only what the activity learned and the clocks); <see cref="RunTurnAsync"/> does one short turn (one
/// battle, one refresh) so the engine can check the settings, the stop request and the person between turns.
/// </summary>
public interface IActivity
{
    string Id { get; }
    string Name { get; }
    bool IsEnabled(AutoplaySettings settings);
    Decision Decide(PlayContext ctx);

    /// <summary>What this area spends is at its cap, so it has stopped regenerating (Ehsan 2026-11-01: nothing may stay full): the area goes before all others.</summary>
    bool Full(PlayContext ctx) => false;
    Task<string> RunTurnAsync(PlayContext ctx, CancellationToken ct);
    AreaStatus Status(PlayContext ctx, Decision decision);
}

/// <summary>What the engine gives an activity: settings, the game, time, clocks, the event log, and the person-like pause.</summary>
public sealed class PlayContext(AutoplaySettings settings, IPlayGame game, IAutoplayHost host, bool dryRun, Action<AutoplayEvent>? events = null)
{
    public AutoplaySettings Settings { get; internal set; } = settings;
    public IPlayGame Game => game;
    public IAutoplayHost Host => host;
    public bool DryRun => dryRun;
    public DateTime Utc => host.UtcNow;
    public DateTime Local => host.UtcNow.ToLocalTime();

    /// <summary>The regenerating resources, by the game's resource name (Token, Energy).</summary>
    public Dictionary<string, ResourceClock> Clocks { get; } = new()
    {
        ["Token"] = new ResourceClock("Token", 10, TimeSpan.FromMinutes(60)),
        ["Energy"] = new ResourceClock("Energy", 130, TimeSpan.FromMinutes(3)),
    };

    public GameResources? LastResources { get; private set; }

    /// <summary>
    /// The open quests as needs (plan/quests.md), read by Free rewards' quest pass; the other areas play them first. Empty when quests are
    /// switched off or not read yet.
    /// </summary>
    public List<QuestNeed> QuestNeeds { get; set; } = [];

    /// <summary>A quest row was won: read the quests at the next chance.</summary>
    public bool QuestsStale { get; set; }

    /// <summary>What is left of a quest kind now (0: no open quest asks it, or quests are off).</summary>
    public int QuestLeft(QuestNeedKind kind) => Settings.Quests.Enabled ? QuestPlanner.Left(QuestNeeds, kind) : 0;

    /// <summary>Reads the resources and feeds the clocks.</summary>
    public async Task<GameResources> ReadResourcesAsync(CancellationToken ct)
    {
        var r = await game.ReadResourcesAsync(ct);
        Clocks["Token"].Observe(r.Utc, r.Tokens, r.TokenTimer, r.TokenCap);
        Clocks["Energy"].Observe(r.Utc, r.Energy, r.EnergyTimer, r.EnergyCap);
        LastResources = r;
        return r;
    }

    /// <summary>The amount a clock expects now (null: never read).</summary>
    public double? Expected(string resource) => Clocks[resource].Out()?.ValueAt(Utc);

    public void Event(string area, string kind, string text) => (events ?? host.Event)(new AutoplayEvent(Utc, area, kind, text));

    private readonly Dictionary<string, (DateTime Until, string Why)> _holds = [];

    /// <summary>
    /// Nothing to try at <paramref name="key"/> before <paramref name="until"/> (no key, no team left, a floor lost with the same team): kept as an event,
    /// so a restart remembers it (Ehsan 2026-11-03: "every time it starts it does the same thing"). The key is an area's id, or "area:thing".
    /// </summary>
    public void Hold(string key, DateTime until, string why)
    {
        _holds[key] = (until, why);
        Event(key.Split(':')[0], "hold", $"{key}|{until:O}|{why}");
    }

    /// <summary>The hold on a key while it lasts.</summary>
    public (DateTime Until, string Why)? Held(string key) => _holds.TryGetValue(key, out var h) && h.Until > Utc ? h : null;

    /// <summary>Lifts a hold (the thing changed: a key came, the team won).</summary>
    public void Release(string key) => _holds.Remove(key);

    /// <summary>The holds from earlier runs' events; the latest per key.</summary>
    public void RestoreHolds(IEnumerable<AutoplayEvent> events)
    {
        foreach (var e in events.Where(e => e.Kind == "hold").OrderBy(e => e.Utc))
        {
            var parts = e.Text.Split('|', 3);
            if (parts.Length == 3 && DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var until))
                _holds[parts[0]] = (until.ToUniversalTime(), parts[2]);
        }
    }

    /// <summary>
    /// Why the area with this id should hand its turn back now (an area that cannot wait wants to act, or the account's turn is over); null: it may go on
    /// with another run in the same turn (Ehsan 2026-11-06: "the only time you go back to base is when something more critical is due"). Set by the engine.
    /// </summary>
    public Func<string, string?>? ChainBreak { get; set; }

    /// <summary>A person's pause before an action (the pacing settings' range).</summary>
    public Task ThinkAsync(CancellationToken ct)
    {
        var p = Settings.Pacing;
        return host.DelayAsync(TimeSpan.FromMilliseconds(host.Random.Next(Math.Max(0, p.ThinkMinMs), Math.Max(p.ThinkMinMs, p.ThinkMaxMs) + 1)), ct);
    }
}
