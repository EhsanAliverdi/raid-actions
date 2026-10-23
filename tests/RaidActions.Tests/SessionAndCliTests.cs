using System.Text.Json.Nodes;
using RaidActions.App;
using RaidActions.App.ViewModels;
using RaidActions.Execution;
using RaidActions.Execution.Fake;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Tests;

/// <summary>A temp folder with the sample account and plan in raid-extractor's layout.</summary>
public sealed class SampleFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));
    public string Extractor => Path.Combine(Root, "ext");
    public string Output => Path.Combine(Root, "out");
    public string Plan { get; }

    public SampleFolder() => Plan = SampleData.Write(Extractor);

    public SessionPaths Paths => new(Extractor, Output);

    public void EditPlan(Action<JsonObject> change)
    {
        var j = JsonNode.Parse(File.ReadAllText(Plan))!.AsObject();
        change(j);
        File.WriteAllText(Plan, j.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

public class PlanSessionTests
{
    private sealed class InputDriver(IGameDriver inner) : IGameDriver
    {
        public string Name => "game";
        public bool SendsInput => true;
        public Task<DriverResult> PerformAsync(UiAction action, CancellationToken ct) => inner.PerformAsync(action, ct);
        public string? SaveScreenshot(string folder, string name) => null;
    }

    [Fact]
    public async Task Load_check_and_run_the_sample()
    {
        using var f = new SampleFolder();
        var game = new FakeGame(SampleData.Account());
        var s = new PlanSession(f.Paths, game, game, NoPacer.Instance);
        Assert.True(s.Load(f.Plan).Ok);
        Assert.True((await s.CheckAsync()).CanStart);
        var r = await s.RunAsync(RunMode.Real, s.Plan!.Plan.PlanId, s.Plan.Sha256);
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(SessionState.Loaded, s.State);
        var again = await s.CheckAsync();
        Assert.False(again.CanStart);
        Assert.Contains(again.Issues, i => i.Code == Plans.IssueCode.NothingToDo);
    }

    [Fact]
    public async Task Start_must_name_the_loaded_plan()
    {
        using var f = new SampleFolder();
        var game = new FakeGame(SampleData.Account());
        var s = new PlanSession(f.Paths, game, game, NoPacer.Instance);
        s.Load(f.Plan);
        await Assert.ThrowsAsync<PlanRefusedException>(() => s.RunAsync(RunMode.DryRun, "another-plan", s.Plan!.Sha256));
        await Assert.ThrowsAsync<PlanRefusedException>(() => s.RunAsync(RunMode.DryRun, s.Plan!.Plan.PlanId, new string('0', 64)));
        Assert.Empty(game.Performed);
    }

    [Fact]
    public async Task Start_checks_the_account_again()
    {
        using var f = new SampleFolder();
        var game = new FakeGame(SampleData.Account());
        var s = new PlanSession(f.Paths, game, game, NoPacer.Instance);
        s.Load(f.Plan);
        Assert.True((await s.CheckAsync()).CanStart);
        game.GameVersion = "11.90.0"; // the game updated between Validate and Start
        var e = await Assert.ThrowsAsync<PlanRefusedException>(() => s.RunAsync(RunMode.Real, s.Plan!.Plan.PlanId, s.Plan.Sha256));
        Assert.Contains("GameVersionMismatch", e.Message);
        Assert.Empty(game.Performed);
    }

    [Fact]
    public async Task The_sample_plan_never_runs_with_a_driver_that_sends_input()
    {
        using var f = new SampleFolder();
        var game = new FakeGame(SampleData.Account());
        var s = new PlanSession(f.Paths, new InputDriver(game), game, NoPacer.Instance);
        s.Load(f.Plan);
        var e = await Assert.ThrowsAsync<PlanRefusedException>(() => s.RunAsync(RunMode.DryRun, s.Plan!.Plan.PlanId, s.Plan.Sha256));
        Assert.Contains("fake executor", e.Message);
    }

    [Fact]
    public async Task Abort_during_a_session_run()
    {
        using var f = new SampleFolder();
        var game = new FakeGame(SampleData.Account()) { ActionDelay = TimeSpan.FromMilliseconds(50) };
        var s = new PlanSession(f.Paths, game, game, NoPacer.Instance);
        s.Load(f.Plan);
        game.BeforeAction = a =>
        {
            if (a.Kind == ActionKind.OpenChampion && a.Step == 3) s.Abort();
        };
        var r = await s.RunAsync(RunMode.Real, s.Plan!.Plan.PlanId, s.Plan.Sha256);
        Assert.Equal(RunOutcome.Aborted, r.Outcome);
        Assert.Equal([1, 2], r.DoneSteps);
        Assert.Equal(SessionState.Loaded, s.State);
        s.Abort(); // harmless when nothing runs
    }

    [Fact]
    public void Confirmation_lists_steps_donors_and_displaced_pieces()
    {
        var loaded = TestData.Load(TestData.PlanJson());
        var check = AccountCheck.Check(loaded.Plan, TestData.Export(TestData.After(1)), TestData.MadeFrom());
        var lines = Confirmation.Lines(loaded, check, RunMode.Real, "fake");
        Assert.StartsWith("REAL RUN", lines[0]);
        Assert.Contains(lines, l => l.Contains("1. Helmet #100") && l.Contains("[already done: skipped]"));
        Assert.Contains(lines, l => l.Contains("2. Gloves #200: Brute (#2) → Aria (#1); #201 goes to the inventory"));
        Assert.Contains("  Brute (#2): Gloves #200, Boots #300", lines);
        Assert.True(Confirmation.Matches(loaded.Plan, "01234567"));
        Assert.True(Confirmation.Matches(loaded.Plan, loaded.Plan.PlanId));
        Assert.False(Confirmation.Matches(loaded.Plan, "0123"));
        Assert.False(Confirmation.Matches(loaded.Plan, null));
    }
}

public class CliTests
{
    private static async Task<(int Code, string Output)> Cli(string input, bool interactive, params string[] args)
    {
        var w = new StringWriter();
        var code = await App.Cli.RunAsync(args, w, new StringReader(input), interactive);
        return (code, w.ToString());
    }

    private static string[] Args(SampleFolder f, string command, params string[] more) =>
        [command, f.Plan, "--output", f.Output, "--pace", "0-0", .. more];

    [Fact]
    public async Task Validate_the_sample()
    {
        using var f = new SampleFolder();
        var (code, output) = await Cli("", false, Args(f, "validate"));
        Assert.Equal(App.Cli.Ok, code);
        Assert.Contains("FAKE EXECUTOR", output);
        Assert.Contains("CHECK ONLY", output);
        Assert.Contains("Confirmation code: sample-0", output);
    }

    [Fact]
    public async Task A_run_needs_the_confirmation_code()
    {
        using var f = new SampleFolder();
        Assert.Equal(App.Cli.NotConfirmed, (await Cli("", false, Args(f, "dry-run"))).Code);
        Assert.Equal(App.Cli.NotConfirmed, (await Cli("", false, Args(f, "run", "--confirm", "wrong"))).Code);
        Assert.Equal(App.Cli.NotConfirmed, (await Cli("no\n", true, Args(f, "run"))).Code);
        Assert.False(Directory.Exists(Path.Combine(f.Output, "logs")));
    }

    [Fact]
    public async Task Dry_run_and_run_with_confirmation()
    {
        using var f = new SampleFolder();
        var (dry, dryOut) = await Cli("", false, Args(f, "dry-run", "--confirm", "sample-0"));
        Assert.Equal(App.Cli.Ok, dry);
        Assert.Contains("Dry run done", dryOut);
        var (run, runOut) = await Cli("sample-0\n", true, Args(f, "run"));
        Assert.Equal(App.Cli.Ok, run);
        Assert.Contains("All steps done and verified.", runOut);
        Assert.Contains("Snapshot: ", runOut);
        Assert.Contains("Log: ", runOut);
    }

    [Fact]
    public async Task A_stale_plan_is_refused()
    {
        using var f = new SampleFolder();
        f.EditPlan(j => j["pieces"]![0]!["level"] = 9); // the plan thinks the helmet was +9
        var (code, output) = await Cli("", false, Args(f, "run", "--confirm", "sample-0"));
        Assert.Equal(App.Cli.Refused, code);
        Assert.Contains("ArtifactUpgraded", output);
    }

    [Fact]
    public async Task An_invalid_plan_file_is_refused()
    {
        using var f = new SampleFolder();
        f.EditPlan(j => j.Remove("schemaVersion"));
        var (code, output) = await Cli("", false, Args(f, "validate"));
        Assert.Equal(App.Cli.Refused, code);
        Assert.Contains("UnversionedPlan", output);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("run")]
    [InlineData("run", "a.json", "--pace", "9-1")]
    [InlineData("run", "a.json", "--nope")]
    public async Task Usage_errors(params string[] args) => Assert.Equal(App.Cli.Usage, (await Cli("", false, args)).Code);

    [Fact]
    public void Plan_in_raid_extractor_layout_finds_its_output_folder()
    {
        var p = AppPaths.ForPlan(@"D:\x\raid-extractor\output\plans\p.json", null, @"D:\out");
        Assert.Equal(@"D:\x\raid-extractor\output", p.ExtractorOutput);
        Assert.Equal(@"D:\y", AppPaths.ForPlan(@"D:\x\plans\p.json", @"D:\y", null).ExtractorOutput);
    }
}

public class MainViewModelTests
{
    private sealed class Dialogs(bool confirm) : IDialogs
    {
        public List<string> Confirmations { get; } = [];
        public string? OpenPlan(string initialFolder) => null;

        public Task<bool> ConfirmAsync(string title, string body, string startText)
        {
            Confirmations.Add(body);
            return Task.FromResult(confirm);
        }
    }

    private static MainViewModel Vm(Dialogs d, SampleFolder f) =>
        new(d, null) { FakeActionDelay = TimeSpan.Zero, Pacer = NoPacer.Instance, OutputOverride = f.Output };

    [Fact]
    public async Task Loading_the_sample_checks_it()
    {
        using var f = new SampleFolder();
        var vm = Vm(new Dialogs(true), f);
        await vm.LoadAsync(f.Plan);
        Assert.True(vm.CanStart);
        Assert.Equal(4, vm.Steps.Count);
        Assert.All(vm.Steps, s => Assert.Equal("pending", s.State));
        Assert.Equal("#9003 goes to the inventory", vm.Steps[2].Note);
        Assert.True(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dry_run_asks_first_and_changes_nothing()
    {
        using var f = new SampleFolder();
        var d = new Dialogs(true);
        var vm = Vm(d, f);
        await vm.LoadAsync(f.Plan);
        await vm.DryRunCommand.ExecuteAsync(null);
        Assert.StartsWith("DRY RUN", Assert.Single(d.Confirmations));
        Assert.All(vm.Steps, s => Assert.Equal("found", s.State));
        Assert.Contains(vm.Log, l => l.Contains("Dry run done"));
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task Cancelled_confirmation_starts_nothing()
    {
        using var f = new SampleFolder();
        var vm = Vm(new Dialogs(false), f);
        await vm.LoadAsync(f.Plan);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Not started.", vm.Status);
        Assert.Empty(vm.Log);
    }

    [Fact]
    public async Task Run_marks_every_step_done()
    {
        using var f = new SampleFolder();
        var vm = Vm(new Dialogs(true), f);
        await vm.LoadAsync(f.Plan);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.All(vm.Steps, s => Assert.Equal("done", s.State));
        Assert.False(vm.CanStart); // nothing left to do
        Assert.Contains(vm.Issues, i => i.Text.Contains("Every step is already done"));
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task A_plan_without_its_account_is_not_loaded()
    {
        using var f = new SampleFolder();
        File.Delete(Path.Combine(f.Extractor, "account.json"));
        var vm = Vm(new Dialogs(true), f);
        await vm.LoadAsync(f.Plan);
        Assert.False(vm.CanStart);
        Assert.Contains(vm.Issues, i => i.Text.Contains("No account.json"));
    }
}
