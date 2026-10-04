using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Swish.App.Views;

/// <summary>3 → 2 → 1 over the blurred camera, then calibration. Cancel (button, Esc or "cancel") goes back.</summary>
public partial class CountdownPage : UserControl, ISwishPage
{
    const double Seconds = 3;
    App? _app;
    ShellWindow? _shell;
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = new();
    bool _done;

    readonly Func<UserControl>? _exitTo;   // where the calibration flow was started from (see HowToCalibratePage)

    public CountdownPage(Func<UserControl>? exitTo = null)
    {
        _exitTo = exitTo;
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
    }

    public bool Fullscreen => true;
    public IInputElement? DefaultFocus => CancelButton;

    public void OnShown(App app, ShellWindow shell)
    {
        app.SetHandsOff(false);   // calibrating needs the camera, even if hand tracking was switched off
        _app = app;
        _shell = shell;
        if (app.Voice is { } v) { v.Suspended = true; v.Heard += OnHeard; }
        PreviewKeyDown += OnKey;
        _clock.Restart();
        _timer.Start();
    }

    public void OnHidden()
    {
        _timer.Stop();
        PreviewKeyDown -= OnKey;
        if (_app?.Voice is { } v) { v.Heard -= OnHeard; v.Suspended = false; }
    }

    void Tick()
    {
        double left = Seconds - _clock.Elapsed.TotalSeconds;
        if (left <= 0)
        {
            if (_done) return;
            _done = true;
            _shell?.Navigate(new CalibrationPage(_exitTo));
            return;
        }
        Number.Text = Math.Ceiling(left).ToString();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
    }

    void OnHeard(string transcript)
    {
        if (Regex.IsMatch(transcript, @"\b(cancel|stop)\b", RegexOptions.IgnoreCase)) Dispatcher.BeginInvoke(Cancel);
    }

    void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    void Cancel()
    {
        if (_done) return;
        _done = true;
        _shell?.Navigate(new HowToCalibratePage(_exitTo));
    }
}
