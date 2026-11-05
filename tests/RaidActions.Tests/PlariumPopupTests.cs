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
}
