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

        Read-only, against the running game (no input, never brings it to the front):
          RaidActions window                   find the game window: size, DPI, state, standard size
          RaidActions window fit               set the game window to the standard size (asks first)
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

        Abort a run: Ctrl+Alt+Esc (anywhere) or Ctrl+C.
        Exit codes: 0 done, 1 refused, 2 failed, 3 aborted, 4 usage error, 5 not confirmed.
        """;

    public sealed record Options(string Command, string? Plan, string? ExtractorOutput, string? Account, string? Output, string? Confirm,
        TimeSpan PaceMin, TimeSpan PaceMax, bool Live = false);

    public static Options? Parse(string[] args, TextWriter err)
    {
        if (args.Length == 0) return null;
        string? plan = null, ext = null, account = null, output = null, confirm = null;
        var live = false;
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
        if (live && command != "validate") throw new ArgumentException("--live is only for validate in this phase (runs use the fake executor).");
        return new Options(command, plan, ext, account, output, confirm, min, max, live);
    }

    /// <param name="interactive">Whether <paramref name="input"/> is a person at a console (to type the confirmation).</param>
    /// <param name="abortHook">Receives the session's abort, for Ctrl+C and the kill switch.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextReader input, bool interactive, Action<Action>? abortHook = null)
    {
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
        if (o.Live) return await ValidateLiveAsync(o, paths, output);
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

    /// <summary>IProgress that reports on the calling thread (console output stays in order).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
