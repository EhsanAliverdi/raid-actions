using System.Runtime.InteropServices;
using RaidActions.Game.Window;

namespace RaidActions.App.Input;

/// <summary>The Win32 calls background input needs; faked in tests.</summary>
public interface IBackgroundNative
{
    /// <summary>The game window now (fresh rectangle), or null when it is gone.</summary>
    GameWindowInfo? ReadWindow(IntPtr handle);
    /// <summary>Milliseconds since the last input of the person (GetLastInputInfo).</summary>
    long IdleMs();
    long TickMs();
    void Sleep(int ms);
    bool GetCursor(out int x, out int y);
    bool SetCursor(int x, int y);
    /// <summary>PostMessage of a mouse message with client coordinates.</summary>
    void PostMouse(IntPtr hwnd, uint msg, uint wParam, int clientX, int clientY);
    /// <summary>PostMessage WM_MOUSEWHEEL: delta in 120ths, the point in screen coordinates.</summary>
    void PostWheel(IntPtr hwnd, int delta, int screenX, int screenY);
    void PostKey(IntPtr hwnd, int vk, bool up);
    /// <summary>ShowWindow(SW_SHOWNOACTIVATE): restores a minimized window without taking the focus.</summary>
    void ShowNoActivate(IntPtr hwnd);
}

/// <summary>
/// Input as messages posted to the game's window, found by the 2026-11-23 probe: keys posted work in the background; posted mouse buttons count
/// only when the REAL cursor is over the target (Unity's legacy input reads the position from the cursor). So a click is a short flick of the
/// cursor to the point, posted move/down/up, and the cursor back. No SetForegroundWindow, no SendInput.
/// Every method returns null when sent, else why not (nothing sent), or <see cref="BackgroundMode.FallbackSignal"/>.
/// </summary>
public sealed class BackgroundInput(IBackgroundNative n)
{
    public const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, MkLButton = 1;
    public const int StillMs = 1000, MaxWaitMs = 60_000;

    private long _lastFlickEndTick = long.MinValue;

    /// <summary>Waits until the person's input has been still for <paramref name="stillMs"/>; false when that did not come within <paramref name="maxWaitMs"/>.</summary>
    public bool WaitStill(int stillMs = StillMs, int maxWaitMs = MaxWaitMs)
    {
        var start = n.TickMs();
        while (true)
        {
            var idle = n.IdleMs();
            var now = n.TickMs();
            // The last input was our own flick (SetCursorPos may count as input): not the person's.
            if (_lastFlickEndTick != long.MinValue && Math.Abs(idle - (now - _lastFlickEndTick)) <= 120) idle = long.MaxValue;
            if (idle >= stillMs) return true;
            if (now - start >= maxWaitMs) return false;
            n.Sleep((int)Math.Clamp(stillMs - idle + 20, 20, 250));
        }
    }

    /// <summary>The window as it is now, restored without activating when minimized; or the reason / fallback signal.</summary>
    private (GameWindowInfo? W, string? Why) Ready(GameWindowInfo w)
    {
        var now = n.ReadWindow(w.Handle);
        if (now is null) return (null, "The game window is gone.");
        if (now.Minimized)
        {
            n.ShowNoActivate(w.Handle);
            n.Sleep(300);
            now = n.ReadWindow(w.Handle);
            if (now is null || now.Minimized) return (null, BackgroundMode.FallbackSignal);
        }
        return (now, null);
    }

    private static string? Inside(GameWindowInfo w, float x, float y) =>
        x < 0 || y < 0 || x >= w.Client.Width || y >= w.Client.Height ? $"({x:0},{y:0}) is outside the client area." : null;

    /// <summary>Waits for the still mouse, then re-reads the window right before the flick (the person may have moved it meanwhile).</summary>
    private (GameWindowInfo? W, string? Why) Prepare(GameWindowInfo w)
    {
        var (ready, why) = Ready(w);
        if (ready is null) return (null, why);
        if (!WaitStill()) return (null, "You kept using the mouse for a minute: no click sent (background clicks wait for the mouse to be still).");
        return Ready(ready);
    }

    public string? Click(GameWindowInfo w, float x, float y, Random random)
    {
        if (Inside(w, x, y) is { } outside) return outside;
        var (win, why) = Prepare(w);
        if (win is null) return why;
        if (Inside(win, x, y) is { } outside2) return outside2;
        var (cx, cy) = ((int)Math.Round(x), (int)Math.Round(y));
        return WithCursor(win.Client.X + cx, win.Client.Y + cy, () =>
        {
            n.PostMouse(w.Handle, WmMouseMove, 0, cx, cy);
            n.PostMouse(w.Handle, WmLButtonDown, MkLButton, cx, cy);
            n.Sleep(random.Next(70, 100));
            n.PostMouse(w.Handle, WmLButtonUp, 0, cx, cy);
        });
    }

    public string? Drag(GameWindowInfo w, float x1, float y1, float x2, float y2, int steps = 18)
    {
        if (Inside(w, x1, y1) is { } o1) return o1;
        if (Inside(w, x2, y2) is { } o2) return o2;
        var (win, why) = Prepare(w);
        if (win is null) return why;
        int ax = (int)Math.Round(x1), ay = (int)Math.Round(y1);
        return WithCursor(win.Client.X + ax, win.Client.Y + ay, () =>
        {
            n.PostMouse(w.Handle, WmMouseMove, 0, ax, ay);
            n.PostMouse(w.Handle, WmLButtonDown, MkLButton, ax, ay);
            n.Sleep(60);
            int lx = ax, ly = ay;
            for (var i = 1; i <= steps; i++)
            {
                lx = (int)Math.Round(x1 + (x2 - x1) * i / steps);
                ly = (int)Math.Round(y1 + (y2 - y1) * i / steps);
                n.SetCursor(win.Client.X + lx, win.Client.Y + ly); // Unity reads the position from the real cursor
                n.PostMouse(w.Handle, WmMouseMove, MkLButton, lx, ly);
                n.Sleep(16);
            }
            n.Sleep(80);
            n.PostMouse(w.Handle, WmLButtonUp, 0, lx, ly);
        });
    }

    public string? Wheel(GameWindowInfo w, float x, float y, int notches, bool single = false, int gapMs = 35)
    {
        if (Inside(w, x, y) is { } outside) return outside;
        var (win, why) = Prepare(w);
        if (win is null) return why;
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        int sx = win.Client.X + cx, sy = win.Client.Y + cy;
        return WithCursor(sx, sy, () =>
        {
            n.PostMouse(w.Handle, WmMouseMove, 0, cx, cy);
            if (single) { n.PostWheel(w.Handle, Math.Clamp(notches, -250, 250) * 120, sx, sy); return; }
            for (var i = 0; i < Math.Abs(notches); i++)
            {
                n.PostWheel(w.Handle, Math.Sign(notches) * 120, sx, sy);
                n.Sleep(gapMs);
            }
        });
    }

    /// <summary>Esc posted as a key: no cursor, so no still-mouse wait.</summary>
    public string? Escape(GameWindowInfo w)
    {
        var (win, why) = Ready(w);
        if (win is null) return why;
        n.PostKey(w.Handle, 0x1B, up: false);
        n.Sleep(60);
        n.PostKey(w.Handle, 0x1B, up: true);
        return null;
    }

    /// <summary>The cursor flick: remember the cursor, move it to the screen point, run the posted messages, put it back (always).</summary>
    private string? WithCursor(int sx, int sy, Action send)
    {
        var hadOld = n.GetCursor(out var ox, out var oy);
        if (!n.SetCursor(sx, sy)) return BackgroundMode.FallbackSignal; // the cursor cannot be moved here (a session that is away): the normal way explains it
        try
        {
            n.Sleep(40);
            send();
            n.Sleep(40);
        }
        finally
        {
            if (hadOld) n.SetCursor(ox, oy);
            _lastFlickEndTick = n.TickMs();
        }
        return null;
    }
}

/// <summary>The real Win32 calls.</summary>
public sealed class Win32Background : IBackgroundNative
{
    public static readonly Win32Background Instance = new();
    private const uint WmMouseWheel = 0x020A, WmKeyDown = 0x0100, WmKeyUp = 0x0101;

    public GameWindowInfo? ReadWindow(IntPtr handle) =>
        GameWindow.Find().Window is { } w && w.Handle == handle ? w : null;

    public long IdleMs()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) : long.MaxValue;
    }

    public long TickMs() => Environment.TickCount64;
    public void Sleep(int ms) => Thread.Sleep(ms);

    public bool GetCursor(out int x, out int y)
    {
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels, like the client rect
        try { var ok = GetCursorPos(out var p); (x, y) = (p.X, p.Y); return ok; }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    public bool SetCursor(int x, int y)
    {
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return SetCursorPos(x, y) && GetCursorPos(out var at) && at.X == x && at.Y == y; }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    public void PostMouse(IntPtr hwnd, uint msg, uint wParam, int cx, int cy) =>
        PostMessage(hwnd, msg, (UIntPtr)wParam, (IntPtr)(((cy & 0xFFFF) << 16) | (cx & 0xFFFF)));

    public void PostWheel(IntPtr hwnd, int delta, int sx, int sy) =>
        PostMessage(hwnd, WmMouseWheel, (UIntPtr)(uint)((delta & 0xFFFF) << 16), (IntPtr)(((sy & 0xFFFF) << 16) | (sx & 0xFFFF)));

    public void PostKey(IntPtr hwnd, int vk, bool up)
    {
        var scan = MapVirtualKey((uint)vk, 0);
        var l = (IntPtr)(1 | (int)(scan << 16) | (up ? (1 << 30) | unchecked((int)(1u << 31)) : 0));
        PostMessage(hwnd, up ? WmKeyUp : WmKeyDown, (UIntPtr)(uint)vk, l);
    }

    public void ShowNoActivate(IntPtr hwnd) => ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
