using RaidActions.Autoplay;

namespace RaidActions.Tests;

/// <summary>The village's red dots and the job each one brings forward (Ehsan 2026-11-16).</summary>
public class RedDotsTests
{
    [Theory]
    [InlineData("InboxButton", null, "inbox")]
    [InlineData("ShopButton", null, "free")]
    [InlineData("Indicator", "BankIndicator_h", "free")]      // the Shop is the game's Bank
    [InlineData("AllianceButton", null, "checkin")]
    [InlineData("BattlePass_h", null, "pass")]
    public void A_dot_brings_its_job_forward(string button, string? dot, string job) => Assert.Equal(job, RedDots.For(button, dot).Job);

    [Fact]
    public void An_unknown_dot_is_a_warning_and_a_known_one_is_left_by_rule()
    {
        Assert.Equal(new RedDotAction(null, null), RedDots.For("Image", "Indicator_h"));
        Assert.NotNull(RedDots.For("QuestButton").Ignore);
    }
}
