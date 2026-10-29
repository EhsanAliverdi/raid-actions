using System.Text.RegularExpressions;
using RaidActions.App.Input;
using RaidActions.Autoplay;
using RaidActions.Game.Memory;
using RaidExtractor;
using RaidExtractor.Autoplay;
using RaidExtractor.Battle;

namespace RaidActions.App.Play;

/// <summary>
/// Faction Wars on the live game: the day's plan from a fresh account read, the team's gear with Manage team's own commands (run in this
/// process: the same plan, vault and remove-gear flows tested on 2026-10-29), and the crypt screens: village → Battle → Faction Wars →
/// the crypt on the map → the stage → the team screen (the saved team must be the one shown) → Start → result.
/// </summary>
public sealed partial class LivePlayGame : IFactionWarsGame
{
    private string ExtractorOutput => Path.Combine(extractorRoot, "output");

    /// <summary>
    /// Why the game cannot be played now, read only: the account is open on another device (the game's "logged into from another
    /// device" box). Re-log in is never pressed (it would log the person out where they play); Auto Play waits. Null when it can.
    /// </summary>
    public string? SessionTaken()
    {
        var (s, _) = game.ReadUi();
        if (s?.Top?.Name != UiState.MessageBoxView) return null;
        var text = $"{s.MessageBox?.Title} {s.MessageBox?.Message}";
        return text.Contains("another device", StringComparison.OrdinalIgnoreCase) || text.Contains("AnotherUserSession", StringComparison.OrdinalIgnoreCase)
            ? "the account is open on another device; waiting (Re-log in is never pressed: it would log you out there)" : null;
    }

    public async Task<FactionWarsDay> PlanDayAsync(CancellationToken ct)
    {
        var (data, gd, ratings, locks) = await Account(ct);
        return FactionWarsPlanner.Plan(data, gd, extractorRoot, ratings, locks, DateTime.UtcNow);
    }

    public async Task<string> GearTeamAsync(CryptPlan crypt, bool dryRun, CancellationToken ct)
    {
        var (data, gd, ratings, locks) = await Account(ct);
        var fight = $"Faction Wars: {crypt.Name}";
        var made = Prepare.Make(data, gd, ratings, crypt.Team, fight, locks: locks);
        var picked = made.Members.Where(m => m.Changes || m.InVault).ToList();
        if (picked.Count == 0) return "nothing better in the inventory; the team keeps its gear";
        var (commands, _, problem) = Prepare.BotCommands(data, gd, fight, picked, ExtractorOutput, dryRun);
        if (commands is null)
        {
            var inVault = picked.Where(m => m.InVault).Select(m => m.ChampionId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (inVault.Count == 0) return problem ?? "nothing to equip";
            commands = [["vault-withdraw", .. inVault, .. (dryRun ? new[] { "--dry-run" } : [])]];
        }
        foreach (var args in commands) await RunCommand(args, ct);
        var pieces = picked.Sum(m => m.Moves.Count);
        return $"{pieces} piece{(pieces == 1 ? "" : "s")} for {picked.Count(m => m.Changes)} champion(s){(picked.Any(m => m.InVault) ? $", {picked.Count(m => m.InVault)} out of a vault" : "")}"
            + (dryRun ? " (dry run)" : "");
    }

    public async Task<string> RemoveGearAsync(IReadOnlyList<int> ids, bool dryRun, CancellationToken ct)
    {
        if (ids.Count == 0) return "nobody";
        await RunCommand(["remove-gear", .. ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "--then", "reserve", .. (dryRun ? new[] { "--dry-run" } : [])], ct);
        return dryRun ? "(dry run)" : "done";
    }

    public async Task<BattleOutcome> FightCryptAsync(CryptPlan crypt, int stage, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        await OpenCrypt(crypt, ct);
        // The stage: its button on the crypt's list (scrolled into view).
        var target = await FindStage(stage, ct);
        await Click("fw-stage", t => t.Id == target.Id, (s2, _) => Top(s2) is PlayTargets.FwTeamView or PlayTargets.RefillView or UiState.MessageBoxView,
            $"the team screen for stage {stage}", ct);
        var after = await Settled(ct);
        if (Top(after) == PlayTargets.RefillView)
        {
            // Not enough keys for this stage (higher stages cost more: 2, then 3): the game offers to buy some. Closed; the crypt is done today.
            await Back(PlayTargets.RefillView, ct);
            return new BattleOutcome(BattleResult.NotStarted, "not enough keys for this stage (the offer to buy keys was closed)");
        }
        if (Top(after) != PlayTargets.FwTeamView) await Refuse(after, $"{crypt.Name} stage {stage}", ct);

        // The team the game shows must be the saved crypt team (phase 1 does not change teams).
        var (_, targets, team) = await Read(ct);
        var shown = team.TeamIds ?? [];
        if (shown.Count == 0) log($"autoplay: the team slots were not read; screen dump: {DumpScreen("team slots not read")}");
        if (shown.Count > 0 && !shown.ToHashSet().SetEquals(crypt.Team))
        {
            await Back(PlayTargets.FwTeamView, ct);
            throw new PlayStopException($"{crypt.Name}: the team screen shows {string.Join(", ", shown.Select(i => $"#{i}"))}, the saved crypt team is "
                + $"{string.Join(", ", crypt.Team.Select(i => $"#{i}"))} ({string.Join(", ", crypt.TeamNames)}). Set the team once in the game; nothing started.");
        }
        if (dryRun)
        {
            var start = targets.FirstOrDefault(t => t.Kind == "team-start" && t.View == PlayTargets.FwTeamView);
            if (start?.Rect is { } r) Shot($"autoplay: WOULD CLICK Start, {crypt.Name} stage {stage} (dry run: NOT pressed)", r, always: true);
            await Back(PlayTargets.FwTeamView, ct);
            return new BattleOutcome(BattleResult.NotStarted, (start?.NotClickable is null && start is not null ? "Start shown, not pressed" : $"Start not identified: {start?.Note ?? "no team-start target"}")
                + (shown.Count == 0 ? "; the team slots were not read" : "; the saved team is set"));
        }
        await Click("team-start", t => t.View == PlayTargets.FwTeamView, (s2, _) => s2.Active.Any(v => BattleViews.Contains(v.Name)) || Top(s2) is PlayTargets.RefillView or UiState.MessageBoxView,
            "the battle starts", ct, within: TimeSpan.FromSeconds(25), shot: true);
        var inBattle = await ReadState(ct);
        if (!inBattle.Active.Any(v => BattleViews.Contains(v.Name))) await Refuse(inBattle, $"Start {crypt.Name} stage {stage}", ct);
        log($"autoplay: {crypt.Name} stage {stage} started");

        var result = await WaitBattle(ct, PlayTargets.FwResultView);
        await LeaveResult(ct, PlayTargets.FwResultView);
        var took = DateTime.UtcNow - started;
        var pve = result?.Pve;
        var outcome = pve?.ResultType switch { 1 => BattleResult.Victory, 2 => BattleResult.Defeat, 3 => BattleResult.Draw, _ => BattleResult.Unknown };
        return new BattleOutcome(outcome, $"{took.TotalSeconds:0} s" + (pve?.Stars is { } st && outcome == BattleResult.Victory ? $", {st} star{(st == 1 ? "" : "s")}" : ""));
    }

    /// <summary>The crypt's stage list on top: from wherever the game is (village → Battle → Faction Wars → the crypt).</summary>
    private async Task OpenCrypt(CryptPlan crypt, CancellationToken ct)
    {
        _nav.FitWindow();
        for (var hop = 0; hop < 10; hop++)
        {
            var s = await Settled(ct);
            if (s.InBattle && !Has(s, PlayTargets.FwResultView)) { await WaitBattle(ct, PlayTargets.FwResultView); continue; }
            if (Has(s, PlayTargets.FwResultView)) { await LeaveResult(ct, PlayTargets.FwResultView); continue; }
            if (Has(s, PlayTargets.ArenaResultView)) { await LeaveResult(ct); continue; }
            if (Top(s) == PlayTargets.FwTeamView) { await Back(PlayTargets.FwTeamView, ct); continue; }
            if (Top(s) == PlayTargets.FwDialogView || Has(s, PlayTargets.FwDialogView) && Top(s) == "FractionWarsHUD")
            {
                var (_, _, play) = await Read(ct);
                if (play.Stages is { Count: > 0 } && OnCrypt(play, crypt)) return;
                await Back(Top(s)!, ct); // another crypt's list: back to the map
                continue;
            }
            if (Has(s, PlayTargets.FwMapView) && Top(s) is PlayTargets.FwMapView or "FractionWarsHUD")
            {
                var (_, _, play) = await Read(ct);
                var mine = play.Crypts?.FirstOrDefault(c => IsFaction(c, crypt));
                if (mine?.Open is null)
                    throw new PlayStopException($"{crypt.Name}: not found on the Faction Wars map ({string.Join(", ", (play.Crypts ?? []).Select(c => $"{c.FactionId} {c.Name}"))}). Screen dump: {DumpScreen("fw-crypt not found")}");
                await Click("fw-crypt", t => t.Id == mine.FactionId, (s2, _) => Has(s2, PlayTargets.FwDialogView), $"{crypt.Name}'s stages open", ct);
                continue;
            }
            if (Top(s) == PlayTargets.BattleMenuView)
            {
                await Click("mode-fw", null, (s2, _) => Has(s2, PlayTargets.FwMapView), "the Faction Wars map opens", ct);
                continue;
            }
            if (!NavigatingDriver.AtVillage(s)) { await _nav.GoHome(Action("to the village"), ct); continue; }
            await Click("village-battle", null, (s2, _) => Has(s2, PlayTargets.BattleMenuView), "the battle menu opens", ct);
        }
        throw new PlayStopException($"Could not reach {crypt.Name}'s stages in 10 steps.");
    }

    /// <summary>Whether a map item is the crypt's faction: the game's enum name for its id, else its shown name.</summary>
    private bool IsFaction(CryptRead c, CryptPlan crypt) =>
        game.Objects.EnumName("SharedModel.Meta.Heroes.HeroFraction", c.FactionId) is { } n ? n == crypt.Faction
        : c.Name?.Replace(" ", "", StringComparison.Ordinal).Contains(crypt.Name.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether the open stage list is the crypt's (the map remembers the opened faction).</summary>
    private bool OnCrypt(PlayScan play, CryptPlan crypt) =>
        play.Crypts is not { Count: > 0 } || play.Crypts.Any(c => IsFaction(c, crypt));

    /// <summary>The stage's button on the list, scrolled into view: by the number in its name, else its place in the list.</summary>
    private async Task<UiTarget> FindStage(int stage, CancellationToken ct)
    {
        for (var i = 0; i < 14; i++)
        {
            var (_, targets, play) = await Read(ct);
            var stages = play.Stages ?? [];
            if (stages.Count == 0) throw new PlayStopException($"No stage read on the crypt's list. Screen dump: {DumpScreen("fw-stage not found")}");
            var byName = stages.Where(x => StageNumber(x.Name) == stage).ToList();
            var mine = byName.Count == 1 ? byName[0] : stages.OrderBy(x => x.Id).ElementAtOrDefault(stage - 1);
            if (mine?.Start is not { } t) throw new PlayStopException($"Stage {stage} not on the list ({string.Join(", ", stages.Select(x => x.Name ?? x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))}).");
            var live = targets.FirstOrDefault(x => x.Kind == "fw-stage" && x.Id == t.Id) ?? t;
            if (live.NotClickable is null) return live;
            if (live.Rect is null || live.Interactable is null) throw new PlayStopException($"Stage {stage}'s button: {live.NotClickable} ({live.Note}). Screen dump: {DumpScreen("fw-stage not clickable")}");
            var shown = targets.Where(x => x.Kind == "fw-stage" && x.FullyVisible && x.Rect is not null).Select(x => x.Rect!.Value).ToList();
            if (shown.Count == 0) throw new PlayStopException("No stage is fully shown to scroll from.");
            var (w, why) = Game.Window.GameWindow.Find();
            if (w is null) throw new PlayStopException(why!);
            var cx = shown.Average(r => r.Center.X);
            var cy = shown.Average(r => r.Center.Y);
            var down = live.Rect.Value.Center.Y > cy;
            var right = live.Rect.Value.Center.X > cx;
            // A list that runs sideways scrolls with the wheel as well (Unity scroll rects take it on their axis).
            if (Input.MouseInput.Wheel(w, cx, cy, down || right ? -2 : 2) is { } notSent) throw new PlayStopException("Not scrolled: " + notSent);
            await Task.Delay(400, ct);
        }
        throw new PlayStopException($"Stage {stage} not reachable by scrolling.");
    }

    private static int? StageNumber(string? name) => name is not null && Regex.Match(name, @"\d+") is { Success: true } m ? int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private async Task<(SavedExport Data, GameDataLookup Gd, ChampionRatings Ratings, Locks Locks)> Account(CancellationToken ct)
    {
        if (extractorRoot.Length == 0) throw new PlayStopException("raid-extractor is not known (Auto Play needs its folder for the teams and gear).");
        var data = await game.Account.ReadAsync(ct);
        var gd = GameDataLookup.Load(ExtractorOutput);
        return (data, gd, ChampionRatings.Load(extractorRoot), Prepare.LocksFor(data, LockStore.Load(ExtractorOutput)));
    }

    /// <summary>Runs one of this repo's own commands in this process (the gear flows), its lines in the log; a failure stops the turn.</summary>
    private async Task RunCommand(string[] args, CancellationToken ct)
    {
        log("autoplay: " + string.Join(" ", args));
        using var writer = new LogWriter(line => log("  " + line));
        Cli.InsideAutoplay = true;
        var exit = await Cli.RunAsync([.. args, "--output", outDir], writer, TextReader.Null, interactive: false);
        ct.ThrowIfCancellationRequested();
        if (exit != Cli.Ok) throw new PlayStopException($"{args[0]} stopped (exit {exit}); see the log above.");
    }

    /// <summary>A TextWriter that hands each line to the log.</summary>
    private sealed class LogWriter(Action<string> line) : StringWriter
    {
        public override void WriteLine(string? value) => line(value ?? "");
        public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) line(value.TrimEnd('\r', '\n')); }
    }
}
