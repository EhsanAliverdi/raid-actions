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
    /// <summary>When this process last sent input (a click, a wheel turn, the keystroke that brings the game to the front): Auto Play
    /// tells the person's input from its own by it.</summary>
    public static DateTime LastSentUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Brings the game to the front if it is not; the reason it could not, or null.</summary>
    public static string? EnsureForeground(GameWindowInfo w)
    {
        if (GetForegroundWindow() == w.Handle) return null;
        if (SessionProblem() is { } session) return session;
        // Each way in turn until Windows confirms the game is in front; a second round after a short pause (another window can take the
        // front for a moment, a notification). Still not in front: refuse.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            foreach (var way in Ways)
            {
                var ok = way(w.Handle);
                LastSentUtc = DateTime.UtcNow;
                if (ok) return null;
            }
            Thread.Sleep(500);
        }
        return "Could not bring the game window to the front.";
    }

    /// <summary>
    /// Why no window can come to the front (nothing can receive input): the session is disconnected (a closed Remote Desktop) or locked.
    /// Null when the session has the input desktop.
    /// </summary>
    public static string? SessionProblem()
    {
        if (WTSQuerySessionInformation(IntPtr.Zero, -1 /* this session */, 8 /* WTSConnectState */, out var buffer, out _))
        {
            var state = Marshal.ReadInt32(buffer);
            WTSFreeMemory(buffer);
            if (state != 0 /* WTSActive */)
                return "This Windows session is not active (a disconnected Remote Desktop session, state " + state + "): no window can be "
                    + "brought to the front and no input reaches the game. Reconnect, or hand the session to the console (tscon) instead of disconnecting.";
        }
        var desk = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
        if (desk == IntPtr.Zero) return "The screen is locked (the input desktop is not this one): no input reaches the game. Unlock the PC.";
        CloseDesktop(desk);
        return null;
    }

    // The ways to the front, in order: Windows lets a process take the front after it sends input, so an Alt tap comes first; then joining
    // the input of the window in front; then minimizing and restoring the game.
    private static readonly Func<IntPtr, bool>[] Ways = [AfterAltTap, Attached, MinimizeRestore];

    private static bool Confirmed(IntPtr game)
    {
        Thread.Sleep(250);
        return GetForegroundWindow() == game;
    }

    private static bool AfterAltTap(IntPtr game)
    {
        keybd_event(VkMenu, 0, 0, IntPtr.Zero);
        keybd_event(VkMenu, 0, KeyUp, IntPtr.Zero);
        SetForegroundWindow(game);
        return Confirmed(game);
    }

    private static bool Attached(IntPtr game)
    {
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(game);
            SetForegroundWindow(game);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
        return Confirmed(game);
    }

    private static bool MinimizeRestore(IntPtr game)
    {
        ShowWindow(game, 6 /* SW_MINIMIZE */);
        Thread.Sleep(150);
        ShowWindow(game, 9 /* SW_RESTORE */);
        SetForegroundWindow(game);
        return Confirmed(game);
    }

    /// <summary>A left click at client point (x, y). Returns null when sent, else why not (nothing sent).</summary>
    public static string? Click(GameWindowInfo w, float x, float y, Random random)
    {
        if (Prepare(w, x, y) is { } problem) return problem;
        mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(random.Next(45, 95));
        mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
        LastSentUtc = DateTime.UtcNow;
        return null;
    }

    /// <summary>
    /// Mouse-wheel notches at client point (x, y): negative scrolls down (towards later items). Sent as one event per notch: the game's
    /// scroll lists move one step per wheel event, whatever the event's delta.
    /// </summary>
    /// <param name="single">One wheel event carrying all the notches (one big turn, as a fast flick of the wheel) instead of one
    /// event per notch.</param>
    /// <param name="gapMs">Pause between two notches (35 ms: a hand turning the wheel; less for a long turn).</param>
    public static string? Wheel(GameWindowInfo w, float x, float y, int notches, bool single = false, int gapMs = 35)
    {
        if (Prepare(w, x, y) is { } problem) return problem;
        if (single)
        {
            mouse_event(WheelFlag, 0, 0, Math.Clamp(notches, -250, 250) * 120, IntPtr.Zero);
            LastSentUtc = DateTime.UtcNow;
            return null;
        }
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            mouse_event(WheelFlag, 0, 0, Math.Sign(notches) * 120, IntPtr.Zero);
            Thread.Sleep(gapMs);
            LastSentUtc = DateTime.UtcNow;
        }
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
            // A point off every screen (a window wider than a narrow Remote Desktop screen) would be clicked at the screen's edge instead.
            var (vx, vy, vw, vh) = (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            if (sx < vx || sy < vy || sx >= vx + vw || sy >= vy + vh)
                return $"({sx},{sy}) is off the screen ({vw}x{vh} at ({vx},{vy})): the game window does not fit the screen. Use a screen at least as large as the window (Remote Desktop in landscape, or a larger resolution).";
            if (under != w.Handle) return $"The window under ({sx},{sy}) is not the game.";
            var moved = SetCursorPos(sx, sy);
            LastSentUtc = DateTime.UtcNow;
            if (!moved || !GetCursorPos(out var at) || at.X != sx || at.Y != sy) return "Could not move the cursor to the point.";
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
        Thread.Sleep(60);
        return null;
    }

    private const uint LeftDown = 0x0002, LeftUp = 0x0004, WheelFlag = 0x0800, KeyUp = 0x0002;
    private const byte VkMenu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("wtsapi32.dll")] private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
