using RaidActions.Autoplay;
using RaidExtractor.Autoplay;

namespace RaidActions.Tests;

public class CryptKeyPlanTests
{
    // Stage costs 1,1,1,2,2,3 ; stages 1-4 beaten.
    private static CryptPlan Crypt(int highest = 4) =>
        new("X", "X", 0, highest, 6, [1], ["a"], "", [], [], [1, 1, 1, 2, 2, 3]);

    [Fact]
    public void Multiplier_steps_down_x3_x2_off()
    {
        Assert.Equal(3, CryptKeyPlan.Next(Crypt(), 6, 4, false, 3)!.Multiplier);   // 2 x 3
        Assert.Equal(2, CryptKeyPlan.Next(Crypt(), 5, 4, false, 3)!.Multiplier);
        Assert.Equal(1, CryptKeyPlan.Next(Crypt(), 3, 4, false, 3)!.Multiplier);
        Assert.Equal(2, CryptKeyPlan.Next(Crypt(), 6, 4, false, 2)!.Multiplier);   // max 2
    }

    [Fact]
    public void Unpayable_single_battle_plays_cheaper_beaten_stage_or_nothing()
    {
        var r = CryptKeyPlan.Next(Crypt(), 1, 4, false, 3)!;
        Assert.Equal((3, 1, true), (r.Stage, r.Multiplier, r.Cheaper));
        Assert.Null(CryptKeyPlan.Next(Crypt(), 0, 4, false, 3));
    }

    [Fact]
    public void Push_stage_is_a_single_battle()
    {
        var r = CryptKeyPlan.Next(Crypt(), 20, 5, true, 3)!;
        Assert.Equal((5, 1), (r.Stage, r.Multiplier));
    }

    [Fact]
    public void Stage_not_beaten_is_never_super_raid()   // the box is locked
    {
        Assert.Equal(1, CryptKeyPlan.Next(Crypt(), 20, 5, false, 3)!.Multiplier);
    }

    [Fact]
    public void Replay_only_when_keys_cover_the_next_battle()
    {
        Assert.False(CryptKeyPlan.MayReplay(3, 2, 2));
        Assert.True(CryptKeyPlan.MayReplay(4, 2, 2));
        Assert.False(CryptKeyPlan.MayReplay(1, 2, 1));
    }
}
