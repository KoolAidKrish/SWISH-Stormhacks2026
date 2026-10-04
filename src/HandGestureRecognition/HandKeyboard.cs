using System.Runtime.InteropServices;
using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

/// <summary>
/// Holds/releases virtual keys the way a game sees a real keyboard and mouse: keys go out as scan codes
/// (extended ones flagged), mouse buttons (VK 1, 2, 4, 5, 6) as mouse events. Used for rebound hand gestures.
/// </summary>
public static class NativeInput
{
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;

    // Keys that share a scan code with a numpad key and need the "extended" flag to mean the right one.
    static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0xA3, 0xA5];

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>Presses the keys in order (modifiers first, as they're listed).</summary>
    public static void Down(IReadOnlyList<ushort> vks) { foreach (var vk in vks) Send(vk, up: false); }
    /// <summary>Releases them in reverse order.</summary>
    public static void Up(IReadOnlyList<ushort> vks) { for (int i = vks.Count - 1; i >= 0; i--) Send(vks[i], up: true); }

    static void Send(ushort vk, bool up)
    {
        INPUT input;
        if (vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06)
        {
            uint flags = vk switch
            {
                0x01 => up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN,
                0x02 => up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN,
                0x04 => up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN,
                _ => up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN,
            };
            uint data = vk == 0x05 ? 1u : vk == 0x06 ? 2u : 0u;   // XBUTTON1 / XBUTTON2
            input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } };
        }
        else
        {
            ushort scan = (ushort)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);
            uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
            input = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } };
        }
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Readable name of a virtual key ("W", "SPACE", "LMB"), for status lines.</summary>
    public static string Name(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x01 => "LMB", 0x02 => "RMB", 0x04 => "MMB", 0x05 => "MOUSE4", 0x06 => "MOUSE5",
        0x20 => "SPACE", 0x10 => "SHIFT", 0x11 => "CTRL", 0x12 => "ALT", 0x0D => "ENTER", 0x09 => "TAB", 0x1B => "ESC",
        0x25 => "LEFT", 0x26 => "UP", 0x27 => "RIGHT", 0x28 => "DOWN",
        _ => $"0x{vk:X2}",
    };
}

public enum Finger { Thumb, Index, Middle, Ring, Pinky }

/// <summary>
/// Turns one hand into a keyboard: each finger you fold down holds a key (or a chord / mouse button).
/// Default layout:  thumb = Space, index = D, middle = W, ring = A, pinky = S. <see cref="SetKeys"/> rebinds them.
/// A fist (4+ fingers down) is the rest pose and presses nothing.
/// Every key is released when the hand leaves view, so nothing gets stuck.
/// </summary>
public sealed class HandKeyboard : IDisposable
{
    /// <summary>Per-finger press/release thresholds. A finger is "down" when its value drops below Press,
    /// and "up" again only when it rises above Release (the gap prevents flicker).</summary>
    public readonly record struct Threshold(double Press, double Release);

    // MediaPipe landmark indices: knuckle (MCP) and tip for each finger
    static readonly (int Mcp, int Tip)[] FingerJoints =
    {
        (2, 4),   // Thumb (unused for the ratio; thumb has its own measure)
        (5, 8),   // Index
        (9, 12),  // Middle
        (13, 16), // Ring
        (17, 20), // Pinky
    };

    /// <summary>The default layout, as virtual keys.</summary>
    public static readonly IReadOnlyDictionary<Finger, ushort[]> DefaultKeys = new Dictionary<Finger, ushort[]>
    {
        [Finger.Thumb] = [0x20],    // Space
        [Finger.Index] = [0x44],    // D
        [Finger.Middle] = [0x57],   // W
        [Finger.Ring] = [0x41],     // A
        [Finger.Pinky] = [0x53],    // S
    };

    Dictionary<Finger, ushort[]> _keys;
    readonly Dictionary<Finger, Threshold> _thresholds;
    readonly int _stableFrames;
    readonly bool _fistIsRest;

    readonly bool[] _down = new bool[5];        // debounced finger state
    readonly bool[] _candidate = new bool[5];   // raw state waiting to be confirmed
    readonly int[] _candidateCount = new int[5];
    readonly double[] _values = new double[5];  // latest measurements, for tuning
    readonly HashSet<ushort> _held = new();
    bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) ReleaseAll(); }
    }

    /// <summary>Keys currently held, for on-screen status.</summary>
    public IReadOnlyCollection<ushort> HeldKeys => _held;

    /// <summary>True when the rest pose (fist) is suppressing keys.</summary>
    public bool IsResting { get; private set; }

    /// <summary>Live per-finger measurements, e.g. "T0.82 I1.91 M1.95 R1.88 P1.70". Use these to tune thresholds.</summary>
    public string DebugValues =>
        $"T{_values[0]:F2} I{_values[1]:F2} M{_values[2]:F2} R{_values[3]:F2} P{_values[4]:F2}";

    /// <param name="keys">Finger -> virtual keys held while it's folded. Default: <see cref="DefaultKeys"/>.</param>
    /// <param name="thresholds">Per-finger thresholds; any finger not given uses the defaults below.</param>
    /// <param name="stableFrames">Frames a finger must stay in its new state before the key changes.</param>
    /// <param name="fistIsRest">If true, 4+ fingers down presses nothing (safe neutral pose).</param>
    public HandKeyboard(IReadOnlyDictionary<Finger, ushort[]>? keys = null,
                        Dictionary<Finger, Threshold>? thresholds = null,
                        int stableFrames = 2, bool fistIsRest = true)
    {
        _keys = new Dictionary<Finger, ushort[]>(keys ?? DefaultKeys);

        // Fingers: tip-to-wrist / knuckle-to-wrist. Straight is about 1.7-2.0, folded about 1.0-1.2.
        // Thumb: thumb tip to pinky knuckle, in hand-widths. Out to the side is about 1.2+, tucked is about 0.6.
        _thresholds = new Dictionary<Finger, Threshold>
        {
            [Finger.Thumb] = new(0.85, 1.00),
            [Finger.Index] = new(1.40, 1.55),
            [Finger.Middle] = new(1.40, 1.55),
            [Finger.Ring] = new(1.40, 1.55),
            [Finger.Pinky] = new(1.30, 1.45), // pinky is shorter, so its ratio is lower even when straight
        };
        if (thresholds is not null)
            foreach (var (finger, t) in thresholds) _thresholds[finger] = t;

        _stableFrames = Math.Max(1, stableFrames);
        _fistIsRest = fistIsRest;
    }

    /// <summary>Rebinds fingers (any not given keep the default). Held keys are let go first.</summary>
    public void SetKeys(IReadOnlyDictionary<Finger, ushort[]>? overrides)
    {
        ReleaseAll();
        _keys = new Dictionary<Finger, ushort[]>(DefaultKeys);
        if (overrides is not null)
            foreach (var (finger, keys) in overrides) _keys[finger] = keys;
    }

    /// <param name="landmarks">21 landmarks of the keyboard hand, or null if it isn't visible.</param>
    public void Update(IReadOnlyList<Point2f>? landmarks)
    {
        if (!_enabled || landmarks is null || landmarks.Count < 21)
        {
            ReleaseAll();
            Array.Clear(_down);
            Array.Clear(_candidateCount);
            IsResting = false;
            return;
        }

        double handSize = Dist(landmarks[0], landmarks[9]); // wrist -> middle knuckle
        if (handSize < 1e-3) return;

        for (int f = 0; f < 5; f++)
        {
            var finger = (Finger)f;
            _values[f] = Measure(landmarks, finger, handSize);

            // Hysteresis: the threshold depends on the current state
            var t = _thresholds[finger];
            bool raw = _down[f] ? _values[f] < t.Release : _values[f] < t.Press;

            // Debounce: the new state must hold for a few frames
            if (raw == _down[f])
            {
                _candidateCount[f] = 0;
            }
            else if (raw == _candidate[f] && _candidateCount[f] > 0)
            {
                if (++_candidateCount[f] >= _stableFrames)
                {
                    _down[f] = raw;
                    _candidateCount[f] = 0;
                }
            }
            else
            {
                _candidate[f] = raw;
                _candidateCount[f] = 1;
                if (_stableFrames == 1) { _down[f] = raw; _candidateCount[f] = 0; }
            }
        }

        int downCount = _down.Count(d => d);
        IsResting = _fistIsRest && downCount >= 4;

        var want = new HashSet<ushort>();
        if (!IsResting)
        {
            for (int f = 0; f < 5; f++)
                if (_down[f] && _keys.TryGetValue((Finger)f, out var keys))
                    foreach (var key in keys) want.Add(key);
        }

        // Shared keys (e.g. two fingers both holding Shift) are pressed once and released when neither wants them.
        var release = _held.Except(want).ToList();
        if (release.Count > 0) NativeInput.Up(release);
        foreach (var key in release) _held.Remove(key);
        var press = want.Except(_held).ToList();
        if (press.Count > 0) NativeInput.Down(press);
        foreach (var key in press) _held.Add(key);
    }

    /// <summary>Lower value = more folded.</summary>
    static double Measure(IReadOnlyList<Point2f> lm, Finger finger, double handSize)
    {
        if (finger == Finger.Thumb)
        {
            // Thumb folded across the palm brings its tip close to the pinky knuckle
            return Dist(lm[4], lm[17]) / handSize;
        }

        var (mcp, tip) = FingerJoints[(int)finger];
        double knuckle = Dist(lm[0], lm[mcp]);
        return knuckle > 1e-3 ? Dist(lm[0], lm[tip]) / knuckle : 2.0;
    }

    public void ReleaseAll()
    {
        if (_held.Count > 0) NativeInput.Up(_held.ToList());
        _held.Clear();
    }

    public void Dispose() => ReleaseAll();

    /// <summary>Readable name for a held key (for the status line).</summary>
    public static string KeyName(ushort vk) => NativeInput.Name(vk);

    static double Dist(Point2f a, Point2f b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
