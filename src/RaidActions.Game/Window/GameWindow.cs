using System.Diagnostics;
using System.Text;

namespace RaidActions.Game.Window;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public override string ToString() => $"{Width}x{Height} at ({X},{Y})";
}

/// <param name="Client">The client area (what the game draws), in physical screen pixels.</param>
/// <param name="Dpi">The window's DPI (96 = 100%, 144 = 150%, 192 = 200%).</param>
/// <param name="Cloaked">Hidden by the desktop window manager (e.g. on another virtual desktop).</param>
public sealed record GameWindowInfo(IntPtr Handle, int ProcessId, string Title, string ClassName, PixelRect Window, PixelRect Client,
    uint Dpi, bool Visible, bool Minimized, bool Foreground, bool Cloaked)
{
    public double Scale => Dpi / 96.0;

    /// <summary>Why a capture or input could not trust this window now, or null when it can.</summary>
    public string? NotUsable =>
        Minimized ? "The game window is minimized."
        : !Visible ? "The game window is hidden."
        : Cloaked ? "The game window is cloaked (another virtual desktop?)."
        : Client.Width < 200 || Client.Height < 200 ? $"The game window's client area is too small ({Client})."
        : null;

    public override string ToString() =>
        $"\"{Title}\" ({ClassName}) PID {ProcessId}, client {Client}, DPI {Dpi} ({Scale:P0}){(Minimized ? ", minimized" : "")}{(Foreground ? ", foreground" : ", not foreground")}";
}

/// <summary>Finds Raid's game window. Never changes it (no focus, move or resize).</summary>
public static class GameWindow
{
    public const string ProcessName = "Raid";
    public const string UnityWindowClass = "UnityWndClass";

    /// <summary>The game window, or the reason there is none (not running, several processes, no window yet).</summary>
    public static (GameWindowInfo? Window, string? Problem) Find()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0) return (null, "Raid.exe is not running.");
        if (processes.Length > 1) return (null, $"Found {processes.Length} Raid.exe processes; expected one.");
        var pid = (uint)processes[0].Id;

        var candidates = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var p);
            if (p == pid && ClassOf(h) == UnityWindowClass) candidates.Add(h);
            return true;
        }, IntPtr.Zero);
        if (candidates.Count == 0) return (null, "Raid.exe has no game window yet (still starting?).");
        var visible = candidates.Where(Native.IsWindowVisible).ToList();
        if (visible.Count > 1) return (null, $"Raid.exe has {visible.Count} visible game windows; expected one.");
        return (Describe(visible.Count == 1 ? visible[0] : candidates[0], (int)pid), null);
    }

    public static GameWindowInfo Describe(IntPtr h, int pid) => Native.PhysicalPixels(() =>
    {
        Native.GetWindowRect(h, out var wr);
        Native.GetClientRect(h, out var cr);
        var origin = new Native.POINT();
        Native.ClientToScreen(h, ref origin);
        var cloaked = Native.DwmGetWindowAttribute(h, Native.DwmwaCloaked, out var c, sizeof(int)) == 0 && c != 0;
        var title = new StringBuilder(256);
        Native.GetWindowText(h, title, title.Capacity);
        return new GameWindowInfo(h, pid, title.ToString(), ClassOf(h),
            new PixelRect(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top),
            new PixelRect(origin.X, origin.Y, cr.Right - cr.Left, cr.Bottom - cr.Top),
            Native.GetDpiForWindow(h), Native.IsWindowVisible(h), Native.IsIconic(h), Native.GetForegroundWindow() == h, cloaked);
    });

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
