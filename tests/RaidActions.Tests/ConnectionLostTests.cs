using RaidActions.App.Play;

namespace RaidActions.Tests;

public class ConnectionLostTests
{
    [Theory]
    [InlineData("CONNECTION ERROR Server connection failed. Check your internet connection and try again.", true)]
    [InlineData("Connection lost", true)]
    [InlineData("Unfinished battle Do you want to continue the battle?", false)]
    [InlineData("You have logged into this account from another device", false)]
    public void The_connection_error_box_is_told_from_the_others(string text, bool lost) =>
        Assert.Equal(lost, LivePlayGame.ConnectionLost(text));
}
