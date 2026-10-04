using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HandGestureRecognition.Model;
using HandGestureRecognition.Custom;
using HandGestureRecognition.Mouse;
using HandGestureRecognition.Utils;
using OpenCvSharp;

namespace HandGestureRecognition;

/// <summary>Everything the frame loop needs to know up front (the console flags, plus where files live).</summary>
public sealed class GestureOptions
{
    public int Device { get; init; } = 0;
    public string Image { get; init; } = "";
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 480;
    public double MinDetectionConfidence { get; init; } = 0.6;
    public bool DisableImageFlip { get; init; }
    /// <summary>Start with the calibration screen even if a calibration is saved.</summary>
    public bool Calibrate { get; init; }
    /// <summary>Which calibration runs when one is needed at startup (no saved calibration, or Calibrate).</summary>
    public CalibrationKind CalibrationKind { get; init; } = CalibrationKind.Points;
    /// <summary>Console mode: Ctrl+Alt+V / "v" toggles an OpenCV debug window. A host with its own UI sets
    /// this false and shows TakePreview() frames instead (it still gets ToggleWindowRequested).</summary>
    public bool UseOpenCvWindow { get; init; } = true;
    /// <summary>Start with the OpenCV debug window open (only with UseOpenCvWindow).</summary>
    public bool ShowWindow { get; init; }
    /// <summary>Folder containing the model/ tree (palm_detection, hand_landmark, ...).</summary>
    public string ModelDir { get; init; } = "model";
    public string CalibrationPath { get; init; } = "calibration.json";
    /// <summary>Records the annotated feed here; null = don't record.</summary>
    public string? RecordPath { get; init; }
    /// <summary>
    /// After this long with no hand in view, the hand models only run on every <see cref="IdleFrameInterval"/>-th
    /// frame (~5 fps instead of ~30). Palm detection is about half a CPU core at full rate, and most of the time
    /// nobody is in front of the camera. A hand that appears is picked up within ~0.2 s and full rate resumes.
    /// </summary>
    public double IdleAfterSeconds { get; init; } = 1.5;
    public int IdleFrameInterval { get; init; } = 6;
}

/// <summary>
/// The hands seen in one processed frame, for recording custom gestures and showing live matches.
/// Right = the mouse hand, Left = the keyboard hand (mirrored camera picture).
/// </summary>
public sealed record HandsFrame(Point2f[]? Right, Point2f[]? Left,
                                string? RightMatch, double RightConfidence, string? LeftMatch, double LeftConfidence);

/// <summary>Read-only snapshot of the engine for a UI. Replaced every frame.</summary>
public sealed record GestureStatus(
    bool Running, double Fps, int Hands, bool Calibrating,
    bool MouseEnabled, MouseMode MouseMode, bool Pinching, bool Clutched, string? Error = null, bool Idle = false)
{
    public static readonly GestureStatus Stopped = new(false, 0, 0, false, false, MouseMode.Relative, false, false);
}

/// <summary>
/// The hand-tracking frame loop: camera → palm detection → landmarks → gesture classifiers → mouse.
/// Used by the console app (Program.cs, runs it on the main thread) and by SWISH.App (runs it on a
/// background thread with a WPF window on top).
///
/// Everything that touches OpenCV or the mouse happens on the loop's own thread. Other threads talk
/// to it through <see cref="Post"/>-ed commands, the <see cref="Status"/> snapshot and <see cref="TakePreview"/>.
/// </summary>
public sealed class GestureEngine : IDisposable
{
    private static readonly int[][] LinesHand =
    {
        new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 3 }, new[] { 3, 4 },
        new[] { 0, 5 }, new[] { 5, 6 }, new[] { 6, 7 }, new[] { 7, 8 },
        new[] { 5, 9 }, new[] { 9, 10 }, new[] { 10, 11 }, new[] { 11, 12 },
        new[] { 9, 13 }, new[] { 13, 14 }, new[] { 14, 15 }, new[] { 15, 16 },
        new[] { 13, 17 }, new[] { 17, 18 }, new[] { 18, 19 }, new[] { 19, 20 }, new[] { 0, 17 },
    };

    private static readonly Scalar Black = new(0, 0, 0);
    private static readonly Scalar White = new(255, 255, 255);
    private static readonly Scalar LabelYellow = new(59, 255, 255);
    private const string MainWindow = "Hand Gesture Recognition";

    private readonly GestureOptions _opt;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly HashSet<char> _heldHotkeys = new();
    private volatile bool _stopRequested;
    private Thread? _thread;
    private Mat? _preview;
    private GestureStatus _status = GestureStatus.Stopped;

    // Owned by the loop thread
    private HandMouse? _mouse;
    private ICalibrator? _calibrator;
    private bool _showWindow;
    private int _screenW, _screenH;

    // Custom gestures
    private IReadOnlyList<CustomGesture> _customGestures = [];
    private readonly GestureTracker _gestureTracker = new();

    public GestureEngine(GestureOptions options)
    {
        _opt = options;
        _showWindow = options.UseOpenCvWindow && options.ShowWindow;
        _gestureTracker.Started += (g, hand) => CustomGestureStarted?.Invoke(g, hand);
        _gestureTracker.Ended += (g, hand) => CustomGestureEnded?.Invoke(g, hand);
    }

    /// <summary>Latest status; safe to read from any thread.</summary>
    public GestureStatus Status => Volatile.Read(ref _status);

    /// <summary>When true, each annotated frame is kept for <see cref="TakePreview"/>. Off by default (it costs a copy per frame).</summary>
    public bool PreviewEnabled { get; set; }

    /// <summary>Raised (on the loop thread) for status/log lines, e.g. "Mouse control ON".</summary>
    public event Action<string>? Log;
    /// <summary>Ctrl+Alt+V. In console mode it also toggles the OpenCV window; a UI host can show its own.</summary>
    public event Action? ToggleWindowRequested;
    /// <summary>Ctrl+Alt+Q or Esc. The loop stops; the host decides whether that quits the whole app.</summary>
    public event Action? QuitRequested;
    /// <summary>The loop died with an exception (camera unplugged, model missing...).</summary>
    public event Action<Exception>? Faulted;
    /// <summary>Every processed frame (loop thread): the visible hands and their live custom-gesture matches.</summary>
    public event Action<HandsFrame>? HandsUpdated;
    /// <summary>A custom gesture was held steadily on a hand (loop thread).</summary>
    public event Action<CustomGesture, GestureHand>? CustomGestureStarted;
    /// <summary>That gesture was released (loop thread).</summary>
    public event Action<CustomGesture, GestureHand>? CustomGestureEnded;

    /// <summary>
    /// While true (recording a custom gesture), the hands don't drive the mouse, the finger keyboard or
    /// custom gestures, so posing for the recording doesn't click or type anything.
    /// </summary>
    public bool Recording { get; set; }

    /// <summary>Replaces the custom gestures to recognise (thread-safe; active ones end first).</summary>
    public void SetCustomGestures(IReadOnlyList<CustomGesture> gestures) => Post(() =>
    {
        _gestureTracker.Reset();
        _customGestures = gestures;
    });

    /// <summary>Takes the newest annotated frame (BGR), or null if none since last time. Caller disposes it.</summary>
    public Mat? TakePreview() => Interlocked.Exchange(ref _preview, null);

    /// <summary>Runs an action on the loop thread before the next frame (thread-safe).</summary>
    public void Post(Action command) => _commands.Enqueue(command);

    public void ToggleMouse() => Post(() => SetMouseEnabled(!(_mouse?.Enabled ?? false)));
    public void SetMouseEnabled(bool on) => Post(() =>
    {
        if (_mouse is null) return;
        _mouse.Enabled = on;
        Log?.Invoke(on ? "Mouse control ON" : "Mouse control OFF");
    });
    public void CycleMouseMode() => Post(() =>
    {
        if (_mouse is null) return;
        _mouse.CycleMode();   // Relative -> Absolute -> Joystick
        Log?.Invoke($"Mouse mode: {_mouse.Mode}");
    });
    /// <param name="kind">Points = hold still on 9 targets; Pursuit = follow a moving dot.</param>
    public void Recalibrate(CalibrationKind kind = CalibrationKind.Points) => Post(() => StartCalibration(kind));

    /// <summary>Starts the loop on a background thread.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _stopRequested = false;
        _thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex)
            {
                Volatile.Write(ref _status, GestureStatus.Stopped with { Error = ex.Message });
                Faulted?.Invoke(ex);
            }
        })
        { IsBackground = true, Name = "GestureEngine" };
        _thread.Start();
    }

    /// <summary>Asks the loop to finish its current frame and exit, and waits for it.</summary>
    public void Stop()
    {
        _stopRequested = true;
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        Interlocked.Exchange(ref _preview, null)?.Dispose();
    }

    /// <summary>The frame loop. Blocks until Stop(), Ctrl+Alt+Q / Esc, or the camera runs out.</summary>
    public void Run()
    {
        bool useImage = !string.IsNullOrEmpty(_opt.Image);
        int capWidth = _opt.Width;
        int capHeight = _opt.Height;
        float minDetectionConfidence = (float)_opt.MinDetectionConfidence;
        string ModelPath(string relative) => Path.Combine(_opt.ModelDir, relative);

        // Camera setup ##########################################################
        using var cap = useImage ? new VideoCapture(_opt.Image) : new VideoCapture(_opt.Device);
        if (!cap.IsOpened())
        {
            throw new InvalidOperationException(useImage ? $"Couldn't open {_opt.Image}" : $"Couldn't open camera {_opt.Device}");
        }
        cap.Set(VideoCaptureProperties.FrameWidth, capWidth);
        cap.Set(VideoCaptureProperties.FrameHeight, capHeight);
        double capFps = cap.Get(VideoCaptureProperties.Fps);
        if (capFps <= 0)
        {
            capFps = 30; // some backends report 0 for still images / certain webcams
        }
        using var videoWriter = _opt.RecordPath is null ? null : new VideoWriter(
            _opt.RecordPath,
            FourCC.FromFourChars('m', 'p', '4', 'v'),
            capFps,
            new Size(capWidth, capHeight));

        // Model loading #########################################################
        using var palmDetection = new PalmDetection(ModelPath("palm_detection/palm_detection_full_inf_post_192x192.onnx"), scoreThreshold: minDetectionConfidence);
        using var handLandmark = new HandLandmark(ModelPath("hand_landmark/hand_landmark_sparse_Nx3x224x224.onnx"));
        using var keypointClassifier = new KeyPointClassifier(ModelPath("keypoint_classifier/keypoint_classifier.onnx"));
        using var pointHistoryClassifier = new PointHistoryClassifier(ModelPath("point_history_classifier/point_history_classifier_lstm.onnx"));

        // Mouse control #########################################################
        (_screenW, _screenH) = NativeMouse.PrimaryScreenSize();
        var calib = CalibrationData.TryLoad(_opt.CalibrationPath, _screenW, _screenH);
        _calibrator = (calib is null || _opt.Calibrate)
            ? CreateCalibrator(_opt.CalibrationKind)
            : null;
        _mouse = _calibrator is null ? CreateMouse(calib!) : null;
        var mouseClock = Stopwatch.StartNew();
        double lastHandSeen = 0;   // for the idle throttle (seconds on mouseClock)
        long frameIndex = 0;

        // Keyboard control (left hand) ##########################################
        var keyboard = new HandKeyboard();

        // Label loading #########################################################
        // (keypoint labels are loaded for parity with the original; it never draws them)
        var keypointClassifierLabels = ReadLabels(ModelPath("keypoint_classifier/keypoint_classifier_label.csv"));
        var pointHistoryClassifierLabels = ReadLabels(ModelPath("point_history_classifier/point_history_classifier_label.csv"));
        _ = keypointClassifierLabels;

        // FPS measurement #######################################################
        var cvFpsCalc = new CvFpsCalc(bufferLen: 10);

        // Coordinate history ####################################################
        const int historyLength = 16;
        var pointHistory = new OrderedMap<int, BoundedQueue<(int X, int Y)>>();
        var prePointHistory = new OrderedMap<int, BoundedQueue<(int X, int Y)>>();

        // Finger gesture history ################################################
        const int gestureHistoryLength = 10;
        var fingerGestureHistory = new Dictionary<string, BoundedQueue<int>>();

        // Latest palm-center per trackid, used for simple tracking:
        // { trackid1: (cx, cy), trackid2: (cx, cy), ... }
        var palmTrackidCxcy = new OrderedMap<int, (int X, int Y)>();

        //  ######################################################################
        int mode = 0;
        double whRatio = capWidth / (double)capHeight;

        bool auto = false;
        int prevNumber = -1;
        Mat? image = null;

        // In Python these loop variables leak out of the per-hand loop (function scope)
        // and are what logging_csv receives, so they live outside the frame loop here too.
        int trackid = 0;
        double[] preProcessedLandmark = Array.Empty<double>();

        try
        {
            while (!_stopRequested)
            {
                double fps = cvFpsCalc.Get();

                while (_commands.TryDequeue(out var command))
                {
                    command();
                }

                // Key handling ##################################################
                int key = (useImage && image != null) ? Cv2.WaitKey(0) : Cv2.WaitKey(1);

                // Poll every hotkey every frame (needed for press detection)
                bool quit = HotkeyPressed('Q') || key == 27;           // Ctrl+Alt+Q or Esc
                bool toggleWindow = HotkeyPressed('V') || key == 'v';  // Ctrl+Alt+V or v
                bool toggleMouse = HotkeyPressed('M') || key == 'm';   // Ctrl+Alt+M or m
                bool cycleMouseMode = HotkeyPressed('J');              // Ctrl+Alt+J
                bool toggleKeyboard = HotkeyPressed('K');              // Ctrl+Alt+K

                if (quit)
                {
                    QuitRequested?.Invoke();
                    break;
                }

                int number;
                (number, mode, auto, prevNumber) = SelectMode(key, mode, auto, prevNumber);

                if (toggleWindow)
                {
                    if (_opt.UseOpenCvWindow)
                    {
                        _showWindow = !_showWindow;
                        if (!_showWindow)
                        {
                            Cv2.DestroyWindow(MainWindow);
                        }
                    }
                    ToggleWindowRequested?.Invoke();
                }
                if (toggleMouse && _mouse is not null)
                {
                    _mouse.Enabled = !_mouse.Enabled;
                    Log?.Invoke(_mouse.Enabled ? "Mouse control ON" : "Mouse control OFF");
                }
                if (cycleMouseMode && _mouse is not null)
                {
                    _mouse.CycleMode();   // Relative -> Absolute -> Joystick
                    Log?.Invoke($"Mouse mode: {_mouse.Mode}");
                }
                if (toggleKeyboard)
                {
                    keyboard.Enabled = !keyboard.Enabled;
                    Log?.Invoke(keyboard.Enabled ? "Keyboard control ON" : "Keyboard control OFF");
                }
                if (key == 'c' && _calibrator is null)
                {
                    StartCalibration(CalibrationKind.Points); // recalibrate
                }
                if (key == 'p' && _calibrator is null)
                {
                    StartCalibration(CalibrationKind.Pursuit); // recalibrate by following a moving dot
                }

                // Camera capture ################################################
                image?.Dispose();
                image = new Mat();
                if (!cap.Read(image) || image.Empty())
                {
                    break;
                }
                // Idle throttle: nobody in view for a while -> run the models on only every Nth frame.
                // The camera is still read every frame so the picture never goes stale. Never while
                // calibrating (the calibration screen needs every frame).
                bool idle = _calibrator is null && mouseClock.Elapsed.TotalSeconds - lastHandSeen > _opt.IdleAfterSeconds;
                if (idle && frameIndex++ % _opt.IdleFrameInterval != 0)
                {
                    continue;
                }

                if (!_opt.DisableImageFlip)
                {
                    Cv2.Flip(image, image, FlipMode.Y); // mirror display
                }
                using var debugImage = image.Clone();
                Point2f[]? mouseLandmarks = null;     // right hand
                Point2f[]? keyboardLandmarks = null;  // left hand

                // Detection #####################################################

                // ===================================================== PalmDetection
                var hands = palmDetection.Run(image);
                if (hands.Count > 0)
                {
                    lastHandSeen = mouseClock.Elapsed.TotalSeconds;
                }
                else if (idle)
                {
                    PutOutlinedText(debugImage, "IDLE: no hand, checking 5x/s", new Point(10, 235), 0.6);
                }

                var rects = new List<RotRect>();
                var notRotateRects = new List<(int Rcx, int Rcy, int X1, int Y1, int X2, int Y2)>();
                var croppedRotatedHandsImages = new List<Mat>();

                // Reset tracking history when no hands are detected
                if (hands.Count == 0)
                {
                    palmTrackidCxcy.Clear();
                }
                // trackid → box (x1, y1), in detection order
                var palmTrackidBoxX1y1s = new OrderedMap<int, (int X, int Y)>();

                if (hands.Count > 0)
                {
                    foreach (var hand in hands)
                    {
                        int cx = (int)(hand.SqnRrCenterX * capWidth);
                        int cy = (int)(hand.SqnRrCenterY * capHeight);
                        int xmin = (int)((hand.SqnRrCenterX - hand.SqnRrSize / 2) * capWidth);
                        int xmax = (int)((hand.SqnRrCenterX + hand.SqnRrSize / 2) * capWidth);
                        int ymin = (int)((hand.SqnRrCenterY - hand.SqnRrSize * whRatio / 2) * capHeight);
                        int ymax = (int)((hand.SqnRrCenterY + hand.SqnRrSize * whRatio / 2) * capHeight);
                        xmin = Math.Max(0, xmin);
                        xmax = Math.Min(capWidth, xmax);
                        ymin = Math.Max(0, ymin);
                        ymax = Math.Min(capHeight, ymax);
                        double degree = hand.Rotation * 180.0 / Math.PI;
                        rects.Add(new RotRect(cx, cy, xmax - xmin, ymax - ymin, (float)degree));
                    }

                    // Palm crops with rotation corrected to 0 degrees
                    croppedRotatedHandsImages = ImageUtils.RotateAndCropRectangle(
                        image,
                        rects,
                        CropOutOfRangeOperation.Padding);

                    // Debug =====================================================
                    foreach (var rect in rects)
                    {
                        // Rotated region (red)
                        var box = Cv2.BoxPoints(new RotatedRect(
                                new Point2f(rect.Cx, rect.Cy),
                                new Size2f(rect.Width, rect.Height),
                                rect.Angle))
                            .Select(p => new Point((int)p.X, (int)p.Y))
                            .ToArray();
                        Cv2.DrawContours(debugImage, new[] { box }, 0, new Scalar(0, 0, 255), 2, LineTypes.AntiAlias);

                        // Unrotated region (orange)
                        int rcx = (int)rect.Cx;
                        int rcy = (int)rect.Cy;
                        int halfW = (int)Math.Floor(rect.Width / 2);
                        int halfH = (int)Math.Floor(rect.Height / 2);
                        int x1 = rcx - halfW;
                        int y1 = rcy - halfH;
                        int x2 = rcx + halfW;
                        int y2 = rcy + halfH;
                        int textX = Math.Min(Math.Max(x1, 10), capWidth - 120);
                        int textY = Math.Min(Math.Max(y1 - 15, 45), capHeight - 20);
                        notRotateRects.Add((rcx, rcy, x1, y1, x2, y2));

                        // Detection box size
                        PutOutlinedText(debugImage, $"{y2 - y1}x{x2 - x1}", new Point(textX, textY), 0.8);
                        // Detection box
                        Cv2.Rectangle(debugImage, new Point(x1, y1), new Point(x2, y2), new Scalar(0, 128, 255), 2, LineTypes.AntiAlias);
                        // Detection center
                        Cv2.Circle(debugImage, new Point(rcx, rcy), 3, new Scalar(0, 255, 255), -1);

                        /*
                         * Save the latest palm center for tracking:
                         *   1. Find the closest stored center to this one
                         *   2. If it's more than 100px away, treat it as a new palm
                         *   3. Otherwise reuse that trackid and overwrite its center
                         */
                        int newTrackid;
                        if (palmTrackidCxcy.Count > 0)
                        {
                            // Nearest-neighbour search
                            int nearestIndex = -1;
                            double nearestDistance = double.MaxValue;
                            int idx = 0;
                            foreach (var p in palmTrackidCxcy.Values)
                            {
                                float dx = p.X - (float)rcx;
                                float dy = p.Y - (float)rcy;
                                double d = Math.Sqrt(dx * dx + dy * dy);
                                if (d < nearestDistance)
                                {
                                    nearestDistance = d;
                                    nearestIndex = idx;
                                }
                                idx++;
                            }
                            // NOTE: like the original, this uses the *list index* + 1 as the trackid.
                            newTrackid = nearestIndex + 1;
                            if (nearestDistance > 100)
                            {
                                // New trackid = most recently inserted trackid + 1
                                newTrackid = palmTrackidCxcy.LastKey + 1;
                            }
                        }
                        else
                        {
                            newTrackid = 1;
                        }

                        palmTrackidCxcy[newTrackid] = (rcx, rcy);
                        palmTrackidBoxX1y1s[newTrackid] = (x1, y1);
                    }
                }

                // ====================================================== HandLandmark
                if (croppedRotatedHandsImages.Count > 0)
                {
                    // Batched landmark inference
                    var (handLandmarks, rotatedImageSizeLeftrights) = handLandmark.Run(croppedRotatedHandsImages, rects);

                    if (handLandmarks.Count > 0)
                    {
                        var preProcessedLandmarks = new List<double[]>();

                        int zipCount = Min(palmTrackidBoxX1y1s.Count, handLandmarks.Count, rotatedImageSizeLeftrights.Count, notRotateRects.Count);
                        for (int i = 0; i < zipCount; i++)
                        {
                            trackid = palmTrackidBoxX1y1s.ElementAt(i).Key;
                            var landmark = handLandmarks[i];
                            var sizeLr = rotatedImageSizeLeftrights[i];
                            var notRotateRect = notRotateRects[i];

                            double thickCoef = sizeLr.RotatedImageWidth / 400.0;
                            var lines = LinesHand
                                .Select(line => line.Select(pointIndex => landmark[pointIndex]).ToArray())
                                .ToArray();
                            int radius = (int)(1 + thickCoef * 5);
                            Cv2.Polylines(debugImage, lines, false, new Scalar(255, 0, 0), radius, LineTypes.AntiAlias);
                            foreach (var pt in landmark)
                            {
                                Cv2.Circle(debugImage, pt, radius, new Scalar(0, 128, 255), -1);
                            }

                            float leftHand0OrRightHand1 = _opt.DisableImageFlip
                                ? sizeLr.LeftHand0OrRightHand1
                                : 1 - sizeLr.LeftHand0OrRightHand1;
                            string handedness = leftHand0OrRightHand1 == 0 ? "Left " : "Right";

                            // Right hand -> mouse, left hand -> keyboard (first of each wins)
                            if (leftHand0OrRightHand1 >= 0.5f)
                            {
                                mouseLandmarks ??= landmark.Select(p => new Point2f(p.X, p.Y)).ToArray();
                            }
                            else
                            {
                                keyboardLandmarks ??= landmark.Select(p => new Point2f(p.X, p.Y)).ToArray();
                            }
                            int textX = Math.Min(Math.Max(notRotateRect.X1, 10), capWidth - 120);
                            int textY = Math.Min(Math.Max(notRotateRect.Y1 - 70, 20), capHeight - 70);
                            PutOutlinedText(debugImage, $"trackid:{trackid} {handedness}", new Point(textX, textY), 0.8);

                            // Convert to relative, normalized coordinates: [x, y] x 21 → 42 values
                            preProcessedLandmark = PreProcessLandmark(landmark);
                            preProcessedLandmarks.Add(preProcessedLandmark);
                        }

                        // Index-finger trajectories → relative coordinates (one list per trackid)
                        var preProcessedPointHistories = PreProcessPointHistory(
                            debugImage.Cols,
                            debugImage.Rows,
                            pointHistory);

                        // Save training data
                        LoggingCsv(number, mode, trackid, preProcessedLandmark, preProcessedPointHistories);

                        // Hand sign classification (batched)
                        long[] handSignIds = keypointClassifier.Run(preProcessedLandmarks);
                        int signZip = Min(palmTrackidBoxX1y1s.Count, handLandmarks.Count, handSignIds.Length);
                        for (int i = 0; i < signZip; i++)
                        {
                            int tid = palmTrackidBoxX1y1s.ElementAt(i).Key;
                            if (!pointHistory.TryGetValue(tid, out var history))
                            {
                                history = new BoundedQueue<(int X, int Y)>(historyLength);
                                pointHistory[tid] = history;
                            }
                            if (handSignIds[i] == 2) // pointing sign
                            {
                                var indexTip = handLandmarks[i][8]; // index fingertip
                                history.Enqueue((indexTip.X, indexTip.Y));
                            }
                            else
                            {
                                history.Enqueue((0, 0));
                            }
                        }

                        /*
                         * So stale index-finger trails don't linger on screen: if a trackid's
                         * whole trajectory is identical to last frame's, it's considered no
                         * longer tracked (left the frame) and its history is dropped.
                         */
                        if (prePointHistory.Count > 0)
                        {
                            foreach (var trackId in pointHistory.Keys.ToList())
                            {
                                if (prePointHistory.TryGetValue(trackId, out var prePoints)
                                    && pointHistory[trackId].SequenceEqual(prePoints))
                                {
                                    pointHistory.Remove(trackId);
                                }
                            }
                        }
                        prePointHistory = DeepCopy(pointHistory);

                        // Finger gesture classification (batched)
                        long[]? fingerGestureIds = null;
                        var tempTrackidX1y1s = new OrderedMap<int, (int X, int Y)>();
                        var tempPreProcessedPointHistory = new List<double[]>();
                        int histZip = Math.Min(palmTrackidBoxX1y1s.Count, preProcessedPointHistories.Count);
                        for (int i = 0; i < histZip; i++)
                        {
                            var (tid, x1y1) = palmTrackidBoxX1y1s.ElementAt(i);
                            var preProcessedPointHistory = preProcessedPointHistories[i];
                            int pointHistoryLen = preProcessedPointHistory.Length;
                            if (pointHistoryLen > 0 && pointHistoryLen % (historyLength * 2) == 0)
                            {
                                tempTrackidX1y1s[tid] = x1y1;
                                tempPreProcessedPointHistory.Add(preProcessedPointHistory);
                            }
                        }
                        if (tempPreProcessedPointHistory.Count > 0)
                        {
                            fingerGestureIds = pointHistoryClassifier.Run(tempPreProcessedPointHistory);
                        }

                        // Most frequent gesture over the recent detections
                        if (fingerGestureIds != null)
                        {
                            int gZip = Math.Min(tempTrackidX1y1s.Count, fingerGestureIds.Length);
                            for (int i = 0; i < gZip; i++)
                            {
                                var (tid, x1y1) = tempTrackidX1y1s.ElementAt(i);
                                string trackidStr = tid.ToString(CultureInfo.InvariantCulture);
                                if (!fingerGestureHistory.TryGetValue(trackidStr, out var gestureHistory))
                                {
                                    gestureHistory = new BoundedQueue<int>(gestureHistoryLength);
                                    fingerGestureHistory[trackidStr] = gestureHistory;
                                }
                                gestureHistory.Enqueue((int)fingerGestureIds[i]);
                                int mostCommonFgId = MostCommon(gestureHistory);

                                int textX = Math.Min(Math.Max(x1y1.X, 10), capWidth - 120);
                                int textY = Math.Min(Math.Max(x1y1.Y - 45, 20), capHeight - 45);
                                string classifierLabel = pointHistoryClassifierLabels[mostCommonFgId];
                                PutOutlinedText(debugImage, classifierLabel, new Point(textX, textY), 0.8);
                            }
                        }
                    }
                    else
                    {
                        pointHistory = new OrderedMap<int, BoundedQueue<(int X, int Y)>>();
                    }
                }
                else
                {
                    pointHistory = new OrderedMap<int, BoundedQueue<(int X, int Y)>>();
                }

                foreach (var m in croppedRotatedHandsImages)
                {
                    m.Dispose();
                }

                DrawPointHistory(debugImage, pointHistory);
                DrawInfo(debugImage, fps, mode, number, auto);

                // Custom gestures ###############################################
                // Recognised before the mouse/keyboard run, so a hand making a custom gesture can be held
                // back from them: otherwise a peace sign would also fold "keyboard" fingers or pinch-click.
                var mouseInput = mouseLandmarks;
                var keyboardInput = keyboardLandmarks;
                if (Recording || _calibrator is not null)
                {
                    _gestureTracker.Update([], null, null); // ends anything active
                    mouseInput = keyboardInput = null;
                    HandsUpdated?.Invoke(new HandsFrame(mouseLandmarks, keyboardLandmarks, null, 0, null, 0));
                }
                else
                {
                    var live = _gestureTracker.Update(_customGestures, mouseLandmarks, keyboardLandmarks);
                    if (_gestureTracker.ActiveRight is not null) mouseInput = null;
                    if (_gestureTracker.ActiveLeft is not null) keyboardInput = null;
                    HandsUpdated?.Invoke(new HandsFrame(mouseLandmarks, keyboardLandmarks,
                        live.Right?.Name, live.RightConfidence, live.Left?.Name, live.LeftConfidence));
                    if (_gestureTracker.ActiveRight is { } r) PutOutlinedText(debugImage, $"GESTURE: {r.Name}", new Point(10, 260), 0.7);
                    if (_gestureTracker.ActiveLeft is { } l) PutOutlinedText(debugImage, $"GESTURE (left): {l.Name}", new Point(10, 285), 0.7);
                }

                // Mouse control #################################################
                double now = mouseClock.Elapsed.TotalSeconds;
                if (_calibrator is not null)
                {
                    _calibrator.Update(mouseLandmarks is null ? null : HandMouse.Anchor(mouseLandmarks), now);
                    _calibrator.Render(debugImage);

                    if (_calibrator.IsComplete)
                    {
                        calib = _calibrator.Result!;
                        calib.Save(_opt.CalibrationPath);
                        Log?.Invoke(_calibrator is PursuitCalibrator pursuit
                            ? $"Calibration saved ({pursuit.Summary})"
                            : $"Calibration saved (avg error {calib.MeanErrorPx:F0}px)");
                        _calibrator.Dispose();
                        _calibrator = null;
                        _mouse = CreateMouse(calib);
                    }
                }
                else if (_mouse is not null)
                {
                    _mouse.Update(mouseInput, now);
                    string status = !_mouse.Enabled ? "MOUSE:OFF"
                        : _mouse.IsPinching && _mouse.IsRightPinching ? "MOUSE:LEFT+RIGHT"
                        : _mouse.IsPinching ? "MOUSE:LEFT CLICK"
                        : _mouse.IsRightPinching ? "MOUSE:RIGHT CLICK"
                        : _mouse.IsClutched ? "MOUSE:LIFTED"
                        : $"MOUSE:{_mouse.Mode.ToString().ToUpperInvariant()}";
                    PutOutlinedText(debugImage, status, new Point(10, 160), 0.6);
                }

                // Keyboard control ##############################################
                keyboard.Update(keyboardInput); // null while calibrating, recording, or making a custom gesture
                string keys = !keyboard.Enabled ? "KEYS:OFF"
                    : keyboard.IsResting ? "KEYS:REST (fist)"
                    : keyboard.HeldKeys.Count == 0 ? "KEYS:-"
                    : "KEYS:" + string.Join("+", keyboard.HeldKeys.Select(HandKeyboard.KeyName));
                PutOutlinedText(debugImage, keys, new Point(10, 185), 0.6);
                if (keyboardLandmarks is not null)
                {
                    // Live finger readings for tuning thresholds (lower = more folded)
                    PutOutlinedText(debugImage, keyboard.DebugValues, new Point(10, 210), 0.5);
                }

                // Display ###################################################
                if (_showWindow && _calibrator is null)
                {
                    Cv2.ImShow(MainWindow, debugImage);
                }
                videoWriter?.Write(debugImage);

                if (PreviewEnabled)
                {
                    Interlocked.Exchange(ref _preview, debugImage.Clone())?.Dispose();
                }
                Volatile.Write(ref _status, new GestureStatus(
                    Running: true, Fps: fps, Hands: hands.Count, Calibrating: _calibrator is not null,
                    MouseEnabled: _mouse?.Enabled ?? false, MouseMode: _mouse?.Mode ?? MouseMode.Relative,
                    Pinching: _mouse?.IsPinching ?? false, Clutched: _mouse?.IsClutched ?? false,
                    Idle: idle && hands.Count == 0));
            }
        }
        finally
        {
            _gestureTracker.Reset();  // end any held custom gesture (its keys come back up)
            keyboard.Dispose();       // release any held keys
            _mouse?.Dispose();        // let go of the mouse button and stop the output thread
            _mouse = null;
            _calibrator?.Dispose();   // close the calibration window before DestroyAllWindows
            _calibrator = null;
            image?.Dispose();
            videoWriter?.Release();
            cap.Release();
            Cv2.DestroyAllWindows();
            Volatile.Write(ref _status, GestureStatus.Stopped);
        }
    }

    private void StartCalibration(CalibrationKind kind)
    {
        if (_calibrator is not null) return;
        _mouse?.Dispose();   // stops its output thread too
        _mouse = null;
        if (_showWindow)
        {
            Cv2.DestroyWindow(MainWindow); // don't let it cover the calibration screen
        }
        _calibrator = CreateCalibrator(kind);
    }

    private ICalibrator CreateCalibrator(CalibrationKind kind) => kind switch
    {
        CalibrationKind.Pursuit => new PursuitCalibrator(_screenW, _screenH),
        _ => new ScreenCalibrator(_screenW, _screenH),
    };

    /// <summary>All mouse tuning lives here; used at startup and after every calibration.</summary>
    private static HandMouse CreateMouse(CalibrationData calib) =>
        new HandMouse(calib,
            sensitivity: 1.5,      // cursor speed (try 1.0 - 3.0)
            acceleration: 0.0005,  // extra speed for fast flicks (0 = off)
            minCutoff: 0.25,       // smoothing at rest (lower = steadier, laggier)
            deadzonePx: 8,         // wobble ignored while holding still
            glideSeconds: 0.03);   // glide between camera frames (higher = silkier, laggier)

    // Global hotkeys #########################################################
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>True on the frame Ctrl+Alt+key is pressed. Works system-wide, even with the window hidden.</summary>
    private bool HotkeyPressed(char key)
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        key = char.ToUpperInvariant(key);
        bool down = Down(0x11) && Down(0x12) && Down(key); // Ctrl + Alt + key
        bool pressed = down && !_heldHotkeys.Contains(key);
        if (down) _heldHotkeys.Add(key); else _heldHotkeys.Remove(key);
        return pressed;
    }

    // Background throttling ##################################################
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PowerThrottlingState info, uint size);

    /// <summary>Stops Windows from throttling this process when its window is minimized or in the background.</summary>
    public static void DisableBackgroundThrottling()
    {
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = 0x1 | 0x4, // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION
            StateMask = 0,           // 0 = turn throttling OFF for those
        };
        SetProcessInformation(Process.GetCurrentProcess().Handle, 4 /* ProcessPowerThrottling */,
            ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PowerThrottlingState>());

        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
    }

    private static (int Number, int Mode, bool Auto, int PrevNumber) SelectMode(int key, int mode, bool auto = false, int prevNumber = -1)
    {
        int number = -1;
        if (key >= 48 && key <= 57) // 0 ~ 9
        {
            number = key - 48;
            prevNumber = number;
        }
        if (key == 110) mode = 0;     // n
        if (key == 107) mode = 1;     // k
        if (key == 104) mode = 2;     // h
        if (key == 97) auto = !auto;  // a

        if (auto)
        {
            if (prevNumber != -1)
            {
                number = prevNumber;
            }
        }
        else
        {
            prevNumber = -1;
        }

        return (number, mode, auto, prevNumber);
    }

    private static double[] PreProcessLandmark(Point[] landmarkList)
    {
        if (landmarkList.Length == 0)
        {
            return Array.Empty<double>();
        }

        // Relative to the wrist (landmark 0), flattened to 1-D
        int baseX = landmarkList[0].X;
        int baseY = landmarkList[0].Y;
        var temp = new double[landmarkList.Length * 2];
        for (int i = 0; i < landmarkList.Length; i++)
        {
            temp[i * 2] = landmarkList[i].X - baseX;
            temp[i * 2 + 1] = landmarkList[i].Y - baseY;
        }

        // Normalize
        double maxValue = temp.Max(v => Math.Abs(v));
        for (int i = 0; i < temp.Length; i++)
        {
            temp[i] /= maxValue;
        }

        return temp;
    }

    /// <summary>
    /// Converts each trackid's index-finger history into coordinates relative to its
    /// first point, normalized by image size, flattened to [rx, ry, rx, ry, ...].
    /// </summary>
    private static List<double[]> PreProcessPointHistory(
        int imageWidth,
        int imageHeight,
        OrderedMap<int, BoundedQueue<(int X, int Y)>> pointHistory)
    {
        var relativeCoordinateListByTrackid = new List<double[]>();
        if (pointHistory.Count == 0)
        {
            return relativeCoordinateListByTrackid;
        }

        foreach (var points in pointHistory.Values)
        {
            var (baseX, baseY) = points.First();
            var list = new double[points.Count * 2];
            int i = 0;
            foreach (var (x, y) in points)
            {
                list[i++] = (x - baseX) / (double)imageWidth;
                list[i++] = (y - baseY) / (double)imageHeight;
            }
            relativeCoordinateListByTrackid.Add(list);
        }

        return relativeCoordinateListByTrackid;
    }

    private void LoggingCsv(int number, int mode, int trackid, double[] landmarkList, List<double[]> pointHistories)
    {
        if (mode == 1 && number >= 0 && number <= 9)
        {
            string csvPath = Path.Combine(_opt.ModelDir, "keypoint_classifier/keypoint.csv");
            File.AppendAllText(csvPath, CsvRow(number, trackid, landmarkList));
        }
        if (mode == 2 && number >= 0 && number <= 9)
        {
            string csvPath = Path.Combine(_opt.ModelDir, "point_history_classifier/point_history.csv");
            var sb = new StringBuilder();
            foreach (var pointHistory in pointHistories)
            {
                sb.Append(CsvRow(number, trackid, pointHistory));
            }
            File.AppendAllText(csvPath, sb.ToString());
        }
    }

    private static string CsvRow(int number, int trackid, IEnumerable<double> values)
    {
        var fields = new List<string>
        {
            number.ToString(CultureInfo.InvariantCulture),
            trackid.ToString(CultureInfo.InvariantCulture),
        };
        fields.AddRange(values.Select(FormatPythonFloat));
        return string.Join(",", fields) + "\r\n"; // Python's csv module writes \r\n
    }

    private static void DrawPointHistory(Mat image, OrderedMap<int, BoundedQueue<(int X, int Y)>> pointHistory)
    {
        foreach (var points in pointHistory.Values)
        {
            int index = 0;
            foreach (var (x, y) in points)
            {
                if (x != 0 && y != 0)
                {
                    Cv2.Circle(image, new Point(x, y), 1 + index / 2, new Scalar(152, 251, 152), 2);
                }
                index++;
            }
        }
    }

    private static void DrawInfo(Mat image, double fps, int mode, int number, bool auto)
    {
        string fpsText = $"FPS:{FormatPythonFloat(fps)}";
        Cv2.PutText(image, fpsText, new Point(10, 30), HersheyFonts.HersheySimplex, 1.0, Black, 4, LineTypes.AntiAlias);
        Cv2.PutText(image, fpsText, new Point(10, 30), HersheyFonts.HersheySimplex, 1.0, White, 2, LineTypes.AntiAlias);

        string[] modeString = { "Logging Key Point", "Logging Point History" };
        if (mode >= 1 && mode <= 2)
        {
            Cv2.PutText(image, $"MODE:{modeString[mode - 1]}", new Point(10, 90), HersheyFonts.HersheySimplex, 0.6, White, 1, LineTypes.AntiAlias);
            if (number >= 0 && number <= 9)
            {
                Cv2.PutText(image, $"NUM:{number}", new Point(10, 110), HersheyFonts.HersheySimplex, 0.6, White, 1, LineTypes.AntiAlias);
            }
        }
        Cv2.PutText(image, $"AUTO:{(auto ? "True" : "False")}", new Point(10, 130), HersheyFonts.HersheySimplex, 0.6, White, 1, LineTypes.AntiAlias);
    }

    /// <summary>Black outline + yellow text, as used for all per-hand labels.</summary>
    private static void PutOutlinedText(Mat image, string text, Point org, double scale)
    {
        Cv2.PutText(image, text, org, HersheyFonts.HersheySimplex, scale, Black, 2, LineTypes.AntiAlias);
        Cv2.PutText(image, text, org, HersheyFonts.HersheySimplex, scale, LabelYellow, 1, LineTypes.AntiAlias);
    }

    /// <summary>Equivalent of Counter(seq).most_common()[0][0] (ties → first seen).</summary>
    private static int MostCommon(IEnumerable<int> values)
    {
        var counts = new Dictionary<int, int>();
        var firstSeen = new List<int>();
        foreach (var v in values)
        {
            if (counts.TryGetValue(v, out int c))
            {
                counts[v] = c + 1;
            }
            else
            {
                counts[v] = 1;
                firstSeen.Add(v);
            }
        }

        int best = firstSeen[0];
        foreach (var v in firstSeen)
        {
            if (counts[v] > counts[best])
            {
                best = v;
            }
        }
        return best;
    }

    private static OrderedMap<int, BoundedQueue<(int X, int Y)>> DeepCopy(OrderedMap<int, BoundedQueue<(int X, int Y)>> source)
    {
        var copy = new OrderedMap<int, BoundedQueue<(int X, int Y)>>();
        foreach (var (key, value) in source)
        {
            copy[key] = new BoundedQueue<(int X, int Y)>(value);
        }
        return copy;
    }

    private static List<string> ReadLabels(string path)
    {
        // Encoding.UTF8 strips the BOM, matching Python's 'utf-8-sig'
        return File.ReadAllLines(path, Encoding.UTF8)
            .Where(line => line.Length > 0)
            .Select(line => line.Split(',')[0])
            .ToList();
    }

    /// <summary>Formats a double the way Python's str()/repr() does (e.g. 30.0, 0.125, nan).</summary>
    private static string FormatPythonFloat(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";

        string s = value.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E'))
        {
            s += ".0";
        }
        return s;
    }

    private static int Min(params int[] values) => values.Min();
}
