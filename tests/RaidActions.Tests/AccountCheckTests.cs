using RaidActions.Plans;
using RaidActions.Verification;
using RaidExtractor;

namespace RaidActions.Tests;

/// <summary>Stale-plan refusal: the plan against a fresh read of a fake account.</summary>
public class AccountCheckTests
{
    private static CheckReport Check(SavedExport fresh, SavedExport? madeFrom = null, Plan? plan = null) =>
        AccountCheck.Check(plan ?? TestData.Plan(), fresh, madeFrom ?? TestData.MadeFrom());

    private static List<ArtifactOut> With(int id, Func<ArtifactOut, ArtifactOut> change) =>
        TestData.Artifacts().Select(a => a.Id == id ? change(a) : a).ToList();

    private static void Refused(CheckReport r, IssueCode code, int? step = null)
    {
        Assert.False(r.CanStart);
        Assert.Contains(r.Issues, i => i.Code == code && i.Severity == Severity.Error && (step is null || i.Step == step));
    }

    [Fact]
    public void An_unchanged_account_can_start()
    {
        var r = Check(TestData.Export());
        Assert.True(r.CanStart, string.Join("\n", r.Issues));
        Assert.Empty(r.Issues);
        Assert.All(r.Steps, s => Assert.Equal(StepState.Pending, s.State));
        Assert.Equal(201, r.Steps[1].Displaced); // Aria's Gloves go to the inventory
        Assert.Null(r.Steps[0].Displaced);
    }

    [Fact]
    public void Different_game_version() => Refused(Check(TestData.Export(version: "11.90.0")), IssueCode.GameVersionMismatch);

    [Fact]
    public void Account_read_older_than_the_plan() => Refused(Check(TestData.Export(exportedAt: "2026-10-23T11:00:00+11:00")), IssueCode.AccountOlderThanPlan);

    [Fact]
    public void Sold_piece() => Refused(Check(TestData.Export(TestData.Artifacts().Where(a => a.Id != 300))), IssueCode.ArtifactGone, 3);

    [Fact]
    public void Sold_piece_mentions_a_partial_read()
    {
        var r = Check(TestData.Export(TestData.Artifacts().Where(a => a.Id != 300), skippedArtifacts: ["#300: bad pointer"]));
        Refused(r, IssueCode.ArtifactGone, 3);
        Assert.Contains(r.Issues, i => i.Code == IssueCode.PartialRead);
        Assert.Contains("skipped", r.Issues.First(i => i.Code == IssueCode.ArtifactGone).Message);
    }

    [Fact]
    public void Upgraded_piece() => Refused(Check(TestData.Export(With(200, a => a with { Level = 16 }))), IssueCode.ArtifactUpgraded, 2);

    [Fact]
    public void Ascended_piece() => Refused(Check(TestData.Export(With(202, a => a with { AscendLevel = 1 }))), IssueCode.ArtifactUpgraded);

    [Fact]
    public void Rerolled_piece_same_level() =>
        Refused(Check(TestData.Export(With(100, a => a with { Substats = [TestData.Stat("atk", 0.05)] }))), IssueCode.ArtifactRerolled, 1);

    [Fact]
    public void Glyph_added() =>
        Refused(Check(TestData.Export(With(300, a => a with { Substats = [TestData.Stat("spd", 5, true, glyph: 2, rolls: 1), TestData.Stat("acc", 10, true)] }))),
            IssueCode.ArtifactRerolled, 3);

    [Fact]
    public void Without_the_plans_snapshot_it_refuses() =>
        Refused(AccountCheck.Check(TestData.Plan(), TestData.Export(), madeFrom: null), IssueCode.SnapshotMissing);

    [Fact]
    public void Fingerprints_pass_when_nothing_changed() =>
        Assert.True(Check(TestData.Export(), plan: TestData.Plan(TestData.AddFingerprints)).CanStart);

    [Fact]
    public void The_snapshot_is_required_even_with_fingerprints() =>
        Refused(AccountCheck.Check(TestData.Plan(TestData.AddFingerprints), TestData.Export(), madeFrom: null), IssueCode.SnapshotMissing);

    [Fact]
    public void A_fingerprint_catches_a_reroll()
    {
        // The snapshot shows the same reroll, so only the fingerprint can catch it.
        var rerolled = With(200, a => a with { Substats = [TestData.Stat("spd", 5, true, rolls: 2), TestData.Stat("acc", 10, true)] });
        var r = Check(TestData.Export(rerolled), TestData.Export(rerolled, exportedAt: TestData.PlanExportedAt), TestData.Plan(TestData.AddFingerprints));
        Refused(r, IssueCode.ArtifactRerolled, 2);
        Assert.Contains("fingerprint", r.Issues.Single(i => i.Code == IssueCode.ArtifactRerolled).Message);
    }

    [Fact]
    public void An_upgrade_is_reported_once_even_with_a_fingerprint()
    {
        var r = Check(TestData.Export(With(200, a => a with { Level = 16 })), plan: TestData.Plan(TestData.AddFingerprints));
        Refused(r, IssueCode.ArtifactUpgraded, 2);
        Assert.DoesNotContain(r.Issues, i => i.Code == IssueCode.ArtifactRerolled);
    }

    [Fact]
    public void The_fingerprint_formula_matches_SCHEMA_md()
    {
        // Recomputed from the text in raid-extractor's SCHEMA.md → Plan files → pieces[].fingerprint, not from its code.
        var a = TestData.Art(200, "Gloves", 2) with { Substats = [TestData.Stat("spd", 5, true, glyph: 1, rolls: 2), TestData.Stat("critRate", 0.15, false)] };
        static string S(RaidExtractor.StatOut x) => $"{x.Stat},{(x.Absolute ? 1 : 0)},{x.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{x.Glyph.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{x.Rolls}";
        var text = string.Join("|", new[] { a.Slot, $"{a.SetKindId}", a.Rarity, $"{a.Rank}", $"{a.Level}", $"{a.AscendLevel}", a.Faction ?? "", S(a.Primary) }
            .Concat(a.Substats.Select(S)).Append(a.AscendBonus is null ? "-" : S(a.AscendBonus)));
        Assert.Equal("Gloves|4|Epic|5|12|0||hp,0,0.33,0,0|spd,1,5,1,2|critRate,0,0.15,0,0|-", text);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(expected, RaidExtractor.Battle.PlanPiece.FingerprintOf(a));
    }

    [Fact]
    public void A_snapshot_from_another_export_is_refused() =>
        Refused(Check(TestData.Export(), madeFrom: TestData.Export(exportedAt: "2026-10-22T10:00:00+11:00")), IssueCode.SnapshotMissing);

    [Fact]
    public void Donor_piece_now_on_someone_else() => Refused(Check(TestData.Export(With(300, a => a with { OwnerHeroId = 1 }))), IssueCode.PieceMoved, 3);

    [Fact]
    public void Inventory_piece_now_worn() => Refused(Check(TestData.Export(With(100, a => a with { OwnerHeroId = 3 }))), IssueCode.PieceMoved, 1);

    [Fact]
    public void Recipient_wears_a_piece_the_plan_does_not_know()
    {
        var arts = TestData.Artifacts().Select(a => a.Id == 201 ? a with { OwnerHeroId = null } : a).Append(TestData.Art(555, "Gloves", 1)).ToList();
        var r = Check(TestData.Export(arts));
        Refused(r, IssueCode.PieceMoved, 2);
        Assert.Contains(r.Issues, i => i.Message.Contains("#555"));
    }

    [Fact]
    public void Champion_gone() => Refused(Check(TestData.Export(champions: TestData.Champions().Where(c => c.Id != 3))), IssueCode.ChampionGone);

    [Fact]
    public void Champion_in_the_vault() =>
        Refused(Check(TestData.Export(champions: TestData.Champions().Select(c => c.Id == 2 ? c with { InVault = true } : c))), IssueCode.ChampionInVault);

    [Fact]
    public void Accessory_of_another_faction()
    {
        // A faction accessory moved to a champion of another faction.
        var plan = TestData.Plan(j =>
        {
            j.Steps().Add(TestData.Step(4, 400, "Ring", 3, "Cora", 1, "Aria"));
            j.Pieces()[5]!["expectedOwner"] = 1;
        });
        var champs = TestData.Champions().Select(c => c.Id == 1 ? c with { Faction = "DarkElves" } : c).ToList();
        Refused(Check(TestData.Export(champions: champs), TestData.Export(champions: champs, exportedAt: TestData.PlanExportedAt), plan), IssueCode.FactionMismatch, 4);
    }

    [Fact]
    public void Steps_already_done_are_skipped_not_refused()
    {
        var r = Check(TestData.Export(TestData.After(1)));
        Assert.True(r.CanStart, string.Join("\n", r.Issues));
        Assert.Equal([StepState.Done, StepState.Pending, StepState.Pending], r.Steps.Select(s => s.State));
    }

    [Fact]
    public void Steps_done_out_of_order_by_hand_are_fine_when_everything_else_is_in_place()
    {
        var r = Check(TestData.Export(TestData.After(3)));
        Assert.True(r.CanStart, string.Join("\n", r.Issues));
        Assert.Equal(StepState.Done, r.Steps[2].State);
    }

    [Fact]
    public void A_finished_plan_has_nothing_to_do()
    {
        var r = Check(TestData.Export(TestData.After(1, 2, 3)));
        Assert.False(r.CanStart);
        Assert.False(r.Issues.HasErrors());
        Assert.Equal(IssueCode.NothingToDo, Assert.Single(r.Issues).Code);
    }

    [Fact]
    public void After_a_step_the_owners_agree_only_with_that_step_done()
    {
        var plan = TestData.Plan();
        var after2 = TestData.Export(TestData.After(1, 2));
        Assert.Empty(AccountCheck.OwnersAgree(plan, after2, new HashSet<int> { 1, 2 }));
        var stale = AccountCheck.OwnersAgree(plan, after2, new HashSet<int> { 1 });
        Assert.Contains(stale, i => i.Code == IssueCode.PieceMoved && i.Message.Contains("#200"));
        Assert.Contains(stale, i => i.Code == IssueCode.PieceMoved && i.Message.Contains("#201")); // displaced to the inventory
    }

    [Fact]
    public void A_structurally_bad_plan_is_refused_before_the_account_is_looked_at()
    {
        var r = Check(TestData.Export(), plan: TestData.Plan(j => j.Steps()[0]!["expectedOwnerAfter"] = 2));
        Refused(r, IssueCode.ExpectedOwnerMismatch);
        Assert.Empty(r.Steps);
    }
}
