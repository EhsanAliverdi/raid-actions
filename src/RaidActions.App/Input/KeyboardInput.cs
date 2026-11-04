using System.Runtime.InteropServices;
using RaidActions.Game.Window;

namespace RaidActions.App.Input;

/// <summary>
/// Text typed into the game (a promo code, Ehsan 2026-11-04): Unicode key presses to the game window once it is in front; the field must be focused
/// (tapped) first. Letters, digits and spaces only, so no shortcut key can be sent by mistake.
/// </summary>
public static class KeyboardInput
{
    public static string? Type(GameWindowInfo w, string text)
    {
        if (text.Length is 0 or > 40 || !text.All(c => char.IsLetterOrDigit(c) || c == ' ')) return "Refused: only letters, digits and spaces, at most 40.";
        if (MouseInput.EnsureForeground(w) is { } problem) return problem;
        foreach (var ch in text)
        {
            var down = Key(ch, up: false);
            var up = Key(ch, up: true);
            if (SendInput(2, [down, up], Marshal.SizeOf<INPUT>()) != 2) return $"Windows refused the key '{ch}'.";
            Thread.Sleep(60);
        }
        return null;
    }

    private const uint InputKeyboard = 1, KeyeventfUnicode = 0x0004, KeyeventfKeyup = 0x0002;

    private static INPUT Key(char ch, bool up) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KeyeventfUnicode | (up ? KeyeventfKeyup : 0) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
