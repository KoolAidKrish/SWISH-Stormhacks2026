using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HandGestureRecognition;
using VoiceKeys;

namespace Swish.App;

/// <summary>
/// One process, two engines:
///   GestureEngine  webcam → hand landmarks → mouse        (its own thread)
///   VoiceEngine    mic → ElevenLabs STT → keystrokes      (async, WebSocket thread)
/// This class owns both, the tray icon and the dashboard window. Either engine can fail on its
/// own (no camera, no API key) without taking the other down.
///
/// The voice "gestures" commands still press Ctrl+Alt+M/J/V, which the gesture engine polls,
/// so the two talk to each other the same way here as when they ran as separate programs.
/// </summary>
public partial class App : Application
{
    Mutex? _singleInstance;
    TrayIcon? _tray;
    MainWindow? _window;
    DispatcherTimer? _trayTimer;
    readonly CancellationTokenSource _voiceStop = new();
    Task? _voiceTask;

    public GestureEngine Gestures { get; private set; } = null!;
    public VoiceEngine? Voice { get; private set; }
    /// <summary>Why voice isn't running (missing API key, bad preset file...), for the UI.</summary>
    public string? VoiceProblem { get; private set; }
    public bool IsExiting { get; private set; }

    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWISH");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\SWISH.App", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("SWISH is already running. Look for it in the system tray.", "SWISH");
            Shutdown();
            return;
        }
        Directory.CreateDirectory(DataDir);
        GestureEngine.DisableBackgroundThrottling();

        // ---- Hand gestures ----
        Gestures = new GestureEngine(new GestureOptions
        {
            ModelDir = Path.Combine(AppContext.BaseDirectory, "Model"),
            CalibrationPath = Path.Combine(DataDir, "calibration.json"),
            RecordPath = null,          // the console app records output.mp4; no need here
            UseOpenCvWindow = false,    // the dashboard shows the preview instead
        });
        Gestures.ToggleWindowRequested += () => Dispatcher.BeginInvoke(ToggleWindow);
        Gestures.QuitRequested += () => Dispatcher.BeginInvoke(Shutdown);
        Gestures.Faulted += ex => Dispatcher.BeginInvoke(() => _tray?.Notify("Hand tracking stopped", ex.Message));

        // ---- Voice ----
        Voice = TryCreateVoice();

        // ---- UI ----
        _tray = new TrayIcon(
            showWindow: ShowWindow,
            toggleHandMouse: Gestures.ToggleMouse,
            toggleVoice: () => { if (Voice is not null) Voice.Paused = !Voice.Paused; },
            choosePreset: id => { if (Voice?.Config.FindPreset(id) is { } p) Voice.SwitchPreset(p); },
            recalibrate: () => Gestures.Recalibrate(),
            openPresetsFolder: () => Process.Start("explorer.exe", Path.Combine(AppContext.BaseDirectory, "presets")),
            exit: Shutdown);
        if (Voice is not null) _tray.SetPresets(Voice.SwitchablePresets.Select(p => (p.Id, p.Name)));
        _trayTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateTray(), Dispatcher);
        _trayTimer.Start();

        // The window subscribes to engine events, so it exists before they start.
        _window = new MainWindow(this);
        _window.Show();

        Gestures.Start();
        if (Voice is not null)
            _voiceTask = Task.Run(() => Voice.RunAsync(_voiceStop.Token));
    }

    VoiceEngine? TryCreateVoice()
    {
        AppConfig cfg;
        try
        {
            cfg = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"), Path.Combine(AppContext.BaseDirectory, "presets"));
        }
        catch (Exception ex)
        {
            VoiceProblem = $"Couldn't load voice settings: {ex.Message}";
            return null;
        }

        // setx only reaches processes started afterwards (and Visual Studio caches its environment),
        // so fall back to reading the user environment directly.
        var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")
            ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            VoiceProblem = "Set your ElevenLabs key, then restart SWISH:\nsetx ELEVENLABS_API_KEY \"your-key\"";
            return null;
        }

        try { return new VoiceEngine(cfg, apiKey, cfg.FindPreset(cfg.Preset)!); }
        catch (Exception ex)
        {
            VoiceProblem = $"Voice couldn't start: {ex.Message}";
            return null;
        }
    }

    void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void ToggleWindow()
    {
        if (_window is null) return;
        if (_window.IsVisible) _window.Hide(); else ShowWindow();
    }

    void UpdateTray() =>
        _tray?.SetState(Gestures.Status.MouseEnabled, Voice is null ? null : !Voice.Paused, Voice?.ActivePreset.Id);

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _trayTimer?.Stop();
        _voiceStop.Cancel();
        try { _voiceTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down anyway */ }
        Voice?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        Gestures?.Dispose();   // stops the loop, releases the mouse button and the camera
        _window?.Close();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
