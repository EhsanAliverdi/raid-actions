using System.Text.Json;
using System.Text.Json.Nodes;
using RaidActions.Plans;
using RaidExtractor.Battle;

namespace RaidActions.Tests;

public class PlanReaderTests
{
    private static PlanReader.Result Read(JsonObject j) => PlanReader.Read(j.ToJsonString());

    [Fact]
    public void Reads_a_v1_plan()
    {
        var r = Read(TestData.PlanJson());
        Assert.True(r.Ok, string.Join("\n", r.Issues));
        var p = r.Plan!.Plan;
        Assert.Equal(1, p.SchemaVersion);
        Assert.Equal("11.85.5", p.GameVersion);
        Assert.Equal("20261023-120000.json.gz", p.SnapshotId);
        Assert.Equal("Arena", p.LocksName);
        Assert.Equal(3, p.Steps.Count);
        Assert.Null(p.Steps[0].FromChampionId);
        Assert.Equal(2, p.Steps[1].FromChampionId);
        Assert.Equal(6, p.Pieces.Count);
        Assert.Null(p.Pieces.Single(x => x.ArtifactId == 201).ExpectedOwner);
        Assert.Matches("^[0-9a-f]{64}$", r.Plan.Sha256);
    }

    [Fact]
    public void Refuses_a_plan_without_schemaVersion()
    {
        // The plan files raid-extractor wrote before v1 (output/plans/swap-plan.json) have no version and no piece state.
        var j = TestData.PlanJson();
        j.Remove("schemaVersion");
        var r = Read(j);
        Assert.False(r.Ok);
        Assert.Equal(IssueCode.UnversionedPlan, Assert.Single(r.Issues).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Refuses_an_unsupported_schema_version(int version)
    {
        var j = TestData.PlanJson();
        j["schemaVersion"] = version;
        Assert.Equal(IssueCode.UnsupportedSchema, Assert.Single(Read(j).Issues).Code);
    }

    [Fact]
    public void Names_the_missing_field()
    {
        var j = TestData.PlanJson();
        j.Steps()[1]!.AsObject().Remove("slot");
        var r = Read(j);
        Assert.False(r.Ok);
        var i = Assert.Single(r.Issues);
        Assert.Equal(IssueCode.MissingField, i.Code);
        Assert.Contains("steps[1].slot", i.Message);
    }

    [Fact]
    public void A_null_fromChampionId_is_the_inventory_but_a_missing_one_is_an_error()
    {
        var j = TestData.PlanJson();
        j.Steps()[0]!.AsObject().Remove("fromChampionId");
        Assert.Contains(Read(j).Issues, i => i.Code == IssueCode.MissingField && i.Message.Contains("steps[0].fromChampionId"));
    }

    [Fact]
    public void Names_the_field_with_the_wrong_type()
    {
        var j = TestData.PlanJson();
        j.Pieces()[0]!["level"] = "12";
        var i = Assert.Single(Read(j).Issues);
        Assert.Equal(IssueCode.WrongType, i.Code);
        Assert.Contains("pieces[0].level", i.Message);
    }

    [Fact]
    public void Refuses_invalid_json_and_bad_dates()
    {
        Assert.Equal(IssueCode.InvalidJson, Assert.Single(PlanReader.Read("{ \"schemaVersion\": 1,").Issues).Code);
        var j = TestData.PlanJson();
        j["exportedAt"] = "yesterday";
        Assert.Equal(IssueCode.BadValue, Assert.Single(Read(j).Issues).Code);
    }

    [Fact]
    public void Ticks_are_only_a_warning()
    {
        var j = TestData.PlanJson();
        j["ticked"] = new JsonArray(1, 2);
        var r = Read(j);
        Assert.True(r.Ok);
        Assert.Equal(IssueCode.TicksIgnored, Assert.Single(r.Issues).Code);
    }

    [Fact]
    public void The_planners_warnings_are_read_and_shown_red_first()
    {
        var j = TestData.PlanJson();
        j["warnings"] = new JsonArray(
            new JsonObject { ["championId"] = 2, ["champion"] = "Brute", ["severity"] = "amber", ["text"] = "Brute: SPD 180 → 176. Used in Arena.", ["stats"] = new JsonArray() },
            new JsonObject { ["championId"] = 3, ["champion"] = "Cora", ["severity"] = "red", ["text"] = "Cora: SPD 225 → 208. Used in Dragon's Lair. This may break them." });
        var r = Read(j);
        Assert.True(r.Ok, string.Join(Environment.NewLine, r.Issues));
        Assert.Equal(["red", "amber"], r.Plan!.Plan.WarningsRedFirst.Select(w => w.Severity));
        var check = RaidActions.Verification.AccountCheck.Check(r.Plan.Plan, TestData.Export(), TestData.MadeFrom());
        var lines = RaidActions.Execution.Confirmation.Lines(r.Plan, check, RaidActions.Execution.RunMode.DryRun, "fake");
        var i = lines.FindIndex(l => l.StartsWith("The planner warns", StringComparison.Ordinal));
        Assert.True(i > 0);
        Assert.StartsWith("  RED   Cora", lines[i + 1]);
        Assert.StartsWith("  AMBER Brute", lines[i + 2]);
    }

    [Fact]
    public void A_warning_with_an_unknown_severity_is_refused()
    {
        var j = TestData.PlanJson();
        j["warnings"] = new JsonArray(new JsonObject { ["championId"] = 2, ["champion"] = "Brute", ["severity"] = "purple", ["text"] = "?" });
        Assert.Contains(Read(j).Issues, i => i.Code == IssueCode.BadValue && i.Message.Contains("severity"));
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        var r = PlanReader.ReadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Equal(IssueCode.FileUnreadable, Assert.Single(r.Issues).Code);
    }

    [Fact]
    public void Reads_what_raid_extractor_writes()
    {
        // raid-extractor's own SwapPlan record, serialized the way it saves plan files: if its format changes, this test says so.
        var plan = new SwapPlan(SwapPlan.Version, "abc", "From the planner", "2026-10-23T12:05:00+11:00", "11.85.5", TestData.PlanExportedAt,
            "20261023-120000.json.gz", "Nightmare", "Force", 1, 2, 28, 35,
            [new SwapStep(1, 100, "Helmet", "Helmet: test", null, "the inventory", 1, "Aria", 1)],
            [new RaidExtractor.Battle.PlanPiece(100, "Helmet", null, 12, 5, 0, 1)], null, [], null, []);
        var r = PlanReader.Read(JsonSerializer.Serialize(plan, SwapPlan.Json));
        Assert.True(r.Ok, string.Join("\n", r.Issues));
        Assert.Equal(new PlanStep(1, 100, "Helmet", "Helmet: test", null, "the inventory", 1, "Aria", 1), r.Plan!.Plan.Steps.Single());
        Assert.Empty(Plans.PlanValidator.Validate(r.Plan.Plan));
    }
}
