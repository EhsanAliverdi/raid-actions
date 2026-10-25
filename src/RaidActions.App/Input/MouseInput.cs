using System.Runtime.InteropServices;
using RaidActions.Game.Window;

namespace RaidActions.App.Input;

/// <summary>
/// The only code that sends input to the game: a left click or a mouse-wheel turn at a point of Raid's client area. Before every
/// event: the game is in front (brought there only when it is not), the point is inside the client area, and the window under the
/// point is Raid's own. Otherwise nothing is sent and the reason is returned.
/// </summary>
public static class MouseInput
{
    /// <summary>Brings the game to the front if it is not; the reason it could not, or null.</summary>
    public static string? EnsureForeground(GameWindowInfo w)
    {
        if (GetForegroundWindow() == w.Handle) return null;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(w.Handle);
            SetForegroundWindow(w.Handle);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
        Thread.Sleep(250);
        return GetForegroundWindow() == w.Handle ? null : "Could not bring the game window to the front.";
    }

    /// <summary>A left click at client point (x, y). Returns null when sent, else why not (nothing sent).</summary>
    public static string? Click(GameWindowInfo w, float x, float y, Random random)
    {
        if (Prepare(w, x, y) is { } problem) return problem;
        mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(random.Next(45, 95));
        mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
        return null;
    }

    /// <summary>Mouse-wheel notches at client point (x, y): negative scrolls down (towards later items).</summary>
    public static string? Wheel(GameWindowInfo w, float x, float y, int notches)
    {
        if (Prepare(w, x, y) is { } problem) return problem;
        mouse_event(WheelFlag, 0, 0, notches * 120, IntPtr.Zero);
        return null;
    }

    private static string? Prepare(GameWindowInfo w, float x, float y)
    {
        if (x < 0 || y < 0 || x >= w.Client.Width || y >= w.Client.Height) return $"({x:0},{y:0}) is outside the client area.";
        if (EnsureForeground(w) is { } fg) return fg;
        var sx = w.Client.X + (int)Math.Round(x);
        var sy = w.Client.Y + (int)Math.Round(y);
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels, like the client rect
        try
        {
            var under = GetAncestor(WindowFromPoint(new POINT { X = sx, Y = sy }), 2 /* GA_ROOT */);
            if (under != w.Handle) return $"The window under ({sx},{sy}) is not the game.";
            if (!SetCursorPos(sx, sy)) return "Could not move the cursor.";
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
        Thread.Sleep(60);
        return null;
    }

    private const uint LeftDown = 0x0002, LeftUp = 0x0004, WheelFlag = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
