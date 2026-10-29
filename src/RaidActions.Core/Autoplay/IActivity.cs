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
        ["Token"] = new ResourceClock("Token", 10, TimeSpan.FromMinutes(12)),
        ["Energy"] = new ResourceClock("Energy", 130, TimeSpan.FromMinutes(3)),
    };

    public GameResources? LastResources { get; private set; }

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

    /// <summary>A person's pause before an action (the pacing settings' range).</summary>
    public Task ThinkAsync(CancellationToken ct)
    {
        var p = Settings.Pacing;
        return host.DelayAsync(TimeSpan.FromMilliseconds(host.Random.Next(Math.Max(0, p.ThinkMinMs), Math.Max(p.ThinkMinMs, p.ThinkMaxMs) + 1)), ct);
    }
}
