using System.IO;
using System.Text.Json;

namespace Swish.App.Services;

/// <summary>Choices made in the app (camera, microphone, last game), kept per user in %LOCALAPPDATA%\SWISH.</summary>
public sealed class UiSettings
{
    public int Camera { get; set; }
    public int Microphone { get; set; }
    /// <summary>Name of the GPU the hand models run on (names survive adapters being renumbered); null = the CPU.</summary>
    public string? Gpu { get; set; }
    /// <summary>Voice assistant (spoken confirmations): ElevenLabs voice id (null = settings.json's), volume 0..1, mute.</summary>
    public string? SpeechVoice { get; set; }
    public double? SpeechVolume { get; set; }
    public bool SpeechMuted { get; set; }
    /// <summary>Preset id of the game last selected on the controls screen.</summary>
    public string? Game { get; set; }

    static string PathIn(string dataDir) => System.IO.Path.Combine(dataDir, "app-settings.json");

    public static UiSettings Load(string dataDir)
    {
        try
        {
            var p = PathIn(dataDir);
            if (File.Exists(p)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(p)) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    public void Save(string dataDir)
    {
        try { File.WriteAllText(PathIn(dataDir), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception) { /* settings are a convenience; never crash over them */ }
    }
}
