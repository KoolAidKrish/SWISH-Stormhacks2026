using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HandGestureRecognition;
using HandGestureRecognition.Custom;
using VoiceKeys;

namespace Swish.App;

/// <summary>How a gesture triggers its action.</summary>
public enum TriggerMode
{
    /// <summary>Run the action once when the gesture appears.</summary>
    Once,
    /// <summary>Hold the action's keys down for as long as the gesture is held.</summary>
    Hold,
}

/// <summary>
/// A user-defined function: an action (key presses, step programs, typed text) triggered by a custom
/// hand gesture, by voice phrases, or both.
/// </summary>
public sealed class CustomFunction
{
    public string Name { get; set; } = "";
    /// <summary>Phrases that run it by voice, in every preset. Empty = no voice trigger.</summary>
    public List<string> VoicePhrases { get; set; } = [];
    /// <summary>One step per line, in the presets' step language. A bare key/chord line means "tap it".</summary>
    public string Action { get; set; } = "";
    public TriggerMode Mode { get; set; } = TriggerMode.Once;
    /// <summary>The recorded gesture, or null for a voice-only function.</summary>
    public CustomGesture? Gesture { get; set; }
    /// <summary>Preset id of the game it belongs to (its voice phrases only work there), or null for every game.</summary>
    public string? Game { get; set; }
    /// <summary>Group on the controls screen (e.g. "Items"); null shows it under "Your commands".</summary>
    public string? Category { get; set; }

    [JsonIgnore]
    public string Summary =>
        string.Join(" · ", new[]
        {
            Gesture is null ? null : $"✋ {Gesture.Hand.ToString().ToLowerInvariant()} hand{(Mode == TriggerMode.Hold ? ", hold" : "")}",
            VoicePhrases.Count == 0 ? null : $"🎙 \"{string.Join("\", \"", VoicePhrases)}\"",
        }.Where(s => s is not null));

    static readonly HashSet<string> StepVerbs =
        ["tap", "hold", "down", "up", "latch", "unlatch", "release", "lift", "resume", "wait", "type"];

    /// <summary>The action as step lines: bare chords like "ctrl+c" become "tap ctrl+c".</summary>
    public static List<string> StepLines(string action) =>
        action.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//"))
            .Select(l => StepVerbs.Contains(l.Split(' ')[0].ToLowerInvariant()) ? l : "tap " + l)
            .ToList();

    /// <summary>
    /// Compiles the action, throwing FormatException with a readable message if a line is wrong.
    /// For Hold mode the action must be plain keys: they go down when the gesture starts and come
    /// back up (in reverse) when it ends.
    /// </summary>
    public (IReadOnlyList<KeySender.Step> Start, IReadOnlyList<KeySender.Step> End) Compile()
    {
        var lines = StepLines(Action);
        if (lines.Count == 0) throw new FormatException("The action is empty - add at least one key or step");

        if (Mode == TriggerMode.Once || Gesture is null)
            return (KeySender.ParseSteps(lines), []);

        var chords = new List<string>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is not ["tap", var chord]) throw new FormatException($"\"Hold while gesture is held\" needs plain keys (like \"w\" or \"ctrl+shift\"), not \"{line}\"");
            KeySender.ParseChord(chord);
            chords.Add(chord);
        }
        return (KeySender.ParseSteps(chords.Select(c => "down " + c)),
                KeySender.ParseSteps(Enumerable.Reverse(chords).Select(c => "up " + c)));
    }

    /// <summary>This function as a voice command (or null if it has no phrases).</summary>
    public CommandDef? ToVoiceCommand()
    {
        if (VoicePhrases.Count == 0) return null;
        // Speak left null: spoken confirmation follows the active preset (games stay quiet).
        var cmd = new CommandDef
        {
            Say = VoicePhrases, Do = StepLines(Action), Label = Name, Category = Category, OnlyInPreset = Game,
        };
        cmd.Compile(new Preset());
        return cmd;
    }
}

/// <summary>
/// Loads/saves the user's custom functions (%LOCALAPPDATA%\SWISH\custom-functions.json) and connects them
/// to the engines: gestures go to the hand-tracking engine, phrases to the voice engine, and when either
/// fires, the action runs through KeySender (the same input path voice presets use).
/// </summary>
public sealed class CustomFunctionManager
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "ctrl+c", not "ctrl+c"
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly GestureEngine _gestures;
    readonly VoiceEngine? _voice;
    readonly Dictionary<CustomGesture, (CustomFunction Fn, IReadOnlyList<KeySender.Step> Start, IReadOnlyList<KeySender.Step> End)> _byGesture = new();

    public List<CustomFunction> Functions { get; private set; } = [];
    public string FilePath => _path;

    /// <summary>A gesture just fired (for the UI log): function name, hand, started/ended.</summary>
    public event Action<string, GestureHand, bool>? Fired;

    public CustomFunctionManager(string dataDir, GestureEngine gestures, VoiceEngine? voice)
    {
        _path = Path.Combine(dataDir, "custom-functions.json");
        _gestures = gestures;
        _voice = voice;
        _gestures.CustomGestureStarted += (g, hand) => OnGesture(g, hand, started: true);
        _gestures.CustomGestureEnded += (g, hand) => OnGesture(g, hand, started: false);
    }

    sealed class FileModel { public List<CustomFunction> Functions { get; set; } = []; }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Functions = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json)?.Functions ?? [];
        }
        catch (Exception)
        {
            // Keep the broken file for the user to inspect rather than overwriting it on the next save.
            File.Copy(_path, _path + ".broken", overwrite: true);
            Functions = [];
        }
        Apply();
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new FileModel { Functions = Functions }, Json));
        Apply();
    }

    /// <summary>Pushes the current functions to both engines. Functions whose action doesn't compile are skipped.</summary>
    public void Apply()
    {
        lock (_byGesture)
        {
            _byGesture.Clear();
            foreach (var fn in Functions.Where(f => f.Gesture is not null))
            {
                try
                {
                    var (start, end) = fn.Compile();
                    fn.Gesture!.Name = fn.Name;
                    _byGesture[fn.Gesture] = (fn, start, end);
                }
                catch (FormatException) { /* shown in the editor; not active until fixed */ }
            }
            _gestures.SetCustomGestures(_byGesture.Keys.ToList());
        }

        if (_voice is not null)
        {
            var commands = new List<CommandDef>();
            foreach (var fn in Functions)
            {
                try { if (fn.ToVoiceCommand() is { } cmd) commands.Add(cmd); }
                catch (FormatException) { }
            }
            _voice.SetCustomCommands(commands);
        }
    }

    void OnGesture(CustomGesture gesture, GestureHand hand, bool started)
    {
        (CustomFunction Fn, IReadOnlyList<KeySender.Step> Start, IReadOnlyList<KeySender.Step> End) entry;
        lock (_byGesture)
            if (!_byGesture.TryGetValue(gesture, out entry)) return;

        if (started) KeySender.Run(entry.Start);
        else if (entry.End.Count > 0) KeySender.Run(entry.End);
        Fired?.Invoke(entry.Fn.Name, hand, started);
    }
}
