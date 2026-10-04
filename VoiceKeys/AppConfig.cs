using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceKeys;

/// <summary>Global settings (settings.json). The commands themselves live in presets/*.json.</summary>
public sealed class AppConfig
{
    public ScribeSettings Scribe { get; set; } = new();
    public PushToTalkSettings PushToTalk { get; set; } = new();
    public SpeechSettings Speech { get; set; } = new();
    public int MicDevice { get; set; } = 0;

    /// <summary>Mic buffer size. Smaller = audio reaches the server sooner, at the cost of more messages.</summary>
    public int ChunkMs { get; set; } = 50;

    /// <summary>Fire commands from live partial transcripts instead of waiting for end-of-speech silence.</summary>
    public bool FireOnPartial { get; set; } = true;

    /// <summary>How long each tap is held down. Games that poll once per frame need at least one frame.</summary>
    public int KeyHoldMs { get; set; } = 35;

    /// <summary>"type hello world" types "hello world" instead of matching a command.</summary>
    public List<string> DictationPrefixes { get; set; } = ["type"];

    /// <summary>Words allowed between chained commands: "copy then paste". Anything else rejects the utterance.</summary>
    public List<string> FillerWords { get; set; } = ["then", "and", "please"];

    public List<string> PausePhrases { get; set; } = ["stop listening"];
    public List<string> ResumePhrases { get; set; } = ["start listening"];

    /// <summary>Preset to start in: a file name in presets/ without ".json".</summary>
    public string Preset { get; set; } = "desktop";

    [JsonIgnore] public List<Preset> Presets { get; private set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string settingsPath, string presetsDir)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(settingsPath), JsonOptions)
            ?? throw new InvalidDataException($"{settingsPath} is empty");

        foreach (var file in Directory.EnumerateFiles(presetsDir, "*.json").Order())
        {
            Preset preset;
            try
            {
                preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(file), JsonOptions)
                    ?? throw new InvalidDataException("file is empty");
                preset.Id = Path.GetFileNameWithoutExtension(file);
                // Fail fast on typos like "crtl+c" or "tap rbm" rather than at the moment you say it.
                foreach (var cmd in preset.Commands) cmd.Compile(preset);
                if (preset.Modifiers.Any(m => (m.Scale is null) == (m.Intensify is null)))
                    throw new FormatException("each modifier needs exactly one of \"scale\" or \"intensify\"");
            }
            catch (Exception ex) { throw new InvalidDataException($"{Path.GetFileName(file)}: {ex.Message}", ex); }
            cfg.Presets.Add(preset);
        }

        if (cfg.Presets.Count == 0) throw new InvalidDataException($"No presets found in {presetsDir}");

        // Pull in shared command sets, e.g. "include": ["gestures"] adds the hand-gesture controls.
        foreach (var preset in cfg.Presets)
            foreach (var id in preset.Include)
            {
                var shared = cfg.FindPreset(id)
                    ?? throw new InvalidDataException($"{preset.Id}.json includes \"{id}\", which doesn't exist");
                preset.Commands.AddRange(shared.Commands);
            }
        if (cfg.FindPreset(cfg.Preset) is null)
            throw new InvalidDataException($"Preset \"{cfg.Preset}\" not found; have: {string.Join(", ", cfg.Presets.Select(p => p.Id))}");
        if (cfg.PushToTalk.Enabled) VoiceKeys.PushToTalk.ParseKey(cfg.PushToTalk.Key);
        return cfg;
    }

    public Preset? FindPreset(string id) =>
        Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One switchable command set, e.g. presets/rocket-league.json.</summary>
public sealed class Preset
{
    [JsonIgnore] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Phrases that switch to this preset from any other one, e.g. "rocket league mode".</summary>
    public List<string> SwitchPhrases { get; set; } = [];
    /// <summary>Spoken when switching to it. Defaults to "{Name} mode".</summary>
    public string? Announce { get; set; }
    /// <summary>Overrides the global keyHoldMs / speech.speakByDefault while this preset is active.</summary>
    public int? KeyHoldMs { get; set; }
    public bool? SpeakByDefault { get; set; }
    public List<CommandDef> Commands { get; set; } = [];
    /// <summary>Other preset files whose commands are added to this one (shared sets like "gestures").</summary>
    public List<string> Include { get; set; } = [];
    /// <summary>A preset with no switch phrases is only a shared command set, not something you switch to.</summary>
    [JsonIgnore] public bool Switchable => SwitchPhrases.Count > 0;

    /// <summary>Words that scale a command's duration: "hard left", "little boost", "left a little".</summary>
    public List<ModifierDef> Modifiers { get; set; } = [];
    /// <summary>Scaled durations are clamped to this range.</summary>
    public int MinMs { get; set; } = 30;
    public int MaxMs { get; set; } = 4000;

    [JsonIgnore] public string AnnounceLine => Announce ?? $"{Name} mode";

    /// <summary>Set for presets that appear as a game in the app (title, tile image, order).</summary>
    public GameInfo? Game { get; set; }

    /// <summary>
    /// What the hand controls do in this game, by key: the left-hand finger keys (space, d, w, a, s) and the
    /// right-hand clicks and scroll pinch (lmb, rmb, scroll). Shown on the controls screen, e.g. { "w": "Forward", "lmb": "Attack" }.
    /// </summary>
    public Dictionary<string, string> HandLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameInfo
{
    public string Title { get; set; } = "";
    /// <summary>Tile image file name under the app's Assets/Games folder (optional; a placeholder is drawn without it).</summary>
    public string? Tile { get; set; }
    public int Order { get; set; }
}

public sealed class ScribeSettings
{
    public string Endpoint { get; set; } = "wss://api.elevenlabs.io/v1/speech-to-text/realtime";
    public string ModelId { get; set; } = "scribe_v2_realtime";
    public string? LanguageCode { get; set; } = "en";
    /// <summary>How long you must be quiet before an utterance is committed (open mic only).</summary>
    public double VadSilenceSecs { get; set; } = 0.4;
    public bool UseKeyterms { get; set; } = true;
}

public sealed class PushToTalkSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>A keyboard key name or mouse button (mouse3/mouse4/mouse5).</summary>
    public string Key { get; set; } = "capslock";
    /// <summary>Audio from just before the press that's included, so the first syllable isn't clipped.</summary>
    public int PrerollMs { get; set; } = 300;
    /// <summary>Audio still sent after release, in case you let go before finishing the word.</summary>
    public int TailMs { get; set; } = 150;
}

public sealed class SpeechSettings
{
    public bool Enabled { get; set; } = true;
    public string VoiceId { get; set; } = "JBFqnCBsd6RMkjVDRZzb";
    public string ModelId { get; set; } = "eleven_flash_v2_5";
    public double Volume { get; set; } = 0.7;
    /// <summary>Commands without their own "speak" line say their first phrase back.</summary>
    public bool SpeakByDefault { get; set; } = true;
    public string PausedLine { get; set; } = "Voice commands paused";
    public string ResumedLine { get; set; } = "Listening";
}

public sealed class ModifierDef
{
    public List<string> Say { get; set; } = [];
    /// <summary>Multiplies the duration: 2.2 = "hard", 0.35 = "little".</summary>
    public double? Scale { get; set; }
    /// <summary>Raises the next modifier to this power: "really hard" = 2.2^1.7, "really little" = 0.35^1.7.</summary>
    public double? Intensify { get; set; }
}

public sealed class CommandDef
{
    /// <summary>Phrases that trigger this command.</summary>
    public List<string> Say { get; set; } = [];
    /// <summary>Simple form: chords tapped in order, e.g. ["ctrl+a", "ctrl+c"].</summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>Full form: a step program, e.g. ["latch w"] or ["tap rmb 50", "wait 70", "tap rmb 50"]. Wins over Keys.</summary>
    public List<string>? Do { get; set; }
    /// <summary>Optional text typed after the keys.</summary>
    public string? Text { get; set; }
    /// <summary>Hold time for each chord in Keys (default: keyHoldMs).</summary>
    public int? HoldMs { get; set; }
    /// <summary>Spoken confirmation; "" for silence. Null falls back to speakByDefault.</summary>
    public string? Speak { get; set; }

    /// <summary>Display name on the controls screen (defaults to the first phrase).</summary>
    public string? Label { get; set; }
    /// <summary>Group on the controls screen, e.g. "Items".</summary>
    public string? Category { get; set; }
    /// <summary>Shown up front on the controls screen; the rest sit in their category.</summary>
    public bool Essential { get; set; }
    /// <summary>For custom commands: only active in this preset (null = every preset).</summary>
    [JsonIgnore] public string? OnlyInPreset { get; set; }

    [JsonIgnore] public string DisplayLabel => Label ?? (Say.Count > 0 ? char.ToUpper(Say[0][0]) + Say[0][1..] : "");

    /// <summary>
    /// Makes the command scalable by modifier words. Write "{ms}" in "do" where the duration goes
    /// (e.g. "hold a {ms}"); with "keys", it's the hold time of each chord.
    /// </summary>
    public int? BaseMs { get; set; }

    [JsonIgnore] public bool Scalable => BaseMs is not null;
    [JsonIgnore] public IReadOnlyList<KeySender.Step> Steps { get; private set; } = [];
    private (int Min, int Max) _clamp = (0, int.MaxValue);

    public void Compile(Preset preset)
    {
        _clamp = (preset.MinMs, preset.MaxMs);
        if (BaseMs is null && Do?.Any(d => d.Contains("{ms}")) == true)
            throw new FormatException($"\"{Say.FirstOrDefault()}\" uses {{ms}} but has no baseMs");
        Steps = Build(BaseMs);
    }

    /// <summary>Steps with the duration scaled; unscaled commands reuse the precompiled steps.</summary>
    public IReadOnlyList<KeySender.Step> StepsFor(double scale) =>
        !Scalable || scale == 1 ? Steps : Build(DurationFor(scale));

    private int DurationFor(double scale) => Math.Clamp((int)Math.Round(BaseMs!.Value * scale), _clamp.Min, _clamp.Max);

    private List<KeySender.Step> Build(int? ms)
    {
        var steps = Do is not null
            ? KeySender.ParseSteps(Do.Select(d => d.Replace("{ms}", ms?.ToString())))
            : KeySender.TapSteps(Keys, ms ?? HoldMs);
        if (Text is not null) steps.Add(KeySender.TypeStep(Text));
        return steps;
    }

    public string Describe(double scale = 1)
    {
        var ms = Scalable ? DurationFor(scale).ToString() : null;
        var body = Do is not null ? string.Join("; ", Do.Select(d => d.Replace("{ms}", ms))) : string.Join(" ", Keys);
        return body + (Text is null ? "" : $" \"{Text}\"") + (Scalable && scale != 1 ? $"  (x{scale:0.##})" : "");
    }
}
