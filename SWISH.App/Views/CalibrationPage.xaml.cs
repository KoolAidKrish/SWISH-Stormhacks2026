using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HandGestureRecognition;
using HandGestureRecognition.Mouse;
using Point2f = OpenCvSharp.Point2f;

namespace Swish.App.Views;

/// <summary>
/// The calibration, drawn the SWISH way but using the existing follow-the-dot schema unchanged: the same
/// SnakePath over the primary screen, the same sampling (palm centre, the point the hand mouse follows),
/// and PursuitCalibrator.Fit for the lag-corrected, outlier-robust camera → screen mapping. The engine's
/// mouse is paused while it runs; the result is applied with GestureEngine.ApplyCalibration.
/// </summary>
public partial class CalibrationPage : UserControl, ISwishPage
{
    App? _app;
    ShellWindow? _shell;
    SnakePath _path = null!;
    int _screenW, _screenH;
    double _dpi = 1;

    readonly Stopwatch _clock = new();
    readonly ConcurrentQueue<(double T, Point2f P)> _samples = new();
    int _frames, _framesWithHand;
    double _lastHandAt;
    bool _running, _finished;

    Polyline _traced = null!;
    Grid _ball = null!;
    // Waypoint X's, which turn pink as the ball passes through them.
    readonly List<(System.Windows.Shapes.Path Mark, Point At)> _marks = new();

    readonly Func<UserControl>? _exitTo;   // where the calibration flow was started from (see HowToCalibratePage)

    public CalibrationPage(Func<UserControl>? exitTo = null)
    {
        InitializeComponent();
        _exitTo = exitTo;
    }

    public bool Fullscreen => true;
    public IInputElement? DefaultFocus => CancelButton;

    public void OnShown(App app, ShellWindow shell)
    {
        app.SetHandsOff(false);   // calibrating needs the camera, even if hand tracking was switched off
        _app = app;
        _shell = shell;
        (_screenW, _screenH) = NativeMouse.PrimaryScreenSize();
        _path = new SnakePath(_screenW, _screenH);

        app.Gestures.Recording = true;           // hand doesn't drive the mouse/keys while calibrating
        app.Gestures.HandsUpdated += OnHands;
        if (app.Voice is { } v) { v.Suspended = true; v.Heard += OnHeard; }
        PreviewKeyDown += OnKey;

        Loaded += OnLoadedOnce;
        if (IsLoaded) OnLoadedOnce(this, new RoutedEventArgs());
    }

    void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOnce;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        DrawRoute();
        Begin();
    }

    public void OnHidden()
    {
        _running = false;
        CompositionTarget.Rendering -= OnRender;
        PreviewKeyDown -= OnKey;
        if (_app is null) return;
        _app.Gestures.HandsUpdated -= OnHands;
        _app.Gestures.Recording = false;
        if (_app.Voice is { } v) { v.Heard -= OnHeard; v.Suspended = false; }
    }

    void Begin()
    {
        _samples.Clear();
        _frames = _framesWithHand = 0;
        _finished = false;
        FailBox.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        _clock.Restart();
        _running = true;
        CompositionTarget.Rendering -= OnRender;
        CompositionTarget.Rendering += OnRender;   // smooth ball, every display frame
        Keyboard.Focus(CancelButton);
    }

    // ---- Engine thread: sample the palm centre of the mouse hand ----

    void OnHands(HandsFrame frame)
    {
        if (!_running) return;
        double t = _clock.Elapsed.TotalSeconds;
        Interlocked.Increment(ref _frames);
        if (frame.Right is { } hand)
        {
            Interlocked.Increment(ref _framesWithHand);
            Volatile.Write(ref _lastHandAt, t);
            _samples.Enqueue((t, HandMouse.Anchor(hand)));
        }
    }

    // ---- UI ----

    /// <summary>An X the ball has reached turns pink, with a little pop.</summary>
    void MarkPassed(Point ball)
    {
        for (int i = _marks.Count - 1; i >= 0; i--)
        {
            var (mark, at) = _marks[i];
            if ((at - ball).Length > 16) continue;
            _marks.RemoveAt(i);
            var pink = ((SolidColorBrush)FindResource("Pink")).Color;
            mark.Stroke.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(pink, TimeSpan.FromMilliseconds(250)));
            var pop = new System.Windows.Media.Animation.DoubleAnimation(1.45, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            mark.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            mark.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    void OnRender(object? sender, EventArgs e)
    {
        if (!_running) return;
        double t = _clock.Elapsed.TotalSeconds;
        double s = _path.DistanceAt(t);
        _traced.Points = Route(0, s);
        var dot = ToDip(_path.At(t));
        Canvas.SetLeft(_ball, dot.X - _ball.Width / 2);
        Canvas.SetTop(_ball, dot.Y - _ball.Height / 2);
        MarkPassed(dot);

        bool lost = t > 1.0 && t - Volatile.Read(ref _lastHandAt) > 0.6;
        StatusText.Text = "HAND NOT IN VIEW: BRING YOUR PALM BACK TO THE BALL";
        StatusBox.Visibility = lost ? Visibility.Visible : Visibility.Collapsed;

        if (t >= _path.FollowSeconds) Finish();
    }

    async void Finish()
    {
        if (_finished) return;
        _finished = true;
        _running = false;
        CompositionTarget.Rendering -= OnRender;
        StatusBox.Visibility = Visibility.Collapsed;

        var samples = _samples.ToList();
        double coverage = _frames == 0 ? 0 : _framesWithHand / (double)_frames;
        if (coverage < 0.6)
        {
            Fail($"Your hand was out of view {100 - coverage * 100:F0}% of the time. Keep your palm in the picture and follow the ball.");
            return;
        }

        var fit = await Task.Run(() => PursuitCalibrator.Fit(samples, _path.At, SnakePath.EaseIn, _screenW, _screenH));
        if (fit.Calibration is null)
        {
            Fail(fit.Error ?? "The calibration didn't fit.");
            return;
        }
        _app!.Gestures.ApplyCalibration(fit.Calibration);
        string summary = $"Lag {fit.LagSeconds * 1000:F0} ms, typical error {fit.Calibration.MeanErrorPx:F0} px, {fit.InlierFraction:P0} of frames used";
        _shell?.Navigate(new CalibrationCompletePage(summary, _exitTo));
    }

    void Fail(string reason)
    {
        FailText.Text = reason;
        FailBox.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        Keyboard.Focus(RetryButton);
    }

    void OnRetry(object sender, RoutedEventArgs e) => _shell?.Navigate(new CountdownPage(_exitTo));
    void OnCancel(object sender, RoutedEventArgs e) => _shell?.Navigate(new HowToCalibratePage(_exitTo));

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _shell?.Navigate(new HowToCalibratePage(_exitTo)); e.Handled = true; }
    }

    void OnHeard(string transcript)
    {
        if (Regex.IsMatch(transcript, @"\bcancel\b", RegexOptions.IgnoreCase))
            Dispatcher.BeginInvoke(() => _shell?.Navigate(new HowToCalibratePage(_exitTo)));
    }

    // ---- Drawing the route ----

    void DrawRoute()
    {
        PathLayer.Children.Clear();
        _marks.Clear();
        var faint = new SolidColorBrush(Color.FromArgb(120, 230, 230, 230));
        PathLayer.Children.Add(new Polyline { Stroke = faint, StrokeThickness = 4, Points = Route(0, _path.Length) });
        _traced = new Polyline
        {
            Stroke = (Brush)FindResource("Pink"), StrokeThickness = 9,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        PathLayer.Children.Add(_traced);

        // Waypoints: the ends and middle of every row.
        for (int row = 0; row < _path.RowCount; row++)
        {
            double y = _path.RowY(row);
            foreach (var x in new[] { _path.Left, (_path.Left + _path.Right) / 2, _path.Right })
            {
                var at = ToDip(new OpenCvSharp.Point2d(x, y));
                var mark = HowToCalibratePage.XMark(at, 14, 5);
                mark.Stroke = new SolidColorBrush(Colors.White);   // animatable (the default brush is frozen)
                mark.RenderTransform = new ScaleTransform(1, 1, at.X, at.Y);
                _marks.Add((mark, at));
                PathLayer.Children.Add(mark);
            }
        }

        // The ball: teal with a white ring.
        _ball = new Grid { Width = 54, Height = 54 };
        _ball.Children.Add(new Ellipse { Fill = (Brush)FindResource("Teal") });
        _ball.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 4.5 });
        PathLayer.Children.Add(_ball);
        var start = ToDip(_path.At(0));
        Canvas.SetLeft(_ball, start.X - 27);
        Canvas.SetTop(_ball, start.Y - 27);
    }

    PointCollection Route(double from, double to)
    {
        var pts = new PointCollection();
        if (to <= from) return pts;
        int n = Math.Max(2, (int)((to - from) / 8));
        for (int i = 0; i <= n; i++) pts.Add(ToDip(_path.AtDistance(from + (to - from) * i / n)));
        return pts;
    }

    /// <summary>Screen pixels (what the calibration maps to) → this window's device-independent units.</summary>
    Point ToDip(OpenCvSharp.Point2d p) => new(p.X / _dpi, p.Y / _dpi);
}
