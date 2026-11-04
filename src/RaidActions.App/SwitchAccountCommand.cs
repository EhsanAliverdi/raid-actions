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
        Thread.Sleep(8000); // Plarium Play notices the game closed (it was 3 s; see SwitchInPlariumPlay)
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
            var (otherX, otherY) = (r.L + (int)(OtherX * scale), r.B - (int)(OtherFromBottom * scale));
            // Plarium Play still counts the game as running for a while after it closed (2026-11-04: both tries failed): its menu then shows "You cannot
            // log out ... while games are running" (a plain grey box) where the other account's avatar is. Opened, looked at, closed and tried again.
            for (var look = 0; look < 12; look++)
            {
                // A Plarium Play dialog over the window (2026-11-04: "Enjoying Raid? Rate", the window dimmed under it) takes every click: Esc closes it,
                // and the window is looked at again (the empty sidebar between Settings and the profile is light when nothing covers it).
                for (var esc = 0; esc < 3 && Dimmed(r.L + (int)(30 * scale), r.T + (r.B - r.T) * 2 / 3); esc++)
                {
                    output.WriteLine("Plarium Play has a dialog open: Esc.");
                    keybd_event(0x1B, 0, 0, IntPtr.Zero);
                    keybd_event(0x1B, 0, 2, IntPtr.Zero);
                    Thread.Sleep(1200);
                }
                if (Dimmed(r.L + (int)(30 * scale), r.T + (r.B - r.T) * 2 / 3))
                    return "Plarium Play has a dialog open that Esc does not close: the account was not switched.";
                Click(r.L + (int)(ProfileX * scale), r.B - (int)(ProfileFromBottom * scale));
                Thread.Sleep(1800);
                // The other account's avatar, found on the screen (2026-11-04: on the PC's own monitor at 100% the menu's rows are not where they were at
                // 150% over Remote Desktop, and the fixed spot missed between the avatar and "+ Add Account").
                if (FindAvatar(r.L, r.B, scale) is { } avatar)
                {
                    Click(avatar.X, avatar.Y);
                    output.WriteLine($"Plarium Play: the other account picked (its avatar at {avatar.X},{avatar.Y}).");
                    Thread.Sleep(5000);
                    return null;
                }
                if (!PlainBox(otherX, otherY, (int)(14 * scale)))
                    return "Plarium Play's menu is open, but no other account's avatar was found in it: the account was not switched.";
                output.WriteLine("Plarium Play still counts the game as running; trying again in 10 s...");
                keybd_event(0x1B, 0, 0, IntPtr.Zero); // Esc closes the menu
                keybd_event(0x1B, 0, 2, IntPtr.Zero);
                Thread.Sleep(10_000);
                SetForegroundWindow(h);
                Thread.Sleep(800);
            }
            return "Plarium Play kept saying a game is running (two minutes): the account was not switched.";
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>
    /// The first "Other Accounts" avatar in Plarium Play's open menu: in the menu's left column near the window's bottom, the lowest band of rows that
    /// are mostly coloured (an avatar is a filled circle; the rows of text around it are thin). Null when there is none.
    /// </summary>
    private static (int X, int Y)? FindAvatar(int left, int bottom, double scale)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            int x0 = left + (int)(26 * scale), x1 = left + (int)(96 * scale), step = Math.Max(2, (int)(2 * scale));
            bool Coloured(uint c)
            {
                int red = (int)(c & 0xFF), green = (int)((c >> 8) & 0xFF), blue = (int)((c >> 16) & 0xFF);
                return red + green + blue < 3 * 170 || Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 60;
            }
            var bands = new List<(int Top, int End, List<int> Xs)>();
            (int Top, int End, List<int> Xs)? band = null;
            for (var y = bottom - (int)(230 * scale); y <= bottom - (int)(20 * scale); y += step)
            {
                var xs = new List<int>();
                var cells = 0;
                for (var x = x0; x <= x1; x += step) { cells++; if (Coloured(GetPixel(dc, x, y))) xs.Add(x); }
                if (xs.Count * 100 >= cells * 35)
                {
                    band = band is { } b ? (b.Top, y, [.. b.Xs, .. xs]) : (y, y, xs);
                }
                else if (band is { } done)
                {
                    bands.Add(done);
                    band = null;
                }
            }
            if (band is { } last) bands.Add(last);
            var avatar = bands.LastOrDefault(b => b.End - b.Top >= 16 * scale && b.End - b.Top <= 60 * scale);
            if (avatar.Xs is null) return null;
            return ((int)avatar.Xs.Average(), (avatar.Top + avatar.End) / 2);
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>The pixel is dark: the light sidebar is under a dialog's dimming.</summary>
    private static bool Dimmed(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var c = GetPixel(dc, x, y);
            return (int)(c & 0xFF) + (int)((c >> 8) & 0xFF) + (int)((c >> 16) & 0xFF) < 3 * 150;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>The square around (x, y) is one flat light grey (the "cannot log out" box), not an avatar.</summary>
    private static bool PlainBox(int x, int y, int half)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var flat = 0;
            var total = 0;
            for (var dy = -half; dy <= half; dy += 4)
                for (var dx = -half; dx <= half; dx += 4)
                {
                    var c = GetPixel(dc, x + dx, y + dy);
                    int red = (int)(c & 0xFF), green = (int)((c >> 8) & 0xFF), blue = (int)((c >> 16) & 0xFF);
                    total++;
                    if (red > 220 && green > 220 && blue > 220 && Math.Abs(red - green) < 8 && Math.Abs(green - blue) < 8) flat++;
                }
            return flat * 10 >= total * 9;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
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
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
