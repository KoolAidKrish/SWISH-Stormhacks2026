using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Swish.App.Views;

/// <summary>
/// Explains calibration next to a looping clip of a real one (Assets/Video/calibration-demo.mp4).
/// Starts on click, Enter, or saying "start" (voice commands are suspended here, so "start" doesn't
/// also press a game key).
/// </summary>
public partial class HowToCalibratePage : UserControl, ISwishPage
{
    App? _app;
    ShellWindow? _shell;
    bool _leaving;

    readonly Func<UserControl>? _exitTo;

    /// <param name="exitTo">Where Esc (and finishing) goes: the screen calibration was started from. Default: Home.</param>
    public HowToCalibratePage(Func<UserControl>? exitTo = null)
    {
        InitializeComponent();
        Steps.ItemsSource = new[]
        {
            "Enable webcam and microphone permissions.",
            "Move your hand into view.",
            "Trace the ball's movement with your palm.",
            "Avoid moving too quickly.",
            "Make sure your lighting is good.",
        }.Select((t, i) => new { Number = $"{i + 1}.", Text = t }).ToList();

        _exitTo = exitTo;
        var clip = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Video", "calibration-demo.mp4");
        if (System.IO.File.Exists(clip)) Demo.Source = new Uri(clip);
    }

    public IInputElement? DefaultFocus => StartButton;

    public void OnShown(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        _leaving = false;
        if (app.Voice is { } voice)
        {
            voice.Suspended = true;                 // "start" moves this screen on; it mustn't also press a game key
            voice.SetExtraHints(["start", "cancel"]);
            voice.Heard += OnHeard;
        }
        else
        {
            StartText.Text = "CLICK TO START";
            VoiceNote.Text = "Voice is off: " + (app.VoiceProblem ?? "unavailable");
            VoiceNote.Visibility = Visibility.Visible;
        }
        Demo.Play();
        PreviewKeyDown += OnKey;
    }

    public void OnHidden()
    {
        Demo.Stop();
        PreviewKeyDown -= OnKey;
        if (_app?.Voice is { } voice)
        {
            voice.Heard -= OnHeard;
            voice.SetExtraHints([]);
            voice.Suspended = false;
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _shell?.Navigate(_exitTo?.Invoke() ?? new HomePage()); e.Handled = true; }
    }

    /// <summary>Voice thread: "start", "let's start", "start calibrating"... all count.</summary>
    void OnHeard(string transcript)
    {
        var words = Regex.Replace(transcript.ToLowerInvariant(), @"[^a-z ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Contains("start") && words.Length <= 4) Dispatcher.BeginInvoke(Start);
    }

    void OnStart(object sender, RoutedEventArgs e) => Start();

    void Start()
    {
        if (_leaving) return;
        _leaving = true;
        _shell?.Navigate(new CountdownPage(_exitTo));
    }

    void OnDemoEnded(object sender, RoutedEventArgs e)
    {
        Demo.Position = TimeSpan.Zero;   // loop
        Demo.Play();
    }

    internal static Path XMark(Point c, double r, double thickness) => new()
    {
        Stroke = Brushes.White,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Square,
        StrokeEndLineCap = PenLineCap.Square,
        Data = new GeometryGroup
        {
            Children =
            {
                new LineGeometry(new Point(c.X - r, c.Y - r), new Point(c.X + r, c.Y + r)),
                new LineGeometry(new Point(c.X - r, c.Y + r), new Point(c.X + r, c.Y - r)),
            },
        },
    };
}
