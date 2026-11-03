using System.Diagnostics;
using System.Runtime.InteropServices;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// switch-account &lt;player id&gt;: the game on another account (raid-extractor plan/multi-account.md). Plarium Play switches only while Raid is closed, and
/// keeps every account signed in once (its "Add Account"), so no password is typed: Raid closed, Plarium Play's profile menu, the other account's avatar,
/// Raid started again, the player's id read back. Two accounts: the menu shows one other account, whichever it is. A wrong account after the start is
/// tried once more, then refused.
/// </summary>
public static class SwitchAccountCommand
{
    // Plarium Play's menu, measured at 150% (2026-11-04) in its own logical pixels, from its window's left and bottom: the profile button, and the first of
    // "Other Accounts" (the menu grows upwards from the bottom).
    private const int ProfileX = 95, ProfileFromBottom = 42, OtherX = 49, OtherFromBottom = 92;

    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output)
    {
        if (args.Length < 2 || !long.TryParse(args[1], out var want)) { output.WriteLine("Usage: switch-account <player id>"); return Cli.Usage; }
        if (InGame(outDir) == want) { output.WriteLine($"The game is on account {want} already."); return Cli.Ok; }
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            CloseRaid(output);
            if (SwitchInPlariumPlay(output) is { } problem) { output.WriteLine(problem); return Cli.Failed; }
            if (!await RestartGameCommand.StartAndWaitAsync(outDir, output, TimeSpan.FromMinutes(5))) return Cli.Failed;
            var now = InGame(outDir);
            output.WriteLine($"The game is on account {now?.ToString() ?? "(not read)"}.");
            if (now == want) return Cli.Ok;
        }
        output.WriteLine($"Account {want} was not reached.");
        return Cli.Failed;
    }

    /// <summary>The player's id of the account in the game; null when the game is not running or not readable.</summary>
    public static long? InGame(string outDir)
    {
        var (game, _) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (game is null) return null;
        using (game) return game.UserId();
    }

    private static void CloseRaid(TextWriter output)
    {
        foreach (var p in Process.GetProcessesByName("Raid"))
        {
            output.WriteLine($"Closing Raid (PID {p.Id}) to switch accounts...");
            p.CloseMainWindow();
            if (!p.WaitForExit(30_000)) { p.Kill(); p.WaitForExit(10_000); }
        }
        Thread.Sleep(3000);
    }

    private static string? SwitchInPlariumPlay(TextWriter output)
    {
        var pp = Process.GetProcessesByName("PlariumPlay").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (pp is null) return "Plarium Play's window is not open.";
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // physical pixels, like the window's rectangle
        try
        {
            var h = pp.MainWindowHandle;
            if (IsIconic(h)) ShowWindow(h, 9);
            SetForegroundWindow(h);
            Thread.Sleep(1200);
            if (!GetWindowRect(h, out var r)) return "Plarium Play's window could not be measured.";
            var scale = GetDpiForWindow(h) / 96.0;
            Click(r.L + (int)(ProfileX * scale), r.B - (int)(ProfileFromBottom * scale));
            Thread.Sleep(1800);
            Click(r.L + (int)(OtherX * scale), r.B - (int)(OtherFromBottom * scale));
            output.WriteLine("Plarium Play: the other account picked.");
            Thread.Sleep(5000);
            return null;
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(200);
        mouse_event(2, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(80);
        mouse_event(4, 0, 0, 0, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
