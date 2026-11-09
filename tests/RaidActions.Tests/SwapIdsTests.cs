using RaidActions.App.Input;

namespace RaidActions.Tests;

/// <summary>The swap confirmation's ids (2026-11-09): storage pieces as planned; a piece another champion wears in the game's reversed order.</summary>
public class SwapIdsTests
{
    [Fact]
    public void A_stored_piece_needs_worn_then_to_equip_and_a_worn_piece_may_come_reversed()
    {
        Assert.True(NavigatingDriver.SwapIdsMatch(983, 2306, 2306, 983, fromChampion: false));
        Assert.False(NavigatingDriver.SwapIdsMatch(2306, 983, 2306, 983, fromChampion: false));
        Assert.True(NavigatingDriver.SwapIdsMatch(1026, 1702, 1026, 1702, fromChampion: true));
        Assert.True(NavigatingDriver.SwapIdsMatch(1026, null, 1026, null, fromChampion: true));
        Assert.False(NavigatingDriver.SwapIdsMatch(1026, 9999, 1026, 1702, fromChampion: true));
        Assert.False(NavigatingDriver.SwapIdsMatch(5555, 1702, 1026, 1702, fromChampion: true));
    }
}
