using System.Text.Json.Nodes;
using RaidActions.Plans;

namespace RaidActions.Tests;

public class PlanValidatorTests
{
    private static List<PlanIssue> Validate(Action<JsonObject> change) => PlanValidator.Validate(TestData.Plan(change));

    private static void Refused(IssueCode code, Action<JsonObject> change, int? step = null)
    {
        var issues = Validate(change);
        Assert.True(issues.HasErrors());
        Assert.Contains(issues, i => i.Code == code && i.Severity == Severity.Error && (step is null || i.Step == step));
    }

    [Fact]
    public void A_good_plan_has_no_issues() => Assert.Empty(PlanValidator.Validate(TestData.Plan()));

    [Fact]
    public void No_steps() => Refused(IssueCode.NoSteps, j => j["steps"] = new JsonArray());

    [Fact]
    public void Steps_out_of_order() => Refused(IssueCode.StepNumbering, j => j.Steps()[1]!["number"] = 3);

    [Fact]
    public void Unknown_slot() => Refused(IssueCode.UnknownSlot, j => { j.Steps()[0]!["slot"] = "Hat"; j.Pieces()[0]!["slot"] = "Hat"; }, 1);

    [Fact]
    public void Same_champion_on_both_sides() => Refused(IssueCode.SameChampion, j => j.Steps()[2]!["toChampionId"] = 2, 3);

    [Fact]
    public void A_piece_moved_twice() => Refused(IssueCode.PieceMovedTwice, j => j.Steps().Add(TestData.Step(4, 300, "Boots", 3, "Cora", 1, "Aria")), 4);

    [Fact]
    public void A_slot_filled_twice() => Refused(IssueCode.SlotFilledTwice, j =>
    {
        j.Steps().Add(TestData.Step(4, 999, "Helmet", null, "the inventory", 1, "Aria"));
        j.Pieces().Add(TestData.Piece(999, "Helmet", null, 1));
    }, 4);

    [Fact]
    public void Taking_from_a_slot_an_earlier_step_refilled() => Refused(IssueCode.DonorSlotReplacedEarlier, j =>
    {
        // Step 2 puts #200 into Aria's Gloves; a step 4 that then takes Aria's old Gloves #201 "from Aria" is wrong: #201 is in the inventory by then.
        j.Steps().Add(TestData.Step(4, 201, "Gloves", 1, "Aria", 3, "Cora"));
        j.Pieces()[2]!["expectedOwner"] = 3;
    }, 4);

    [Fact]
    public void ExpectedOwnerAfter_must_be_the_recipient() => Refused(IssueCode.ExpectedOwnerMismatch, j => j.Steps()[0]!["expectedOwnerAfter"] = 2, 1);

    [Fact]
    public void Every_moved_piece_must_be_in_pieces() => Refused(IssueCode.PieceNotListed, j => j.Pieces().RemoveAt(0), 1);

    [Fact]
    public void The_step_and_pieces_must_agree_on_the_owner() => Refused(IssueCode.PieceOwnerMismatch, j => j.Pieces()[1]!["owner"] = 3, 2);

    [Fact]
    public void The_recorded_end_state_must_follow_from_the_steps()
    {
        // Aria's old Gloves #201 go to the inventory in step 2; a plan that says they stay on Aria is inconsistent.
        var issues = Validate(j => j.Pieces()[2]!["expectedOwner"] = 1);
        var i = Assert.Single(issues);
        Assert.Equal(IssueCode.PieceOwnerMismatch, i.Code);
        Assert.Contains("#201", i.Message);
    }
}
