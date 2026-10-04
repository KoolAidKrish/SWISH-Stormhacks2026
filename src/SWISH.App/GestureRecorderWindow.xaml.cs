using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandGestureRecognition;
using HandGestureRecognition.Custom;
using HandGestureRecognition.Mouse;
using Point2f = OpenCvSharp.Point2f;

namespace Swish.App;

/// <summary>
/// Guided gesture recording, paced like the calibration: one instruction at a time, a list of steps
/// showing what's done and what's next, an animated hand acting out each motion, and a green/red border
/// for whether the hand is tracked. A step only advances while the chosen hand is in view, so nobody has
/// to rush. Ends with a review: sample count, consistency, look-alike warnings, and a live "try it" test
/// before the gesture is kept.
/// </summary>
public partial class GestureRecorderWindow : Window
{
    enum Kind { Ready, Capture, Targets, Review }
    enum Cue { Still, TiltLeft, TiltRight, TipToward, TipAway, Circle, Move, Done }

    sealed record Step(string Title, string Instruction, string Detail, Kind Kind, double Seconds, Cue Cue);

    static Step[] BuildSteps(string hand) =>
    [
        new("Get ready", $"Make your gesture with {hand} and hold it up",
            "Keep it inside the picture. Recording starts once it's been seen steadily for a moment.", Kind.Ready, 0, Cue.Still),
        new("Face the camera", "Hold it facing the camera",
            "Keep it steady, front of the gesture toward the camera.", Kind.Capture, 2.5, Cue.Still),
        new("Tilt left", "Slowly tilt it to the left",
            "Keep the gesture exactly the same and turn your wrist a little to the left, then back to centre.", Kind.Capture, 2.5, Cue.TiltLeft),
        new("Tilt right", "Now slowly tilt it to the right",
            "Same gesture, a little to the right, then back.", Kind.Capture, 2.5, Cue.TiltRight),
        new("Tip toward you", "Tip the top toward the camera",
            "Lean the fingers forward a little, as if showing the camera the top of your hand.", Kind.Capture, 2.5, Cue.TipToward),
        new("Tip away", "Tip the top away from the camera",
            "Lean the fingers back a little, then return to centre.", Kind.Capture, 2.5, Cue.TipAway),
        new("Small circles", "Make slow, small circles with it",
            "Keep the gesture and gently circle your wrist, mixing all the tilts together.", Kind.Capture, 3.5, Cue.Circle),
        new("Move it around", "Move your hand onto each dot",
            "Hold the gesture over the dot until its ring fills, then go to the next one.", Kind.Targets, 1.0, Cue.Move),
        new("Check it", "Try it: make the gesture",
            "Make the gesture and watch the bar at the top. Then try a different pose: it shouldn't be recognised.", Kind.Review, 0, Cue.Done),
    ];

    // "Move it around" targets, as fractions of the camera picture.
    static readonly System.Windows.Point[] Targets = [new(0.30, 0.35), new(0.70, 0.35), new(0.70, 0.68), new(0.30, 0.68)];
    const double TargetRadiusFraction = 0.13;    // how close (fraction of picture width) counts as "on the dot"
    const double ReadySeconds = 1.0, CountdownSeconds = 3.0;
    const int MaxSamples = 400;

    static readonly Brush Green = Frozen("#5BDB5B"), Red = Frozen("#F07178"), Amber = Frozen("#E8C46A"),
        Grey = Frozen("#7D8693"), White = Brushes.White, Current = Frozen("#25303D"), Clear = Brushes.Transparent;

    readonly App _app;
    readonly string _name;
    readonly GestureHand _hand;
    readonly IReadOnlyList<CustomGesture> _others;
    readonly Step[] _steps;
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly ConcurrentQueue<float[]> _samples = new();
    WriteableBitmap? _bitmap;
    int _frameW = 640, _frameH = 480;

    int _step;
    double _stepTime;        // seconds of this step done (only counts while the hand is tracked)
    double _readyTime;       // seconds the hand has been seen during "Get ready"
    double _countdown = -1;  // >= 0 while counting down
    int _target;
    double _lastTick;
    volatile bool _capturing;
    HandsFrame? _latest;
    double _latestAt = -1;
    CustomGesture? _draft;

    /// <summary>The recorded gesture, set when the user keeps it.</summary>
    public CustomGesture? Result { get; private set; }

    public GestureRecorderWindow(App app, string name, GestureHand hand, IReadOnlyList<CustomGesture> others)
    {
        InitializeComponent();
        _app = app;
        _name = name.Length > 0 ? name : "your gesture";
        _hand = hand;
        _others = others;
        string handText = hand switch { GestureHand.Right => "your right hand", GestureHand.Left => "your left hand", _ => "either hand" };
        _steps = BuildSteps(handText);
        HeaderText.Text = $"Record \"{_name}\"";
        HandText.Text = $"Using {handText}";

        _app.Gestures.HandsUpdated += OnHands;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTick(), Dispatcher);
        Loaded += (_, _) =>
        {
            _app.Gestures.Recording = true;   // posing mustn't click, type or fire other gestures
            _app.ClaimPreview(this);
            _lastTick = _clock.Elapsed.TotalSeconds;
            _timer.Start();
            EnterStep(0);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _capturing = false;
            _app.Gestures.HandsUpdated -= OnHands;
            _app.Gestures.Recording = false;
            _app.ReleasePreview(this);
        };
    }

    // ---- Engine thread ----

    void OnHands(HandsFrame frame)
    {
        Volatile.Write(ref _latest, frame);
        Volatile.Write(ref _latestAt, _clock.Elapsed.TotalSeconds);
        if (!_capturing) return;
        if (Chosen(frame) is ({ } landmarks, bool mirror)) _samples.Enqueue(HandFeatures.From(landmarks, mirror));
    }

    /// <summary>The chosen hand's landmarks in this frame (either hand: stored in right-hand orientation).</summary>
    (Point2f[] Landmarks, bool Mirror)? Chosen(HandsFrame f) => _hand switch
    {
        GestureHand.Right => f.Right is { } r ? (r, false) : null,
        GestureHand.Left => f.Left is { } l ? (l, false) : null,
        _ => f.Right is { } r2 ? (r2, false) : f.Left is { } l2 ? (l2, true) : null,
    };

    // ---- UI thread ----

    void OnTick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - _lastTick);
        _lastTick = now;

        BlitPreview();
        var frame = Volatile.Read(ref _latest);
        bool fresh = frame is not null && now - Volatile.Read(ref _latestAt) < 0.4;
        var chosen = fresh ? Chosen(frame!) : null;
        bool tracked = chosen is not null;
        ShowTracking(tracked, fresh ? frame : null);
        AnimateCue(now);

        var step = _steps[_step];
        switch (step.Kind)
        {
            case Kind.Ready:
                if (!tracked) { _readyTime = 0; _countdown = -1; CountdownText.Visibility = Visibility.Collapsed; break; }
                if (_countdown < 0)
                {
                    _readyTime += dt;
                    StepProgress.Value = Math.Min(1, _readyTime / ReadySeconds);
                    if (_readyTime >= ReadySeconds) _countdown = CountdownSeconds;
                }
                else
                {
                    _countdown -= dt;
                    CountdownText.Visibility = Visibility.Visible;
                    CountdownText.Text = Math.Max(1, Math.Ceiling(_countdown)).ToString();
                    InstructionText.Text = "Hold it there…";
                    if (_countdown <= 0) { CountdownText.Visibility = Visibility.Collapsed; EnterStep(_step + 1); }
                }
                break;

            case Kind.Capture:
                _capturing = tracked;
                if (tracked) _stepTime += dt;
                StepProgress.Value = Math.Min(1, _stepTime / step.Seconds);
                if (_stepTime >= step.Seconds) EnterStep(_step + 1);
                break;

            case Kind.Targets:
                _capturing = tracked;
                bool onTarget = tracked && OnTarget(chosen!.Value.Landmarks);
                if (onTarget) _stepTime += dt;
                DrawTarget(Math.Min(1, _stepTime / step.Seconds), onTarget);
                StepProgress.Value = (_target + Math.Min(1, _stepTime / step.Seconds)) / Targets.Length;
                if (_stepTime >= step.Seconds)
                {
                    _stepTime = 0;
                    if (++_target >= Targets.Length) EnterStep(_step + 1);
                }
                break;

            case Kind.Review:
                UpdateTry(chosen);
                break;
        }
        SampleText.Text = _draft is null ? $"{_samples.Count} samples" : SampleText.Text;
    }

    void EnterStep(int index)
    {
        _step = index;
        _stepTime = _readyTime = 0;
        _countdown = -1;
        _target = 0;
        _capturing = false;
        var step = _steps[index];
        InstructionText.Text = step.Instruction;
        DetailText.Text = step.Detail;
        StepProgress.Value = 0;
        TargetLayer.Visibility = step.Kind == Kind.Targets ? Visibility.Visible : Visibility.Collapsed;
        TryPanel.Visibility = Visibility.Collapsed;
        KeepButton.Visibility = Visibility.Collapsed;
        if (step.Kind == Kind.Review) FinishRecording();

        StepList.ItemsSource = _steps.Select((s, i) => new
        {
            s.Title,
            Marker = i < index ? "✓" : (i + 1).ToString(),
            MarkerBrush = i < index ? Green : i == index ? White : Grey,
            TitleBrush = i < index ? Grey : i == index ? White : Grey,
            Background = i == index ? Current : Clear,
        }).ToList();
    }

    void FinishRecording()
    {
        var samples = _samples.ToList();
        if (samples.Count > MaxSamples)
            samples = Enumerable.Range(0, MaxSamples).Select(i => samples[i * samples.Count / MaxSamples]).ToList();

        _draft = new CustomGesture
        {
            Name = _name,
            Hand = _hand,
            Samples = samples,
            Threshold = GestureMatcher.AutoThreshold(samples),
        };

        // Consistency: how much of the recording agrees with itself. Low = the pose changed partway through.
        // Compared with samples from other moments (neighbouring frames are near-identical and would always agree).
        int agree = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double nearest = double.MaxValue;
            for (int j = 0; j < samples.Count; j++)
                if (Math.Abs(i - j) > 6) nearest = Math.Min(nearest, HandFeatures.Distance(samples[i], samples[j]));
            if (nearest < _draft.Threshold) agree++;
        }
        double consistent = samples.Count < 2 ? 0 : agree / (double)samples.Count;
        var clash = _others.Select(g => (g.Name, Overlap: GestureMatcher.Overlap(samples, g)))
                           .OrderByDescending(x => x.Overlap).FirstOrDefault();

        var notes = new List<string> { $"{samples.Count} samples" };
        notes.Add(consistent >= 0.9 ? "pose consistent ✓" : $"only {consistent:P0} consistent: the pose may have changed while recording");
        if (clash.Name is not null)
            notes.Add(clash.Overlap >= 0.25 ? $"{clash.Overlap:P0} looks like \"{clash.Name}\": they may get confused"
                                            : $"distinct from your other gestures ✓");
        SampleText.Text = string.Join("  ·  ", notes);
        SampleText.Foreground = consistent >= 0.9 && clash.Overlap < 0.25 ? Green : Amber;

        TryPanel.Visibility = Visibility.Visible;
        KeepButton.Visibility = Visibility.Visible;
        StepProgress.Value = 1;
    }

    void UpdateTry((Point2f[] Landmarks, bool Mirror)? chosen)
    {
        if (_draft is null) return;
        if (chosen is not { } c)
        {
            TryText.Text = "Show your hand to try it";
            TryText.Foreground = Grey;
            TryMeter.Value = 0;
            return;
        }
        // Match against the new gesture and the existing ones together, as it will be used.
        bool isRight = _hand == GestureHand.Right || (_hand == GestureHand.Either && !c.Mirror);
        var library = _others.Append(_draft).ToList();
        var (match, confidence) = GestureMatcher.Match(library, c.Landmarks, isRight);
        TryMeter.Value = match == _draft ? confidence : 0;
        (TryText.Text, TryText.Foreground) =
            match == _draft ? ($"✓ Recognised ({confidence:P0})", Green)
            : match is not null ? ($"Looks like \"{match.Name}\"", Amber)
            : ("Not recognised", Grey);
    }

    // ---- Targets ----

    bool OnTarget(Point2f[] landmarks)
    {
        var anchor = HandMouse.Anchor(landmarks);
        var t = Targets[_target];
        double dx = anchor.X / _frameW - t.X, dy = (anchor.Y / _frameH - t.Y) * _frameH / _frameW;
        return Math.Sqrt(dx * dx + dy * dy) < TargetRadiusFraction;
    }

    void DrawTarget(double progress, bool onTarget)
    {
        var t = Targets[Math.Min(_target, Targets.Length - 1)];
        double x = t.X * CameraLayer.Width, y = t.Y * CameraLayer.Height;
        Place(TargetRing, x, y);
        Place(TargetDot, x, y);
        TargetRing.Stroke = onTarget ? Green : White;
        TargetDot.Fill = onTarget ? Green : Red;

        // Progress arc around the ring
        double r = TargetRing.Width / 2, a = Math.Min(progress, 0.999) * 2 * Math.PI;
        var start = new System.Windows.Point(x, y - r);
        var end = new System.Windows.Point(x + r * Math.Sin(a), y - r * Math.Cos(a));
        TargetProgress.Data = progress <= 0 ? null : new PathGeometry([new PathFigure(start,
            [new ArcSegment(end, new System.Windows.Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true)], false)]);
    }

    static void Place(FrameworkElement e, double x, double y)
    {
        System.Windows.Controls.Canvas.SetLeft(e, x - e.Width / 2);
        System.Windows.Controls.Canvas.SetTop(e, y - e.Height / 2);
    }

    // ---- Feedback ----

    void ShowTracking(bool tracked, HandsFrame? frame)
    {
        CameraBorder.BorderBrush = tracked ? Green : Red;
        StatusBadge.Background = tracked ? Green : Red;
        string wrongHand = _hand switch
        {
            GestureHand.Right when frame?.Left is not null => "  (that's your left hand: use your right)",
            GestureHand.Left when frame?.Right is not null => "  (that's your right hand: use your left)",
            _ => "",
        };
        StatusText.Text = tracked ? "HAND TRACKED" : "NO HAND" + (wrongHand.Length > 0 ? wrongHand : ": bring it into view");

        // Paused mid-step? Say so.
        var step = _steps[_step];
        if (!tracked && step.Kind is Kind.Capture or Kind.Targets && _stepTime > 0)
            DetailText.Text = "Paused: bring your hand back into view and carry on.";
        else if (tracked && step.Kind is Kind.Capture or Kind.Targets)
            DetailText.Text = step.Detail;
    }

    /// <summary>The hand icon acts out the current motion (squashing it reads as a tilt).</summary>
    void AnimateCue(double now)
    {
        double w = Math.Sin(now * 2.2);            // slow back-and-forth
        CueScale.ScaleX = CueScale.ScaleY = 1;
        CueRotate.Angle = 0;
        CueMove.X = CueMove.Y = 0;
        switch (_steps[_step].Cue)
        {
            case Cue.Still: CueScale.ScaleX = CueScale.ScaleY = 1 + 0.06 * Math.Sin(now * 4); break;     // gentle pulse
            case Cue.TiltLeft: CueScale.ScaleX = 1 - 0.45 * Math.Max(0, w); CueMove.X = -14 * Math.Max(0, w); break;
            case Cue.TiltRight: CueScale.ScaleX = 1 - 0.45 * Math.Max(0, w); CueMove.X = 14 * Math.Max(0, w); break;
            case Cue.TipToward: CueScale.ScaleY = 1 - 0.4 * Math.Max(0, w); CueScale.ScaleX = 1 + 0.12 * Math.Max(0, w); break;
            case Cue.TipAway: CueScale.ScaleY = 1 - 0.4 * Math.Max(0, w); CueScale.ScaleX = 1 - 0.12 * Math.Max(0, w); break;
            case Cue.Circle: CueMove.X = 14 * Math.Cos(now * 2.5); CueMove.Y = 14 * Math.Sin(now * 2.5); break;
            case Cue.Move: CueMove.X = 18 * Math.Sin(now * 1.6); break;
            case Cue.Done: CueRotate.Angle = 0; break;
        }
    }

    void BlitPreview()
    {
        if (!_app.OwnsPreview(this)) return;
        using var frame = _app.Gestures.TakePreview();
        if (frame is null) return;
        _frameW = frame.Width;
        _frameH = frame.Height;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            PreviewImage.Source = _bitmap;
        }
        int stride = (int)frame.Step();
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, stride * frame.Height, stride);
    }

    // ---- Buttons ----

    void OnKeep(object sender, RoutedEventArgs e)
    {
        Result = _draft;
        DialogResult = true;
    }

    void OnRedo(object sender, RoutedEventArgs e)
    {
        _samples.Clear();
        _draft = null;
        SampleText.Foreground = Grey;
        EnterStep(0);
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
