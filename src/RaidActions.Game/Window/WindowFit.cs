using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RaidActions.Game.Window;

/// <summary>
/// The game window sizes the actioner accepts (config/window.json). Any size works: click targets come from memory rectangles,
/// which scale with the window. Refused only when minimized or smaller than the minimum client area; the minimum keeps the
/// smallest click target (a champion tile or picker item) at least <see cref="TargetPx"/> pixels and is never below the floor.
/// </summary>
public sealed record WindowPolicy(int MinWidth, int MinHeight, int FloorWidth, int FloorHeight, int TargetPx, string How, string Path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static WindowPolicy Load(string path)
    {
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var min = o["minimumClient"]!;
        var floor = o["floorClient"]!;
        return new WindowPolicy((int)min["width"]!, (int)min["height"]!, (int)floor["width"]!, (int)floor["height"]!, (int)o["targetPx"]!,
            (string?)o["how"] ?? "", path);
    }

    /// <summary>Writes a new minimum (and how it was worked out) to the config file.</summary>
    public WindowPolicy SaveMinimum(int width, int height, string how)
    {
        var o = JsonNode.Parse(File.ReadAllText(Path))!.AsObject();
        o["minimumClient"] = new JsonObject { ["width"] = width, ["height"] = height };
        o["how"] = how;
        File.WriteAllText(Path, o.ToJsonString(Json) + "\n");
        return this with { MinWidth = width, MinHeight = height, How = how };
    }

    /// <summary>Why a click may not use this window now (minimized, hidden, smaller than the minimum), or null.</summary>
    public string? Problem(GameWindowInfo w) =>
        w.Minimized ? "The game window is minimized."
        : !w.Visible ? "The game window is hidden."
        : w.Cloaked ? "The game window is cloaked (another virtual desktop?)."
        : w.Client.Width < MinWidth || w.Client.Height < MinHeight
            ? $"The game window's client area is {w.Client.Width}x{w.Client.Height}, below the minimum {MinWidth}x{MinHeight}."
            : null;

    /// <summary>Why a capture of this window is not usable (minimized, hidden, tiny), or null: captures work at any size.</summary>
    public static string? CaptureProblem(GameWindowInfo w) => w.NotUsable;

    /// <summary>
    /// The smallest client area at which the smallest click target is <paramref name="targetPx"/> pixels, at the window's aspect
    /// ratio (the game's UI scales with the client area), never below the floor. <paramref name="smallestSide"/> is the smallest
    /// side of the measured targets at <paramref name="width"/>x<paramref name="height"/>.
    /// </summary>
    public static (int Width, int Height) ComputeMinimum(int width, int height, float smallestSide, int targetPx, int floorWidth, int floorHeight)
    {
        if (smallestSide <= 0 || width <= 0 || height <= 0) return (floorWidth, floorHeight);
        // One factor, the largest of: the target size, the floor's width, the floor's height; applied once (one rounding).
        var f = Math.Max(targetPx / (double)smallestSide, Math.Max(floorWidth / (double)width, floorHeight / (double)height));
        return ((int)Math.Ceiling(width * f - 1e-6), (int)Math.Ceiling(height * f - 1e-6));
    }

    public override string ToString() => $"minimum {MinWidth}x{MinHeight} (floor {FloorWidth}x{FloorHeight}, smallest target ≥ {TargetPx} px)";
}

/// <summary>The planned resize: the outer window rect that gives the wanted client area, at the window's own place (inside the work area).</summary>
public sealed record FitPlan(PixelRect Window, int ClientWidth, int ClientHeight, PixelRect WorkArea, bool Restore);

/// <summary>Where a window was, to put it back after a run: its normal rectangle and whether it was minimized.</summary>
public sealed record WindowPlacement(PixelRect Normal, bool Minimized, bool Maximized);

/// <summary>
/// Sizes the game window: SetWindowPos (no activation, no z-order change) on Raid's window only, after restoring it without
/// activation when minimized; and puts it back afterwards. No input is sent to the game.
/// </summary>
public static class WindowFit
{
    /// <summary>The resize that gives a <paramref name="width"/>×<paramref name="height"/> client area, the window staying where it is.</summary>
    public static FitPlan Plan(GameWindowInfo w, int width, int height) => Native.PhysicalPixels(() =>
    {
        var style = (uint)Native.GetWindowLongPtr(w.Handle, Native.GwlStyle).ToInt64();
        var exStyle = (uint)Native.GetWindowLongPtr(w.Handle, Native.GwlExStyle).ToInt64();
        var frame = new Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        Native.AdjustWindowRectExForDpi(ref frame, style, false, exStyle, w.Dpi == 0 ? 96 : w.Dpi);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromWindow(w.Handle, Native.MonitorDefaultToNearest), ref info);
        var work = new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        // Where the window is now (its normal rectangle, also while minimized): it stays there, only its size changes.
        var p = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(w.Handle, ref p);
        return Keep(work, p.rcNormalPosition.Left, p.rcNormalPosition.Top, frame.Right - frame.Left, frame.Bottom - frame.Top, width, height, w.Minimized);
    });

    /// <summary>
    /// The outer size at the window's own top-left corner: the window is not moved, only resized; it is pushed back inside the work
    /// area only as far as the bigger size needs (never above or left of it).
    /// </summary>
    public static FitPlan Keep(PixelRect work, int x, int y, int outerWidth, int outerHeight, int clientWidth, int clientHeight, bool restore)
    {
        var nx = Math.Max(work.X, Math.Min(x, work.X + work.Width - outerWidth));
        var ny = Math.Max(work.Y, Math.Min(y, work.Y + work.Height - outerHeight));
        return new FitPlan(new PixelRect(nx, ny, outerWidth, outerHeight), clientWidth, clientHeight, work, restore);
    }

    /// <summary>Centres an outer size in the work area (never above or left of it).</summary>
    public static FitPlan Center(PixelRect work, int outerWidth, int outerHeight, int clientWidth, int clientHeight, bool restore)
    {
        var x = work.X + Math.Max(0, (work.Width - outerWidth) / 2);
        var y = work.Y + Math.Max(0, (work.Height - outerHeight) / 2);
        return new FitPlan(new PixelRect(x, y, outerWidth, outerHeight), clientWidth, clientHeight, work, restore);
    }

    /// <summary>Does the move. Returns the window as it is afterwards (read back), or the reason it failed.</summary>
    public static (GameWindowInfo? After, string? Problem) Apply(GameWindowInfo w, FitPlan plan)
    {
        if (plan.Restore) Native.ShowWindow(w.Handle, Native.SwShowNoActivate);
        var ok = Native.PhysicalPixels(() => Native.SetWindowPos(w.Handle, IntPtr.Zero, plan.Window.X, plan.Window.Y, plan.Window.Width, plan.Window.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate | Native.SwpNoOwnerZOrder));
        if (!ok) return (null, $"SetWindowPos failed (error {Marshal.GetLastPInvokeError()}).");
        Thread.Sleep(1500); // the game re-lays out (it may snap the size)
        return (GameWindow.Describe(w.Handle, w.ProcessId), null);
    }

    /// <summary>The window's placement now (its normal rectangle even while minimized).</summary>
    public static WindowPlacement Save(GameWindowInfo w) => Native.PhysicalPixels(() =>
    {
        var p = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(w.Handle, ref p);
        var r = p.rcNormalPosition;
        return new WindowPlacement(new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), p.showCmd == Native.SwShowMinimized, p.showCmd == Native.SwShowMaximized);
    });

    /// <summary>Puts the window back where <paramref name="before"/> says, without activating it (minimized again if it was).</summary>
    public static string? Restore(GameWindowInfo w, WindowPlacement before) => Native.PhysicalPixels(() =>
    {
        var p = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(w.Handle, ref p);
        p.rcNormalPosition = new Native.RECT { Left = before.Normal.X, Top = before.Normal.Y, Right = before.Normal.Right, Bottom = before.Normal.Bottom };
        p.showCmd = before.Minimized ? Native.SwShowMinNoActive : before.Maximized ? Native.SwShowMaximized : Native.SwShowNoActivate;
        return Native.SetWindowPlacement(w.Handle, ref p) ? null : $"SetWindowPlacement failed (error {Marshal.GetLastPInvokeError()}).";
    });
}
