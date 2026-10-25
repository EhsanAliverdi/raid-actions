using RaidActions.Execution.Fake;
using RaidActions.Plans;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Execution;

/// <summary>Where the session reads raid-extractor's output (history snapshots) and writes its own (snapshots, logs, screenshots).</summary>
public sealed record SessionPaths(string ExtractorOutput, string Output)
{
    public RunFolders Folders => new(Path.Combine(Output, "snapshots"), Path.Combine(Output, "logs"), Path.Combine(Output, "screenshots"));
}

public enum SessionState { Empty, Loaded, Running }

/// <summary>Start was refused: the plan did not pass the fresh check, or it is not the plan that was loaded.</summary>
public sealed class PlanRefusedException(string message, CheckReport? check = null) : Exception(message)
{
    public CheckReport? Check { get; } = check;
}

/// <summary>
/// One plan at a time: load, check against a fresh read, run (dry or real), pause, abort. Used by the CLI, the window and the
/// planner's pipe. Start always checks the account again right before the first action and refuses on any error.
/// </summary>
public sealed class PlanSession(SessionPaths paths, IGameDriver driver, IAccountSource account, IPacer pacer)
{
    private readonly object _lock = new();
    private CancellationTokenSource? _abort;
    private PauseGate? _pause;

    public IGameDriver Driver => driver;
    public SessionPaths Paths => paths;
    public LoadedPlan? Plan { get; private set; }
    public List<PlanIssue> LoadIssues { get; private set; } = [];
    public CheckReport? LastCheck { get; private set; }
    public SessionState State { get; private set; }

    public PlanReader.Result Load(string path)
    {
        lock (_lock)
        {
            if (State == SessionState.Running) throw new InvalidOperationException("A run is in progress.");
            var r = PlanReader.ReadFile(path);
            Plan = r.Ok ? r.Plan : null;
            LoadIssues = r.Issues;
            LastCheck = null;
            State = Plan is null ? SessionState.Empty : SessionState.Loaded;
            return r;
        }
    }

    /// <summary>A fresh read, the plan's snapshot export from raid-extractor's history, and the full check.</summary>
    public async Task<CheckReport> CheckAsync(CancellationToken ct = default)
    {
        var plan = Plan ?? throw new InvalidOperationException("No plan loaded.");
        var (report, _) = await CheckFreshAsync(plan, ct);
        return report;
    }

    private async Task<(CheckReport Report, SavedExport Read)> CheckFreshAsync(LoadedPlan plan, CancellationToken ct)
    {
        var fresh = await account.ReadAsync(ct);
        var (madeFrom, problem) = PlanSnapshotLocator.Load(paths.ExtractorOutput, plan.Plan.SnapshotId);
        var report = AccountCheck.Check(plan.Plan, fresh, madeFrom);
        if (problem is not null)
            report = report with { Issues = report.Issues.Select(i => i.Code == IssueCode.SnapshotMissing && i.Step is null ? i with { Message = i.Message + " " + problem } : i).ToList() };
        report = report with { Issues = report.Issues.Concat(LoadIssues.Where(i => i.Severity == Severity.Warning)).ToList() };
        LastCheck = report;
        return (report, fresh);
    }

    /// <summary>
    /// Runs the loaded plan. <paramref name="planId"/> and <paramref name="sha256"/> are what the user confirmed; they must match
    /// the loaded plan. Throws <see cref="PlanRefusedException"/> when the fresh check fails.
    /// </summary>
    public async Task<RunResult> RunAsync(RunMode mode, string planId, string sha256, IProgress<RunEvent>? progress = null, IReadOnlySet<int>? onlySteps = null)
    {
        LoadedPlan plan;
        lock (_lock)
        {
            plan = Plan ?? throw new PlanRefusedException("No plan loaded.");
            if (State == SessionState.Running) throw new PlanRefusedException("A run is in progress.");
            if (plan.Plan.PlanId != planId || !string.Equals(plan.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                throw new PlanRefusedException($"Start was confirmed for plan {planId} ({Short(sha256)}), but the loaded plan is {plan.Plan.PlanId} ({Short(plan.Sha256)}).");
            if (plan.Plan.PlanId.StartsWith(SampleData.PlanIdPrefix, StringComparison.Ordinal) && driver.SendsInput)
                throw new PlanRefusedException("The sample plan runs only with the fake executor.");
            State = SessionState.Running;
            _abort = new CancellationTokenSource();
            _pause = new PauseGate();
        }
        try
        {
            var (check, read) = await CheckFreshAsync(plan, _abort.Token);
            if (!check.CanStart)
                throw new PlanRefusedException("Refused: " + string.Join(" ", check.Issues.Where(i => i.Severity == Severity.Error).Select(i => i.ToString())
                    .DefaultIfEmpty(check.Issues.FirstOrDefault(i => i.Code == IssueCode.NothingToDo)?.Message ?? "nothing to do.")), check);
            if (onlySteps is not null && onlySteps.Any(n => plan.Plan.Steps.All(s => s.Number != n)))
                throw new PlanRefusedException($"Steps {string.Join(", ", onlySteps)}: not all are in the plan.");
            var script = ActionPlanner.Build(plan.Plan, check, mode, onlySteps);
            using var log = new RunLog(paths.Folders.Logs, plan.Plan.PlanId, DateTimeOffset.Now);
            return await new Runner(driver, account, pacer, paths.Folders).RunAsync(plan, read, script, log, _pause, progress, _abort.Token);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            return new RunResult(RunOutcome.Aborted, [], null, "Aborted before the first action.", null, null, null);
        }
        finally
        {
            lock (_lock)
            {
                _abort.Dispose();
                _abort = null;
                _pause = null;
                State = SessionState.Loaded;
            }
        }
    }

    public bool IsPaused { get { lock (_lock) return _pause?.IsPaused ?? false; } }

    public void Pause() { lock (_lock) _pause?.Pause(); }
    public void Resume() { lock (_lock) _pause?.Resume(); }

    /// <summary>Stops the run at once (safe to call from any thread, any time).</summary>
    public void Abort()
    {
        lock (_lock)
        {
            try { _abort?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
