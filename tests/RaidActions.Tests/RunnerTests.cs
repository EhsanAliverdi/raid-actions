using System.Diagnostics;
using System.Text.Json;
using RaidActions.Execution;
using RaidActions.Execution.Fake;
using RaidActions.Verification;

namespace RaidActions.Tests;

/// <summary>The runner against the fake game: whole runs, abort, pause, failures, verification. No game, no input.</summary>
public class RunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));
    private RunFolders Folders => new(Path.Combine(_dir, "snapshots"), Path.Combine(_dir, "logs"), Path.Combine(_dir, "screens"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed record Run(RunResult Result, FakeGame Game, RunLog Log, List<RunEvent> Events);

    private async Task<Run> RunFake(RunMode mode, Action<FakeGame, CancellationTokenSource, PauseGate>? setup = null)
    {
        var game = new FakeGame(TestData.Export());
        var loaded = TestData.Load(TestData.PlanJson());
        var read = await game.ReadAsync(default);
        var check = AccountCheck.Check(loaded.Plan, read, TestData.MadeFrom());
        var script = ActionPlanner.Build(loaded.Plan, check, mode);
        using var cts = new CancellationTokenSource();
        var pause = new PauseGate();
        setup?.Invoke(game, cts, pause);
        var events = new List<RunEvent>();
        var log = new RunLog(Folders.Logs, loaded.Plan.PlanId, DateTimeOffset.Now);
        var result = await new Runner(game, game, NoPacer.Instance, Folders)
            .RunAsync(loaded, read, script, log, pause, new Inline(events.Add), cts.Token);
        log.Dispose();
        return new Run(result, game, log, events);
    }

    private sealed class Inline(Action<RunEvent> a) : IProgress<RunEvent>
    {
        public void Report(RunEvent value) => a(value);
    }

    private static async Task<Dictionary<int, int?>> Owners(FakeGame g) => (await g.ReadAsync(default)).Artifacts.ToDictionary(a => a.Id, a => a.OwnerHeroId);

    [Fact]
    public async Task A_real_run_ends_in_the_plans_end_state()
    {
        var r = await RunFake(RunMode.Real);
        Assert.Equal(RunOutcome.Completed, r.Result.Outcome);
        Assert.Equal([1, 2, 3], r.Result.DoneSteps);
        var owners = await Owners(r.Game);
        foreach (var p in TestData.Plan().Pieces) Assert.Equal(p.ExpectedOwner, owners[p.ArtifactId]);
        Assert.Equal(3, r.Game.Performed.Count(a => a.Kind == ActionKind.Equip));
    }

    [Fact]
    public async Task The_snapshot_is_saved_before_any_input()
    {
        var r = await RunFake(RunMode.Real);
        Assert.True(File.Exists(r.Result.SnapshotPath));
        var first = r.Log.Entries.First(e => e.Action != "Run");
        Assert.Equal("SaveSnapshot", first.Action);
        var snapshotDone = r.Log.Entries.ToList().FindIndex(e => e.Action == "SaveSnapshot" && e.Result == "ok");
        var firstInput = r.Log.Entries.ToList().FindIndex(e => e.Action == "CheckGameIdle" && e.Result == "started");
        Assert.True(snapshotDone < firstInput);
    }

    [Fact]
    public async Task A_dry_run_never_equips_and_changes_nothing()
    {
        var before = TestData.Export().Artifacts.ToDictionary(a => a.Id, a => a.OwnerHeroId);
        var r = await RunFake(RunMode.DryRun);
        Assert.Equal(RunOutcome.Completed, r.Result.Outcome);
        Assert.DoesNotContain(r.Game.Performed, a => a.ChangesGear);
        Assert.Equal(3, r.Game.Performed.Count(a => a.Kind == ActionKind.Highlight));
        Assert.Equal(before, await Owners(r.Game));
        Assert.Empty(r.Result.DoneSteps);
    }

    [Fact]
    public async Task Abort_stops_at_once_and_reports_what_is_done()
    {
        var r = await RunFake(RunMode.Real, (g, cts, _) => g.BeforeAction = a =>
        {
            if (a.Kind == ActionKind.Equip && a.Step == 2) cts.Cancel();
        });
        Assert.Equal(RunOutcome.Aborted, r.Result.Outcome);
        Assert.Equal(2, r.Result.FailedStep);
        Assert.Equal([1], r.Result.DoneSteps); // read from the account after the stop
        Assert.DoesNotContain(r.Game.Performed, a => a.Step >= 2 && a.Kind == ActionKind.Equip);
        Assert.Equal(ActionKind.VerifySelection, r.Game.Performed[^1].Kind); // nothing after the abort
        Assert.Contains(r.Log.Entries, e => e.Result == "aborted");
    }

    [Fact]
    public async Task Abort_cuts_a_slow_action_short()
    {
        var watch = Stopwatch.StartNew();
        var r = await RunFake(RunMode.Real, (g, cts, _) =>
        {
            g.ActionDelay = TimeSpan.FromSeconds(30);
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        });
        Assert.Equal(RunOutcome.Aborted, r.Result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Empty(r.Game.Performed);
    }

    [Fact]
    public async Task Pause_holds_before_the_next_action_until_resumed()
    {
        PauseGate? gate = null;
        var game = default(FakeGame);
        var task = RunFake(RunMode.Real, (g, _, p) =>
        {
            game = g;
            gate = p;
            g.BeforeAction = a =>
            {
                if (a.Kind == ActionKind.OpenChampion && a.Step == 2) p.Pause();
            };
        });
        await WaitUntil(() => gate?.IsPaused == true);
        var count = game!.Performed.Count;
        await Task.Delay(300);
        Assert.Equal(count, game.Performed.Count); // the action in flight finished, nothing new started
        Assert.False(task.IsCompleted);
        gate!.Resume();
        var r = await task;
        Assert.Equal(RunOutcome.Completed, r.Result.Outcome);
    }

    [Fact]
    public async Task Abort_while_paused()
    {
        var r = await RunFake(RunMode.Real, (g, cts, p) => g.BeforeAction = a =>
        {
            if (a.Kind == ActionKind.OpenChampion && a.Step == 3)
            {
                p.Pause();
                _ = Task.Delay(100).ContinueWith(_ => cts.Cancel());
            }
        });
        Assert.Equal(RunOutcome.Aborted, r.Result.Outcome);
        Assert.Equal([1, 2], r.Result.DoneSteps);
    }

    [Fact]
    public async Task A_driver_failure_stops_the_run_without_retrying()
    {
        var r = await RunFake(RunMode.Real, (g, _, _) => g.FailWhen = a => a.Kind == ActionKind.FindPiece && a.Step == 2 ? "Unexpected popup" : null);
        Assert.Equal(RunOutcome.Failed, r.Result.Outcome);
        Assert.Equal(2, r.Result.FailedStep);
        Assert.Contains("Unexpected popup", r.Result.Message);
        Assert.Single(r.Game.Performed, a => a.Kind == ActionKind.FindPiece && a.Step == 2);
        Assert.Equal(ActionKind.FindPiece, r.Game.Performed[^1].Kind);
        Assert.Equal([1], r.Result.DoneSteps);
    }

    [Fact]
    public async Task A_wrong_equip_is_caught_by_the_read_after_the_step()
    {
        var r = await RunFake(RunMode.Real, (g, _, _) => g.MisEquip = (2, 3)); // #200 lands on Cora instead of Aria
        Assert.Equal(RunOutcome.Failed, r.Result.Outcome);
        Assert.Equal(2, r.Result.FailedStep);
        Assert.Contains("Verification failed", r.Result.Message);
        Assert.Contains("#200", r.Result.Message);
        Assert.DoesNotContain(r.Game.Performed, a => a.Step == 3);
    }

    [Fact]
    public async Task A_battle_in_progress_stops_before_any_step()
    {
        var r = await RunFake(RunMode.Real, (g, _, _) => g.BattleRunning = true);
        Assert.Equal(RunOutcome.Failed, r.Result.Outcome);
        Assert.Null(r.Result.FailedStep);
        Assert.Contains("battle", r.Result.Message);
        Assert.True(File.Exists(r.Result.SnapshotPath));
        Assert.DoesNotContain(r.Game.Performed, a => a.Step > 0);
    }

    [Fact]
    public async Task A_game_update_during_the_run_stops_it()
    {
        var r = await RunFake(RunMode.Real, (g, _, _) => g.BeforeAction = a =>
        {
            if (a.Kind == ActionKind.Equip && a.Step == 1) g.GameVersion = "11.90.0";
        });
        Assert.Equal(RunOutcome.Failed, r.Result.Outcome);
        Assert.Contains("11.90.0", r.Result.Message);
    }

    [Fact]
    public async Task Every_action_is_logged_to_a_jsonl_file()
    {
        var r = await RunFake(RunMode.Real);
        var lines = File.ReadAllLines(r.Result.LogPath!);
        var entries = lines.Select(l => JsonSerializer.Deserialize<LogEntry>(l, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList();
        var script = r.Events.Where(e => e.State == "started").ToList();
        Assert.Equal(script.Count, entries.Count(e => e.Result == "ok" && e.Action != "Run"));
        Assert.All(entries, e => Assert.Equal(r.Log.RunId, e.RunId));
        Assert.Contains(entries, e => e.Action == "VerifyStep" && e.Detail?.Contains("steps 1, 2 done") == true);
    }

    [Fact]
    public async Task A_dry_script_with_an_equip_is_refused()
    {
        var loaded = TestData.Load(TestData.PlanJson());
        var game = new FakeGame(TestData.Export());
        var bad = new ActionScript(RunMode.DryRun, [new UiAction(ActionKind.Equip, 1, "Equip", 1, "Helmet", 100)], []);
        using var log = new RunLog(null, loaded.Plan.PlanId, DateTimeOffset.Now);
        var r = await new Runner(game, game, NoPacer.Instance, Folders).RunAsync(loaded, TestData.Export(), bad, log, new PauseGate(), null, default);
        Assert.Equal(RunOutcome.Failed, r.Outcome);
        Assert.Empty(game.Performed);
    }

    [Fact]
    public async Task Random_pacing_stays_in_range_and_skips_reads()
    {
        var pacer = new RandomPacer(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(60), new Random(1));
        var w = Stopwatch.StartNew();
        await pacer.DelayAsync(new UiAction(ActionKind.VerifyStep, 1, "read"), default);
        Assert.True(w.ElapsedMilliseconds < 15);
        w.Restart();
        await pacer.DelayAsync(new UiAction(ActionKind.OpenChampion, 1, "open"), default);
        Assert.InRange(w.ElapsedMilliseconds, 15, 1000);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var w = Stopwatch.StartNew();
        while (!condition())
        {
            if (w.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
