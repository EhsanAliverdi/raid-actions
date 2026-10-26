using System.Text.Json.Nodes;
using RaidActions.Execution;
using RaidActions.Plans;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Tests;

/// <summary>Running chosen steps only, and the undo of executed steps (no game).</summary>
public class RealStepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raid-actions-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Running_one_step_has_one_equip_and_no_end_check()
    {
        var plan = TestData.Plan();
        var check = AccountCheck.Check(plan, TestData.Export(), TestData.MadeFrom());
        var one = ActionPlanner.Build(plan, check, RunMode.Real, new HashSet<int> { 1 });
        Assert.Equal([1], one.Actions.Where(a => a.Kind == ActionKind.Equip).Select(a => a.Step));
        Assert.DoesNotContain(one.Actions, a => a.Step is 2 or 3);
        Assert.DoesNotContain(one.Actions, a => a.Kind == ActionKind.VerifyEnd);
        Assert.Equal([1], one.Actions.Single(a => a.Kind == ActionKind.VerifyStep).DoneThrough!);
        Assert.Contains(ActionPlanner.Build(plan, check, RunMode.Real, new HashSet<int> { 1, 2, 3 }).Actions, a => a.Kind == ActionKind.VerifyEnd);
    }

    /// <summary>The test plan with Aria wearing Helmet #101, which step 1 replaces (so its undo is: equip #101 again).</summary>
    private static (Plan Plan, List<ArtifactOut> Before) WithReplacedHelmet()
    {
        var plan = TestData.Plan(j => j.Pieces().Add(TestData.Piece(101, "Helmet", 1, null)));
        var before = TestData.Artifacts().Append(TestData.Art(101, "Helmet", 1)).ToList();
        return (plan, before);
    }

    [Fact]
    public void The_undo_of_an_inventory_step_equips_the_replaced_piece_again()
    {
        var (plan, before) = WithReplacedHelmet();
        var check = AccountCheck.Check(plan, TestData.Export(before), TestData.Export(before, exportedAt: TestData.PlanExportedAt));
        Assert.True(check.CanStart, string.Join(Environment.NewLine, check.Issues));
        Assert.Equal(101, check.Steps[0].Displaced);

        // After step 1: #100 on Aria, #101 in the inventory.
        var after = TestData.Export(before.Select(a => a.Id == 100 ? a with { OwnerHeroId = 1 } : a.Id == 101 ? a with { OwnerHeroId = null } : a),
            exportedAt: "2026-10-23T14:00:00+11:00");
        var undo = UndoPlanner.Write(plan, check, [1], after, _dir, new DateTimeOffset(2026, 10, 23, 14, 1, 0, TimeSpan.FromHours(11)));
        Assert.Null(undo.Problem);
        var u = Assert.Single(undo.Steps);
        Assert.Equal((101, "Helmet", (int?)null, 1), (u.ArtifactId, u.Slot, u.FromChampionId, u.ToChampionId));

        // The undo is a valid plan that matches the account after the step, with the read it was made from beside it.
        var read = PlanReader.ReadFile(undo.PlanPath!);
        Assert.True(read.Ok, string.Join(Environment.NewLine, read.Issues));
        Assert.Empty(PlanValidator.Validate(read.Plan!.Plan));
        var madeFrom = History.Load(Path.Combine(_dir, "history", read.Plan.Plan.SnapshotId!));
        var undoCheck = AccountCheck.Check(read.Plan.Plan, after, madeFrom);
        Assert.True(undoCheck.CanStart, string.Join(Environment.NewLine, undoCheck.Issues));
        Assert.Equal(100, undoCheck.Steps[0].Displaced);                      // the step's piece goes back to the inventory
        Assert.Null(read.Plan.Plan.Pieces.Single(p => p.ArtifactId == 100).ExpectedOwner);
        Assert.Equal(1, read.Plan.Plan.Pieces.Single(p => p.ArtifactId == 101).ExpectedOwner);
    }

    [Fact]
    public void Steps_that_cannot_be_undone_with_equip_alone_are_refused()
    {
        var plan = TestData.Plan();
        var check = AccountCheck.Check(plan, TestData.Export(), TestData.MadeFrom());
        var empty = UndoPlanner.Write(plan, check, [1], TestData.Export(TestData.After(1)), _dir, DateTimeOffset.Now);
        Assert.Contains("would need Remove", empty.Problem);                   // Aria had no helmet
        var donor = UndoPlanner.Write(plan, check, [2], TestData.Export(TestData.After(2)), _dir, DateTimeOffset.Now);
        Assert.Contains("from Brute", donor.Problem);
    }

    [Fact]
    public void The_swap_confirmation_expects_the_planned_worn_piece()
    {
        var (plan, _) = WithReplacedHelmet();
        Assert.Equal(101, App.Input.NavigatingDriver.PlannedWorn(plan, plan.Steps.Single(s => s.Number == 1)));
        Assert.Null(App.Input.NavigatingDriver.PlannedWorn(TestData.Plan(), TestData.Plan().Steps.Single(s => s.Number == 1))); // Aria had no helmet
    }

    [Theory]
    [InlineData("Replace artifact", "The artifact currently equipped will be moved to the inventory.", null)]
    [InlineData("Replace artifact", "<color=#FFAA00>Swap</color> the artifacts?", null)]
    [InlineData("Replace artifact", "Removing it costs Silver.", "cost")]
    [InlineData("Replace artifact", "Takes 25000 to swap.", "2")]
    [InlineData("Replace artifact", "Spend gems?", "Spend")]
    [InlineData("Replace artifact", null, "could not be read")]
    [InlineData("", "Swap?", "could not be read")]
    public void The_swap_confirmation_is_refused_when_its_text_names_a_cost(string? title, string? warning, string? problem)
    {
        var why = App.Input.NavigatingDriver.SwapTextProblem(title, warning);
        if (problem is null) Assert.Null(why);
        else Assert.Contains(problem, why);
    }

    [Fact]
    public void Continue_is_never_on_the_allow_list()
    {
        Assert.Contains("swap-cancel", App.Input.NavigatingDriver.AllowedClicks);
        Assert.DoesNotContain("swap-continue", App.Input.NavigatingDriver.AllowedClicks);
        Assert.DoesNotContain("popup-equip", App.Input.NavigatingDriver.AllowedClicks);
    }

    [Theory]
    [InlineData("run", "p.json", "--live")]
    [InlineData("run", "p.json", "--live", "--navigate")]
    [InlineData("run", "p.json", "--live", "--steps", "1")]
    [InlineData("validate", "p.json", "--steps", "1")]
    public async Task A_live_run_must_name_navigate_and_its_steps(params string[] args)
    {
        var w = new StringWriter();
        Assert.Equal(App.Cli.Usage, await App.Cli.RunAsync(args, w, new StringReader(""), false));
    }
}
