using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using HandGestureRecognition;
using HandGestureRecognition.Mouse;
using Swish.App.Services;
using Swish.App.Views;
using VoiceKeys;

namespace Swish.App;

/// <summary>
/// One process, two engines:
///   GestureEngine  webcam → hand landmarks → mouse        (its own thread)
///   VoiceEngine    mic → ElevenLabs STT → keystrokes      (async, WebSocket thread)
/// This class owns both, the tray icon, the main SWISH window (ShellWindow) and the debug dashboard
/// (MainWindow, reachable from Settings › Advanced). Either engine can fail on its own (no camera,
/// no API key) without taking the other down.
/// </summary>
public partial class App : Application
{
    Mutex? _singleInstance;
    TrayIcon? _tray;
    ShellWindow? _shell;
    MainWindow? _debug;
    DispatcherTimer? _trayTimer;
    readonly CancellationTokenSource _voiceStop = new();
    Task? _voiceTask;

    public GestureEngine Gestures { get; private set; } = null!;
    public VoiceEngine? Voice { get; private set; }
    /// <summary>Presets and voice settings, loaded even when voice itself can't run (no API key).</summary>
    public AppConfig? Config { get; private set; }
    /// <summary>Why voice isn't running (missing API key, bad preset file...), for the UI.</summary>
    public string? VoiceProblem { get; private set; }
    public bool IsExiting { get; private set; }
    /// <summary>The user's custom gesture/voice functions.</summary>
    public CustomFunctionManager CustomFunctions { get; private set; } = null!;
    public UiSettings Settings { get; private set; } = new();
    /// <summary>Built-in hand controls the user rebound, per game.</summary>
    public HandBindings HandBindings { get; private set; } = null!;

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWISH");
    string CalibrationPath => Path.Combine(DataDir, "calibration.json");

    // There's one stream of preview frames. Whichever view claimed it most recently gets them; each
    // claim says whether it wants the plain picture (styled UI) or the annotated debug view.
    readonly List<(object Owner, bool Clean)> _previewClaims = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Dev aid: write the splash animation's frames to PNGs and exit (no camera, mic or tray).
        //   SWISH.exe --render-splash <folder>
        int render = Array.IndexOf(e.Args, "--render-splash");
        if (render >= 0)
        {
            var folder = render + 1 < e.Args.Length ? e.Args[render + 1] : "splash-frames";
            new SplashWindow().RenderFrames(folder, new[] { 0.0, 0.3, 0.6, 0.9, 1.2, 1.6, 2.4, 3.2, 3.5, 3.8 });
            Shutdown();
            return;
        }

        // An unexpected error in a screen shouldn't fail silently or take the engines down: log it and say so.
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(DataDir, "errors.log"), $"{DateTime.Now:u} {args.Exception}\n\n"); } catch { }
            MessageBox.Show($"Something went wrong:\n\n{args.Exception.Message}\n\nDetails were saved to errors.log in {DataDir}.",
                            "SWISH", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _singleInstance = new Mutex(true, @"Local\SWISH.App", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("SWISH is already running. Look for it in the system tray.", "SWISH");
            Shutdown();
            return;
        }
        Directory.CreateDirectory(DataDir);
        GestureEngine.DisableBackgroundThrottling();
        Settings = UiSettings.Load(DataDir);
        HandBindings = HandBindings.Load(DataDir);

        // ---- Hand gestures ----
        Gestures = new GestureEngine(new GestureOptions
        {
            Device = Settings.Camera,
            GpuAdapter = Services.GpuDevices.List().FirstOrDefault(g => g.Name == Settings.Gpu)?.Index,
            ModelDir = Path.Combine(AppContext.BaseDirectory, "Model"),
            CalibrationPath = CalibrationPath,
            UseOpenCvWindow = false,        // the app draws its own preview
            CalibrateWhenMissing = false,   // the app has its own calibration screens
        });
        Gestures.ToggleWindowRequested += () => Dispatcher.BeginInvoke(ToggleWindow);
        Gestures.QuitRequested += () => Dispatcher.BeginInvoke(Shutdown);
        Gestures.Faulted += ex => Dispatcher.BeginInvoke(() => _tray?.Notify("Hand tracking stopped", ex.Message));

        // ---- Voice ----
        Voice = TryCreateVoice();

        ApplyHandBindings();   // the current game's rebound hand controls (needs the game, from voice settings)

        // ---- Custom functions (gestures + voice phrases) ----
        CustomFunctions = new CustomFunctionManager(DataDir, Gestures, Voice);
        CustomFunctions.Load();

        // ---- UI ----
        _tray = new TrayIcon(
            showWindow: ShowWindow,
            toggleHandMouse: Gestures.ToggleMouse,
            toggleVoice: () => { if (Voice is not null) Voice.Paused = !Voice.Paused; },
            choosePreset: SelectGame,
            recalibrate: () => { ShowWindow(); _shell?.Navigate(new HowToCalibratePage(exitTo: () => new ControlsPage())); },
            customFunctions: () => ShowCommandEditor(),
            openPresetsFolder: () => Process.Start("explorer.exe", Path.Combine(AppContext.BaseDirectory, "presets")),
            exit: Shutdown);
        if (Config is not null) _tray.SetPresets(Config.Presets.Where(p => p.Switchable).Select(p => (p.Id, p.Name)));
        _trayTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateTray(), Dispatcher);
        _trayTimer.Start();

        // The debug dashboard subscribes to engine events at construction, so it exists (hidden) from the start.
        _debug = new MainWindow(this);
        _shell = new ShellWindow(this);
        _shell.Navigate(HasCalibration() ? new ControlsPage() : new HomePage());

        if (e.Args.Contains("--no-splash"))
        {
            ShowWindow();
        }
        else
        {
            // The engines start loading behind the splash; the main window appears when it ends.
            var splash = new SplashWindow();
            splash.Finished += () => { if (!IsExiting) ShowWindow(); };
            splash.Show();
        }

        Gestures.Start();
        if (Voice is not null)
            _voiceTask = Task.Run(() => Voice.RunAsync(_voiceStop.Token));
    }

    VoiceEngine? TryCreateVoice()
    {
        try
        {
            Config = AppConfig.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"), Path.Combine(AppContext.BaseDirectory, "presets"));
        }
        catch (Exception ex)
        {
            VoiceProblem = $"Couldn't load voice settings: {ex.Message}";
            return null;
        }
        Config.MicDevice = Settings.Microphone;
        if (Settings.SpeechVoice is { Length: > 0 } speechVoice) Config.Speech.VoiceId = speechVoice;
        if (Settings.SpeechVolume is { } speechVolume) Config.Speech.Volume = Math.Clamp(speechVolume, 0, 1);
        if (Settings.Game is { } game && Config.FindPreset(game) is not null) Config.Preset = game;

        // setx only reaches processes started afterwards (and Visual Studio caches its environment),
        // so fall back to reading the user environment directly.
        var apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")
            ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            VoiceProblem = "Set your ElevenLabs key, then restart SWISH:\nsetx ELEVENLABS_API_KEY \"your-key\"";
            return null;
        }

        try { return new VoiceEngine(Config, apiKey, Config.FindPreset(Config.Preset)!) { SpeechMuted = Settings.SpeechMuted }; }
        catch (Exception ex)
        {
            VoiceProblem = $"Voice couldn't start: {ex.Message}";
            return null;
        }
    }

    // ---- Shared actions used by the screens ----

    /// <summary>Is there a saved calibration for this screen? (Then launch skips the calibration flow.)</summary>
    public bool HasCalibration()
    {
        var (w, h) = NativeMouse.PrimaryScreenSize();
        return CalibrationData.TryLoad(CalibrationPath, w, h) is not null;
    }

    /// <summary>The game whose controls are shown (and whose voice preset is active).</summary>
    public string? CurrentGame => Voice?.ActivePreset.Id ?? Settings.Game ?? Config?.Preset;

    public void SelectGame(string presetId)
    {
        if (Config?.FindPreset(presetId) is not { } preset) return;
        Voice?.SwitchPreset(preset);
        Settings.Game = presetId;
        Settings.Save(DataDir);
        ApplyHandBindings();
    }

    readonly object _handsGate = new();

    /// <summary>Hand tracking is off: camera closed, models stopped (no CPU), nothing sent.</summary>
    public bool HandsOff { get; private set; }
    /// <summary>The microphone is off: nothing recorded or transcribed (or there's no voice at all).</summary>
    public bool VoiceOff => Voice?.MicOff ?? true;
    /// <summary>Paused: both off. Nothing is recorded, transcribed or sent.</summary>
    public bool IsPaused => HandsOff && VoiceOff;

    public void SetHandsOff(bool off)
    {
        if (HandsOff == off) return;
        HandsOff = off;
        // Opening/closing the camera takes a moment; keep it off the UI thread, one switch at a time.
        Task.Run(() => { lock (_handsGate) { if (HandsOff) Gestures.Stop(); else Gestures.Start(); } });
    }

    public void SetPaused(bool paused)
    {
        SetHandsOff(paused);
        if (Voice is not null) Voice.MicOff = paused;
    }

    /// <summary>Pushes the current game's rebound hand controls to the gesture engine (defaults where not rebound).</summary>
    public void ApplyHandBindings()
    {
        var game = CurrentGame;
        ushort[]? Keys(string id) =>
            HandBindings.Get(game, id) is { } chord ? (TryParseChord(chord) is { } keys ? keys : null) : null;
        var fingers = HandControl.All.Where(c => c.Finger is not null && Keys(c.Id) is not null)
            .ToDictionary(c => c.Finger!.Value, c => Keys(c.Id)!);
        Gestures.SetHandBindings(fingers, Keys("pinch-index"), Keys("pinch-middle"), Keys("pinch-ring"));
    }

    static ushort[]? TryParseChord(string chord)
    {
        try { return KeySender.ParseChord(chord).ToArray(); }
        catch (FormatException) { return null; }
    }

    public void SelectCamera(int index)
    {
        Settings.Camera = index;
        Settings.Save(DataDir);
        Task.Run(() => Gestures.SwitchCamera(index));   // closing a camera can take a moment
    }

    /// <summary>Runs the hand models on this GPU, or the CPU (null). The engine reloads them on its own thread.</summary>
    public void SelectGpu(Services.GpuDevices.Gpu? gpu)
    {
        Settings.Gpu = gpu?.Name;
        Settings.Save(DataDir);
        Task.Run(() => Gestures.SwitchGpu(gpu?.Index));   // restarting tracking (camera + models) takes a moment
    }

    public void SelectMicrophone(int index)
    {
        Settings.Microphone = index;
        Settings.Save(DataDir);
        if (Voice is not null) Voice.SwitchMic(index);
        else if (Config is not null) Config.MicDevice = index;
    }

    /// <summary>Opens the command editor in the main window: editing a saved command, or a new one
    /// (pre-set to a game/category, and starting with a gesture or a phrase as its trigger).</summary>
    /// <param name="template">A new command pre-filled from this (e.g. a built-in control's card); not saved until the user saves.</param>
    public void ShowCommandEditor(CustomFunction? edit = null, string? game = null, string? category = null, bool gesture = false,
                                  CustomFunction? template = null)
    {
        ShowWindow();
        _shell?.Navigate(new CommandEditorPage(edit, game ?? template?.Game ?? CurrentGame, category, gesture, template));
    }

    public void ShowDebugDashboard()
    {
        if (_debug is null) return;
        _debug.Show();
        if (_debug.WindowState == WindowState.Minimized) _debug.WindowState = WindowState.Normal;
        _debug.Activate();
    }

    // ---- Preview ownership ----

    public void ClaimPreview(object owner, bool clean = true)
    {
        _previewClaims.RemoveAll(c => c.Owner == owner);
        _previewClaims.Add((owner, clean));
        UpdatePreview();
    }

    public void ReleasePreview(object owner)
    {
        _previewClaims.RemoveAll(c => c.Owner == owner);
        UpdatePreview();
    }

    /// <summary>Should this view take the preview frames right now?</summary>
    public bool OwnsPreview(object owner) => _previewClaims.Count > 0 ? _previewClaims[^1].Owner == owner : owner == _debug;

    public bool PreviewClaimed => _previewClaims.Count > 0;

    /// <summary>Preview frames are only produced while someone can see them; plain or annotated per the owner.</summary>
    public void UpdatePreview()
    {
        bool debugSeen = _debug is { IsVisible: true } d && d.WindowState != WindowState.Minimized;
        Gestures.PreviewEnabled = _previewClaims.Count > 0 || debugSeen;
        Gestures.PreviewClean = _previewClaims.Count > 0 && _previewClaims[^1].Clean;
    }

    void ShowWindow()
    {
        if (_shell is null) return;
        _shell.Show();
        if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal;
        _shell.Activate();
    }

    void ToggleWindow()
    {
        if (_shell is null) return;
        if (_shell.IsVisible) _shell.Hide(); else ShowWindow();
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
        _shell?.Close();
        _debug?.Close();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
