using RaidActions.App.Input;
using RaidActions.Game.Memory;
using RaidActions.Game.Window;
using RaidExtractor;
using RaidExtractor.Battle;

namespace RaidActions.App;

/// <summary>
/// <c>RaidActions sell &lt;sell-plan.json&gt; [--dry-run] [--confirm &lt;code&gt;]</c>: sells exactly the pieces of a sell plan from
/// raid-extractor's Sell review (output/plans/sell, SCHEMA.md → Sell plans), slot by slot with the picker's sell mode.
/// Refused unless a fresh read of the game matches the plan (every piece there, unequipped and unchanged; same game version). The
/// plan's code must be typed (or passed). A real run saves the full account read first and, after, checks that only the planned pieces
/// are gone. A dry run ticks the pieces, reads the game's confirmation and closes it: nothing is sold.
/// </summary>
public static class SellCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter o, TextReader input, bool interactive, Action<Action>? abortHook)
    {
        string? planPath = null, confirm = null, outDir = null;
        var dry = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dry = true; break;
                case "--confirm" when i + 1 < args.Length: confirm = args[++i]; break;
                case "--output" when i + 1 < args.Length: outDir = args[++i]; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || planPath is not null)
                    {
                        o.WriteLine($"Unexpected argument {args[i]}. Usage: RaidActions sell <sell-plan.json> [--dry-run] [--confirm <code>]");
                        return Cli.Usage;
                    }
                    planPath = args[i];
                    break;
            }
        }
        if (planPath is null || !File.Exists(planPath))
        {
            o.WriteLine(planPath is null ? "sell needs a sell plan file (raid-extractor: Artifacts → Sell… → Make sell plan)." : $"{planPath} not found.");
            return Cli.Usage;
        }
        planPath = Path.GetFullPath(planPath);
        var plan = SellPlan.LoadFile(planPath);
        if (plan is null || plan.Kind != SellPlan.KindName || plan.SchemaVersion > SellPlan.Version || plan.Items.Count == 0)
        {
            o.WriteLine($"Refused: {planPath} is not a sell plan this version knows (kind \"sell\", schema {SellPlan.Version}, at least one piece).");
            return Cli.Refused;
        }
        var ids = plan.Items.Select(x => x.Piece.ArtifactId).ToList();
        if (ids.Distinct().Count() != ids.Count)
        {
            o.WriteLine("Refused: the plan lists a piece twice.");
            return Cli.Refused;
        }
        // The plan's snapshot: <raid-extractor output>\history\<snapshotId> (the plan sits in <output>\plans\sell).
        var extractorOutput = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(planPath)!, "..", ".."));
        if (plan.SnapshotId is null || !File.Exists(Path.Combine(extractorOutput, "history", plan.SnapshotId)))
        {
            o.WriteLine($"Refused: the plan's snapshot ({plan.SnapshotId ?? "none"}) is not in {Path.Combine(extractorOutput, "history")}.");
            return Cli.Refused;
        }

        var output = AppPaths.Resolve(null, outDir).Output;
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
            var problems = plan.Check(before);
            o.WriteLine($"{(dry ? "SELL DRY RUN (nothing is sold)" : "SELL")}: {plan.Name}, {ids.Count} pieces, made {plan.Created} for game {plan.GameVersion}; live read {before.Meta.ExportedAt}.");
            var bySlot = plan.Items.GroupBy(x => x.Piece.Slot).OrderBy(g => Array.IndexOf(UiTargets.SlotIds, g.Key)).ToList();
            foreach (var g in bySlot)
            {
                o.WriteLine($"  {UiTargets.DisplayName(g.Key)}: {g.Count()}");
                foreach (var x in g)
                    o.WriteLine($"    #{x.Piece.ArtifactId} {x.Piece.Rarity} {x.Piece.Rank}★ +{x.Piece.Level}: {x.Reason}");
            }
            if (problems.Count > 0)
            {
                foreach (var p in problems) o.WriteLine("  REFUSED: " + p);
                o.WriteLine("Refused: the plan does not match the game. Make a new sell plan in raid-extractor.");
                return Cli.Refused;
            }
            if (!bySlot.All(g => UiTargets.SlotIds.Contains(g.Key)))
            {
                o.WriteLine("Refused: a piece is in an unknown slot.");
                return Cli.Refused;
            }

            var typed = confirm;
            if (typed is null && interactive)
            {
                o.Write(dry ? $"Type {plan.Code} to start the dry run (nothing is sold): " : $"Selling cannot be undone. Type {plan.Code} to sell these {ids.Count} pieces: ");
                typed = input.ReadLine();
            }
            if (!string.Equals(typed?.Trim(), plan.Code, StringComparison.OrdinalIgnoreCase))
            {
                o.WriteLine(typed is null ? $"Not confirmed: pass --confirm {plan.Code}." : "Not confirmed. Nothing was done.");
                return Cli.NotConfirmed;
            }

            var folder = Path.Combine(output, dry ? "dryrun" : "runs", $"{DateTime.Now:yyyyMMdd-HHmmss}-sell-{plan.Code}");
            Directory.CreateDirectory(folder);
            var logFile = Path.Combine(folder, "log.txt");
            void Log(string line)
            {
                var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
                o.WriteLine(stamped);
                File.AppendAllText(logFile, stamped + Environment.NewLine);
            }
            if (!dry)
            {
                // The whole account before the first input (raid-extractor's history format), kept with the run.
                var snapshot = History.Save(Path.Combine(folder, "before"), before);
                Log($"account before the sale saved: {snapshot}");
            }

            using var cts = new CancellationTokenSource();
            abortHook?.Invoke(cts.Cancel);
            var policy = WindowPolicy.Load(GameCommands.WindowConfig);
            var driver = new NavigatingDriver(game, ui, null, folder, policy, Log, sell: true);
            var results = new List<NavigatingDriver.SellSlotResult>();
            var outcome = Cli.Ok;
            try
            {
                foreach (var g in bySlot)
                {
                    // Every slot checked again against the account as it is now.
                    var now = await game.Account.ReadAsync(cts.Token);
                    var slotPlan = plan with { Items = g.ToList() };
                    if (slotPlan.Check(now) is { Count: > 0 } changed) throw new NavigatingDriver.StopException("Before " + g.Key + ": " + string.Join(" ", changed));
                    var r = await driver.SellSlot(g.Key, g.Select(x => x.Piece.ArtifactId).ToList(), real: !dry, cts.Token);
                    results.Add(r);
                    Log($"{UiTargets.DisplayName(g.Key)}: {r.Detail}");
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

            if (!dry && results.Any(r => r.Sold) || outcome != Cli.Ok && !dry)
                await AfterSaleAsync(game, before, results.Where(r => r.Sold).SelectMany(r => r.Ids).ToHashSet(), Log);
            Log(outcome == Cli.Ok
                ? dry ? $"Dry run done: {ids.Count} pieces ticked, the confirmation read and closed each time. Nothing sold. To sell: RaidActions sell \"{planPath}\" --confirm {plan.Code}"
                      : $"Done: {results.Sum(r => r.Ids.Count)} pieces sold."
                : "Not finished: see above.");
            Log($"Log: {logFile}");
            return outcome;
        }
    }

    /// <summary>After a sale: the account before and after must differ only in the sold pieces being gone; the silver it brought.</summary>
    private static async Task AfterSaleAsync(LiveGame game, SavedExport before, IReadOnlySet<int> sold, Action<string> log)
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
            if (!has) { if (!sold.Contains(id)) changes.Add($"#{id} is gone (not planned)"); continue; }
            if (sold.Contains(id)) { changes.Add($"#{id} should be sold but is still there"); continue; }
            if (x!.OwnerHeroId != y!.OwnerHeroId) changes.Add($"#{id} owner {x.OwnerHeroId} → {y.OwnerHeroId}");
            if (PlanPiece.FingerprintOf(x) != PlanPiece.FingerprintOf(y)) changes.Add($"#{id} changed (level, stats or glyphs)");
        }
        var silver = after.Account.Resources.GetValueOrDefault("Silver") - before.Account.Resources.GetValueOrDefault("Silver");
        log($"Silver: {before.Account.Resources.GetValueOrDefault("Silver"):N0} → {after.Account.Resources.GetValueOrDefault("Silver"):N0} ({silver:+#,0;-#,0;0}).");
        log(changes.Count == 0
            ? $"Nothing else changed: {sold.Count} pieces gone as planned, {a2.Count} artifacts compared."
            : "UNEXPECTED CHANGES:" + Environment.NewLine + string.Join(Environment.NewLine, changes.Select(c => "  " + c)));
    }
}
