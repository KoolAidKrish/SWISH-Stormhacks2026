using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandGestureRecognition;
using HandGestureRecognition.Mouse;
using VoiceKeys;

namespace Swish.App;

/// <summary>One line in the activity log (voice commands and hand-mouse events).</summary>
public sealed record LogRow(string Time, string Text, string? Action, Brush Brush)
{
    public Visibility ActionVisibility => string.IsNullOrEmpty(Action) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Dashboard: camera preview + hand-mouse controls on the left, voice status/transcript/log on the right.
/// Reads the engines' snapshots on a UI timer and never blocks them; closing it hides to the tray.
/// </summary>
public partial class MainWindow : Window
{
    const int MaxLogRows = 200;

    static readonly Brush CommandBrush = Frozen("#8FD694"), EarlyBrush = Frozen("#E8C46A"), IgnoredBrush = Frozen("#5C6573"),
        ErrorBrush = Frozen("#F07178"), PresetBrush = Frozen("#82AAFF"), InfoBrush = Frozen("#C9CED6"), HandBrush = Frozen("#C792EA");
    static readonly Brush TalkOn = Frozen("#1E3A2B"), TalkOff = Frozen("#22262E"), TalkOnBorder = Frozen("#4CAF50"), TalkOffBorder = Frozen("#2A313B");

    readonly App _app;
    readonly ObservableCollection<LogRow> _log = new();
    readonly DispatcherTimer _timer;
    WriteableBitmap? _bitmap;
    bool _updatingPreset;
    int _tick;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
        LogList.ItemsSource = _log;

        var voice = app.Voice;
        if (voice is not null)
        {
            PresetCombo.ItemsSource = voice.SwitchablePresets;
            voice.Log += e => Dispatcher.BeginInvoke(() => AddVoiceLog(e));
            voice.Partial += t => Dispatcher.BeginInvoke(() => LiveText.Text = t);
            voice.StateChanged += () => Dispatcher.BeginInvoke(UpdateVoiceState);
        }
        else
        {
            VoiceButton.IsEnabled = false;
            PresetCombo.IsEnabled = false;
            Add("Voice is off", app.VoiceProblem, ErrorBrush);
        }
        app.Gestures.Log += msg => Dispatcher.BeginInvoke(() => Add($"✋ {msg}", null, HandBrush));
        app.CustomFunctions.Fired += (name, hand, started) => Dispatcher.BeginInvoke(() =>
        {
            if (started) Add($"✋ {name}", $"custom gesture, {hand.ToString().ToLowerInvariant()} hand", CommandBrush);
        });
        UpdateVoiceState();

        // 15 Hz: copy the newest camera frame in; refresh the status lines every few ticks. A debug
        // preview doesn't need the camera's full 30 fps, and redrawing it was ~12% of a core.
        // The timer only runs while the window can be seen.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Render, (_, _) => OnTick(), Dispatcher);
        IsVisibleChanged += (_, _) => UpdatePreviewFlag();
        StateChanged += (_, _) => UpdatePreviewFlag();
        Loaded += (_, _) => UpdatePreviewFlag();
    }

    /// <summary>Drawing preview frames costs a copy per frame, so only do it while they can be seen.</summary>
    internal void UpdatePreviewFlag()
    {
        bool seen = IsVisible && WindowState != WindowState.Minimized;
        _app.Gestures.PreviewEnabled = seen || _app.PreviewClaimed;
        if (seen) _timer.Start(); else _timer.Stop();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true; // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    // ---- Buttons ----

    void OnToggleHandMouse(object sender, RoutedEventArgs e) => _app.Gestures.ToggleMouse();
    void OnCycleMouseMode(object sender, RoutedEventArgs e) => _app.Gestures.CycleMouseMode();
    void OnRecalibrate(object sender, RoutedEventArgs e) => _app.Gestures.Recalibrate(CalibrationKind.Points);
    void OnRecalibratePursuit(object sender, RoutedEventArgs e) => _app.Gestures.Recalibrate(CalibrationKind.Pursuit);
    void OnCustomFunctions(object sender, RoutedEventArgs e) => _app.ShowCustomFunctions();

    void OnToggleVoice(object sender, RoutedEventArgs e)
    {
        if (_app.Voice is { } v) v.Paused = !v.Paused;
    }

    void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPreset || _app.Voice is null || PresetCombo.SelectedItem is not Preset p) return;
        _app.Voice.SwitchPreset(p);
    }

    // ---- Refresh ----

    void OnTick()
    {
        // Another window (custom functions, recorder) takes the preview frames while it's open.
        if (_app.Gestures.PreviewEnabled && _app.OwnsPreview(this)) BlitPreview();
        if (++_tick % 3 == 0) UpdateGestureState(); // ~5 Hz is plenty for text
    }

    void BlitPreview()
    {
        using var frame = _app.Gestures.TakePreview();
        if (frame is null) return;

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            PreviewImage.Source = _bitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        int stride = (int)frame.Step();
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, stride * frame.Height, stride);
    }

    void UpdateGestureState()
    {
        var s = _app.Gestures.Status;
        HandMouseButton.Content = $"Hand mouse: {(s.MouseEnabled ? "ON" : "OFF")}";
        MouseModeButton.Content = $"Mode: {s.MouseMode}";

        (GestureBanner.Text, GestureBanner.Foreground) =
            s.Error is not null ? ("Camera stopped", ErrorBrush)
            : !s.Running ? ("Starting…", InfoBrush)
            : s.Calibrating ? ("Calibrating: follow the instructions on screen", EarlyBrush)
            : !s.MouseEnabled ? ("Hand mouse off", IgnoredBrush)
            : s.Idle ? ("Idle: no hand in view", IgnoredBrush)
            : s.Pinching ? ("Click", CommandBrush)
            : s.Clutched ? ("Lifted (fist)", EarlyBrush)
            : (s.Hands > 0 ? "Tracking" : "No hand", s.Hands > 0 ? CommandBrush : IgnoredBrush);

        GestureStatusText.Text = s.Error is not null
            ? $"Hand tracking stopped: {s.Error}"
            : $"{s.Fps,3:0} fps · hands {s.Hands} · mouse {(s.MouseEnabled ? s.MouseMode.ToString().ToLowerInvariant() : "off")}";
        if (s.Error is not null)
        {
            PreviewPlaceholder.Text = "Camera stopped";
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    void UpdateVoiceState()
    {
        var v = _app.Voice;
        if (v is null)
        {
            VoiceButton.Content = "Voice: off";
            VoiceStatusText.Text = _app.VoiceProblem;
            return;
        }

        var cfg = v.Config;
        VoiceButton.Content = v.Paused ? "Voice: paused" : "Voice: listening";
        _updatingPreset = true;
        PresetCombo.SelectedItem = v.ActivePreset;
        _updatingPreset = false;

        var connection = v.Connected ? "● connected" : "○ connecting…";
        var how = cfg.PushToTalk.Enabled ? $"hold {cfg.PushToTalk.Key} to talk" : "open mic";
        VoiceStatusText.Text = $"{connection} · {v.ActivePreset.Name} · {how}" +
            (v.Paused ? $"\nPaused: say \"{cfg.ResumePhrases.FirstOrDefault()}\" or press the button" : "");

        bool talking = v.Talking;
        TalkBorder.Background = talking ? TalkOn : TalkOff;
        TalkBorder.BorderBrush = talking ? TalkOnBorder : TalkOffBorder;
        if (talking && string.IsNullOrEmpty(LiveText.Text)) LiveText.Text = "🎙 …";
        else if (!talking && LiveText.Text == "🎙 …") LiveText.Text = "";
    }

    void AddVoiceLog(VoiceLogEntry e)
    {
        var (text, brush) = e.Kind switch
        {
            VoiceLogKind.Command or VoiceLogKind.Dictation => ($"“{e.Text}”", CommandBrush),
            VoiceLogKind.Early => ($"⚡ “{e.Text}”", EarlyBrush),
            VoiceLogKind.Ignored => ($"“{e.Text}”", IgnoredBrush),
            VoiceLogKind.Preset => ($"→ {e.Text} preset", PresetBrush),
            VoiceLogKind.Paused or VoiceLogKind.Resumed => (e.Text, PresetBrush),
            VoiceLogKind.Error => ($"! {e.Text}", ErrorBrush),
            _ => (e.Text, InfoBrush),
        };
        Add(text, e.Action, brush, e.Time);
        UpdateVoiceState();
    }

    void Add(string text, string? action, Brush brush, DateTime? time = null)
    {
        _log.Insert(0, new LogRow((time ?? DateTime.Now).ToString("HH:mm:ss"), text, action, brush));
        while (_log.Count > MaxLogRows) _log.RemoveAt(_log.Count - 1);
    }

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
