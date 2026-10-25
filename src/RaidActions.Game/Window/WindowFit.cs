using System.Text.Json.Nodes;

namespace RaidActions.Game.Window;

/// <summary>The game window size the actioner owns (config/window.json): the client area, in physical pixels.</summary>
public sealed record WindowStandard(int Width, int Height, int RequestedWidth, int RequestedHeight, string Status, string Path)
{
    public static WindowStandard Load(string path)
    {
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var s = o["standardClient"]!;
        var r = o["requested"] ?? s;
        return new WindowStandard((int)s["width"]!, (int)s["height"]!, (int)r["width"]!, (int)r["height"]!, (string)o["status"]!, path);
    }

    /// <summary>Records the size the game took as the standard ("applied" when it is the requested one, else "snapped").</summary>
    public WindowStandard Save(int width, int height)
    {
        var status = width == RequestedWidth && height == RequestedHeight ? "applied" : "snapped";
        var o = JsonNode.Parse(File.ReadAllText(Path))!.AsObject();
        o["standardClient"] = new JsonObject { ["width"] = width, ["height"] = height };
        o["requested"] = new JsonObject { ["width"] = RequestedWidth, ["height"] = RequestedHeight };
        o["status"] = status;
        File.WriteAllText(Path, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        return this with { Width = width, Height = height, Status = status };
    }

    /// <summary>Null when the window's client area is the standard size, else why not (and what to run).</summary>
    public string? Mismatch(GameWindowInfo w) =>
        w.Client.Width == Width && w.Client.Height == Height ? null
        : $"The game window's client area is {w.Client.Width}x{w.Client.Height}, not the standard {Width}x{Height}: run  RaidActions window fit";

    public override string ToString() => $"{Width}x{Height} ({Status}{(Status == "snapped" ? $", requested {RequestedWidth}x{RequestedHeight}" : "")})";
}

/// <summary>The planned move: the outer window rect that gives the wanted client area, centred on the monitor's work area.</summary>
public sealed record FitPlan(PixelRect Window, int ClientWidth, int ClientHeight, PixelRect WorkArea, bool Restore);

/// <summary>
/// Sets the game window to the standard client size: SetWindowPos (no activation, no z-order change) on Raid's window only, after
/// restoring it without activation when minimized. No input is sent to the game.
/// </summary>
public static class WindowFit
{
    /// <summary>The move that gives a <paramref name="width"/>×<paramref name="height"/> client area, centred in the work area.</summary>
    public static FitPlan Plan(GameWindowInfo w, int width, int height) => Native.PhysicalPixels(() =>
    {
        var style = (uint)Native.GetWindowLongPtr(w.Handle, Native.GwlStyle).ToInt64();
        var exStyle = (uint)Native.GetWindowLongPtr(w.Handle, Native.GwlExStyle).ToInt64();
        var frame = new Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        Native.AdjustWindowRectExForDpi(ref frame, style, false, exStyle, w.Dpi == 0 ? 96 : w.Dpi);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromWindow(w.Handle, Native.MonitorDefaultToNearest), ref info);
        var work = new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        return Center(work, frame.Right - frame.Left, frame.Bottom - frame.Top, width, height, w.Minimized);
    });

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
        if (!ok) return (null, $"SetWindowPos failed (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
        // Give the game time to re-layout (it may snap the size), then read it back.
        Thread.Sleep(1500);
        return (GameWindow.Describe(w.Handle, w.ProcessId), null);
    }
}
