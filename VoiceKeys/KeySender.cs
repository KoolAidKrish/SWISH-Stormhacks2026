using System.Runtime.InteropServices;

namespace VoiceKeys;

/// <summary>
/// Synthesizes keyboard and mouse-button input with Win32 SendInput.
///
/// Keys are sent as hardware scan codes rather than virtual-key codes, because most games
/// read raw input / DirectInput and ignore virtual keys. Every tap is held briefly: a game
/// that polls once per frame never sees a key that goes down and up in the same microsecond.
///
/// Commands run as small step programs (see <see cref="ParseSteps"/>), each on its own thread,
/// so a 1.5 s boost never delays the "left" you say during it. Keys are reference-counted:
/// if two commands hold the same key, it only comes up when the last one lets go.
/// </summary>
public static class KeySender
{
    /// <summary>Default time a tap stays held. ~2 frames at 60 fps.</summary>
    public static int DefaultHoldMs { get; set; } = 35;

    // ---- Steps ----

    public abstract record Step;
    private sealed record Tap(List<ushort> Keys, int? Ms) : Step;      // tap <chord> [ms]   (also: hold <chord> <ms>)
    private sealed record Down(List<ushort> Keys) : Step;              // down <chord>       (pair with up)
    private sealed record Up(List<ushort> Keys) : Step;                // up <chord>
    private sealed record Latch(List<ushort> Keys) : Step;             // latch <chord>      stays down until unlatched/released
    private sealed record Unlatch(List<ushort> Keys) : Step;           // unlatch <chord>
    private sealed record ReleaseLatched : Step;                       // release            unlatch everything
    private sealed record Lift : Step;                                 // lift               temporarily unlatch everything...
    private sealed record Resume : Step;                               // resume             ...and put it back, unless something else changed it meanwhile
    private sealed record Wait(int Ms) : Step;                         // wait <ms>
    private sealed record Type(string Text) : Step;

    /// <summary>Parses step strings like "latch w", "tap rmb 50", "wait 70". Throws on mistakes.</summary>
    public static List<Step> ParseSteps(IEnumerable<string> lines) => lines.Select(ParseStep).ToList();

    private static Step ParseStep(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int Ms(int i) => parts.Length > i && int.TryParse(parts[i], out var ms) && ms >= 0 ? ms
            : throw new FormatException($"Expected a number of milliseconds in \"{line}\"");
        List<ushort> Chord() => parts.Length >= 2 ? ParseChord(parts[1]) : throw new FormatException($"Missing key in \"{line}\"");

        return parts.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "tap" => new Tap(Chord(), parts.Length > 2 ? Ms(2) : null),
            "hold" => new Tap(Chord(), Ms(2)),
            "down" => new Down(Chord()),
            "up" => new Up(Chord()),
            "latch" => new Latch(Chord()),
            "unlatch" => new Unlatch(Chord()),
            "release" => new ReleaseLatched(),
            "lift" => new Lift(),
            "resume" => new Resume(),
            "wait" => new Wait(Ms(1)),
            _ => throw new FormatException($"Unknown step \"{line}\" (use tap, hold, down, up, latch, unlatch, release, lift, resume, wait)"),
        };
    }

    /// <summary>Steps for the simple "keys" form: tap each chord in order.</summary>
    public static List<Step> TapSteps(IEnumerable<string> chords, int? holdMs) =>
        chords.Select(c => (Step)new Tap(ParseChord(c), holdMs)).ToList();

    public static Step TypeStep(string text) => new Type(text);

    /// <summary>Runs a step program on its own thread so its waits don't block anything else.</summary>
    public static void Run(IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) return;
        new Thread(() => Execute(steps)) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
    }

    public static void TypeText(string text) => Run([new Type(text)]);
    public static void PressChord(string chord, int? holdMs = null) => Run(TapSteps([chord], holdMs));

    private static void Execute(IReadOnlyList<Step> steps)
    {
        List<ushort>? lifted = null;
        int liftedAtGeneration = 0;

        foreach (var step in steps)
        {
            switch (step)
            {
                case Tap t:
                    t.Keys.ForEach(Press);
                    Thread.Sleep(t.Ms ?? DefaultHoldMs);
                    Enumerable.Reverse(t.Keys).ToList().ForEach(Unpress);
                    break;
                case Down d: d.Keys.ForEach(Press); break;
                case Up u: Enumerable.Reverse(u.Keys).ToList().ForEach(Unpress); break;
                case Wait w: Thread.Sleep(w.Ms); break;
                case Type t: SendText(t.Text); break;

                case Latch l:
                    lock (Gate) { foreach (var k in l.Keys) if (Latched.Add(k)) Press(k); _generation++; }
                    break;
                case Unlatch u:
                    lock (Gate) { foreach (var k in u.Keys) if (Latched.Remove(k)) Unpress(k); _generation++; }
                    break;
                case ReleaseLatched:
                    ReleaseLatchedKeys();
                    break;
                case Lift:
                    lock (Gate)
                    {
                        lifted = Latched.ToList();
                        liftedAtGeneration = _generation;
                        foreach (var k in lifted) Unpress(k);
                        Latched.Clear();
                    }
                    break;
                case Resume:
                    lock (Gate)
                    {
                        // If you said "stop" or "reverse" mid-flip, don't drag the old state back.
                        if (lifted is not null && _generation == liftedAtGeneration)
                            foreach (var k in lifted) if (Latched.Add(k)) Press(k);
                        lifted = null;
                    }
                    break;
            }
        }
    }

    // ---- Held-key bookkeeping ----

    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, int> DownCount = new();
    private static readonly HashSet<ushort> Latched = new();
    private static int _generation;

    static KeySender()
    {
        // Thread.Sleep is ~15 ms granular by default, too coarse for flip timing. 1 ms timer resolution
        // is what most games request anyway.
        timeBeginPeriod(1);
    }

    /// <summary>Lets go of every latched key ("stop"), e.g. on pause or preset switch.</summary>
    public static void ReleaseLatchedKeys()
    {
        lock (Gate)
        {
            foreach (var k in Latched) Unpress(k);
            Latched.Clear();
            _generation++;
        }
    }

    /// <summary>Forces every key we pressed back up. Call on exit so nothing stays stuck down.</summary>
    public static void ReleaseEverything()
    {
        lock (Gate)
        {
            foreach (var (k, n) in DownCount) if (n > 0) SendKey(k, up: true);
            DownCount.Clear();
            Latched.Clear();
            _generation++;
        }
    }

    private static void Press(ushort vk)
    {
        lock (Gate)
        {
            DownCount.TryGetValue(vk, out var n);
            DownCount[vk] = n + 1;
            if (n == 0) SendKey(vk, up: false);
        }
    }

    private static void Unpress(ushort vk)
    {
        lock (Gate)
        {
            if (!DownCount.TryGetValue(vk, out var n) || n == 0) return;
            DownCount[vk] = n - 1;
            if (n == 1) SendKey(vk, up: true);
        }
    }

    // ---- Key names ----

    /// <summary>Throws if a chord contains an unknown key; used to validate config at startup.</summary>
    public static List<ushort> ParseChord(string chord)
    {
        var result = new List<ushort>();
        foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.ToLowerInvariant();
            if (Named.TryGetValue(name, out var vk)) result.Add(vk);
            else if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) result.Add(char.ToUpperInvariant(name[0])); // VK_A..Z / VK_0..9 match ASCII
            else if (name.Length is 2 or 3 && name[0] == 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) result.Add((ushort)(0x70 + f - 1));
            else throw new FormatException($"Unknown key '{raw}' in \"{chord}\"");
        }
        return result;
    }

    private static readonly Dictionary<string, ushort> Named = new()
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = VK_RETURN, ["return"] = VK_RETURN, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["printscreen"] = 0x2C, ["capslock"] = 0x14,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["mute"] = 0xAD,
        ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1,
        ["minus"] = 0xBD, ["plus"] = 0xBB, ["equals"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE, ["slash"] = 0xBF,
        // Mouse buttons (virtual-key codes 1-6; sent as mouse events, not key events)
        ["lmb"] = 0x01, ["mouse1"] = 0x01, ["rmb"] = 0x02, ["mouse2"] = 0x02,
        ["mmb"] = 0x04, ["mouse3"] = 0x04, ["middlemouse"] = 0x04,
        ["mouse4"] = 0x05, ["xbutton1"] = 0x05, ["mouse5"] = 0x06, ["xbutton2"] = 0x06,
    };

    // Keys that need KEYEVENTF_EXTENDEDKEY or Windows confuses them with their numpad twins.
    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B];

    // ---- Sending ----

    private static void SendKey(ushort vk, bool up) => Send([vk is >= 0x01 and <= 0x06 and not 0x03 ? Mouse(vk, up) : Key(vk, up)]);

    private static INPUT Mouse(ushort vk, bool up)
    {
        var (down, release, data) = vk switch
        {
            0x01 => (0x0002u, 0x0004u, 0u),   // left
            0x02 => (0x0008u, 0x0010u, 0u),   // right
            0x04 => (0x0020u, 0x0040u, 0u),   // middle
            0x05 => (0x0080u, 0x0100u, 1u),   // XBUTTON1
            _ => (0x0080u, 0x0100u, 2u),      // XBUTTON2
        };
        return new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = up ? release : down, mouseData = data } } };
    }

    private static INPUT Key(ushort vk, bool up)
    {
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        // Media keys have no plain scan code; those fall back to the virtual key (desktop apps handle them fine).
        var ki = scan != 0
            ? new KEYBDINPUT { wScan = scan, dwFlags = flags | KEYEVENTF_SCANCODE }
            : new KEYBDINPUT { wVk = vk, dwFlags = flags };
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = ki } };
    }

    /// <summary>Types arbitrary text as Unicode, independent of keyboard layout.</summary>
    private static void SendText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\n') { inputs.Add(Key(VK_RETURN, false)); inputs.Add(Key(VK_RETURN, true)); continue; }
            inputs.Add(Unicode(c, up: false));
            inputs.Add(Unicode(c, up: true));
        }
        Send(inputs);
    }

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    private static void Send(IEnumerable<INPUT> inputs)
    {
        var arr = inputs.ToArray();
        if (arr.Length == 0) return;
        uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
            // Usually UIPI: the focused window belongs to an elevated (admin) process and we aren't.
            Console.Error.WriteLine($"  ! SendInput delivered {sent}/{arr.Length} events (target app may be running as admin)");
    }

    // ---- Win32 interop ----
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const ushort VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    // The union must include MOUSEINPUT (its largest member) so sizeof(INPUT) matches what Windows expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);
}
