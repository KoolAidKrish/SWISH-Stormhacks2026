using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HandGestureRecognition;
using Swish.App.Views;
using VoiceKeys;

namespace Swish.App.Controls;

/// <summary>Which menu screen the sidebar sits beside.</summary>
public enum MenuSection { Controls, Settings, Editor }

/// <summary>
/// The menu's left column, shared by Controls, Settings and the command editor: the game list, a live
/// status panel (hands in view, voice state, mic level, last thing heard), Pause, and Settings.
/// Picking a game switches the voice preset; away from the Controls screen it also goes back there.
/// </summary>
public partial class MenuSidebar : UserControl
{
    App? _app;
    ShellWindow? _shell;
    MenuSection _section;
    Func<bool>? _canLeave;
    bool _loading;
    HandsFrame? _latest;
    string? _heard;
    float _level;
    readonly DispatcherTimer _timer;

    /// <summary>The selected game changed (Controls screen only: it rebuilds for the new game).</summary>
    public event Action<Preset>? GameChanged;

    public MenuSidebar()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => ShowStatus(), Dispatcher);
        SizeChanged += (_, _) => DrawDecor();
        Unloaded += (_, _) => Detach();
    }

    public Preset? SelectedGame => (Games.SelectedItem as ListBoxItem)?.Tag as Preset;

    /// <summary>The selected game's tile, for initial keyboard focus.</summary>
    public IInputElement? FocusTarget => Games.SelectedItem as IInputElement ?? SettingsButton;

    /// <param name="canLeave">Asked before leaving this screen (e.g. the editor confirms unsaved changes).</param>
    public void Attach(App app, ShellWindow shell, MenuSection section, Func<bool>? canLeave = null)
    {
        Detach();
        _app = app;
        _shell = shell;
        _section = section;
        _canLeave = canLeave;

        _loading = true;
        var games = (app.Config?.Presets ?? []).Where(p => p.Game is not null).OrderBy(p => p.Game!.Order).ToList();
        // Real ListBoxItems so screen readers (and arrow keys) get each game's name.
        Games.ItemsSource = games.Select(g =>
        {
            var item = new ListBoxItem { Content = GameTile(g), Tag = g, Style = (Style)FindResource("GameTile") };
            System.Windows.Automation.AutomationProperties.SetName(item, g.Game!.Title);
            return item;
        }).ToList();
        var current = games.FindIndex(g => g.Id == app.CurrentGame);
        Games.SelectedIndex = current >= 0 ? current : (games.Count > 0 ? 0 : -1);
        if (current < 0 && SelectedGame is { } first) app.SelectGame(first.Id);
        _loading = false;

        // The settings button reads as "you are here" on the settings screen.
        SettingsButton.Background = section == MenuSection.Settings ? (Brush)FindResource("Blue") : Brushes.Transparent;

        app.Gestures.HandsUpdated += OnHands;
        if (app.Voice is { } voice) { voice.Heard += OnHeard; voice.MicLevel += OnLevel; }
        _timer.Start();
        ShowStatus();
    }

    void Detach()
    {
        _timer.Stop();
        if (_app is null) return;
        _app.Gestures.HandsUpdated -= OnHands;
        if (_app.Voice is { } voice) { voice.Heard -= OnHeard; voice.MicLevel -= OnLevel; }
    }

    // ---- Actions ----

    void OnGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _app is null || SelectedGame is not { } preset) return;
        if (_section != MenuSection.Controls)
        {
            if (_canLeave?.Invoke() == false)
            {
                _loading = true;   // stay put: put the selection back
                Games.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
                _loading = false;
                return;
            }
            _app.SelectGame(preset.Id);
            _shell?.Navigate(new ControlsPage());
            return;
        }
        _app.SelectGame(preset.Id);
        GameChanged?.Invoke(preset);
    }

    void OnMoreGames(object sender, RoutedEventArgs e) =>
        SwishDialog.Inform(Window.GetWindow(this), "MORE",
            "Adding your own games and apps is coming soon. For now SWISH ships with Desktop, Minecraft, Aimlabs, Rocket League and Bloons TD 6.");

    void OnPause(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;
        _app.SetPaused(!_app.IsPaused);   // partly off (just hands) → all off; all off → all on
        ShowStatus();
    }

    void OnHands(object sender, RoutedEventArgs e)
    {
        if (_app is null) return;
        _app.SetHandsOff(!_app.HandsOff);
        ShowStatus();
    }

    void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_section == MenuSection.Settings || _canLeave?.Invoke() == false) return;
        _shell?.Navigate(new SettingsPage());
    }

    // ---- Live status ----

    void OnHands(HandsFrame frame) => Volatile.Write(ref _latest, frame);          // engine thread
    void OnHeard(string text) => Volatile.Write(ref _heard, text.Trim());          // WebSocket thread
    void OnLevel(float level) => Volatile.Write(ref _level, level);               // audio thread

    void ShowStatus()
    {
        if (_app is null) return;
        Brush pink = (Brush)FindResource("Pink");
        bool paused = _app.IsPaused;
        PauseText.Text = paused ? "▶   RESUME" : "❚❚   PAUSE";
        PauseButton.BorderBrush = PauseText.Foreground = paused ? pink : (Brush)FindResource("White");
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, paused ? "Resume SWISH" : "Pause SWISH: camera and microphone off");
        bool handsOff = _app.HandsOff;
        HandsSlash.Visibility = handsOff ? Visibility.Visible : Visibility.Collapsed;
        HandsButton.BorderBrush = handsOff ? pink : (Brush)FindResource("White");
        HandsButton.ToolTip = handsOff ? "Turn hand tracking back on" : "Turn hand tracking off (camera closed); voice keeps working";
        System.Windows.Automation.AutomationProperties.SetName(HandsButton, handsOff ? "Turn hand tracking on" : "Turn hand tracking off");

        // Hands
        var f = Volatile.Read(ref _latest);
        int hands = f is null ? 0 : (f.Right is null ? 0 : 1) + (f.Left is null ? 0 : 1);
        var status = _app.Gestures.Status;
        bool tracking = status.Error is null;
        HandsText.Text = handsOff ? "OFF" : !tracking ? "NO CAMERA" : !status.Running ? "STARTING" : hands == 0 ? "NONE IN VIEW" : hands == 1 ? "1 IN VIEW" : "2 IN VIEW";
        Paint(HandsDot, HandsLabel, HandsText, handsOff ? State.Off : !tracking ? State.Error : !status.Running ? State.Waiting : State.Active);

        // Voice
        if (_app.Voice is not { } voice)
        {
            VoiceText.Text = "OFF";
            Paint(VoiceDot, VoiceLabel, VoiceText, State.Off);
            LevelBar.Width = 0;
            HeardText.Text = "No ElevenLabs key set";
            return;
        }
        VoiceText.Text = voice.MicOff ? "MIC OFF" : voice.Paused ? "PAUSED" : !voice.Connected ? "CONNECTING" : "LISTENING";
        Paint(VoiceDot, VoiceLabel, VoiceText, voice.MicOff || voice.Paused ? State.Off : voice.Connected ? State.Active : State.Waiting);
        HeardText.Foreground = (Brush)FindResource(voice.MicOff || voice.Paused ? "Dim" : "Gray");
        double width = Math.Max(0, ((FrameworkElement)LevelBar.Parent).ActualWidth);
        LevelBar.Width = width * (voice.Paused || voice.MicOff ? 0 : Math.Clamp(Volatile.Read(ref _level), 0, 1));
        if (Volatile.Read(ref _heard) is { Length: > 0 } heard) HeardText.Text = $"“{heard}”";
    }

    enum State { Active, Waiting, Off, Error }

    /// <summary>One status row: pink while active, greyed out when off or paused, muted while starting, red on an error.</summary>
    void Paint(System.Windows.Shapes.Ellipse dot, TextBlock label, TextBlock value, State state)
    {
        dot.Fill = (Brush)FindResource(state switch { State.Active => "Pink", State.Error => "Danger", State.Waiting => "Muted", _ => "Dim" });
        label.Foreground = (Brush)FindResource(state == State.Off ? "Dim" : "White");
        value.Foreground = (Brush)FindResource(state switch { State.Active => "Pink", State.Error => "Danger", State.Waiting => "Muted", _ => "Dim" });
    }

    // ---- Look ----

    /// <summary>Blue line down the right edge with a pink hairline beside it.</summary>
    void DrawDecor()
    {
        Decor.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (h < 200) return;
        // Two straight parallel lines, full height.
        Decor.Children.Add(Trace((Brush)FindResource("Blue"), 3, (w - 1.5, 0), (w - 1.5, h)));
        Decor.Children.Add(Trace((Brush)FindResource("Pink"), 1, (w - 8, 0), (w - 8, h)));
    }

    static Polyline Trace(Brush stroke, double thickness, params (double X, double Y)[] pts) =>
        new() { Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Miter,
                Points = new PointCollection(pts.Select(p => new Point(p.X, p.Y))) };

    FrameworkElement GameTile(Preset preset)
    {
        // Banner art is shown whole (no cropping of the logo); placeholders match its height.
        var tile = new Grid { Tag = preset, ToolTip = preset.Game!.Title };
        // Rounded corners: clip the art to a rounded rectangle at whatever size the tile lays out to.
        tile.SizeChanged += (_, e) => tile.Clip = new RectangleGeometry(new Rect(e.NewSize), 6, 6);
        if (LoadAsset($"Games/{preset.Game.Tile}") is { } image)
        {
            tile.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform });
        }
        else
        {
            // Placeholder until real tile art is supplied: a tinted block with the game's name.
            tile.Height = 48;
            tile.Children.Add(new Border { Background = Placeholder(preset.Game.Order) });
            tile.Children.Add(new TextBlock
            {
                Text = preset.Game.Title.ToUpperInvariant(), Style = (Style)FindResource("Display"), FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0),
            });
        }
        return tile;
    }

    static Brush Placeholder(int order) => new LinearGradientBrush(
        (Color)ColorConverter.ConvertFromString(order switch { 0 => "#1761FF", 1 => "#3E6B2F", 2 => "#2E3E7A", 3 => "#1F4E7A", _ => "#6A3A2A" }),
        (Color)ColorConverter.ConvertFromString("#1E1E1E"), 0);

    /// <summary>An image from SWISH.App/Assets, or null if it hasn't been supplied (callers show a placeholder).</summary>
    public static ImageSource? LoadAsset(string relative)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{relative}");
            if (Application.GetResourceStream(uri) is null) return null;
            return new BitmapImage(uri);
        }
        catch (Exception) { return null; }   // asset not supplied yet → placeholder
    }
}
