using System.Runtime.InteropServices;
using RaidActions.Execution;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// go-home: from whatever screen is open, back to the village with back/close elements read from memory (top view first, one at a
/// time, read again after each), then Champions. No Equip, no other click. --dry: only says what it would click. --watch N: N times,
/// waits for the screen to change (someone opened another screen and left the mouse and keyboard alone for 10 s), then goes home.
/// </summary>
public static class GoHomeCommand
{
    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output, Action<Action>? abortHook)
    {
        var dry = args.Contains("--dry");
        var watchAt = Array.IndexOf(args, "--watch");
        var rounds = watchAt >= 0 && watchAt + 1 < args.Length && int.TryParse(args[watchAt + 1], out var n) && n > 0 ? n : watchAt >= 0 ? -1 : 1;
        if (rounds < 0)
        {
            output.WriteLine("go-home [--dry] [--watch <n>] [--output <dir>]");
            return Cli.Usage;
        }

        var (game, problem) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (game is null)
        {
            output.WriteLine(problem);
            return Cli.Failed;
        }
        using (game)
        {
            UnityUi ui;
            try { ui = game.Ui(UiCommands.NativeLayoutPath); }
            catch (UiReadException e)
            {
                output.WriteLine(e.Message);
                return Cli.Failed;
            }
            var folder = Path.Combine(outDir, "go-home", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            var logFile = Path.Combine(folder, "go-home.log");
            void Log(string line)
            {
                var l = $"{DateTime.Now:HH:mm:ss} {line}";
                output.WriteLine(l);
                File.AppendAllText(logFile, l + Environment.NewLine);
            }
            var policy = Game.Window.WindowPolicy.Load(GameCommands.WindowConfig);
            var driver = new Input.NavigatingDriver(game, ui, null, folder, policy, Log);
            using var abort = new CancellationTokenSource();
            abortHook?.Invoke(() => { try { abort.Cancel(); } catch (ObjectDisposedException) { } });
            var action = new UiAction(ActionKind.OpenChampion, 0, "go home");

            if (dry)
            {
                Log(await driver.WouldGoHome(abort.Token));
                return Cli.Ok;
            }

            Log($"go home: back/close only ({string.Join(", ", Input.NavigatingDriver.BackKinds)}), then Champions. Kill switch: {KillSwitch.Keys}. Log: {logFile}");
            var results = new List<string>();
            var failed = false;
            try
            {
                for (var round = 1; round <= rounds; round++)
                {
                    if (watchAt >= 0)
                    {
                        Log($"round {round}/{rounds}: waiting for another screen to be opened (then 10 s without mouse or keyboard input)");
                        await WaitForNewScreen(game, abort.Token);
                    }
                    var (s, _) = game.ReadUi();
                    var from = Describe(s);
                    Log($"round {round}: from {from}");
                    try
                    {
                        var left = await driver.GoHome(action, abort.Token);
                        await driver.OpenChampions(action, abort.Token);
                        var line = $"OK    from {from}: {(left.Count == 0 ? "already at the village" : string.Join(" → ", left))} → village → Champions";
                        results.Add(line);
                        Log(line);
                    }
                    catch (Input.NavigatingDriver.StopException e)
                    {
                        results.Add($"STOP  from {from}: {e.Message}");
                        Log("stopped: " + e.Message);
                        failed = true;
                    }
                    if (driver.RestoreWindow() is { } restored) Log(restored);
                    if (failed && watchAt < 0) break; // watching: the next screen is tried after a stop too
                }
            }
            catch (Input.NavigatingDriver.StopException e)
            {
                results.Add("STOP  " + e.Message);
                Log("stopped: " + e.Message);
                failed = true;
            }
            catch (OperationCanceledException)
            {
                Log("aborted");
                if (driver.RestoreWindow() is { } restored) Log(restored);
                return Cli.Aborted;
            }
            output.WriteLine();
            foreach (var r in results) output.WriteLine(r);
            var clicks = Path.Combine(folder, "clicks.txt");
            File.WriteAllLines(clicks, driver.Clicks.Select(c => $"{c.What,-40} {c.Rect,-24} {c.Overlay}"));
            output.WriteLine($"Clicks: {clicks}");
            return failed ? Cli.Failed : Cli.Ok;
        }
    }

    private static string Describe(UiState? s) =>
        s is null ? "an unreadable screen"
        : s.Top?.Name == UiState.ChampionScreenView && s.ChampionScreen?.Champion is { } c ? $"{s.Top.Name} (champion #{c})"
        : s.Top?.Name ?? "no view";

    /// <summary>Until the top view (or the champion shown) differs from now, has stayed so for 3 s, and nobody has used the mouse or keyboard for 10 s.</summary>
    private static async Task WaitForNewScreen(LiveGame game, CancellationToken ct)
    {
        var start = Describe(game.ReadUi().State);
        string? seen = null;
        var since = DateTime.Now;
        var until = DateTime.Now + TimeSpan.FromMinutes(20);
        while (DateTime.Now < until)
        {
            await Task.Delay(1000, ct);
            var (s, _) = game.ReadUi();
            if (s is null || s.InBattle) { seen = null; continue; }
            var now = Describe(s);
            if (now != seen) { seen = now; since = DateTime.Now; }
            if (now != start && DateTime.Now - since >= TimeSpan.FromSeconds(3) && IdleFor() >= TimeSpan.FromSeconds(10)) return;
        }
        throw new Input.NavigatingDriver.StopException("No other screen was opened within 20 minutes.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }
}
