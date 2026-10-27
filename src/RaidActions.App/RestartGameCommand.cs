using System.Diagnostics;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// restart-game [--wait 300]: for a stuck game. Closes Raid (its window's close first, then the process after 30 s), starts it again
/// through Plarium Play as its desktop shortcut does, and waits until memory shows the village (or the wait runs out). No click in the
/// game; offers shown after the start are left for the next run's go-home to close. Never used during a battle.
/// </summary>
public static class RestartGameCommand
{
    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output)
    {
        var waitAt = Array.IndexOf(args, "--wait");
        var wait = TimeSpan.FromSeconds(waitAt >= 0 && waitAt + 1 < args.Length && int.TryParse(args[waitAt + 1], out var s) && s > 0 ? s : 300);
        var plarium = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlariumPlay", "PlariumPlay.exe");
        if (!File.Exists(plarium)) { output.WriteLine($"Plarium Play not found at {plarium}."); return Cli.Failed; }

        // Not in the middle of a battle (a stuck game in battle is left for you).
        var (game, _) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (game is not null)
            using (game)
                try
                {
                    var (state, _) = game.ReadUi();
                    if (state?.BattleRunning == true) { output.WriteLine("A battle is running: not restarting the game."); return Cli.Refused; }
                }
                catch (UiReadException) { }   // unreadable UI is one of the ways a game is stuck

        foreach (var p in Process.GetProcessesByName("Raid"))
        {
            output.WriteLine($"Closing Raid (PID {p.Id})...");
            p.CloseMainWindow();
            if (!p.WaitForExit(30_000)) { output.WriteLine("Raid did not close in 30 s: ending the process."); p.Kill(); p.WaitForExit(10_000); }
        }
        output.WriteLine("Starting Raid through Plarium Play...");
        Process.Start(new ProcessStartInfo(plarium, "-gameid=101 -tray-start") { UseShellExecute = true });

        var until = DateTime.Now + wait;
        string last = "Raid not running yet";
        while (DateTime.Now < until)
        {
            await Task.Delay(10_000);
            var (g, problem) = LiveGame.Open(GameCommands.LivePaths(outDir));
            if (g is null) { last = problem ?? last; continue; }
            using (g)
                try
                {
                    var (state, _) = g.ReadUi();
                    last = $"top view {state?.Top?.Name ?? "none"}";
                    if (state is not null && (Input.NavigatingDriver.AtVillage(state) || state.Top?.Name is { } top && top.Contains("Offer", StringComparison.Ordinal)))
                    {
                        output.WriteLine($"Raid is back ({last}).");
                        return Cli.Ok;
                    }
                }
                catch (UiReadException e) { last = e.Message; }
        }
        output.WriteLine($"Raid did not reach the village within {wait.TotalSeconds:0} s ({last}).");
        return Cli.Failed;
    }
}
