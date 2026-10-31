namespace RaidActions.Autoplay.Fake;

/// <summary>A fake Clan Boss for tests: keys, best damage, a key regenerated every <see cref="KeyEvery"/> on the host's clock. Records calls.</summary>
public sealed class FakeClanBossGame(Func<DateTime> now) : IClanBossGame
{
    public double Keys { get; set; } = 2;
    public TimeSpan KeyEvery { get; set; } = TimeSpan.FromHours(12);
    public Dictionary<string, long> BestDamage { get; set; } = new() { ["Brutal"] = 16_000_000, ["Nightmare"] = 24_000_000 };
    public string? MaxQuick { get; set; } = "Brutal";
    public List<string> Calls { get; } = [];

    /// <summary>The next battle spends its key, then fails as a screen the bot does not know would.</summary>
    public bool FailAfterSpend { get; set; }
    private DateTime _tick = now();

    private void Regen()
    {
        while (now() - _tick >= KeyEvery) { _tick += KeyEvery; Keys++; }
    }

    public Task<ClanBossAccount> ReadClanBossAsync(CancellationToken ct)
    {
        Regen();
        return Task.FromResult(new ClanBossAccount(Keys, BestDamage, MaxQuick));
    }

    public Task<BattleOutcome> FightClanBossAsync(string difficulty, bool quick, bool dryRun, CancellationToken ct)
    {
        if (FailAfterSpend && !dryRun)
        {
            FailAfterSpend = false;
            Keys--;
            Calls.Add($"fight {difficulty}");
            throw new PlayStopException("a screen the bot does not know");
        }
        Regen();
        if (dryRun)
        {
            Calls.Add($"show {difficulty}");
            return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown, not pressed"));
        }
        if (Keys < 1) throw new PlayStopException("no key");
        Keys--;
        Calls.Add($"fight {difficulty}{(quick ? " quick" : "")}");
        return Task.FromResult(new BattleOutcome(BattleResult.Victory, "damage 20,000,000"));
    }

    public double HydraKeys { get; set; } = 3;

    public Task<ClanBossAccount> ReadHydraAsync(CancellationToken ct) =>
        Task.FromResult(new ClanBossAccount(HydraKeys, new Dictionary<string, long> { ["Normal"] = 776_000 }, null));

    public Task<BattleOutcome> FightHydraAsync(string difficulty, bool dryRun, CancellationToken ct)
    {
        if (dryRun) { Calls.Add($"show hydra {difficulty}"); return Task.FromResult(new BattleOutcome(BattleResult.NotStarted, "Start shown, not pressed")); }
        if (HydraKeys < 1) throw new PlayStopException("no hydra key");
        HydraKeys--;
        Calls.Add($"hydra {difficulty}");
        return Task.FromResult(new BattleOutcome(BattleResult.Victory, "damage 800,000"));
    }
}
