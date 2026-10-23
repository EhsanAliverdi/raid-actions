using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RaidActions.Execution;
using RaidActions.Execution.Fake;
using RaidActions.Plans;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.App.ViewModels;

/// <summary>Dialogs the window shows (replaced in tests).</summary>
public interface IDialogs
{
    string? OpenPlan(string initialFolder);
    Task<bool> ConfirmAsync(string title, string body, string startText);
}

public sealed partial class StepRow : ObservableObject
{
    public required int Number { get; init; }
    public required string Slot { get; init; }
    public required int ArtifactId { get; init; }
    public required string Piece { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _note = "";
}

public sealed record IssueRow(string Severity, string Text);

/// <summary>
/// The window: open a plan, check it, dry run, run, pause, abort, the log. Phase 1: the fake executor only. The fake game starts
/// from the account.json of the raid-extractor output the plan belongs to.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private PlanSession? _session;
    private RunMode _mode;

    public MainViewModel(IDialogs dialogs, string? killSwitchProblem)
    {
        _dialogs = dialogs;
        KillSwitchText = killSwitchProblem ?? $"Kill switch: {KillSwitch.Keys} (works while the game has focus)";
        KillSwitchOk = killSwitchProblem is null;
    }

    public string ExecutorBanner => "FAKE EXECUTOR: no input is sent to the game. Runs move pieces in an in-memory copy of the account.";
    public string KillSwitchText { get; }
    public bool KillSwitchOk { get; }

    /// <summary>The fake executor's pause between actions (zero in tests).</summary>
    public TimeSpan FakeActionDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public IPacer Pacer { get; init; } = new RandomPacer(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(450));
    public string? OutputOverride { get; init; }

    public ObservableCollection<StepRow> Steps { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private string _status = "Open a plan (raid-extractor output\\plans\\*.json).";
    [ObservableProperty] private string? _planPath;
    [ObservableProperty] private string? _planName;
    [ObservableProperty] private string? _planDetails;
    [ObservableProperty] private string? _accountSource;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DryRunCommand), nameof(RunCommand))]
    private bool _canStart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPlanCommand), nameof(ValidateCommand), nameof(DryRunCommand), nameof(RunCommand),
        nameof(PauseResumeCommand), nameof(AbortCommand), nameof(WriteSampleCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _isPaused;

    public bool HasPlan => _session?.Plan is not null;

    private bool Idle => !IsRunning;
    private bool CanCheck => Idle && HasPlan;
    private bool CanRun => Idle && CanStart;

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task OpenPlan()
    {
        var folder = Path.Combine(AppPaths.Resolve(null, null).ExtractorOutput, "plans");
        if (_dialogs.OpenPlan(Directory.Exists(folder) ? folder : Environment.CurrentDirectory) is { } path) await LoadAsync(path);
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task WriteSample()
    {
        var folder = Path.Combine(AppPaths.Resolve(null, OutputOverride).Output, "sample", "extractor-output");
        await LoadAsync(SampleData.Write(folder));
    }

    /// <summary>Loads a plan, starts the fake game from its account.json, and checks it.</summary>
    public async Task LoadAsync(string path)
    {
        Steps.Clear();
        Issues.Clear();
        CanStart = false;
        PlanPath = path;
        PlanName = PlanDetails = null;
        var paths = AppPaths.ForPlan(path, null, OutputOverride);
        var accountPath = Path.Combine(paths.ExtractorOutput, "account.json");
        if (!File.Exists(accountPath))
        {
            _session = null;
            Issues.Add(new("error", $"No account.json at {accountPath}: the fake executor needs the saved account the plan belongs to."));
            Status = "Cannot load the plan.";
            Notify();
            return;
        }
        var game = new FakeGame(JsonSerializer.Deserialize<SavedExport>(await File.ReadAllTextAsync(accountPath), Exporter.JsonRead)!) { ActionDelay = FakeActionDelay };
        _session = new PlanSession(paths, game, game, Pacer);
        AccountSource = $"Fake game started from {accountPath} (a saved export, not a live read)";

        var r = _session.Load(path);
        foreach (var i in r.Issues) Issues.Add(Row(i));
        if (!r.Ok)
        {
            Status = "Refused: the plan file is not valid.";
            Notify();
            return;
        }
        var plan = r.Plan!.Plan;
        PlanName = plan.Name;
        PlanDetails = $"id {plan.PlanId} · game {plan.GameVersion} · made from the export of {plan.ExportedAt} · sha256 {r.Plan.Sha256[..12]}"
            + (plan.LocksName is { } l ? $" · locks \"{l}\"" : "");
        foreach (var s in plan.Steps)
            Steps.Add(new StepRow
            {
                Number = s.Number, Slot = s.Slot, ArtifactId = s.ArtifactId, Piece = s.Piece,
                From = s.FromChampionId is null ? "inventory" : $"{s.FromName} (#{s.FromChampionId})", To = $"{s.ToName} (#{s.ToChampionId})",
            });
        Notify();
        await Validate();
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Validate()
    {
        if (_session is null) return;
        Status = "Checking against the account…";
        try { Show(await _session.CheckAsync()); }
        catch (Exception e)
        {
            Issues.Add(new("error", e.Message));
            Status = "The check failed.";
        }
    }

    private void ShowIssues(CheckReport report)
    {
        Issues.Clear();
        foreach (var i in report.Issues) Issues.Add(Row(i));
        if (report.Issues.Count == 0) Issues.Add(new("ok", "No issues: the plan matches the account (game version, every piece, every owner)."));
    }

    private void Show(CheckReport report)
    {
        ShowIssues(report);
        foreach (var s in report.Steps)
            if (Steps.FirstOrDefault(r => r.Number == s.Step.Number) is { } row)
            {
                row.State = s.State == StepState.Done ? "done" : "pending";
                row.Note = s.Displaced is { } d ? $"#{d} goes to the inventory" : "";
            }
        CanStart = report.CanStart;
        Status = report.CanStart ? $"The plan can start ({report.Pending.Count()} step(s) to do)."
            : report.Issues.HasErrors() ? "Refused: the plan does not match the account. See the issues." : "Nothing to do: every step is done.";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task DryRun() => StartAsync(RunMode.DryRun);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task Run() => StartAsync(RunMode.Real);

    private async Task StartAsync(RunMode mode)
    {
        if (_session?.Plan is not { } loaded || _session.LastCheck is not { } check) return;
        var body = string.Join(Environment.NewLine, Confirmation.Lines(loaded, check, mode, _session.Driver.Name));
        if (!await _dialogs.ConfirmAsync(mode == RunMode.DryRun ? "Start the dry run?" : "Start the run?", body, mode == RunMode.DryRun ? "Start dry run" : "Start run"))
        {
            Status = "Not started.";
            return;
        }

        _mode = mode;
        IsRunning = true;
        IsPaused = false;
        Log.Clear();
        Status = mode == RunMode.DryRun ? "Dry run…" : "Running…";
        var progress = new UiProgress<RunEvent>(this, OnEvent);
        try
        {
            var result = await Task.Run(() => _session.RunAsync(mode, loaded.Plan.PlanId, loaded.Sha256, progress));
            await progress.Drain();
            Status = result.Message;
            Log.Add(result.Message);
            if (result.SnapshotPath is not null) Log.Add($"Snapshot: {result.SnapshotPath}");
            if (result.LogPath is not null) Log.Add($"Log: {result.LogPath}");
            if (result.ScreenshotPath is not null) Log.Add($"Screenshot: {result.ScreenshotPath}");
        }
        catch (PlanRefusedException e)
        {
            Status = e.Message;
            if (e.Check is { } c) Show(c);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
        }
        // Show where things are now (the fake game's account after the run).
        var after = _session.LastCheck;
        try { after = await _session.CheckAsync(); } catch (Exception) { }
        if (after is not null)
        {
            ShowIssues(after);
            foreach (var s in after.Steps)
                if (Steps.FirstOrDefault(r => r.Number == s.Step.Number) is { } row && row.State is not ("failed" or "aborted"))
                    row.State = s.State == StepState.Done ? "done" : row.State == "found" ? "found" : "pending";
        }
        CanStart = after?.CanStart ?? false;
    }

    private void OnEvent(RunEvent e)
    {
        Log.Add($"{e.Time:HH:mm:ss}  step {e.Step,-2} {e.Kind,-22} {e.State,-8} {e.Detail ?? e.Text}");
        if (Steps.FirstOrDefault(r => r.Number == e.Step) is not { } row) return;
        row.State = e.State switch
        {
            "failed" => "failed",
            "aborted" => "aborted",
            "ok" when e.Kind == ActionKind.VerifyStep => _mode == RunMode.DryRun ? "found" : "done",
            _ => "running",
        };
        if (e.State is "failed" or "aborted") row.Note = e.Detail ?? "";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void PauseResume()
    {
        if (_session is null) return;
        if (IsPaused) _session.Resume(); else _session.Pause();
        IsPaused = !IsPaused;
        Status = IsPaused ? "Paused (before the next action)." : "Running…";
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Abort()
    {
        AbortNow();
        Status = "Aborting…";
    }

    /// <summary>Safe from any thread (the kill switch calls it from its own).</summary>
    public void AbortNow() => _session?.Abort();

    public void AbortFromKillSwitch()
    {
        if (!IsRunning) return;
        Log.Add($"{DateTime.Now:HH:mm:ss}  {KillSwitch.Keys}: abort");
        Status = "Aborting…";
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasPlan));
        ValidateCommand.NotifyCanExecuteChanged();
    }

    private static IssueRow Row(PlanIssue i) =>
        new(i.Severity == Severity.Error ? "error" : "warning", (i.Step is { } s ? $"Step {s}: " : "") + i.Message);

    /// <summary>Progress onto the UI thread (or inline without one, in tests), in order.</summary>
    private sealed class UiProgress<T>(MainViewModel vm, Action<T> report) : IProgress<T>
    {
        private Task _last = Task.CompletedTask;
        private readonly object _lock = new();

        public void Report(T value)
        {
            if (vm._ui is null)
            {
                lock (_lock) report(value);
                return;
            }
            var done = new TaskCompletionSource();
            lock (_lock) _last = done.Task;
            vm._ui.Post(_ => { report(value); done.SetResult(); }, null);
        }

        public Task Drain()
        {
            lock (_lock) return _last;
        }
    }
}
