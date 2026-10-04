using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Swish.App.Controls;
using Swish.App.Services;

namespace Swish.App.Views;

/// <summary>
/// Rebinds one built-in hand control for the current game: shows the gesture and what it does now, and takes
/// the key, chord or mouse button it should press instead (held for as long as the pose is held). No voice
/// or recording here: the gesture itself stays the same.
/// </summary>
public partial class BindGesturePage : UserControl, ISwishPage
{
    readonly App _app = (App)Application.Current;
    readonly HandControl _control;
    readonly string _game;
    ShellWindow? _shell;

    public BindGesturePage(string controlId, string game)
    {
        InitializeComponent();
        _control = HandControl.Get(controlId);
        _game = game;

        var preset = _app.Config?.FindPreset(game);
        GameText.Text = preset?.Game?.Title.ToUpperInvariant() ?? game.ToUpperInvariant();
        SectionText.Text = $"{_control.Gesture}  ({_control.Hand} HAND)";
        GestureText.Text = _control.Gesture;
        HandText.Text = $"{_control.Hand} HAND";
        if (MenuSidebar.LoadAsset($"Gestures/{_control.Asset}.png") is { } img) Art.Source = img;
        else ArtPlaceholder.Visibility = Visibility.Visible;

        HelpText.Text = _control.DefaultChord is null
            ? "By default this scrolls. A key here replaces scrolling in this game, held for as long as you pinch."
            : "Held down for as long as you hold the gesture. Keys, chords (like ctrl+c) and mouse buttons all work.";
        KeyBox.Chord = _app.HandBindings.Get(game, _control.Id) ?? "";
        KeyBox.ChordChanged += _ => Say("", null);
        ShowCurrent();
    }

    public bool ShowsCameraCorner => true;

    public IInputElement? DefaultFocus => KeyBox;

    public void OnShown(App app, ShellWindow shell)
    {
        _shell = shell;
        Sidebar.Attach(app, shell, MenuSection.Editor);
        PreviewKeyDown += OnPageKey;
    }

    public void OnHidden() => PreviewKeyDown -= OnPageKey;

    /// <summary>The default meaning in this game ("W (FORWARD)"), or the user's binding.</summary>
    void ShowCurrent()
    {
        var custom = _app.HandBindings.Get(_game, _control.Id);
        var preset = _app.Config?.FindPreset(_game);
        string meaning = preset?.HandLabels.TryGetValue(_control.LabelKey, out var l) == true ? l : "";
        string key = _control.DefaultChord is null ? "SCROLL" : KeyCaptureBox.Pretty(_control.DefaultChord);
        CurrentText.Text = custom is not null
            ? $"{KeyCaptureBox.Pretty(custom)}  (YOURS)"
            : meaning.Length > 0 && !meaning.Equals(key, StringComparison.OrdinalIgnoreCase) ? $"{key}  ({meaning.ToUpperInvariant()})" : key;
        CurrentText.Foreground = (Brush)FindResource(custom is not null ? "Pink" : "White");
        ResetButton.IsEnabled = custom is not null;
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var chord = KeyBox.Chord;
        if (chord.Length == 0) { Say("Click the box, then press the key you want.", "Danger"); KeyBox.Focus(); return; }
        try { VoiceKeys.KeySender.ParseChord(chord); }
        catch (FormatException ex) { Say(ex.Message, "Danger"); return; }

        // Binding it back to exactly the default is the same as resetting.
        bool isDefault = _control.DefaultChord is not null && chord.Equals(_control.DefaultChord, StringComparison.OrdinalIgnoreCase);
        _app.HandBindings.Set(_game, _control.Id, isDefault ? null : chord);
        _app.ApplyHandBindings();
        ShowCurrent();
        Say("Saved. Try the gesture.", "Success");
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        _app.HandBindings.Set(_game, _control.Id, null);
        _app.ApplyHandBindings();
        KeyBox.Chord = "";
        ShowCurrent();
        Say("Back to the default.", "Success");
    }

    void OnBack(object sender, RoutedEventArgs e) => _shell?.Navigate(new ControlsPage());

    void OnPageKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || KeyBox.IsListening) return;   // Esc while listening is the key being bound
        e.Handled = true;
        OnBack(this, new RoutedEventArgs());
    }

    void Say(string text, string? brush)
    {
        MessageText.Text = text;
        if (brush is not null) MessageText.Foreground = (Brush)FindResource(brush);
    }
}
