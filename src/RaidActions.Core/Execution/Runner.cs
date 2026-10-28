using RaidActions.Plans;
using RaidActions.Snapshots;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Execution;

public enum RunOutcome { Completed, Aborted, Failed }

/// <param name="DoneSteps">The steps the account shows as done after the run (read without input), or the run's own count when that read failed.</param>
public sealed record RunResult(RunOutcome Outcome, IReadOnlyList<int> DoneSteps, int? FailedStep, string Message, string? LogPath,
    string? SnapshotPath, string? ScreenshotPath);

/// <param name="State">"started", "ok", "failed", "aborted".</param>
public sealed record RunEvent(DateTimeOffset Time, int Step, ActionKind Kind, string Text, string State, string? Detail);

/// <summary>Where a run writes: snapshots, logs, screenshots.</summary>
public sealed record RunFolders(string Snapshots, string Logs, string Screenshots);

/// <summary>
/// Runs an action script, one action at a time, in order (a driver that is an <see cref="IStepPicker"/> may choose the order of
/// consecutive interchangeable steps: same champion, each from the inventory). Stops at the first failure (a driver failure, a verification mismatch,
/// an exception) and never tries again; stops at once on abort; holds between actions while paused. The snapshot is saved by the
/// script's first action, before any input.
/// </summary>
public sealed class Runner(IGameDriver driver, IAccountSource account, IPacer pacer, RunFolders folders)
{
    public async Task<RunResult> RunAsync(LoadedPlan loaded, SavedExport checkedRead, ActionScript script, RunLog log, PauseGate pause,
        IProgress<RunEvent>? progress, CancellationToken abort)
    {
        var plan = loaded.Plan;
        string? snapshot = null;
        var doneByRun = script.SkippedDone.ToList();
        log.Write(0, "Run", $"{script.Mode} run of \"{plan.Name}\" ({plan.PlanId}, sha256 {loaded.Sha256}) with the {driver.Name} driver", "info",
            script.SkippedDone.Count > 0 ? $"already done, skipped: {string.Join(", ", script.SkippedDone)}" : null);

        void Report(UiAction a, string state, string? detail)
        {
            var e = log.Write(a.Step, a.Kind.ToString(), a.Text, state, detail);
            progress?.Report(new RunEvent(e.Time, a.Step, a.Kind, a.Text, state, detail));
        }

        UiAction? current = null;
        // Steps that may run in any order: consecutive steps onto the same champion, each from the inventory (none takes a piece another
        // gives back). A driver that can tell which is cheapest next (the piece already on screen) picks among them; else plan order.
        var picker = driver as IStepPicker;
        var stepOf = plan.Steps.ToDictionary(s => s.Number);
        bool Free(int n) => stepOf.TryGetValue(n, out var ps) && ps.FromChampionId is null;
        var items = new List<(int Step, List<UiAction> Actions)>();
        foreach (var a in script.Actions)
        {
            if (a.Step > 0 && items.Count > 0 && items[^1].Step == a.Step) items[^1].Actions.Add(a);
            else items.Add((a.Step, [a]));
        }
        try
        {
            for (var i = 0; i < items.Count;)
            {
                var first = items[i];
                var j = i + 1;
                if (picker is not null && first.Step > 0 && Free(first.Step))
                    while (j < items.Count && items[j].Step > 0 && Free(items[j].Step) && stepOf[items[j].Step].ToChampionId == stepOf[first.Step].ToChampionId) j++;
                var batch = items.GetRange(i, j - i);
                i = j;
                while (batch.Count > 0)
                {
                    var next = batch[0];
                    if (batch.Count > 1)
                    {
                        var steps = batch.Select(b => b.Step).ToList();
                        var pick = await picker!.PickAsync(steps, abort);
                        if (steps.Contains(pick) && pick != next.Step)
                        {
                            next = batch.First(b => b.Step == pick);
                            log.Write(pick, "Pick", $"Step {pick} next (its piece is nearest on screen)", "info", $"left in this champion's group: {string.Join(", ", steps)}");
                        }
                    }
                    batch.Remove(next);
                    foreach (var a in next.Actions)
                        if (await RunOne(a) is { } stopped) return stopped;
                }
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            return await Stop(RunOutcome.Aborted, current, "Aborted.");
        }
        catch (Exception e)
        {
            return await Stop(RunOutcome.Failed, current, $"{e.GetType().Name}: {e.Message}");
        }

        var end = script.Mode == RunMode.Real ? "All steps done and verified." : "Dry run done: every step found and highlighted, nothing changed.";
        log.Write(0, "Run", "End", "ok", end);
        return new RunResult(RunOutcome.Completed, doneByRun.Order().ToList(), null, end, log.Path, snapshot, null);

        // One action; a result when the run stops there, null to go on.
        async Task<RunResult?> RunOne(UiAction a)
        {
            {
                current = a;
                abort.ThrowIfCancellationRequested();
                if (pause.IsPaused) log.Write(a.Step, "Pause", "Paused before " + a.Text, "info");
                await pause.WaitAsync(abort);
                if (script.Mode == RunMode.DryRun && a.ChangesGear)
                    throw new InvalidOperationException($"A dry run must not {a.Kind}."); // the planner never builds this; refuse anyway

                Report(a, "started", null);
                string? detail = null;
                switch (a.Kind)
                {
                    case ActionKind.SaveSnapshot:
                        snapshot = GearSnapshot.Take(loaded, checkedRead, DateTimeOffset.Now).Save(folders.Snapshots, checkedRead);
                        detail = snapshot;
                        break;
                    case ActionKind.VerifyStep:
                    case ActionKind.VerifyEnd:
                    {
                        // Real run: the steps done before this run, those this run verified, and this one (the order may differ from the plan's).
                        var expectDone = a.Kind == ActionKind.VerifyEnd ? plan.Steps.Select(s => s.Number).ToHashSet()
                            : script.Mode == RunMode.Real ? doneByRun.Append(a.Step).ToHashSet() : a.DoneThrough!.ToHashSet();
                        var fresh = await account.ReadAsync(abort);
                        var issues = Verify(plan, fresh, expectDone);
                        if (issues.Count > 0) return await Stop(RunOutcome.Failed, a, "Verification failed: " + string.Join(" ", issues));
                        detail = $"verified: {expectDone.Count switch { 0 => "no step", 1 => "step " + expectDone.Single(), _ => "steps " + string.Join(", ", expectDone.Order()) }} done, every plan piece where expected";
                        if (a.Kind == ActionKind.VerifyStep && script.Mode == RunMode.Real && !doneByRun.Contains(a.Step)) doneByRun.Add(a.Step);
                        break;
                    }
                    default:
                    {
                        await pacer.DelayAsync(a, abort);
                        abort.ThrowIfCancellationRequested();
                        var r = await driver.PerformAsync(a, abort);
                        if (!r.Ok) return await Stop(RunOutcome.Failed, a, r.Message ?? $"{a.Kind} failed.");
                        detail = r.Message;
                        break;
                    }
                }
                Report(a, "ok", detail);
                return null;
            }
        }

        async Task<RunResult> Stop(RunOutcome outcome, UiAction? at, string message)
        {
            string? shot = null;
            if (outcome == RunOutcome.Failed)
            {
                try { shot = driver.SaveScreenshot(folders.Screenshots, $"{log.RunId}-step{at?.Step ?? 0}"); }
                catch (Exception e) { message += $" (screenshot failed: {e.Message})"; }
            }
            if (at is not null) Report(at, outcome == RunOutcome.Aborted ? "aborted" : "failed", message);
            var done = await DoneInAccount(plan) ?? doneByRun.Order().ToList();
            var where = at is null || at.Step == 0 ? "" : $" at step {at.Step} ({at.Kind})";
            var text = $"{outcome}{where}: {message} The account shows step(s) {(done.Count == 0 ? "none" : string.Join(", ", done))} done.";
            log.Write(at?.Step ?? 0, "Run", "End", outcome == RunOutcome.Aborted ? "aborted" : "failed", text);
            return new RunResult(outcome, done, at is { Step: > 0 } ? at.Step : null, text, log.Path, snapshot, shot);
        }
    }

    /// <summary>The fresh read agrees with the plan after <paramref name="doneSteps"/>: same game version, every piece where expected.</summary>
    public static List<PlanIssue> Verify(Plan plan, SavedExport fresh, IReadOnlySet<int> doneSteps)
    {
        var issues = AccountCheck.OwnersAgree(plan, fresh, doneSteps);
        if (fresh.Meta.GameVersion != plan.GameVersion)
            issues.Insert(0, PlanIssue.Error(IssueCode.GameVersionMismatch, $"The game is now version {fresh.Meta.GameVersion}."));
        return issues;
    }

    /// <summary>After a stop: which steps the account shows as done (a read, no input). Null when the read fails.</summary>
    private async Task<List<int>?> DoneInAccount(Plan plan)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var fresh = await account.ReadAsync(timeout.Token);
            var owner = fresh.Artifacts.ToDictionary(a => a.Id, a => a.OwnerHeroId);
            return plan.Steps.Where(s => owner.GetValueOrDefault(s.ArtifactId) == s.ToChampionId).Select(s => s.Number).ToList();
        }
        catch (Exception) { return null; }
    }
}
