using System.IO;
using System.Text.Json;
using HandGestureRecognition.Mouse;

namespace Swish.App.Services;

/// <summary>
/// One built-in hand control that can be rebound.
/// </summary>
/// <param name="Id">Stable id, used in the saved file ("middle", "pinch-index"...).</param>
/// <param name="Gesture">What you do, e.g. "FOLD MIDDLE FINGER".</param>
/// <param name="Hand">"LEFT" or "RIGHT".</param>
/// <param name="Asset">Art in Assets/Gestures (without .png).</param>
/// <param name="DefaultChord">What it presses by default, in KeySender chord syntax; null = scrolling.</param>
/// <param name="LabelKey">The preset's handLabels key for its default meaning ("w", "lmb", "scroll").</param>
/// <param name="Finger">For the left-hand finger keys.</param>
public sealed record HandControl(string Id, string Gesture, string Hand, string Asset, string? DefaultChord, string LabelKey, Finger? Finger)
{
    /// <summary>The built-in controls, in the order the controls screen shows them (fist isn't rebindable: it lifts the mouse).</summary>
    public static readonly IReadOnlyList<HandControl> All =
    [
        new("middle", "FOLD MIDDLE FINGER", "LEFT", "finger-middle", "w", "w", HandGestureRecognition.Mouse.Finger.Middle),
        new("ring", "FOLD RING FINGER", "LEFT", "finger-ring", "a", "a", HandGestureRecognition.Mouse.Finger.Ring),
        new("index", "FOLD INDEX FINGER", "LEFT", "finger-index", "d", "d", HandGestureRecognition.Mouse.Finger.Index),
        new("thumb", "FOLD THUMB", "LEFT", "finger-thumb", "space", "space", HandGestureRecognition.Mouse.Finger.Thumb),
        new("pinky", "FOLD PINKY", "LEFT", "finger-pinky", "s", "s", HandGestureRecognition.Mouse.Finger.Pinky),
        new("pinch-index", "PINCH THUMB + INDEX", "RIGHT", "pinch-index", "lmb", "lmb", null),
        new("pinch-middle", "PINCH THUMB + MIDDLE", "RIGHT", "pinch-middle", "rmb", "rmb", null),
        new("pinch-ring", "PINCH THUMB + RING", "RIGHT", "pinch-ring", null, "scroll", null),
    ];

    public static HandControl Get(string id) => All.First(c => c.Id == id);
}

/// <summary>
/// The user's rebound hand controls, per game: { "minecraft": { "middle": "shift+w" } }. Only changes are
/// stored; a control without an entry does its default. Kept in %LOCALAPPDATA%\SWISH\hand-bindings.json.
/// </summary>
public sealed class HandBindings
{
    readonly string _path;
    Dictionary<string, Dictionary<string, string>> _games = new(StringComparer.OrdinalIgnoreCase);

    HandBindings(string path) => _path = path;

    public static HandBindings Load(string dataDir)
    {
        var b = new HandBindings(Path.Combine(dataDir, "hand-bindings.json"));
        try
        {
            if (File.Exists(b._path))
                b._games = new(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(b._path)) ?? [],
                               StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { /* a broken file means defaults; it's rewritten on the next change */ }
        return b;
    }

    /// <summary>The chord this control is rebound to in this game, or null for its default.</summary>
    public string? Get(string? game, string controlId) =>
        game is not null && _games.TryGetValue(game, out var map) && map.TryGetValue(controlId, out var chord) ? chord : null;

    /// <summary>Rebinds (chord) or resets (null) a control for a game, and saves.</summary>
    public void Set(string game, string controlId, string? chord)
    {
        if (!_games.TryGetValue(game, out var map)) _games[game] = map = new(StringComparer.OrdinalIgnoreCase);
        if (chord is null) map.Remove(controlId); else map[controlId] = chord;
        if (map.Count == 0) _games.Remove(game);
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_games, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keeps "+" readable in the file
        })); }
        catch (Exception) { /* not worth crashing over; the change still applies until restart */ }
    }
}
