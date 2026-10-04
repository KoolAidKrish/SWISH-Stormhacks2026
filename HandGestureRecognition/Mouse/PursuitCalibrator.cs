using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

/// <summary>
/// The route the calibration dot follows: a snake that sweeps left→right, curves down, sweeps
/// right→left, and so on, top to bottom. Moves at a constant, easy speed (rounded turns, no sudden
/// changes), starting from rest. Built from the screen size alone, so tests can use it without a window.
/// </summary>
public sealed class SnakePath
{
    const int Rows = 4;
    const double EaseInSeconds = 1.5;   // speed ramps up from 0 over this long so the dot is easy to pick up

    readonly double _xLeft, _xRight, _top, _radius, _straight, _turn;

    /// <summary>Dot speed in screen px per second.</summary>
    public double Speed { get; }
    /// <summary>Total length of the route in px.</summary>
    public double Length { get; }
    /// <summary>Seconds from the dot starting to move to reaching the end.</summary>
    public double FollowSeconds => Length / Speed + EaseInSeconds / 2;
    /// <summary>The start, where people are still catching up; the fit ignores it.</summary>
    public static double EaseIn => EaseInSeconds;

    /// <param name="speedScreensPerSecond">Speed as a fraction of the screen width per second (0.12 ≈ 230 px/s at 1080p).</param>
    public SnakePath(int screenWidth, int screenHeight, double marginX = 0.06, double marginY = 0.12,
                     double speedScreensPerSecond = 0.12)
    {
        _top = screenHeight * marginY;
        double bottom = screenHeight * (1 - marginY);
        _radius = (bottom - _top) / (Rows - 1) / 2;              // turns are half circles joining the rows
        _xLeft = screenWidth * marginX + _radius;               // so the turns stay inside the margin
        _xRight = screenWidth * (1 - marginX) - _radius;
        _straight = _xRight - _xLeft;
        _turn = Math.PI * _radius;
        Length = Rows * _straight + (Rows - 1) * _turn;
        Speed = screenWidth * speedScreensPerSecond;
    }

    /// <summary>Where the dot is, <paramref name="t"/> seconds after it starts moving.</summary>
    public Point2d At(double t) => AtDistance(DistanceAt(t));

    /// <summary>Distance along the route at time t, with the ease-in.</summary>
    public double DistanceAt(double t)
    {
        double s = t <= 0 ? 0
            : t < EaseInSeconds ? Speed * t * t / (2 * EaseInSeconds)
            : Speed * (t - EaseInSeconds / 2);
        return Math.Min(s, Length);
    }

    public Point2d AtDistance(double s)
    {
        s = Math.Clamp(s, 0, Length);
        for (int row = 0; row < Rows; row++)
        {
            double y = _top + row * 2 * _radius;
            bool leftToRight = row % 2 == 0;

            if (s <= _straight || row == Rows - 1)
            {
                double x = leftToRight ? _xLeft + s : _xRight - s;
                return new Point2d(x, y);
            }
            s -= _straight;

            if (s <= _turn)
            {
                // Half circle from this row down to the next, bulging outward on the side we ended on.
                double theta = -Math.PI / 2 + Math.PI * (s / _turn);
                double cx = leftToRight ? _xRight : _xLeft;
                double dx = _radius * Math.Cos(theta) * (leftToRight ? 1 : -1);
                return new Point2d(cx + dx, y + _radius + _radius * Math.Sin(theta));
            }
            s -= _turn;
        }
        return AtDistance(Length);
    }
}

/// <summary>
/// "Follow the dot" calibration: a dot snakes across the screen for ~30 s and you follow it with your
/// palm. Every frame becomes a (hand position, dot position) pair, so the mapping is fitted from
/// hundreds of samples spread across the whole screen instead of 9 held points, and nobody has to
/// hold still (hard with an unsteady hand).
///
/// The camera feed fills the screen behind the path (dimmed, with the tracked hand drawn on it), and
/// a green/red border shows at a glance whether your hand is being tracked.
///
/// The catch is lag: people trail a moving target by ~100-300 ms, plus camera/tracking delay. Pairing
/// the hand *now* with the dot *now* would skew the mapping along the direction of motion. So the fit
/// tries every delay from 0 to 600 ms, pairs the hand at t with the dot at t - lag, and keeps the lag
/// whose mapping fits best. The snake runs alternate rows in opposite directions, so leftover lag
/// error also cancels. A RANSAC fit then drops frames where tracking glitched or the hand overshot.
///
/// Drive it like ScreenCalibrator: Update() then Render() every frame, keep calling Cv2.WaitKey().
/// </summary>
public sealed class PursuitCalibrator : ICalibrator
{
    const string WindowName = "Calibration";
    const double HoldToStartSeconds = 0.6;
    const double CountdownSeconds = 3;
    const double FailMessageSeconds = 3.5;
    const double LookAheadSeconds = 2.0;   // highlight this much of the route ahead of the dot

    static readonly Scalar Green = new(90, 220, 90), Red = new(70, 70, 240), DotRed = new(60, 60, 255);

    enum Phase { WaitForHand, Countdown, Following, Failed }

    readonly int _screenW, _screenH;
    readonly SnakePath _path;
    readonly Point[] _routePolyline;
    readonly Mat _canvas;
    readonly Mat _background = new();
    readonly List<(double T, Point2f P)> _samples = new();

    Phase _phase = Phase.WaitForHand;
    double _phaseStart = double.NaN;
    double _handSince = -1;
    bool _handVisible;
    int _followFrames, _followFramesWithHand;
    double _now;
    string? _failReason;

    public bool IsComplete => Result is not null;
    public CalibrationData? Result { get; private set; }
    /// <summary>Human-readable result, e.g. "lag 180 ms, typical error 34 px, 92% of frames used".</summary>
    public string? Summary { get; private set; }

    public PursuitCalibrator(int screenWidth, int screenHeight)
    {
        _screenW = screenWidth;
        _screenH = screenHeight;
        _path = new SnakePath(screenWidth, screenHeight);
        _routePolyline = PolylineBetween(0, _path.Length);

        _canvas = new Mat(screenHeight, screenWidth, MatType.CV_8UC3, Scalar.Black);
        Cv2.NamedWindow(WindowName, WindowFlags.Normal);
        Cv2.MoveWindow(WindowName, 0, 0); // primary monitor
        Cv2.SetWindowProperty(WindowName, WindowPropertyFlags.Fullscreen, 1);
    }

    public void Update(Point2f? anchor, double now)
    {
        _now = now;
        if (IsComplete) return;
        if (double.IsNaN(_phaseStart)) _phaseStart = now;
        _handVisible = anchor.HasValue;
        double t = now - _phaseStart;

        switch (_phase)
        {
            case Phase.Failed:
                if (t >= FailMessageSeconds) Restart(now);
                break;

            case Phase.WaitForHand:
                // Start once a hand has been visible for a moment.
                if (!_handVisible) { _handSince = -1; break; }
                if (_handSince < 0) _handSince = now;
                if (now - _handSince >= HoldToStartSeconds) Enter(Phase.Countdown, now);
                break;

            case Phase.Countdown:
                if (!_handVisible) { _handSince = -1; Enter(Phase.WaitForHand, now); break; }
                if (t >= CountdownSeconds) Enter(Phase.Following, now);
                break;

            case Phase.Following:
                _followFrames++;
                if (anchor is Point2f p)
                {
                    _followFramesWithHand++;
                    _samples.Add((t, p));
                }
                if (t >= _path.FollowSeconds) Finish(now);
                break;
        }
    }

    void Finish(double now)
    {
        double coverage = _followFrames == 0 ? 0 : _followFramesWithHand / (double)_followFrames;
        if (coverage < 0.6)
        {
            Fail($"Lost track of your hand {100 - coverage * 100:F0}% of the time - keep it inside the green border", now);
            return;
        }

        var fit = Fit(_samples, _path.At, skipBefore: SnakePath.EaseIn, _screenW, _screenH);
        if (fit.Error is not null)
        {
            Fail(fit.Error, now);
            return;
        }

        Result = fit.Calibration;
        Summary = $"lag {fit.LagSeconds * 1000:F0} ms, typical error {fit.Calibration!.MeanErrorPx:F0} px, {fit.InlierFraction:P0} of frames used";
    }

    void Fail(string reason, double now)
    {
        _failReason = reason;
        Enter(Phase.Failed, now);
    }

    void Restart(double now)
    {
        _samples.Clear();
        _followFrames = _followFramesWithHand = 0;
        _handSince = -1;
        _failReason = null;
        Enter(Phase.WaitForHand, now);
    }

    void Enter(Phase phase, double now)
    {
        _phase = phase;
        _phaseStart = now;
    }

    // ---- Fitting (static so it can be tested without a camera or a window) ----

    public sealed record FitResult(CalibrationData? Calibration, double LagSeconds, double InlierFraction, string? Error);

    /// <summary>
    /// Fits camera → screen from pursuit samples. Tries each lag in [0, 0.6 s], pairing the hand at t with
    /// the dot at t - lag, keeps the best, then refits with RANSAC to drop outliers.
    /// </summary>
    /// <param name="samples">(seconds since the dot started moving, hand position in camera px).</param>
    /// <param name="dotAt">Dot position (screen px) at a given time.</param>
    /// <param name="skipBefore">Ignore pairs whose dot time is earlier than this (the ease-in, while people catch up).</param>
    public static FitResult Fit(IReadOnlyList<(double T, Point2f P)> samples, Func<double, Point2d> dotAt,
                                double skipBefore, int screenW, int screenH)
    {
        if (samples.Count < 60)
            return new FitResult(null, 0, 0, "Not enough hand frames - keep your hand in view of the camera");

        // A homography from a hand that barely moved would magnify tracking noise across the whole screen.
        float minX = samples.Min(s => s.P.X), maxX = samples.Max(s => s.P.X);
        float minY = samples.Min(s => s.P.Y), maxY = samples.Max(s => s.P.Y);
        if (maxX - minX < 40 || maxY - minY < 30)
            return new FitResult(null, 0, 0, "Your hand barely moved - follow the dot all the way to the edges");

        // 1. Find the lag: least-squares fit at each candidate, keep the one with the smallest mean error.
        double bestLag = 0, bestErr = double.MaxValue;
        for (int ms = 0; ms <= 600; ms += 10)
        {
            double lag = ms / 1000.0;
            var (cam, scr) = Pairs(samples, dotAt, lag, skipBefore);
            if (cam.Count < 40) continue;
            using var h = Cv2.FindHomography(cam, scr, HomographyMethods.None);
            if (h.Empty()) continue;
            double err = MeanError(ToData(h, screenW, screenH), cam, scr, null);
            if (err < bestErr) { bestErr = err; bestLag = lag; }
        }
        if (bestErr == double.MaxValue)
            return new FitResult(null, 0, 0, "Couldn't fit a mapping - try again, following the dot more closely");

        // 2. Robust fit at that lag. Threshold: 6% of the screen width (~115 px at 1080p) counts as "following".
        var (camPts, scrPts) = Pairs(samples, dotAt, bestLag, skipBefore);
        using var mask = new Mat();
        using var hr = Cv2.FindHomography(camPts, scrPts, HomographyMethods.Ransac, screenW * 0.06, mask);
        if (hr.Empty())
            return new FitResult(null, bestLag, 0, "Couldn't fit a mapping - try again, following the dot more closely");

        var inliers = new bool[camPts.Count];
        for (int i = 0; i < inliers.Length; i++) inliers[i] = mask.At<byte>(i) != 0;
        double inlierFraction = inliers.Count(x => x) / (double)inliers.Length;
        if (inlierFraction < 0.5)
            return new FitResult(null, bestLag, inlierFraction,
                $"Only {inlierFraction:P0} of the path was followed closely - stay on the dot along the white line");

        var data = ToData(hr, screenW, screenH);
        data.MeanErrorPx = MeanError(data, camPts, scrPts, inliers);
        return new FitResult(data, bestLag, inlierFraction, null);
    }

    static (List<Point2d> Cam, List<Point2d> Screen) Pairs(IReadOnlyList<(double T, Point2f P)> samples,
                                                           Func<double, Point2d> dotAt, double lag, double skipBefore)
    {
        var cam = new List<Point2d>(samples.Count);
        var scr = new List<Point2d>(samples.Count);
        foreach (var (t, p) in samples)
        {
            double dotTime = t - lag;
            if (dotTime < skipBefore) continue;
            cam.Add(new Point2d(p.X, p.Y));
            scr.Add(dotAt(dotTime));
        }
        return (cam, scr);
    }

    static CalibrationData ToData(Mat h, int screenW, int screenH)
    {
        var arr = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                arr[r * 3 + c] = h.At<double>(r, c);
        return new CalibrationData { Homography = arr, ScreenWidth = screenW, ScreenHeight = screenH };
    }

    static double MeanError(CalibrationData data, List<Point2d> cam, List<Point2d> scr, bool[]? use)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < cam.Count; i++)
        {
            if (use is not null && !use[i]) continue;
            var m = data.Map(new Point2f((float)cam[i].X, (float)cam[i].Y));
            sum += Math.Sqrt((m.X - scr[i].X) * (m.X - scr[i].X) + (m.Y - scr[i].Y) * (m.Y - scr[i].Y));
            n++;
        }
        return n == 0 ? double.MaxValue : sum / n;
    }

    // ---- Drawing ----

    public void Render(Mat? cameraFrame = null)
    {
        double t = double.IsNaN(_phaseStart) ? 0 : _now - _phaseStart;
        DrawCameraBackground(cameraFrame);

        // Route: whole thing faint, the part already covered green, the next couple of seconds bright.
        double here = _phase == Phase.Following ? _path.DistanceAt(t) : 0;
        double ahead = _phase == Phase.Following
            ? _path.DistanceAt(t + LookAheadSeconds)
            : _path.DistanceAt(LookAheadSeconds + SnakePath.EaseIn);
        DrawRoute(_routePolyline, new Scalar(110, 110, 110), 3);
        if (here > 0) DrawRoute(PolylineBetween(0, here), Green, 5);
        DrawRoute(PolylineBetween(here, ahead), Scalar.White, 6);

        // The dot: big, red, white-outlined so it shows up on top of the video.
        var dot = ToPoint(_path.AtDistance(here));
        Cv2.Circle(_canvas, dot, 38, Scalar.White, 6, LineTypes.AntiAlias);
        Cv2.Circle(_canvas, dot, 38, DotRed, 3, LineTypes.AntiAlias);
        Cv2.Circle(_canvas, dot, 15, DotRed, -1, LineTypes.AntiAlias);
        Cv2.Circle(_canvas, dot, 15, Scalar.White, 2, LineTypes.AntiAlias);

        string msg;
        switch (_phase)
        {
            case Phase.WaitForHand:
                msg = _handVisible
                    ? "Hand found - hold it there..."
                    : "Follow-the-dot calibration: raise your hand until the border turns green";
                break;
            case Phase.Countdown:
                msg = "When the dot moves, follow it with your palm along the white line";
                int n = (int)Math.Ceiling(CountdownSeconds - t);
                DrawText(n.ToString(), new Point(dot.X, dot.Y + 120), scale: 2.8, centered: true);
                break;
            case Phase.Following:
                msg = _handVisible ? "Follow the dot" : "Hand lost - bring it back into view";
                DrawProgress(Math.Clamp(t / _path.FollowSeconds, 0, 1));
                break;
            default:
                msg = $"{_failReason}. Restarting...";
                break;
        }
        if (IsComplete) msg = $"Calibration complete ({Summary})";
        DrawText(msg, new Point(_screenW / 2, (int)(_screenH * 0.055)), scale: Math.Max(0.7, _screenW / 1700.0), centered: true);

        DrawHandStatus();
        Cv2.ImShow(WindowName, _canvas);
    }

    /// <summary>The camera feed (with the hand skeleton already drawn on it) fills the screen, dimmed.</summary>
    void DrawCameraBackground(Mat? cameraFrame)
    {
        if (cameraFrame is null || cameraFrame.Empty() || cameraFrame.Type() != MatType.CV_8UC3)
        {
            _canvas.SetTo(Scalar.Black);
            return;
        }

        // Scale to cover the screen, then crop the middle (keeps the aspect ratio).
        double scale = Math.Max(_screenW / (double)cameraFrame.Cols, _screenH / (double)cameraFrame.Rows);
        int w = (int)Math.Ceiling(cameraFrame.Cols * scale), h = (int)Math.Ceiling(cameraFrame.Rows * scale);
        Cv2.Resize(cameraFrame, _background, new Size(w, h), interpolation: InterpolationFlags.Linear);
        using var crop = new Mat(_background, new Rect((w - _screenW) / 2, (h - _screenH) / 2, _screenW, _screenH));
        crop.ConvertTo(_canvas, -1, alpha: 0.45, beta: 0); // dim it so the route stands out
    }

    /// <summary>Thick screen border + label: green while a hand is tracked, red when not.</summary>
    void DrawHandStatus()
    {
        var color = _handVisible ? Green : Red;
        int border = Math.Max(10, _screenW / 120);
        Cv2.Rectangle(_canvas, new Rect(0, 0, _screenW, _screenH), color, border * 2);

        string label = _handVisible ? "HAND TRACKED" : "NO HAND - move it into view";
        double scale = Math.Max(0.7, _screenW / 2200.0);
        var size = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, scale, 2, out _);
        var box = new Rect(border + 12, _screenH - border - size.Height - 34, size.Width + 28, size.Height + 22);
        Cv2.Rectangle(_canvas, box, color, -1);
        Cv2.PutText(_canvas, label, new Point(box.X + 14, box.Y + box.Height - 12), HersheyFonts.HersheySimplex,
                    scale, Scalar.Black, 2, LineTypes.AntiAlias);
    }

    void DrawRoute(Point[] points, Scalar color, int thickness)
    {
        if (points.Length < 2) return;
        Cv2.Polylines(_canvas, new[] { points }, false, Scalar.Black, thickness + 4, LineTypes.AntiAlias); // outline for contrast
        Cv2.Polylines(_canvas, new[] { points }, false, color, thickness, LineTypes.AntiAlias);
    }

    Point[] PolylineBetween(double from, double to)
    {
        if (to <= from) return Array.Empty<Point>();
        int n = Math.Max(2, (int)((to - from) / 6)); // a point every ~6 px
        return Enumerable.Range(0, n + 1).Select(i => ToPoint(_path.AtDistance(from + (to - from) * i / n))).ToArray();
    }

    void DrawProgress(double fraction)
    {
        int y = (int)(_screenH * 0.095), x0 = _screenW * 3 / 8, x1 = _screenW * 5 / 8;
        Cv2.Line(_canvas, new Point(x0, y), new Point(x1, y), new Scalar(60, 60, 60), 6, LineTypes.AntiAlias);
        Cv2.Line(_canvas, new Point(x0, y), new Point(x0 + (int)((x1 - x0) * fraction), y), Green, 6, LineTypes.AntiAlias);
    }

    /// <summary>White text on a darkened box so it's readable over the video.</summary>
    void DrawText(string text, Point at, double scale, bool centered)
    {
        const HersheyFonts font = HersheyFonts.HersheySimplex;
        int thickness = scale > 1.5 ? 4 : 2;
        var size = Cv2.GetTextSize(text, font, scale, thickness, out int baseline);
        int x = centered ? at.X - size.Width / 2 : at.X;
        var box = new Rect(x - 14, at.Y - size.Height - 12, size.Width + 28, size.Height + baseline + 20)
                  & new Rect(0, 0, _screenW, _screenH);
        if (box.Width > 0 && box.Height > 0)
        {
            using var roi = new Mat(_canvas, box);
            roi.ConvertTo(roi, -1, alpha: 0.35, beta: 0);
        }
        Cv2.PutText(_canvas, text, new Point(x, at.Y), font, scale, Scalar.White, thickness, LineTypes.AntiAlias);
    }

    static Point ToPoint(Point2d p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));

    public void Dispose()
    {
        _canvas.Dispose();
        _background.Dispose();
        Cv2.DestroyWindow(WindowName);
    }
}
