using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

/// <summary>
/// Shows a full-screen window with targets in a grid (3x3 by default). For each highlighted target,
/// the user holds their hand still at the position they want to correspond to that spot on screen.
/// After all targets are captured, a homography (camera -> screen) is solved.
///
/// Drive it from your existing frame loop: call Update() then Render() every frame.
/// Your loop must keep calling Cv2.WaitKey() so the window repaints.
/// </summary>
public sealed class ScreenCalibrator : ICalibrator
{
    const string WindowName = "Calibration";

    readonly int _screenW, _screenH;
    readonly Point2d[] _targets;
    readonly List<Point2d> _captured = new();          // camera-space points, one per target
    readonly List<Point2f> _dwellSamples = new();
    readonly Queue<(Point2f P, double T)> _window = new();
    readonly double _dwellSeconds;
    readonly float _stabilityPx, _minMovePx;
    readonly Mat _canvas;

    double _stableSince = -1;
    double _progress;
    bool _handVisible;
    bool _lastSolveFailed;
    Point2f _lastCapture;

    public bool IsComplete => Result is not null;
    public CalibrationData? Result { get; private set; }

    /// <param name="stabilityPx">Max hand wobble (camera px) still counted as "holding still".</param>
    /// <param name="minMovePx">Hand must move at least this far (camera px) from the previous capture,
    /// so one long hold doesn't fill several targets.</param>
    public ScreenCalibrator(int screenWidth, int screenHeight, int grid = 3, double margin = 0.07,
                            double dwellSeconds = 1.0, float stabilityPx = 8f, float minMovePx = 25f)
    {
        if (grid < 2) throw new ArgumentException("grid must be >= 2 (homography needs at least 4 points)");

        _screenW = screenWidth;
        _screenH = screenHeight;
        _dwellSeconds = dwellSeconds;
        _stabilityPx = stabilityPx;
        _minMovePx = minMovePx;

        var targets = new List<Point2d>();
        for (int r = 0; r < grid; r++)
        for (int c = 0; c < grid; c++)
        {
            double fx = margin + (1 - 2 * margin) * c / (grid - 1);
            double fy = margin + (1 - 2 * margin) * r / (grid - 1);
            targets.Add(new Point2d(fx * screenWidth, fy * screenHeight));
        }
        _targets = targets.ToArray();

        _canvas = new Mat(screenHeight, screenWidth, MatType.CV_8UC3, Scalar.Black);
        Cv2.NamedWindow(WindowName, WindowFlags.Normal);
        Cv2.MoveWindow(WindowName, 0, 0); // primary monitor
        Cv2.SetWindowProperty(WindowName, WindowPropertyFlags.Fullscreen, 1);
    }

    /// <param name="anchor">Hand point in camera pixel coordinates (use HandMouse.Anchor), or null if no hand.</param>
    /// <param name="now">Time in seconds.</param>
    public void Update(Point2f? anchor, double now)
    {
        if (IsComplete) return;

        _handVisible = anchor.HasValue;
        if (anchor is not Point2f p)
        {
            _window.Clear();
            ResetDwell();
            return;
        }

        _window.Enqueue((p, now));
        while (now - _window.Peek().T > 0.25) _window.Dequeue();

        // Too close to the last captured spot: user hasn't moved to the next target yet.
        if (_captured.Count > 0 && Dist(p, _lastCapture) < _minMovePx) { ResetDwell(); return; }

        if (MaxDeviationInWindow() > _stabilityPx) { ResetDwell(); return; }

        if (_stableSince < 0) _stableSince = now;
        _dwellSamples.Add(p);
        _progress = Math.Clamp((now - _stableSince) / _dwellSeconds, 0, 1);

        if (_progress < 1) return;

        var mean = Mean(_dwellSamples);
        _captured.Add(new Point2d(mean.X, mean.Y));
        _lastCapture = mean;
        ResetDwell();

        if (_captured.Count == _targets.Length) Solve();
    }

    public void Render(Mat? cameraFrame = null)
    {
        _canvas.SetTo(Scalar.Black);

        for (int i = 0; i < _targets.Length; i++)
        {
            var color = i < _captured.Count ? new Scalar(80, 200, 80) : new Scalar(70, 70, 70);
            Cv2.Circle(_canvas, ToPoint(_targets[i]), 8, color, -1, LineTypes.AntiAlias);
        }

        if (!IsComplete && _captured.Count < _targets.Length)
        {
            var t = ToPoint(_targets[_captured.Count]);
            var red = new Scalar(60, 60, 255);
            Cv2.Circle(_canvas, t, 30, red, 2, LineTypes.AntiAlias);
            Cv2.Circle(_canvas, t, 8, red, -1, LineTypes.AntiAlias);
            if (_progress > 0)
                Cv2.Ellipse(_canvas, t, new Size(30, 30), -90, 0, 360 * _progress,
                            new Scalar(80, 255, 80), 5, LineTypes.AntiAlias);
        }

        string msg =
            IsComplete ? $"Calibration complete (avg error {Result!.MeanErrorPx:F0}px)" :
            _lastSolveFailed ? "Calibration failed - spread your hand positions further apart. Restarting..." :
            !_handVisible ? "No hand detected - show your hand to the camera" :
            $"Move your hand to where this target should be, then hold still ({_captured.Count + 1}/{_targets.Length})";
        DrawCenteredText(msg, (int)(_screenH * 0.29));

         if (cameraFrame is not null && !cameraFrame.Empty() && cameraFrame.Type() == MatType.CV_8UC3)
        {
            int w = Math.Min(320, _screenW / 4);
            int h = cameraFrame.Rows * w / cameraFrame.Cols;
            using var small = cameraFrame.Resize(new Size(w, h));
            var roi = new Rect((_screenW - w) / 2, (int)(_screenH * 0.715) - h / 2, w, h);
            using var dst = new Mat(_canvas, roi);
            small.CopyTo(dst);
        }

        Cv2.ImShow(WindowName, _canvas);
    }

    void Solve()
    {
        using var h = Cv2.FindHomography(_captured, _targets, HomographyMethods.None);
        if (h.Empty())
        {
            _lastSolveFailed = true;
            _captured.Clear();
            return;
        }

        var arr = new double[9];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            arr[r * 3 + c] = h.At<double>(r, c);

        var data = new CalibrationData { Homography = arr, ScreenWidth = _screenW, ScreenHeight = _screenH };

        double err = 0;
        for (int i = 0; i < _targets.Length; i++)
        {
            var m = data.Map(new Point2f((float)_captured[i].X, (float)_captured[i].Y));
            err += Math.Sqrt(Math.Pow(m.X - _targets[i].X, 2) + Math.Pow(m.Y - _targets[i].Y, 2));
        }
        data.MeanErrorPx = err / _targets.Length;

        _lastSolveFailed = false;
        Result = data;
    }

    void ResetDwell()
    {
        _stableSince = -1;
        _progress = 0;
        _dwellSamples.Clear();
    }

    double MaxDeviationInWindow()
    {
        var mean = Mean(_window.Select(w => w.P));
        return _window.Max(w => Dist(w.P, mean));
    }

    void DrawCenteredText(string text, int y)
    {
        const HersheyFonts font = HersheyFonts.HersheySimplex;
        double scale = Math.Max(0.6, _screenW / 1920.0);
        int thickness = 2;
        var size = Cv2.GetTextSize(text, font, scale, thickness, out _);
        Cv2.PutText(_canvas, text, new Point((_screenW - size.Width) / 2, y), font, scale,
                    Scalar.White, thickness, LineTypes.AntiAlias);
    }

    static Point2f Mean(IEnumerable<Point2f> pts)
    {
        float sx = 0, sy = 0; int n = 0;
        foreach (var p in pts) { sx += p.X; sy += p.Y; n++; }
        return n == 0 ? default : new Point2f(sx / n, sy / n);
    }

    static double Dist(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static Point ToPoint(Point2d p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));

    public void Dispose()
    {
        _canvas.Dispose();
        Cv2.DestroyWindow(WindowName);
    }
}
