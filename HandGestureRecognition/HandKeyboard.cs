using System.Runtime.InteropServices;
using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

/// <summary>Hardware scan codes (US layout). Games read these, so they're more reliable than virtual-key codes.</summary>
public static class ScanCode
{
    public const ushort Esc = 0x01, D1 = 0x02, D2 = 0x03, D3 = 0x04, D4 = 0x05, D5 = 0x06;
    public const ushort Tab = 0x0F, Q = 0x10, W = 0x11, E = 0x12, R = 0x13, T = 0x14;
    public const ushort A = 0x1E, S = 0x1F, D = 0x20, F = 0x21, G = 0x22;
    public const ushort Z = 0x2C, X = 0x2D, C = 0x2E, V = 0x2F;
    public const ushort LShift = 0x2A, LCtrl = 0x1D, Space = 0x39;
}

/// <summary>Sends key presses through SendInput using scan codes.</summary>
public static class NativeKeyboard
{
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // Real union so INPUT has the correct native size on both x86 and x64
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void KeyDown(ushort scanCode) => Send(scanCode, KEYEVENTF_SCANCODE);
    public static void KeyUp(ushort scanCode) => Send(scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP);

    static void Send(ushort scanCode, uint flags)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = scanCode, dwFlags = flags } } },
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
}

public enum Finger { Thumb, Index, Middle, Ring, Pinky }

/// <summary>
/// Turns one hand into a keyboard: each finger you fold down holds a key.
/// Default layout:  thumb = Space, index = D, middle = W, ring = A, pinky = S.
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

    readonly Dictionary<Finger, ushort> _keys;
    readonly Dictionary<Finger, Threshold> _thresholds;
    readonly int _stableFrames;
    readonly bool _fistIsRest;

    readonly bool[] _down = new bool[5];        // debounced finger state
    readonly bool[] _candidate = new bool[5];   // raw state waiting to be confirmed
    readonly int[] _candidateCount = new int[5];
    readonly double[] _values = new double[5];  // latest measurements, for tuning
    readonly HashSet<ushort> _held = new();
    bool _enabled = true;
    static readonly int[] Tips = { 4, 8, 12, 16, 20 };
    readonly int[] _frozenFrames = new int[5];
    const int MaxFrozenFrames = 15; // ~0.5 s at 30 fps, then the finger's key is released

    static bool NearEdge(Point2f p, int w, int h, int margin) =>
        p.X < margin || p.Y < margin || p.X > w - margin || p.Y > h - margin;

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

    /// <param name="keys">Finger -> scan code. Default: thumb Space, index D, middle W, ring A, pinky S.</param>
    /// <param name="thresholds">Per-finger thresholds; any finger not given uses the defaults below.</param>
    /// <param name="stableFrames">Frames a finger must stay in its new state before the key changes.</param>
    /// <param name="fistIsRest">If true, 4+ fingers down presses nothing (safe neutral pose).</param>
    public HandKeyboard(Dictionary<Finger, ushort>? keys = null,
                        Dictionary<Finger, Threshold>? thresholds = null,
                        int stableFrames = 2, bool fistIsRest = true)
    {
        _keys = keys ?? new Dictionary<Finger, ushort>
        {
            [Finger.Thumb] = ScanCode.Space,
            [Finger.Index] = ScanCode.D,
            [Finger.Middle] = ScanCode.W,
            [Finger.Ring] = ScanCode.A,
            [Finger.Pinky] = ScanCode.S,
        };

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

    /// <param name="landmarks">21 landmarks of the keyboard hand, or null if it isn't visible.</param>
    public void Update(IReadOnlyList<Point2f>? landmarks, int frameW = 0, int frameH = 0, int margin = 12)
    {
        if (!_enabled || landmarks is null || landmarks.Count < 21)
        {
            ReleaseAll();
            Array.Clear(_down);
            Array.Clear(_candidateCount);
            Array.Clear(_frozenFrames);
            IsResting = false;
            return;
        }

        double handSize = Dist(landmarks[0], landmarks[9]); // wrist -> middle knuckle
        if (handSize < 1e-3) return;

        for (int f = 0; f < 5; f++)
        {
            // Tip (or wrist) at the frame edge: the model is guessing, so keep this finger's last state
            bool offFrame = frameW > 0 &&
                (NearEdge(landmarks[Tips[f]], frameW, frameH, margin) || NearEdge(landmarks[0], frameW, frameH, margin));
            if (offFrame)
            {
                if (++_frozenFrames[f] > MaxFrozenFrames) { _down[f] = false; _candidateCount[f] = 0; }
                continue;
            }
            _frozenFrames[f] = 0;

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
                if (_down[f] && _keys.TryGetValue((Finger)f, out ushort key))
                    want.Add(key);
        }

        foreach (ushort key in _held.Except(want).ToList())
        {
            NativeKeyboard.KeyUp(key);
            _held.Remove(key);
        }
        foreach (ushort key in want.Except(_held).ToList())
        {
            NativeKeyboard.KeyDown(key);
            _held.Add(key);
        }
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
        foreach (ushort key in _held) NativeKeyboard.KeyUp(key);
        _held.Clear();
    }

    public void Dispose() => ReleaseAll();

    /// <summary>Readable name for a scan code (for the status line).</summary>
    public static string KeyName(ushort scanCode) => scanCode switch
    {
        ScanCode.W => "W",
        ScanCode.A => "A",
        ScanCode.S => "S",
        ScanCode.D => "D",
        ScanCode.Space => "SPACE",
        ScanCode.LShift => "SHIFT",
        ScanCode.LCtrl => "CTRL",
        ScanCode.E => "E",
        ScanCode.R => "R",
        ScanCode.Q => "Q",
        ScanCode.F => "F",
        ScanCode.C => "C",
        _ => $"0x{scanCode:X2}",
    };

    static double Dist(Point2f a, Point2f b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
