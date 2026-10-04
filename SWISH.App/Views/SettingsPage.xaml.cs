using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Swish.App.Services;
using VoiceKeys;

namespace Swish.App.Views;

/// <summary>
/// Camera (real Media Foundation device list; switching restarts hand tracking on it), microphone (real
/// device list + live level meter), GPU (placeholder: tracking is CPU-only today), and Advanced → debug dashboard.
/// </summary>
public partial class SettingsPage : UserControl, ISwishPage
{
    const int Segments = 34;
    App? _app;
    ShellWindow? _shell;
    bool _loading, _testing;
    float _level;
    readonly Rectangle[] _bars = new Rectangle[Segments];
    readonly System.Windows.Threading.DispatcherTimer _meterTimer;
    MicCapture? _testMic;   // used only when voice isn't running, so the meter still works
    readonly System.Windows.Threading.DispatcherTimer _gpuTimer;

    public SettingsPage()
    {
        InitializeComponent();
        for (int i = 0; i < Segments; i++)
            _bars[i] = new Rectangle { Margin = new Thickness(1.5, 0, 1.5, 0), Stroke = (Brush)FindResource("Gray"), StrokeThickness = 1 };
        Meter.ItemsSource = _bars;
        _gpuTimer = new(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Background, (_, _) => ShowGpuNote(), Dispatcher);
        _meterTimer = new(TimeSpan.FromMilliseconds(50), System.Windows.Threading.DispatcherPriority.Render, (_, _) => DrawMeter(), Dispatcher);
    }

    public IInputElement? DefaultFocus => CameraBox;

    public void OnShown(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        _loading = true;

        var cameras = CameraDevices.List();
        Sidebar.Attach(app, shell, Controls.MenuSection.Settings);
        CameraBox.ItemsSource = cameras.Select(c => new ComboBoxItem { Content = c.Name.ToUpperInvariant(), Tag = c.Index, ToolTip = c.FullName }).ToList();
        CameraBox.SelectedIndex = Math.Clamp(app.Gestures.Device, 0, Math.Max(0, cameras.Count - 1));

        var mics = AudioDevices.List();
        MicBox.ItemsSource = mics.Select(m => new ComboBoxItem { Content = m.Name.ToUpperInvariant(), Tag = m.Index, ToolTip = m.FullName }).ToList();
        MicBox.SelectedIndex = mics.Count == 0 ? -1 : Math.Clamp(app.Settings.Microphone, 0, mics.Count - 1);

        // GPU: the CPU, or any DirectX 12 GPU (DirectML), labelled discrete/integrated.
        var gpus = GpuDevices.List();
        GpuBox.ItemsSource = new[] { new ComboBoxItem { Content = "CPU", Tag = null } }
            .Concat(gpus.Select(g => new ComboBoxItem { Content = $"{g.Name.ToUpperInvariant()} ({(g.Discrete ? "DISCRETE" : "INTEGRATED")})", Tag = g }))
            .ToList();
        GpuBox.SelectedIndex = app.Gestures.GpuAdapter is { } adapter ? Math.Max(0, gpus.ToList().FindIndex(g => g.Index == adapter) + 1) : 0;
        ShowGpuNote();
        _gpuTimer.Start();

        ShowSpeech(app);

        _loading = false;
        PreviewKeyDown += OnKey;
    }

    public void OnHidden()
    {
        StopTest();
        _gpuTimer.Stop();
        PreviewKeyDown -= OnKey;
        _app = null;   // tells a still-loading voice list that the page is gone
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _shell?.Navigate(new ControlsPage()); e.Handled = true; }
    }

    void OnBack(object sender, RoutedEventArgs e) => _shell?.Navigate(new ControlsPage());

    void OnRecalibrate(object sender, RoutedEventArgs e) => _shell?.Navigate(new HowToCalibratePage(exitTo: () => new SettingsPage()));
    void OnDebug(object sender, RoutedEventArgs e) => _app?.ShowDebugDashboard();

    void OnCamera(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _app is null || CameraBox.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        _app.SelectCamera(index);
    }

    void OnMic(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _app is null || MicBox.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        bool wasTesting = _testing;
        StopTest();
        _app.SelectMicrophone(index);
        if (wasTesting) StartTest();
    }

    void OnGpu(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _app is null || GpuBox.SelectedItem is not ComboBoxItem item) return;
        _app.SelectGpu(item.Tag as GpuDevices.Gpu);
        ShowGpuNote();
    }

    /// <summary>Says where tracking really runs (the engine reloads its models after a switch, and may fall back to the CPU).</summary>
    void ShowGpuNote()
    {
        if (_app is null) return;
        string where = _app.Gestures.InferenceDevice;
        GpuNote.Text = where switch
        {
            "" => "Loading the hand models…",
            "GPU" => "Hand tracking runs on the GPU, which frees up the CPU for your game.",
            "CPU (GPU unavailable)" => "That GPU couldn't run the hand models (it needs DirectX 12), so tracking is on the CPU.",
            _ => "Hand tracking runs on the CPU. A GPU takes that load off the processor.",
        };
        GpuNote.Foreground = (Brush)FindResource(where == "CPU (GPU unavailable)" ? "Danger" : "Muted");
    }

    // ---- Voice assistant ----

    // Stock ElevenLabs voices, shown if the account's voice list can't be fetched.
    static readonly (string Id, string Name)[] FallbackVoices =
    [
        ("JBFqnCBsd6RMkjVDRZzb", "George"), ("21m00Tcm4TlvDq8ikWAM", "Rachel"), ("pNInz6obpgDQGcFmaJgB", "Adam"),
        ("XB0fDUnXU5powFXDhCwa", "Charlotte"), ("onwK4e9ZLuTAKqWW03F9", "Daniel"), ("ErXwobaYiN019PkySvjV", "Antoni"),
    ];

    async void ShowSpeech(App app)
    {
        if (app.Voice is not { SpeechAvailable: true } voice)
        {
            SpeechPanel.IsEnabled = false;
            SpeechNote.Text = app.Voice is null ? "Voice is off: " + (app.VoiceProblem ?? "unavailable") : "Spoken replies are turned off in settings.json.";
            return;
        }
        (voice.SpeechMuted ? SpeechMuted : SpeechOn).IsChecked = true;
        SpeechVolume.Value = Math.Round(voice.SpeechVolume * 100);
        SpeechVolumeText.Text = $"{SpeechVolume.Value:0}%";

        // The voice list comes from the account (premade voices plus any the user made); fall back to stock ones.
        SpeechVoiceBox.ItemsSource = new[] { new ComboBoxItem { Content = "LOADING VOICES…", IsEnabled = false } };
        SpeechVoiceBox.SelectedIndex = 0;
        IReadOnlyList<(string Id, string Name)> voices;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            voices = (await voice.ListSpeechVoicesAsync(cts.Token))
                .Select(v => (v.Id, v.Description is null ? v.Name : $"{v.Name} ({v.Description})")).ToList();
            if (voices.Count == 0) voices = FallbackVoices;
        }
        catch (Exception) { voices = FallbackVoices; }
        if (_app is null) return;   // left the page meanwhile

        _loading = true;
        var items = voices.Select(v => new ComboBoxItem { Content = v.Name.ToUpperInvariant(), Tag = v.Id }).ToList();
        if (!voices.Any(v => v.Id == voice.SpeechVoiceId))
            items.Insert(0, new ComboBoxItem { Content = "CURRENT VOICE", Tag = voice.SpeechVoiceId });
        SpeechVoiceBox.ItemsSource = items;
        SpeechVoiceBox.SelectedItem = items.First(i => (string)i.Tag == voice.SpeechVoiceId);
        _loading = false;
    }

    void OnSpeechMute(object sender, RoutedEventArgs e)
    {
        if (_loading || _app?.Voice is not { } voice) return;
        voice.SpeechMuted = SpeechMuted.IsChecked == true;
        _app.Settings.SpeechMuted = voice.SpeechMuted;
        _app.Settings.Save(App.DataDir);
    }

    void OnSpeechVoice(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _app?.Voice is not { } voice || SpeechVoiceBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        voice.SetSpeechVoice(id);
        _app.Settings.SpeechVoice = id;
        _app.Settings.Save(App.DataDir);
    }

    void OnSpeechVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeechVolumeText is null) return;   // during InitializeComponent
        SpeechVolumeText.Text = $"{SpeechVolume.Value:0}%";
        if (_loading || _app?.Voice is not { } voice) return;
        voice.SpeechVolume = SpeechVolume.Value / 100;
        _app.Settings.SpeechVolume = voice.SpeechVolume;
        _app.Settings.Save(App.DataDir);
    }

    void OnTestSpeech(object sender, RoutedEventArgs e)
    {
        if (_app?.Voice is not { } voice) return;
        if (voice.SpeechMuted) { SpeechNote.Text = "Spoken replies are muted: switch them ON to hear the voice."; return; }
        voice.SayNow("Voice check. Ready when you are.");
    }

    // ---- Mic test ----

    void OnTestMic(object sender, RoutedEventArgs e)
    {
        if (_testing) StopTest(); else StartTest();
    }

    void StartTest()
    {
        if (_app is null) return;
        _testing = true;
        TestMicText.Text = "STOP";
        if (_app.Voice is { } voice) voice.MicLevel += OnLevel;
        else
        {
            _testMic = new MicCapture(_app.Settings.Microphone);
            _testMic.LevelChanged += OnLevel;
            _testMic.Start();
        }
        _meterTimer.Start();
    }

    void StopTest()
    {
        if (!_testing) return;
        _testing = false;
        TestMicText.Text = "TEST MIC";
        if (_app?.Voice is { } voice) voice.MicLevel -= OnLevel;
        _testMic?.Stop();
        _testMic?.Dispose();
        _testMic = null;
        _meterTimer.Stop();
        _level = 0;
        DrawMeter();
    }

    void OnLevel(float level) => _level = Math.Max(level, _level * 0.85f);   // audio thread; quick attack, gentle fall

    void DrawMeter()
    {
        int lit = (int)Math.Round(_level * Segments);
        var pink = (Brush)FindResource("Pink");
        for (int i = 0; i < Segments; i++) _bars[i].Fill = i < lit ? pink : Brushes.Transparent;
        _level *= 0.9f;
    }
}
