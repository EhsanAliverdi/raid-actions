using RaidExtractor;
using RaidExtractor.Autoplay;
using RaidExtractor.Battle;

namespace RaidActions.App.Play;

/// <summary>
/// The one team preparation of every area (raid-extractor plan/team-prep.md, Core <see cref="TeamPrep"/>; Ehsan 2026-11-13: "a common Prepare team module
/// shared between all areas"): the team the page's picker says; with "gear up", before the fight and off its screens, the team's places and pieces noted,
/// the champions out of the vaults and geared (once a day per team, the locks always win, the noted pieces kept off other teams); with "put back", an hour
/// after the team's last use (Faction Wars: after its day) each champion gets its own pieces back and returns where it was.
/// </summary>
public sealed partial class LivePlayGame
{
    private readonly HashSet<string> _gearedToday = [];

    /// <summary>Prepares the bot's team for <paramref name="tag"/> when its choice asks for it; nothing otherwise.</summary>
    /// <param name="team">The team already chosen (Faction Wars' crypt team); null: the bot's pick for <paramref name="contentArea"/>.</param>
    private async Task<string> PrepareTeam(string tag, string area, string? contentArea, int size, TeamChoice? choice, bool dryRun, CancellationToken ct,
        IReadOnlyList<int>? team = null, string? faction = null)
    {
        if (choice is not { Mode: TeamMode.BotChooses, GearUp: true }) return "";
        var (data, gd, ratings, locks) = await Account(ct);
        team ??= BotTeams.Pick(data, gd, extractorRoot, contentArea, size, faction);
        if (team.Count == 0) return "";
        var now = DateTime.UtcNow;
        // Every use keeps a prepared team out a while longer.
        if (choice.PutBack && !dryRun) TeamPrep.Save(ExtractorOutput, TeamPrep.Merge(TeamPrep.Load(ExtractorOutput), [], [.. team], now));
        if (!_gearedToday.Add($"{now:yyyyMMdd}|{tag}")) return "";
        var fight = $"Auto Play: {tag}";
        var snapshot = TeamPrep.Snapshot(data, team, fight, area, now);
        log($"autoplay: STEP preparing the bot's team for {tag}: {string.Join(", ", snapshot.Select(e => $"{Who(e.Id)} ({e.Place}, {e.Before.Count} pieces)"))}");
        string[] dry = dryRun ? ["--dry-run"] : [];
        var vaulted = snapshot.Where(e => e.Place != ChampionPlace.List).Select(e => e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (vaulted.Count > 0)
        {
            await RunCommand(["vault-withdraw", .. vaulted, .. dry], ct);
            (data, gd, ratings, locks) = await Account(ct);
        }
        var made = Prepare.Make(data, gd, ratings, team, fight, locks: locks);
        var picked = made.Members.Where(m => m.Changes).ToList();
        if (picked.Count > 0)
        {
            var (commands, _, problem) = Prepare.BotCommands(data, gd, fight, picked, ExtractorOutput, dryRun);
            if (commands is null) log($"autoplay: {tag}: no gear moved ({problem ?? "nothing better in the inventory"})");
            foreach (var args in commands ?? []) await RunCommand(args, ct);
        }
        if (choice.PutBack && !dryRun) TeamPrep.Save(ExtractorOutput, TeamPrep.Merge(TeamPrep.Load(ExtractorOutput), snapshot, [.. team], now));
        await HomeAsync(Action("after preparing the team"), ct);
        var pieces = picked.Sum(m => m.Moves.Count);
        return $"{pieces} piece(s) for {picked.Count} champion(s), {vaulted.Count} out of a vault" + (choice.PutBack ? "; put back after" : "") + (dryRun ? " (dry run)" : "");
    }

    /// <summary>Your locks (and the Clan Boss team) plus the pieces of champions the bot has out: those stay off other teams until they are put back.</summary>
    private Locks WithPrepared(Locks locks, SavedExport data)
    {
        var reserved = TeamPrep.Reserved(TeamPrep.Load(ExtractorOutput));
        if (reserved.Count == 0 || locks.Set is not { } set) return locks;
        return new Locks(set with { Name = $"{set.Name} + pieces of champions out", ProtectedArtifacts = [.. set.Artifacts, .. reserved] }, data);
    }

    public IReadOnlyList<int> PutBackDue(TimeSpan after) => [.. TeamPrep.Load(ExtractorOutput).Where(e => TeamPrep.Due(e, DateTime.UtcNow, after)).Select(e => e.Id)];

    /// <summary>The champions whose team the bot is done with: their own pieces back, then each to where it was. The locks always win.</summary>
    public async Task<string> PutBackAsync(TimeSpan after, bool dryRun, CancellationToken ct)
    {
        var due = TeamPrep.Load(ExtractorOutput).Where(e => TeamPrep.Due(e, DateTime.UtcNow, after)).ToList();
        if (due.Count == 0) return "";
        var (data, gd, _, _) = await Account(ct);
        var locks = Prepare.LocksFor(data, LockStore.Load(ExtractorOutput));            // your locks only: the noted pieces are the ones going back
        var steps = due.Select(e => (Entry: e, Step: TeamPrep.Restore(e, data, locks))).ToList();
        foreach (var (e, step) in steps.Where(s => s.Step.Skip is not null))
            log($"autoplay: {Who(e.Id)} is left as it is ({step.Skip})");
        var go = steps.Where(s => s.Step.Skip is null).ToList();
        string[] dry = dryRun ? ["--dry-run"] : [];
        static string N(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 1. The gear: what the bot put on comes off, the champion's own pieces go back on.
        var regear = go.Where(s => s.Step.GearChanged).ToList();
        if (regear.Count > 0)
        {
            log($"autoplay: STEP putting their own gear back on: {string.Join(", ", regear.Select(s => Who(s.Entry.Id)))}");
            await RunCommand(["unequip", .. regear.Select(s => N(s.Entry.Id)), .. dry], ct);
            var withPieces = regear.Where(s => s.Step.Reequip.Count > 0).ToList();
            if (withPieces.Count > 0 && !dryRun)
            {
                (data, gd, _, _) = await Account(ct);
                var byId = data.Artifacts.ToDictionary(a => a.Id);
                var members = withPieces.Select(s => new PrepareMember(s.Entry.Id, Who(s.Entry.Id), "Restore", "its own gear back", false, null,
                    [.. s.Step.Reequip.Where(byId.ContainsKey).Select(id => new PieceMove($"{byId[id].Set} {byId[id].Slot}", null, id, byId[id].Slot))], [], [])).ToList();
                var (commands, _, problem) = Prepare.BotCommands(data, gd, "Auto Play: put back", members, ExtractorOutput, dryRun);
                if (commands is null) log($"autoplay: their own pieces were not put back on: {problem}");
                foreach (var args in commands ?? []) await RunCommand(args, ct);
            }
        }

        // 2. The place: back into the vault it came from (a vault champion that wore gear goes to the master vault with it: not built yet, see below).
        var reserve = go.Where(s => s.Entry.Place == ChampionPlace.Reserve).Select(s => s.Entry.Id).ToList();
        var master = go.Where(s => s.Entry.Place == ChampionPlace.Master && s.Entry.Before.Count == 0).Select(s => s.Entry.Id).ToList();
        var geared = go.Where(s => s.Entry.Place == ChampionPlace.Master && s.Entry.Before.Count > 0).ToList();
        if (reserve.Count > 0) await StripGear(reserve, "reserve", dryRun, ct);
        if (master.Count > 0) await StripGear(master, "master", dryRun, ct);
        foreach (var (e, _) in geared)
            log($"autoplay: {Who(e.Id)} has its own gear back and stays in the champion list: moving a geared champion into the master vault is not built yet (the vault screen must be explored first)");

        if (!dryRun)
        {
            var done = steps.Select(s => s.Entry.Id).ToHashSet();
            TeamPrep.Save(ExtractorOutput, [.. TeamPrep.Load(ExtractorOutput).Where(e => !done.Contains(e.Id))]);
        }
        await HomeAsync(Action("after putting back"), ct);
        return $"{go.Count} champion(s) put back ({regear.Count} with their own gear, {reserve.Count + master.Count} into their vault)"
            + (steps.Count > go.Count ? $", {steps.Count - go.Count} left as they are" : "") + (dryRun ? " (dry run)" : "");
    }
}
