using System.Runtime.InteropServices;

namespace HandGestureRecognition.Mouse;

/// <summary>Thin Win32 wrapper for moving the cursor and clicking.</summary>
public static class NativeMouse
{
    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi; // largest union member, so the struct size is correct on x86 and x64
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>
    /// Call this as the very first line of Main, before any window is created.
    /// Without it, Windows display scaling (125%, 150%...) makes the cursor land in the wrong place.
    /// </summary>
    public static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } // PER_MONITOR_AWARE_V2
        catch (EntryPointNotFoundException) { /* pre-Windows 10 1703; ignore */ }
    }

    /// <summary>Primary monitor size in physical pixels.</summary>
    public static (int Width, int Height) PrimaryScreenSize() => (GetSystemMetrics(0), GetSystemMetrics(1));

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    /// <summary>
    /// Relative movement, like a physical mouse being moved. Games that use raw input
    /// (most FPS games) see this; they ignore SetCursorPos.
    /// </summary>
    public static void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
    public static void LeftDown() => Send(MOUSEEVENTF_LEFTDOWN);
    public static void LeftUp() => Send(MOUSEEVENTF_LEFTUP);

    static void Send(uint flags)
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
}
