using System.Runtime.InteropServices;

namespace RaidActions.App;

/// <summary>
/// The global kill switch, Ctrl+Alt+Esc: a system-wide hotkey (works while the game has focus) on its own thread with its own
/// message loop, so it fires even when the UI thread is busy. <see cref="Registered"/> is false when another program holds the key.
/// </summary>
public sealed class KillSwitch : IDisposable
{
    public const string Keys = "Ctrl+Alt+Esc";
    private const int HotkeyId = 0x5241; // "RA"
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, VkEscape = 0x1B, WmHotkey = 0x0312, WmQuit = 0x0012;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    public bool Registered { get; private set; }
    public string? Problem { get; private set; }

    /// <summary>Raised on the hotkey's own thread.</summary>
    public event Action? Pressed;

    public KillSwitch()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Kill switch" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        Registered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkEscape);
        if (!Registered) Problem = $"{Keys} could not be registered (error {Marshal.GetLastPInvokeError()}); another program may hold it. Use the Abort button or Ctrl+C.";
        _ready.Set();
        if (!Registered) return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == WmHotkey && (int)msg.wParam == HotkeyId) Pressed?.Invoke();
        }
        finally { UnregisterHotKey(IntPtr.Zero, HotkeyId); }
    }

    public void Dispose()
    {
        if (Registered && _threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
