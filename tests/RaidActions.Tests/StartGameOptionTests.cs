using RaidActions.App;

namespace RaidActions.Tests;

/// <summary>--start-game on the gear run (2026-11-09: the app's Apply failed at once with "Raid.exe is not running"): live runs only.</summary>
public class StartGameOptionTests
{
    [Fact]
    public void A_live_run_takes_start_game_and_an_offline_one_refuses_it()
    {
        var o = Cli.Parse(["run", "plan.json", "--live", "--navigate", "--steps", "1", "--confirm", "abcd1234", "--start-game"], TextWriter.Null)!;
        Assert.True(o.StartGame);
        Assert.False(Cli.Parse(["dry-run", "plan.json", "--live", "--navigate"], TextWriter.Null)!.StartGame);
        Assert.Throws<ArgumentException>(() => Cli.Parse(["dry-run", "plan.json", "--start-game"], TextWriter.Null));
        Assert.Equal(163404481, Cli.Parse(["run", "plan.json", "--live", "--navigate", "--steps", "1", "--start-game", "--player", "163404481"], TextWriter.Null)!.Player);
        Assert.Throws<ArgumentException>(() => Cli.Parse(["run", "plan.json", "--live", "--navigate", "--steps", "1", "--player", "163404481"], TextWriter.Null));
    }
}
