using System.Text.Json;
using RaidActions.Execution;
using RaidActions.Execution.Fake;
using RaidActions.Plans;
using RaidExtractor;

namespace RaidActions.App;

/// <summary>
/// The command line. Exit codes: 0 done (or the check passed), 1 refused, 2 failed, 3 aborted, 4 usage error, 5 not confirmed.
/// </summary>
public static class Cli
{
    public const int Ok = 0, Refused = 1, Failed = 2, Aborted = 3, Usage = 4, NotConfirmed = 5;

    public const string Help = """
        RaidActions: executes raid-extractor gear plans. Phase 1: FAKE EXECUTOR ONLY, no game input.

          RaidActions                          open the window
          RaidActions open <plan.json>         open the window with a plan
          RaidActions validate <plan.json>     check the plan against the account
          RaidActions dry-run <plan.json>      navigate and highlight every step, never equip
          RaidActions run <plan.json>          run the plan
          RaidActions sample [<folder>]        write a made-up account and plan to try the app
          RaidActions remove-gear <champion id>... [--then reserve|master|list] [--dry-run]
                                               all gear off at once by moving them into the Reserve Vault, then
                                               leave them there (default), on to the Master Vault, or back to the list
          RaidActions unequip <champion id>... [--dry-run]
                                               take off every piece these champions wear (back to the inventory),
                                               each removal checked in the account; --dry-run shows each Remove, presses none
          RaidActions sell <sell-plan.json> [--dry-run] [--confirm <code>] [--start-game]
                                               sell a sell plan's pieces (raid-extractor's Sell review), slot by slot;
                                               --dry-run ticks them, reads the confirmation and cancels: nothing is sold;
                                               --start-game starts Raid through Plarium Play first when it is not running
          RaidActions upgrade <upgrade-plan.json> [--dry-run] [--confirm <code>] [--start-game]
                                               upgrade an upgrade plan's pieces (raid-extractor's Upgrade review) to their
                                               targets, one level at a time; stops before an attempt it cannot pay for;
                                               --dry-run opens each piece's upgrade screen and closes it: nothing is upgraded

        Read-only, against the running game (no input, never brings it to the front):
          RaidActions window                   find the game window: size, DPI, state, usable for clicks
          RaidActions window fit [--size WxH]  restore / size the game window to the minimum (or the given size)
          RaidActions window minimum           measure the minimum window size from the champion screen's targets
          RaidActions capture [--out f.png]    save a capture of the game's client area
          RaidActions ui-state [--watch [--for <minutes>]]  the game's UI from memory: views, battle, selected champion and artifact
          RaidActions recognize [--image f]    recognize the screen (live, or an image) and save an annotated copy
          RaidActions templates seed|recut     cut the anchor templates (from reference screenshots | from the live window)
          RaidActions ui-tree [View] [--watch --for <min>]  Unity UI from memory: elements, rectangles, contexts (output\ui-trees)
          RaidActions ui-rects [View]          clickable elements with their rectangles, interactable and context
          RaidActions overlay [View]           memory rectangles drawn on a capture (output\overlays)
          RaidActions probe views|dump|class   read-only memory diagnostics for the native layouts

        Options:
          --extractor-output <dir>   raid-extractor's output folder (default: the folder holding the plan's plans\ folder,
                                     else ..\raid-extractor\output)
          --account <account.json>   the fake game's starting account (default: <extractor-output>\account.json)
          --output <dir>             logs, snapshots, screenshots (default: output\ in this repo)
          --confirm <code>           the plan's confirmation code (first 8 characters of its id) instead of typing it
          --pace <min>-<max>         milliseconds between inputs (default 150-450)
          --live                     validate: read the account from the running game instead of account.json
                                     dry-run: observe the running game, sending no input (you open the screens it asks for)
          --navigate                 dry-run --live: navigate by itself with allowed clicks only; Equip is never pressed
                                     run --live --navigate --steps <n>: the same, and Equip clicked once for each named step

        Abort a run: Ctrl+Alt+Esc (anywhere) or Ctrl+C.
        Exit codes: 0 done, 1 refused, 2 failed, 3 aborted, 4 usage error, 5 not confirmed.
        """;

    public sealed record Options(string Command, string? Plan, string? ExtractorOutput, string? Account, string? Output, string? Confirm,
        TimeSpan PaceMin, TimeSpan PaceMax, bool Live = false, bool Navigate = false, IReadOnlySet<int>? Steps = null);

    public static Options? Parse(string[] args, TextWriter err)
    {
        if (args.Length == 0) return null;
        string? plan = null, ext = null, account = null, output = null, confirm = null;
        var live = false;
        var navigate = false;
        HashSet<int>? steps = null;
        TimeSpan min = TimeSpan.FromMilliseconds(150), max = TimeSpan.FromMilliseconds(450);
        var command = args[0].ToLowerInvariant();
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--extractor-output": ext = Next(); break;
                case "--account": account = Next(); break;
                case "--output": output = Next(); break;
                case "--confirm": confirm = Next(); break;
                case "--live": live = true; break;
                case "--navigate": navigate = true; break;
                case "--steps":
                    steps = Next().Split(',').Select(x => int.TryParse(x.Trim(), out var n) && n > 0 ? n : throw new ArgumentException("--steps needs step numbers, e.g. 1 or 1,2.")).ToHashSet();
                    break;
                case "--pace":
                    var parts = Next().Split('-');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || a < 0 || b < a)
                        throw new ArgumentException("--pace needs <min>-<max> milliseconds, e.g. 150-450.");
                    (min, max) = (TimeSpan.FromMilliseconds(a), TimeSpan.FromMilliseconds(b));
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {args[i]}.");
                    if (plan is not null) throw new ArgumentException($"Unexpected argument {args[i]}.");
                    plan = args[i];
                    break;
            }
        }
        if (live && command == "run" && (!navigate || steps is null))
            throw new ArgumentException("run --live needs --navigate and --steps <n>: a real run names the steps whose Equip it may click.");
        if (navigate && !(live && command is "dry-run" or "run")) throw new ArgumentException("--navigate is only for dry-run --live and run --live.");
        if (steps is not null && !live) throw new ArgumentException("--steps is only for live runs.");
        return new Options(command, plan, ext, account, output, confirm, min, max, live, navigate, steps);
    }

    /// <param name="interactive">Whether <paramref name="input"/> is a person at a console (to type the confirmation).</param>
    /// <param name="abortHook">Receives the session's abort, for Ctrl+C and the kill switch.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextReader input, bool interactive, Action<Action>? abortHook = null)
    {
        if (args.Length > 0 && args[0].Equals("go-home", StringComparison.OrdinalIgnoreCase))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await GoHomeCommand.RunAsync(args, outDir, output, abortHook);
        }
        if (args.Length > 0 && args[0].Equals("sell", StringComparison.OrdinalIgnoreCase))
            return await SellCommand.RunAsync(args, output, input, interactive, abortHook);
        if (args.Length > 0 && args[0].Equals("upgrade", StringComparison.OrdinalIgnoreCase))
            return await UpgradeCommand.RunAsync(args, output, input, interactive, abortHook);
        if (args.Length > 0 && (args[0].Equals("vault-open", StringComparison.OrdinalIgnoreCase) || args[0].Equals("vault-withdraw", StringComparison.OrdinalIgnoreCase)))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await VaultCommand.RunAsync(args, outDir, output, abortHook);
        }
        if (args.Length > 0 && args[0].Equals("remove-gear", StringComparison.OrdinalIgnoreCase))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await RemoveGearCommand.RunAsync(args, outDir, output, abortHook);
        }
        if (args.Length > 0 && args[0].Equals("unequip", StringComparison.OrdinalIgnoreCase))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await UnequipCommand.RunAsync(args, outDir, output, abortHook);
        }
        if (args.Length > 0 && args[0].Equals("restart-game", StringComparison.OrdinalIgnoreCase))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await RestartGameCommand.RunAsync(args, outDir, output);
        }
        if (args.Length > 0 && UiCommands.Names.Contains(args[0].ToLowerInvariant()))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await UiCommands.RunAsync(args[0].ToLowerInvariant(), args, outDir, output);
        }
        if (args.Length > 0 && GameCommands.Names.Contains(args[0].ToLowerInvariant()))
        {
            var outIndex = Array.IndexOf(args, "--output");
            var outDir = AppPaths.Resolve(null, outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null).Output;
            return await GameCommands.RunAsync(args[0].ToLowerInvariant(), args, outDir, output, input);
        }

        Options? o;
        try { o = Parse(args, output); }
        catch (ArgumentException e)
        {
            output.WriteLine(e.Message);
            output.WriteLine(Help);
            return Usage;
        }
        if (o is null || o.Command is "help" or "--help" or "-h" or "/?")
        {
            output.WriteLine(Help);
            return o is null ? Usage : Ok;
        }

        var paths = AppPaths.Resolve(o.ExtractorOutput, o.Output);
        if (o.Command == "sample")
        {
            var folder = Path.GetFullPath(o.Plan ?? Path.Combine(paths.Output, "sample", "extractor-output"));
            var plan = SampleData.Write(folder);
            output.WriteLine($"Sample account and plan written to {folder}");
            output.WriteLine($"Try:  RaidActions dry-run \"{plan}\" --extractor-output \"{folder}\"");
            return Ok;
        }
        if (o.Command is not ("validate" or "dry-run" or "run") || o.Plan is null)
        {
            output.WriteLine(o.Plan is null && o.Command is "validate" or "dry-run" or "run" ? $"{o.Command} needs a plan file." : $"Unknown command {o.Command}.");
            output.WriteLine(Help);
            return Usage;
        }

        paths = AppPaths.ForPlan(o.Plan, o.ExtractorOutput, o.Output);
        if (o.Live) return o.Command is "dry-run" or "run" ? await DryRunLiveAsync(o, paths, input, interactive, output, abortHook) : await ValidateLiveAsync(o, paths, output);
        var accountPath = Path.GetFullPath(o.Account ?? Path.Combine(paths.ExtractorOutput, "account.json"));
        if (!File.Exists(accountPath))
        {
            output.WriteLine($"No account at {accountPath}. The fake executor starts from a saved account.json (--account).");
            return Usage;
        }
        var game = new FakeGame(JsonSerializer.Deserialize<SavedExport>(File.ReadAllText(accountPath), Exporter.JsonRead)
            ?? throw new InvalidDataException($"{accountPath} is empty."));
        IPacer pacer = o.PaceMax == TimeSpan.Zero ? NoPacer.Instance : new RandomPacer(o.PaceMin, o.PaceMax);
        var session = new PlanSession(paths, game, game, pacer);
        output.WriteLine($"FAKE EXECUTOR: no game input. Account: {accountPath} (a saved export, not a live read).");

        var load = session.Load(Path.GetFullPath(o.Plan));
        foreach (var i in load.Issues) output.WriteLine("  " + i);
        if (!load.Ok)
        {
            output.WriteLine("Refused: the plan file is not valid.");
            return Refused;
        }
        var loaded = load.Plan!;
        var check = await session.CheckAsync();
        foreach (var i in check.Issues.Where(i => !load.Issues.Contains(i))) output.WriteLine("  " + i);
        var mode = o.Command == "run" ? RunMode.Real : RunMode.DryRun;
        foreach (var line in Confirmation.Lines(loaded, check, o.Command == "validate" ? null : mode, game.Name)) output.WriteLine(line);
        if (!check.CanStart)
        {
            output.WriteLine(check.Issues.HasErrors() ? "Refused: the plan does not match the account." : "Nothing to do.");
            return check.Issues.HasErrors() ? Refused : Ok;
        }
        if (o.Command == "validate")
        {
            output.WriteLine($"The plan can start. Confirmation code: {Confirmation.Code(loaded.Plan)}");
            return Ok;
        }

        var code = Confirmation.Code(loaded.Plan);
        var typed = o.Confirm;
        if (typed is null && interactive)
        {
            output.Write($"Type {code} to start, anything else cancels: ");
            typed = input.ReadLine();
        }
        if (!Confirmation.Matches(loaded.Plan, typed))
        {
            output.WriteLine(typed is null ? $"Not confirmed: pass --confirm {code}." : "Not confirmed. Nothing was done.");
            return NotConfirmed;
        }

        abortHook?.Invoke(session.Abort);
        var progress = new SyncProgress<RunEvent>(e =>
        {
            if (e.State != "started") output.WriteLine($"  {e.Time:HH:mm:ss} step {e.Step,-2} {e.Kind,-22} {e.State,-7} {e.Detail ?? e.Text}");
        });
        RunResult result;
        try { result = await session.RunAsync(mode, loaded.Plan.PlanId, loaded.Sha256, progress); }
        catch (PlanRefusedException e)
        {
            output.WriteLine(e.Message);
            return Refused;
        }
        output.WriteLine(result.Message);
        if (result.SnapshotPath is not null) output.WriteLine($"Snapshot: {result.SnapshotPath}");
        if (result.LogPath is not null) output.WriteLine($"Log: {result.LogPath}");
        if (result.ScreenshotPath is not null) output.WriteLine($"Screenshot: {result.ScreenshotPath}");
        return result.Outcome switch { RunOutcome.Completed => Ok, RunOutcome.Aborted => Aborted, _ => Failed };
    }

    /// <summary>
    /// After a real run: the whole account before (the snapshot's full read) and after (a fresh read) must differ only in the owners
    /// the run's steps change; then the undo of those steps is written and checked against the account, not run.
    /// </summary>
    private static async Task AfterRealRunAsync(RaidActions.Game.Memory.LiveGame game, Plan plan, RaidActions.Verification.CheckReport check,
        IReadOnlySet<int> steps, string snapshotPath, string folder, TextWriter output)
    {
        var beforeRead = RaidExtractor.History.Load(snapshotPath.Replace(".json", ".account.json.gz"));
        var after = await game.Account.ReadAsync(default);
        var expected = new Dictionary<int, int?>();
        foreach (var sc in check.Steps.Where(s => steps.Contains(s.Step.Number) && s.State == RaidActions.Verification.StepState.Pending))
        {
            expected[sc.Step.ArtifactId] = sc.Step.ToChampionId;
            if (sc.Displaced is { } d) expected[d] = null;
        }
        var b = beforeRead.Artifacts.ToDictionary(a => a.Id);
        var a2 = after.Artifacts.ToDictionary(a => a.Id);
        var changes = new List<string>();
        foreach (var id in b.Keys.Union(a2.Keys).Order())
        {
            if (!b.TryGetValue(id, out var x)) { changes.Add($"#{id} is new"); continue; }
            if (!a2.TryGetValue(id, out var y)) { changes.Add($"#{id} is gone"); continue; }
            var ownerExpected = expected.TryGetValue(id, out var e) ? e : x.OwnerHeroId;
            if (y.OwnerHeroId != ownerExpected) changes.Add($"#{id} owner {Who(x.OwnerHeroId)} → {Who(y.OwnerHeroId)} (expected {Who(ownerExpected)})");
            if (RaidExtractor.Battle.PlanPiece.FingerprintOf(x) != RaidExtractor.Battle.PlanPiece.FingerprintOf(y)) changes.Add($"#{id} changed (level, stats or glyphs)");
        }
        foreach (var (id, owner) in expected)
            output.WriteLine($"  #{id}: {Who(b.GetValueOrDefault(id)?.OwnerHeroId)} → {Who(a2.GetValueOrDefault(id)?.OwnerHeroId)} (expected {Who(owner)})");
        output.WriteLine(changes.Count == 0
            ? $"Nothing else changed: {a2.Count} artifacts compared before ({beforeRead.Meta.ExportedAt}) and after ({after.Meta.ExportedAt})."
            : "UNEXPECTED CHANGES:" + Environment.NewLine + string.Join(Environment.NewLine, changes.Select(c => "  " + c)));

        var undo = UndoPlanner.Write(plan, check, steps.ToList(), after, Path.Combine(folder, "undo"), DateTimeOffset.Now);
        if (undo.PlanPath is null)
        {
            output.WriteLine("Undo: " + undo.Problem);
            return;
        }
        var read = PlanReader.ReadFile(undo.PlanPath);
        var madeFrom = RaidExtractor.History.Load(Path.Combine(folder, "undo", "history", read.Plan!.Plan.SnapshotId!));
        var undoCheck = RaidActions.Verification.AccountCheck.Check(read.Plan.Plan, after, madeFrom);
        output.WriteLine($"Undo written (not run): {undo.PlanPath}");
        foreach (var u in undo.Steps) output.WriteLine($"  undo step {u.Number}: {u.Piece}: the inventory → {u.ToName} (#{u.ToChampionId})");
        output.WriteLine(undoCheck.CanStart ? "  The undo matches the account now: it can run." : "  The undo does not pass the check: " + string.Join(" ", undoCheck.Issues));
        output.WriteLine($"  To run it:  RaidActions run \"{undo.PlanPath}\" --live --navigate --steps {string.Join(",", undo.Steps.Select(s => s.Number))} --confirm {Confirmation.Code(read.Plan.Plan)}");

        static string Who(int? id) => id is null ? "the inventory" : $"#{id}";
    }

    /// <summary>validate --live: the plan against a fresh read of the running game (read-only; no executor).</summary>
    private static async Task<int> ValidateLiveAsync(Options o, SessionPaths paths, TextWriter output)
    {
        var (game, problem) = RaidActions.Game.Memory.LiveGame.Open(GameCommands.LivePaths(paths.Output));
        if (game is null)
        {
            output.WriteLine(problem);
            return Failed;
        }
        using (game)
        {
            output.WriteLine($"LIVE READ of the running game ({game.GameVersion}), read-only. No executor: nothing can run.");
            var session = new PlanSession(paths, new NoInputDriver(), game.Account, NoPacer.Instance);
            var load = session.Load(Path.GetFullPath(o.Plan!));
            foreach (var i in load.Issues) output.WriteLine("  " + i);
            if (!load.Ok)
            {
                output.WriteLine("Refused: the plan file is not valid.");
                return Refused;
            }
            var check = await session.CheckAsync();
            foreach (var i in check.Issues.Where(i => !load.Issues.Contains(i))) output.WriteLine("  " + i);
            foreach (var line in Confirmation.Lines(load.Plan!, check, null, "none")) output.WriteLine(line);
            output.WriteLine(check.CanStart ? "The plan matches the live account." : check.Issues.HasErrors() ? "Refused: the plan does not match the live account." : "Nothing to do.");
            return check.Issues.HasErrors() ? Refused : Ok;
        }
    }

    /// <summary>
    /// dry-run --live: every step against the running game with NO input. The observing driver waits for the screen each action
    /// needs (saying in one plain line what to open), resolves the target from memory and saves an overlay of the would-be click.
    /// </summary>
    private static async Task<int> DryRunLiveAsync(Options o, SessionPaths paths, TextReader input, bool interactive, TextWriter output, Action<Action>? abortHook)
    {
        var (game, problem) = RaidActions.Game.Memory.LiveGame.Open(GameCommands.LivePaths(paths.Output));
        if (game is null)
        {
            output.WriteLine(problem);
            return Failed;
        }
        using (game)
        {
            RaidActions.Game.Memory.UnityUi ui;
            try { ui = game.Ui(UiCommands.NativeLayoutPath); }
            catch (RaidActions.Game.Memory.UiReadException e)
            {
                output.WriteLine(e.Message);
                return Failed;
            }
            var load = PlanReader.ReadFile(Path.GetFullPath(o.Plan!));
            foreach (var i in load.Issues) output.WriteLine("  " + i);
            if (!load.Ok)
            {
                output.WriteLine("Refused: the plan file is not valid.");
                return Refused;
            }
            var folder = Path.Combine(paths.Output, "dryrun", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Confirmation.Code(load.Plan!.Plan)}");
            Directory.CreateDirectory(folder);
            var prompts = Path.Combine(folder, "prompts.txt");
            void Say(string line)
            {
                output.WriteLine($"{DateTime.Now:HH:mm:ss} OPEN: {line}");
                File.AppendAllText(prompts, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            var policy = RaidActions.Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var mode = o.Command == "run" ? RunMode.Real : RunMode.DryRun;
            IGameDriver driver = o.Navigate
                ? new Input.NavigatingDriver(game, ui, load.Plan.Plan, folder, policy, line => output.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"),
                    mode == RunMode.Real ? o.Steps : null)
                : new RaidActions.Game.Dry.ObservingDriver(game, ui, load.Plan.Plan, folder, policy.Problem, Say);
            var session = new PlanSession(paths, driver, game.Account, NoPacer.Instance);
            session.Load(Path.GetFullPath(o.Plan!));
            output.WriteLine(o.Navigate
                ? $"LIVE DRY RUN, navigating: allowed clicks only ({string.Join(", ", Input.NavigatingDriver.AllowedClicks)}); Equip is never pressed. Overlays: {folder}"
                : $"LIVE DRY RUN, observing only: no input is sent to the game. Overlays and prompts: {folder}");
            var check = await session.CheckAsync();
            foreach (var i in check.Issues) output.WriteLine("  " + i);
            foreach (var line in Confirmation.Lines(load.Plan, check, mode, driver.Name)) output.WriteLine(line);
            if (o.Steps is not null) output.WriteLine($"Only step(s) {string.Join(", ", o.Steps.Order())} run now; the others stay pending.");
            if (!check.CanStart)
            {
                output.WriteLine(check.Issues.HasErrors() ? "Refused: the plan does not match the live account." : "Nothing to do.");
                return check.Issues.HasErrors() ? Refused : Ok;
            }
            var typed = o.Confirm;
            if (typed is null && interactive)
            {
                output.Write($"Type {Confirmation.Code(load.Plan.Plan)} to start the dry run: ");
                typed = input.ReadLine();
            }
            if (!Confirmation.Matches(load.Plan.Plan, typed))
            {
                output.WriteLine($"Not confirmed: pass --confirm {Confirmation.Code(load.Plan.Plan)}.");
                return NotConfirmed;
            }
            abortHook?.Invoke(session.Abort);
            var progress = new SyncProgress<RunEvent>(e =>
            {
                if (e.State != "started") output.WriteLine($"  {e.Time:HH:mm:ss} step {e.Step,-2} {e.Kind,-16} {e.State,-7} {e.Detail ?? e.Text}");
            });
            RunResult result;
            var before = session.LastCheck;
            try { result = await session.RunAsync(mode, load.Plan.Plan.PlanId, load.Plan.Sha256, progress, o.Steps); }
            catch (PlanRefusedException e)
            {
                output.WriteLine(e.Message);
                return Refused;
            }
            finally
            {
                // The window goes back to the size, position and state it had before the run.
                if (driver is Input.NavigatingDriver navigating && navigating.RestoreWindow() is { } restored) output.WriteLine(restored);
            }
            var table = new System.Text.StringBuilder();
            if (driver is Input.NavigatingDriver nav)
            {
                table.AppendLine($"{"step",-5} {"click",-36} {"sent",-5} {"rectangle",-26} overlay");
                foreach (var c in nav.Clicks)
                    table.AppendLine($"{c.Step,-5} {c.What,-36} {(c.Sent ? "yes" : "NO"),-5} {c.Rect,-26} {Path.GetFileName(c.Overlay)}");
            }
            else if (driver is RaidActions.Game.Dry.ObservingDriver obs)
            {
                table.AppendLine($"{"step",-5} {"action",-14} {"target",-44} {"rectangle",-26} overlay");
                foreach (var w in obs.WouldClick)
                    table.AppendLine($"{w.Step,-5} {w.Kind,-14} {w.Target,-44} {w.Rect,-26} {Path.GetFileName(w.Overlay)}");
            }
            File.WriteAllText(Path.Combine(folder, "would-click.txt"), table.ToString());
            output.WriteLine(result.Message);
            output.Write(table);
            output.WriteLine($"Log: {result.LogPath}");
            if (mode == RunMode.Real && driver is Input.NavigatingDriver real)
            {
                output.WriteLine(real.EquipStillUnlocked.Count == 0
                    ? "Equip was used once and is locked again."
                    : $"Equip was not clicked for step(s) {string.Join(", ", real.EquipStillUnlocked)}; the unlock ended with this run.");
                if (result.Outcome == RunOutcome.Completed && result.SnapshotPath is { } snap)
                    await AfterRealRunAsync(game, load.Plan.Plan, session.LastCheck ?? before!, o.Steps!, snap, folder, output);
            }
            return result.Outcome switch { RunOutcome.Completed => Ok, RunOutcome.Aborted => Aborted, _ => Failed };
        }
    }

    /// <summary>IProgress that reports on the calling thread (console output stays in order).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
