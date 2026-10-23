using RaidActions.Execution;
using RaidActions.Verification;

namespace RaidActions.Tests;

/// <summary>Plan-to-actions conversion.</summary>
public class ActionPlannerTests
{
    private static ActionScript Build(RunMode mode, params int[] doneSteps)
    {
        var plan = TestData.Plan();
        var check = AccountCheck.Check(plan, TestData.Export(TestData.After(doneSteps)), TestData.MadeFrom());
        return ActionPlanner.Build(plan, check, mode);
    }

    [Fact]
    public void Real_run_equips_each_step_in_plan_order_and_verifies_after_each()
    {
        var s = Build(RunMode.Real);
        Assert.Equal([100, 200, 300], s.Actions.Where(a => a.Kind == ActionKind.Equip).Select(a => a.ArtifactId!.Value));
        Assert.Equal([2, 3], s.Actions.Where(a => a.Kind == ActionKind.ConfirmRemoveFromDonor).Select(a => a.Step)); // from Brute, not from the inventory
        Assert.Equal([[1], [1, 2], [1, 2, 3]], s.Actions.Where(a => a.Kind == ActionKind.VerifyStep).Select(a => a.DoneThrough!.ToArray()));
        Assert.Equal(ActionKind.VerifyEnd, s.Actions[^1].Kind);
    }

    [Fact]
    public void Snapshot_and_idle_check_come_before_any_input()
    {
        var s = Build(RunMode.Real);
        Assert.Equal([ActionKind.SaveSnapshot, ActionKind.CheckGameIdle, ActionKind.FocusGame], s.Actions.Take(3).Select(a => a.Kind));
    }

    [Fact]
    public void Every_equip_is_preceded_by_a_selection_check_and_followed_by_a_step_check()
    {
        var a = Build(RunMode.Real).Actions;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Kind != ActionKind.Equip) continue;
            Assert.Equal(ActionKind.VerifySelection, a[i - 1].Kind);
            Assert.Equal(a[i].Step, a[i - 1].Step);
            var next = a.Skip(i + 1).First(x => x.Kind is ActionKind.VerifyStep or ActionKind.Equip);
            Assert.Equal(ActionKind.VerifyStep, next.Kind);
            Assert.Equal(a[i].Step, next.Step);
        }
    }

    [Fact]
    public void Dry_run_never_changes_gear()
    {
        var s = Build(RunMode.DryRun);
        Assert.DoesNotContain(s.Actions, a => a.ChangesGear);
        Assert.Equal([1, 2, 3], s.Actions.Where(a => a.Kind == ActionKind.Highlight).Select(a => a.Step));
        Assert.All(s.Actions.Where(a => a.Kind == ActionKind.VerifyStep), a => Assert.Empty(a.DoneThrough!)); // nothing may change
        Assert.DoesNotContain(s.Actions, a => a.Kind == ActionKind.VerifyEnd);
    }

    [Fact]
    public void Done_steps_get_no_input()
    {
        var s = Build(RunMode.Real, 1);
        Assert.Equal([1], s.SkippedDone);
        Assert.DoesNotContain(s.Actions, a => a.Step == 1);
        Assert.Equal([[1, 2], [1, 2, 3]], s.Actions.Where(a => a.Kind == ActionKind.VerifyStep).Select(a => a.DoneThrough!.ToArray()));
    }

    [Fact]
    public void A_refused_plan_gets_no_actions()
    {
        var plan = TestData.Plan();
        var check = AccountCheck.Check(plan, TestData.Export(version: "12.0.0"), TestData.MadeFrom());
        var e = Assert.Throws<InvalidOperationException>(() => ActionPlanner.Build(plan, check, RunMode.DryRun));
        Assert.Contains("GameVersionMismatch", e.Message);
    }

    [Fact]
    public void A_finished_plan_gets_no_actions()
    {
        var plan = TestData.Plan();
        var check = AccountCheck.Check(plan, TestData.Export(TestData.After(1, 2, 3)), TestData.MadeFrom());
        Assert.Throws<InvalidOperationException>(() => ActionPlanner.Build(plan, check, RunMode.Real));
    }
}
