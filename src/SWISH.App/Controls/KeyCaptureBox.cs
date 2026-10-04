using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Swish.App.Controls;

/// <summary>
/// Records a key chord the way games do: click (or Enter/Space) to start listening, then press the keys,
/// or click a mouse button. Modifiers held with a key make a chord ("ctrl+c"); a modifier pressed and
/// released on its own is recorded by itself ("shift"). Clicking away cancels. The result is in
/// KeySender's chord syntax, so it goes straight into an action.
/// </summary>
public class KeyCaptureBox : Button
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord), typeof(string), typeof(KeyCaptureBox), new PropertyMetadata("", (d, _) => ((KeyCaptureBox)d).ShowChord()));
    public static readonly DependencyProperty IsListeningProperty = DependencyProperty.Register(
        nameof(IsListening), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false));

    /// <summary>The recorded chord ("ctrl+c", "space", "lmb"), or "" if none yet.</summary>
    public string Chord { get => (string)GetValue(ChordProperty); set => SetValue(ChordProperty, value); }
    public bool IsListening { get => (bool)GetValue(IsListeningProperty); private set => SetValue(IsListeningProperty, value); }

    public event Action<KeyCaptureBox>? ChordChanged;

    readonly List<string> _held = new();   // modifiers held while listening, in press order

    public KeyCaptureBox()
    {
        ShowChord();
        LostKeyboardFocus += (_, _) => Stop();
    }

    protected override void OnClick()
    {
        base.OnClick();
        Listen();
    }

    /// <summary>Starts waiting for keys (as if clicked).</summary>
    public void Listen()
    {
        if (IsListening) return;
        IsListening = true;
        _held.Clear();
        Content = "PRESS A KEY…";
        Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsListening) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;   // while listening every key is the user's answer (Tab, Esc and Enter included)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Modifier(key) is { } mod)
        {
            if (!_held.Contains(mod)) _held.Add(mod);
            Content = string.Join(" + ", _held).ToUpperInvariant() + " + …";
            return;
        }
        if (KeyName(key) is not { } name) { Content = "THAT KEY ISN'T SUPPORTED"; return; }
        // Modifiers already down when listening started (or whose key-down we missed) still count.
        var mods = Keyboard.Modifiers;
        foreach (var (flag, held) in new[] { (ModifierKeys.Control, "ctrl"), (ModifierKeys.Shift, "shift"), (ModifierKeys.Alt, "alt"), (ModifierKeys.Windows, "win") })
            if (mods.HasFlag(flag) && !_held.Contains(held)) _held.Add(held);
        Finish(name);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsListening) { base.OnPreviewKeyUp(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // A modifier let go before any other key: the modifier itself is the answer.
        if (Modifier(key) is { } mod && _held.Count > 0)
        {
            _held.Remove(mod);
            Finish(mod);
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!IsListening) { base.OnPreviewMouseDown(e); return; }
        e.Handled = true;
        Finish(e.ChangedButton switch
        {
            MouseButton.Left => "lmb", MouseButton.Right => "rmb", MouseButton.Middle => "mmb",
            MouseButton.XButton1 => "mouse4", _ => "mouse5",
        });
    }

    void Finish(string key)
    {
        var parts = _held.Where(m => m != key).Append(key).ToList();
        IsListening = false;
        _held.Clear();
        Chord = string.Join("+", parts);
        ShowChord();
        ChordChanged?.Invoke(this);
    }

    void Stop()
    {
        if (!IsListening) return;
        IsListening = false;
        _held.Clear();
        ShowChord();
    }

    void ShowChord() => Content = Chord.Length == 0 ? "CLICK, THEN PRESS A KEY" : Pretty(Chord);

    /// <summary>"ctrl+c" → "CTRL + C".</summary>
    public static string Pretty(string chord) => string.Join(" + ", chord.Split('+')).ToUpperInvariant();

    static string? Modifier(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => "ctrl",
        Key.LeftShift or Key.RightShift => "shift",
        Key.LeftAlt or Key.RightAlt => "alt",
        Key.LWin or Key.RWin => "win",
        _ => null,
    };

    /// <summary>A WPF key as a KeySender key name, or null if KeySender can't send it.</summary>
    static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "f" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "space", Key.Enter => "enter", Key.Tab => "tab", Key.Escape => "esc",
            Key.Back => "backspace", Key.Delete => "delete", Key.Insert => "insert",
            Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right",
            Key.Home => "home", Key.End => "end", Key.PageUp => "pageup", Key.PageDown => "pagedown",
            Key.PrintScreen => "printscreen", Key.CapsLock => "capslock",
            Key.VolumeUp => "volumeup", Key.VolumeDown => "volumedown", Key.VolumeMute => "mute",
            Key.MediaPlayPause => "playpause", Key.MediaNextTrack => "nexttrack", Key.MediaPreviousTrack => "prevtrack",
            Key.OemMinus or Key.Subtract => "minus", Key.OemPlus or Key.Add => "plus",
            Key.OemComma => "comma", Key.OemPeriod or Key.Decimal => "period", Key.OemQuestion or Key.Divide => "slash",
            _ => null,
        };
    }
}
