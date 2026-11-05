using RaidActions.App;

namespace RaidActions.Tests;

public class PlariumPopupTests
{
    [Theory]
    [InlineData("Close", null, true)]
    [InlineData("×", null, true)]
    [InlineData("Maybe later", null, true)]
    [InlineData("Not now", null, true)]
    [InlineData("Got it", null, true)]
    [InlineData("", "close-button", true)]
    [InlineData("Play now", null, false)]
    [InlineData("Buy AUD 22.99", null, false)]
    [InlineData("Get it", null, false)]
    [InlineData("Install", null, false)]
    [InlineData("Learn more", null, false)]
    [InlineData("Rate", null, false)]
    [InlineData("", "", false)]
    [InlineData("", "buy-close", false)]
    public void Only_a_close_or_later_button_is_pressed(string name, string? id, bool press) =>
        Assert.Equal(press, PlariumPopup.IsDismiss(name, id));

    [Fact]
    public void The_nameless_button_at_a_dialogs_top_right_is_its_close_button()
    {
        var digest = (588.0, 50.0, 1152.0, 900.0); // 2026-11-05: the "RAID Digest" dialog, its × at 1684,66 40x40
        Assert.True(PlariumPopup.CornerClose(digest, (1684, 66, 40, 40)));
        Assert.False(PlariumPopup.CornerClose(digest, (660, 2182, 104, 80))); // a play button far below
        Assert.False(PlariumPopup.CornerClose(digest, (600, 66, 40, 40)));    // top left
        Assert.False(PlariumPopup.CornerClose(digest, (1684, 600, 40, 40)));  // right, but low
        Assert.False(PlariumPopup.CornerClose(digest, (1500, 60, 230, 50)));  // a wide button
    }

    [Theory]
    [InlineData("sc-1az45dy-0 jwqFrW", true)]                    // the Digest's ×
    [InlineData("sc-1az45dy-0 jwqFrW sc-m09c0s-1 kYbXdR", true)] // the Rate box's ×
    [InlineData("sc-m09c0s-6 byGjFm", false)]                    // a rating star
    [InlineData("sc-1az45dy-01 x", false)]
    [InlineData(null, false)]
    public void Plarium_Plays_close_component_is_known_by_its_class(string? className, bool close) =>
        Assert.Equal(close, PlariumPopup.IsCloseComponent(className));
}
