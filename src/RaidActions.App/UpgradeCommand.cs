using RaidActions.App.Input;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;
using RaidExtractor;
using RaidExtractor.Battle;

namespace RaidActions.App;

/// <summary>
/// <c>RaidActions upgrade &lt;upgrade-plan.json&gt; [--dry-run] [--confirm &lt;code&gt;] [--start-game]</c>: upgrades the pieces of an upgrade
/// plan from raid-extractor's Upgrade review (output/plans/upgrade, SCHEMA.md → Upgrade plans), each to its target, one level at a
/// time. Refused unless a fresh read of the game matches the plan (same game version; every piece there and unchanged). The plan's
/// code must be typed (or passed). What it handles without stopping, each piece's line saying so: a piece gone or not found in the
/// game's list (skipped), already at its target (skipped), changed since the plan (skipped), out of silver (the run ends there; the rest
/// is listed as not done). Anything else unexpected stops the run. A real run saves the full account read first and checks after that
/// only the planned pieces changed. A dry run opens each piece's upgrade screen and closes it: nothing is upgraded.
/// </summary>
public static class UpgradeCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter o, TextReader input, bool interactive, Action<Action>? abortHook)
    {
        string? planPath = null, confirm = null, outDir = null;
        var dry = false;
        var startGame = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dry = true; break;
                case "--start-game": startGame = true; break;
                case "--confirm" when i + 1 < args.Length: confirm = args[++i]; break;
                case "--output" when i + 1 < args.Length: outDir = args[++i]; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || planPath is not null)
                    {
                        o.WriteLine($"Unexpected argument {args[i]}. Usage: RaidActions upgrade <upgrade-plan.json> [--dry-run] [--confirm <code>] [--start-game]");
                        return Cli.Usage;
                    }
                    planPath = args[i];
                    break;
            }
        }
        if (planPath is null || !File.Exists(planPath))
        {
            o.WriteLine(planPath is null ? "upgrade needs an upgrade plan file (raid-extractor: Artifacts → Upgrade…)." : $"{planPath} not found.");
            return Cli.Usage;
        }
        planPath = Path.GetFullPath(planPath);
        var plan = UpgradePlan.LoadFile(planPath);
        if (plan is null || plan.Kind != UpgradePlan.KindName || plan.SchemaVersion > UpgradePlan.Version || plan.Items.Count == 0)
        {
            o.WriteLine($"Refused: {planPath} is not an upgrade plan this version knows (kind \"upgrade\", schema {UpgradePlan.Version}, at least one piece).");
            return Cli.Refused;
        }
        var ids = plan.Items.Select(x => x.Piece.ArtifactId).ToList();
        if (ids.Distinct().Count() != ids.Count || plan.Items.Any(x => x.Target is < 1 or > 16))
        {
            o.WriteLine("Refused: the plan lists a piece twice, or a target outside +1..+16.");
            return Cli.Refused;
        }
        // The plan's snapshot: <raid-extractor output>\history\<snapshotId> (the plan sits in <output>\plans\upgrade).
        var extractorOutput = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(planPath)!, "..", ".."));
        if (plan.SnapshotId is null || !File.Exists(Path.Combine(extractorOutput, "history", plan.SnapshotId)))
        {
            o.WriteLine($"Refused: the plan's snapshot ({plan.SnapshotId ?? "none"}) is not in {Path.Combine(extractorOutput, "history")}.");
            return Cli.Refused;
        }
        // One attempt's silver at each level, from the game data raid-extractor read (none: the run still stops when the game refuses).
        var rules = GameDataLookup.Load(extractorOutput).ArtifactRules;
        long? PriceOf(ArtifactOut a) => rules is null ? null : GearRating.AttemptPrice(a, rules);

        var output = AppPaths.Resolve(null, outDir).Output;
        if (startGame && System.Diagnostics.Process.GetProcessesByName("Raid").Length == 0)
        {
            o.WriteLine("Raid is not running: starting it.");
            if (!await RestartGameCommand.StartAndWaitAsync(output, o, TimeSpan.FromMinutes(5))) return Cli.Failed;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        var (game, problem) = LiveGame.Open(GameCommands.LivePaths(output));
        if (game is null)
        {
            o.WriteLine(problem);
            return Cli.Failed;
        }
        using (game)
        {
            UnityUi ui;
            try { ui = game.Ui(UiCommands.NativeLayoutPath); }
            catch (UiReadException e)
            {
                o.WriteLine(e.Message);
                return Cli.Failed;
            }

            var before = await game.Account.ReadAsync(default);
            o.WriteLine($"{(dry ? "UPGRADE DRY RUN (nothing is upgraded)" : "UPGRADE")}: {plan.Name}, {ids.Count} pieces, about {plan.Silver:N0} silver, made {plan.Created} for game {plan.GameVersion}; live read {before.Meta.ExportedAt}; silver now {before.Account.Resources.GetValueOrDefault("Silver"):N0}.");
            foreach (var x in plan.Items)
                o.WriteLine($"  #{x.Piece.ArtifactId} {UiTargets.DisplayName(x.Piece.Slot)} {x.Piece.Rarity} {x.Piece.Rank}★ +{x.Piece.Level} → +{x.Target}: {x.Reason}");
            if (!string.IsNullOrEmpty(plan.GameVersion) && before.Meta.GameVersion is { } v && v != plan.GameVersion)
            {
                o.WriteLine($"  REFUSED: Game version changed ({plan.GameVersion} → {v}): make the upgrade plan again.");
                o.WriteLine("Refused: the plan does not match the game. Make a new upgrade plan in raid-extractor.");
                return Cli.Refused;
            }

            var typed = confirm;
            if (typed is null && interactive)
            {
                o.Write(dry ? $"Type {plan.Code} to start the dry run (nothing is upgraded): " : $"Upgrades spend silver and cannot be undone. Type {plan.Code} to upgrade these {ids.Count} pieces: ");
                typed = input.ReadLine();
            }
            if (!string.Equals(typed?.Trim(), plan.Code, StringComparison.OrdinalIgnoreCase))
            {
                o.WriteLine(typed is null ? $"Not confirmed: pass --confirm {plan.Code}." : "Not confirmed. Nothing was done.");
                return Cli.NotConfirmed;
            }

            var folder = Path.Combine(output, dry ? "dryrun" : "runs", $"{DateTime.Now:yyyyMMdd-HHmmss}-upgrade-{plan.Code}");
            Directory.CreateDirectory(folder);
            var logFile = Path.Combine(folder, "log.txt");
            void Log(string line)
            {
                var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
                o.WriteLine(stamped);
                File.AppendAllText(logFile, stamped + Environment.NewLine);
            }
            if (!dry) Log($"account before the upgrades saved: {History.Save(Path.Combine(folder, "before"), before)}");

            using var cts = new CancellationTokenSource();
            abortHook?.Invoke(cts.Cancel);
            var policy = WindowPolicy.Load(GameCommands.WindowConfig);
            var driver = new NavigatingDriver(game, ui, null, folder, policy, Log, upgrade: true);
            var results = new List<NavigatingDriver.UpgradeResult>();
            var outcome = Cli.Ok;
            var n = 0;
            try
            {
                foreach (var item in plan.Items)
                {
                    n++;
                    var id = item.Piece.ArtifactId;
                    // Each piece checked again against the account as it is now: changed since the plan → skipped.
                    var now = await game.Account.ReadAsync(cts.Token);
                    var piece = now.Artifacts.FirstOrDefault(x => x.Id == id);
                    if (piece is not null && piece.Level < item.Target && item.Piece.Fingerprint is { } fp && fp != PlanPiece.FingerprintOf(piece))
                    {
                        results.Add(new NavigatingDriver.UpgradeResult(id, piece.Level, piece.Level, item.Target, 0, 0, "changed since the plan (rerolled, glyphed or upgraded): skipped"));
                        Log($"#{id}: changed since the plan: skipped");
                        continue;
                    }
                    var r = await driver.UpgradePiece(id, item.Piece.Slot, item.Target, real: !dry, PriceOf, n, plan.Items.Count, cts.Token);
                    results.Add(r);
                    Log($"#{id}: {r.Outcome} (+{r.From} → +{r.To}, target +{r.Target}, {r.Attempts} attempt{(r.Attempts == 1 ? "" : "s")}, {r.SilverSpent:N0} silver)");
                    if (r.Outcome.StartsWith("out of silver", StringComparison.Ordinal)) { Log("Out of silver: the rest is not done."); break; }
                }
            }
            catch (NavigatingDriver.StopException e)
            {
                Log("STOPPED: " + e.Message);
                driver.SaveScreenshot(folder, "stopped");
                outcome = Cli.Failed;
            }
            catch (OperationCanceledException)
            {
                Log("ABORTED.");
                outcome = Cli.Aborted;
            }
            finally
            {
                if (driver.RestoreWindow() is { } restored) Log(restored);
            }

            // What was done and what was not, piece by piece.
            var done = results.Where(r => r.Done).Select(r => r.Id).ToHashSet();
            var notReached = plan.Items.Where(x => !results.Any(r => r.Id == x.Piece.ArtifactId)).Select(x => $"#{x.Piece.ArtifactId}").ToList();
            Log($"Summary: {done.Count} of {ids.Count} at their target" + (notReached.Count > 0 ? $"; not reached: {string.Join(", ", notReached)}" : "")
                + $"; {results.Sum(r => r.Attempts)} attempts, {results.Sum(r => r.SilverSpent):N0} silver.");
            if (!dry && results.Any(r => r.Attempts > 0)) await AfterAsync(game, before, results.Select(r => r.Id).ToHashSet(), Log);
            Log(outcome == Cli.Ok
                ? dry ? $"Dry run done: each piece's upgrade screen opened and closed. Nothing upgraded. To upgrade: RaidActions upgrade \"{planPath}\" --confirm {plan.Code}" : "Done."
                : "Not finished: see above.");
            Log($"Log: {logFile}");
            return outcome;
        }
    }

    /// <summary>After the upgrades: only the planned pieces may have changed (level and rolls), and the silver spent.</summary>
    private static async Task AfterAsync(LiveGame game, SavedExport before, IReadOnlySet<int> planned, Action<string> log)
    {
        var after = await game.Account.ReadAsync(default);
        var b = before.Artifacts.ToDictionary(a => a.Id);
        var a2 = after.Artifacts.ToDictionary(a => a.Id);
        var changes = new List<string>();
        foreach (var id in b.Keys.Union(a2.Keys).Order())
        {
            var had = b.TryGetValue(id, out var x);
            var has = a2.TryGetValue(id, out var y);
            if (!had) { changes.Add($"#{id} is new"); continue; }
            if (!has) { changes.Add($"#{id} is gone"); continue; }
            if (x!.OwnerHeroId != y!.OwnerHeroId) changes.Add($"#{id} owner {x.OwnerHeroId} → {y.OwnerHeroId}");
            if (!planned.Contains(id) && PlanPiece.FingerprintOf(x) != PlanPiece.FingerprintOf(y)) changes.Add($"#{id} changed (not planned)");
        }
        var silver = after.Account.Resources.GetValueOrDefault("Silver") - before.Account.Resources.GetValueOrDefault("Silver");
        log($"Silver: {before.Account.Resources.GetValueOrDefault("Silver"):N0} → {after.Account.Resources.GetValueOrDefault("Silver"):N0} ({silver:+#,0;-#,0;0}).");
        log(changes.Count == 0 ? $"Nothing else changed: {a2.Count} artifacts compared." : "UNEXPECTED CHANGES:" + Environment.NewLine + string.Join(Environment.NewLine, changes.Select(c => "  " + c)));
    }
}
