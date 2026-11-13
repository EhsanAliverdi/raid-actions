using RaidExtractor;
using RaidExtractor.Autoplay;
using RaidExtractor.Battle;

namespace RaidActions.App.Play;

/// <summary>
/// "The bot picks the best" with "gear up" and "put back" (Ehsan 2026-11-13: "an auto gear option which gears the team it builds, and return it to where it
/// was after use: master vault, reserve vault"): before an area's fight, off its team screen, the bot's team comes out of the vaults and gets the best gear
/// (once a day per team); the champions that came out of a vault are noted (put-back.json) and go back to it, gear off, an hour later (the chores).
/// </summary>
public sealed partial class LivePlayGame
{
    private readonly HashSet<string> _gearedToday = [];

    /// <summary>Gears the bot's team for <paramref name="tag"/> when its choice asks for it; nothing otherwise. Never on the fight's own screens.</summary>
    private async Task GearIfAsked(string tag, string? contentArea, int size, TeamChoice? choice, bool gearUp, bool putBack, bool dryRun, CancellationToken ct)
    {
        if (!gearUp || choice is not null && choice.Mode != TeamMode.BotChooses) return;
        if (!_gearedToday.Add($"{DateTime.UtcNow:yyyyMMdd}|{tag}")) return;
        var (data, gd, ratings, locks) = await Account(ct);
        var team = BotTeams.Pick(data, gd, extractorRoot, contentArea, size);
        if (team.Count == 0) return;
        var byId = data.Champions.ToDictionary(c => c.Id);
        var reserve = team.Where(id => byId.TryGetValue(id, out var c) && c.InBathhouse).ToList();
        var master = team.Where(id => byId.TryGetValue(id, out var c) && c.InVault).ToList();
        string[] dry = dryRun ? ["--dry-run"] : [];
        log($"autoplay: STEP gearing the bot's team for {tag}: {string.Join(", ", team.Select(Who))}");
        if (reserve.Count > 0)
        {
            await RunCommand(["vault-withdraw", .. reserve.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "--reserve", .. dry], ct);
            (data, gd, ratings, locks) = await Account(ct);
        }
        var fight = $"Auto Play: {tag}";
        var made = Prepare.Make(data, gd, ratings, team, fight, locks: locks);
        var picked = made.Members.Where(m => m.Changes || m.InVault).ToList();
        if (picked.Count > 0)
        {
            var (commands, _, problem) = Prepare.BotCommands(data, gd, fight, picked, ExtractorOutput, dryRun);
            if (commands is null && picked.Any(m => m.InVault))
                commands = [["vault-withdraw", .. picked.Where(m => m.InVault).Select(m => m.ChampionId.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. dry]];
            if (commands is null) log($"autoplay: {tag}: no gear to move ({problem ?? "nothing better in the inventory"})");
            foreach (var args in commands ?? []) await RunCommand(args, ct);
        }
        if (putBack && !dryRun && reserve.Count + master.Count > 0)
        {
            var list = PutBackList.Load(ExtractorOutput).Where(e => !team.Contains(e.Id)).ToList();
            list.AddRange(reserve.Select(id => new PutBackEntry(id, "reserve", DateTime.UtcNow, fight)));
            list.AddRange(master.Select(id => new PutBackEntry(id, "master", DateTime.UtcNow, fight)));
            PutBackList.Save(ExtractorOutput, list);
            log($"autoplay: {tag}: {reserve.Count + master.Count} champion(s) go back to their vault later");
        }
        await HomeAsync(Action("after gearing"), ct);
    }

    public IReadOnlyList<int> PutBackDue(TimeSpan after) =>
        [.. PutBackList.Load(ExtractorOutput).Where(e => DateTime.UtcNow - e.Utc >= after).Select(e => e.Id)];

    public async Task<string> PutBackAsync(TimeSpan after, bool dryRun, CancellationToken ct)
    {
        var all = PutBackList.Load(ExtractorOutput);
        var due = all.Where(e => DateTime.UtcNow - e.Utc >= after).ToList();
        if (due.Count == 0) return "";
        foreach (var g in due.GroupBy(e => e.Where))
        {
            log($"autoplay: STEP putting back into the {g.Key} vault: {string.Join(", ", g.Select(e => Who(e.Id)))}");
            await StripGear([.. g.Select(e => e.Id)], g.Key, dryRun, ct);
        }
        if (!dryRun) PutBackList.Save(ExtractorOutput, [.. all.Except(due)]);
        await HomeAsync(Action("after putting back"), ct);
        return $"{due.Count} champion(s) back in their vault, gear off" + (dryRun ? " (dry run)" : "");
    }
}
