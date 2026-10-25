using System.Runtime.InteropServices;
using OpenCvSharp;

namespace RaidActions.Game.Window;

/// <param name="Image">The client area, BGR, physical pixels (owned by the caller).</param>
/// <param name="Source">"printWindow" or "screen".</param>
public sealed record Capture(Mat Image, GameWindowInfo Window, string Source, DateTimeOffset Time) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Captures the game's client area without bringing it to the front: PrintWindow with full content (works for a covered window).
/// A blank result (black, e.g. a minimized or occluded DirectX window) is refused rather than used. Copying from the screen is only
/// a fallback when the game is the foreground window, since otherwise it would capture whatever covers it.
/// </summary>
public static class WindowCapture
{
    public static (Capture? Capture, string? Problem) Take(GameWindowInfo w)
    {
        if (w.NotUsable is { } why) return (null, why);
        var img = Native.PhysicalPixels(() => PrintWindow(w));
        if (img is not null && !IsBlank(img)) return (new Capture(img, w, "printWindow", DateTimeOffset.Now), null);
        img?.Dispose();
        if (!w.Foreground) return (null, "PrintWindow gave a blank image, and the game is not the foreground window (a screen copy would show whatever covers it).");
        img = Native.PhysicalPixels(() => FromScreen(w.Client));
        if (img is null || IsBlank(img))
        {
            img?.Dispose();
            return (null, "The capture is blank.");
        }
        return (new Capture(img, w, "screen", DateTimeOffset.Now), null);
    }

    /// <summary>Nearly uniform and dark: nothing was drawn.</summary>
    public static bool IsBlank(Mat bgr)
    {
        Cv2.MeanStdDev(bgr, out var mean, out var sd);
        return mean.Val0 + mean.Val1 + mean.Val2 < 6 && sd.Val0 + sd.Val1 + sd.Val2 < 6;
    }

    private static Mat? PrintWindow(GameWindowInfo w)
    {
        var whole = Grab(w.Window.Width, w.Window.Height, hdc => Native.PrintWindow(w.Handle, hdc, Native.PwRenderFullContent));
        if (whole is null) return null;
        var x = w.Client.X - w.Window.X;
        var y = w.Client.Y - w.Window.Y;
        if (x < 0 || y < 0 || x + w.Client.Width > whole.Width || y + w.Client.Height > whole.Height)
        {
            whole.Dispose();
            return null;
        }
        using (whole) return new Mat(whole, new Rect(x, y, w.Client.Width, w.Client.Height)).Clone();
    }

    private static Mat? FromScreen(PixelRect r)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        try { return Grab(r.Width, r.Height, hdc => Native.BitBlt(hdc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, Native.SrcCopy)); }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Draws into a 32-bit top-down DIB with <paramref name="draw"/> and returns it as a BGR Mat.</summary>
    private static Mat? Grab(int width, int height, Func<IntPtr, bool> draw)
    {
        if (width <= 0 || height <= 0) return null;
        var screen = Native.GetDC(IntPtr.Zero);
        var dc = Native.CreateCompatibleDC(screen);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32,
        };
        var bmp = Native.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bmp == IntPtr.Zero) return null;
            var old = Native.SelectObject(dc, bmp);
            var ok = draw(dc);
            Native.SelectObject(dc, old);
            if (!ok) return null;
            using var bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, bits);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            Native.DeleteDC(dc);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
