using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

public enum MouseMode
{
    /// <summary>Hand moves the cursor like a real mouse: hand motion = cursor motion. Works in games too.</summary>
    Relative,
    /// <summary>Hand position = cursor position (desktop only; games ignore it).</summary>
    Absolute,
    /// <summary>Hand offset from center = turn speed (FPS games).</summary>
    Joystick,
}

/// <summary>
/// Drives the mouse from hand landmarks, with pinch-to-click (hold pinch to drag).
/// In Relative mode, make a fist to "lift the mouse": the cursor stops so you can
/// reposition your hand, just like lifting a real mouse off the desk.
/// Landmarks: the 21 MediaPipe hand points, in camera pixel coordinates.
/// </summary>
public sealed class HandMouse
{
    // Palm landmarks: wrist + the four finger knuckles (MCPs). Stable during pinches,
    // and averaging five points cancels much of the per-point noise.
    static readonly int[] PalmPoints = { 0, 5, 9, 13, 17 };
    static readonly int[] FingerTips = { 8, 12, 16, 20 };

    readonly CalibrationData _cal;
    readonly OneEuroFilter _fx, _fy;
    readonly double _deadzonePx;
    readonly double _pinchOn, _pinchOff, _clickFreezeSeconds;
    readonly double _sensitivity, _acceleration;
    readonly double _stickDeadzone, _stickMaxSpeed;

    bool _buttonDown;
    double _freezeUntil;
    bool _enabled = true;
    bool _hasCursor;
    double _cursorX, _cursorY;   // smoothed + deadzoned hand position, in screen px
    double _prevX, _prevY;       // last frame's, for relative deltas
    double _lastTime = -1;
    double _accX, _accY;         // sub-pixel remainders for relative output

    public MouseMode Mode { get; set; } = MouseMode.Relative;
    public bool IsPinching => _buttonDown;
    /// <summary>Relative mode: true while a fist is "lifting the mouse".</summary>
    public bool IsClutched { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) Release(); }
    }

    /// <param name="minCutoff">Smoothing at rest. Lower = steadier but laggier (try 0.2 to 1.0).</param>
    /// <param name="beta">Responsiveness during fast moves. Higher = less lag (try 0.001 to 0.02).</param>
    /// <param name="deadzonePx">Screen px of wobble ignored while holding still. 0 disables it.</param>
    /// <param name="sensitivity">Relative mode: cursor px per px of hand movement (in calibrated screen units).</param>
    /// <param name="acceleration">Relative mode: extra gain for fast moves. 0 = none; 0.0005 doubles gain at 2000 px/s.</param>
    /// <param name="stickDeadzone">Joystick mode: distance from center (screen px) before turning starts.</param>
    /// <param name="stickMaxSpeed">Joystick mode: turn speed (counts/s) at the edge.</param>
    public HandMouse(CalibrationData calibration,
                     double minCutoff = 0.4, double beta = 0.004, double deadzonePx = 6,
                     double sensitivity = 1.5, double acceleration = 0.0005,
                     double pinchOn = 0.25, double pinchOff = 0.40, double clickFreezeSeconds = 0.15,
                     double stickDeadzone = 120, double stickMaxSpeed = 2500)
    {
        _cal = calibration;
        _fx = new OneEuroFilter(minCutoff, beta);
        _fy = new OneEuroFilter(minCutoff, beta);
        _deadzonePx = deadzonePx;
        _sensitivity = sensitivity;
        _acceleration = acceleration;
        _pinchOn = pinchOn;
        _pinchOff = pinchOff;
        _clickFreezeSeconds = clickFreezeSeconds;
        _stickDeadzone = stickDeadzone;
        _stickMaxSpeed = stickMaxSpeed;
    }

    /// <summary>The hand point that drives the cursor: palm center. Calibration must use the same anchor.</summary>
    public static Point2f Anchor(IReadOnlyList<Point2f> lm)
    {
        float x = 0, y = 0;
        foreach (int i in PalmPoints) { x += lm[i].X; y += lm[i].Y; }
        return new Point2f(x / PalmPoints.Length, y / PalmPoints.Length);
    }

    /// <summary>Cycles Relative -> Absolute -> Joystick.</summary>
    public void CycleMode()
    {
        Mode = (MouseMode)(((int)Mode + 1) % 3);
        _accX = _accY = 0;
    }

    /// <param name="landmarks">21 landmarks of the controlling hand, or null if no hand this frame.</param>
    /// <param name="now">Time in seconds.</param>
    public void Update(IReadOnlyList<Point2f>? landmarks, double now)
    {
        double dt = _lastTime < 0 ? 0 : Math.Min(now - _lastTime, 0.1);
        _lastTime = now;

        if (!_enabled || landmarks is null || landmarks.Count < 21)
        {
            // Hand gone: like lifting a mouse. Next appearance starts fresh, no jump.
            Release();
            _fx.Reset();
            _fy.Reset();
            _hasCursor = false;
            _accX = _accY = 0;
            IsClutched = false;
            return;
        }

        // 1. Map to screen units, 2. One Euro filter
        var screen = _cal.Map(Anchor(landmarks));
        double fx = _fx.Filter(screen.X, now);
        double fy = _fy.Filter(screen.Y, now);

        // 3. Soft deadzone: position trails the filtered point on a short leash, so
        //    tiny wobble is ignored but real movement passes through smoothly.
        if (!_hasCursor)
        {
            _cursorX = _prevX = fx;
            _cursorY = _prevY = fy;
            _hasCursor = true;
        }
        else
        {
            double dx = fx - _cursorX, dy = fy - _cursorY;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > _deadzonePx)
            {
                double k = (d - _deadzonePx) / d;
                _cursorX += dx * k;
                _cursorY += dy * k;
            }
        }

        // This frame's smoothed hand motion (always consumed, so frozen/clutched motion is discarded, not saved up)
        double moveX = _cursorX - _prevX, moveY = _cursorY - _prevY;
        _prevX = _cursorX;
        _prevY = _cursorY;

        double handSize = Dist(landmarks[0], landmarks[9]);
        IsClutched = Mode == MouseMode.Relative && IsFist(landmarks, handSize);

        // Pinch = left button. A fist can bring the thumb near the index, so never start a click from a fist.
        double pinch = handSize > 1e-3 ? Dist(landmarks[4], landmarks[8]) / handSize : 1.0;
        bool wantDown = _buttonDown ? pinch < _pinchOff : (pinch < _pinchOn && !IsClutched);
        if (wantDown != _buttonDown)
        {
            _freezeUntil = now + _clickFreezeSeconds;
            if (wantDown) NativeMouse.LeftDown(); else NativeMouse.LeftUp();
            _buttonDown = wantDown;
        }

        if (now < _freezeUntil) return;

        switch (Mode)
        {
            case MouseMode.Relative:
                if (!IsClutched) MoveRelative(moveX, moveY, dt);
                break;
            case MouseMode.Absolute:
                NativeMouse.MoveTo(
                    (int)Math.Round(Math.Clamp(_cursorX, 0, _cal.ScreenWidth - 1)),
                    (int)Math.Round(Math.Clamp(_cursorY, 0, _cal.ScreenHeight - 1)));
                break;
            case MouseMode.Joystick:
                MoveJoystick(fx, fy, dt);
                break;
        }
    }

    /// <summary>Hand motion -> mouse motion, with optional acceleration (fast flicks travel further).</summary>
    void MoveRelative(double moveX, double moveY, double dt)
    {
        if (moveX == 0 && moveY == 0) return;

        double gain = _sensitivity;
        if (_acceleration > 0 && dt > 0)
        {
            double speed = Math.Sqrt(moveX * moveX + moveY * moveY) / dt; // screen px/s
            gain *= Math.Min(1 + _acceleration * speed, 4);
        }
        SendAccumulated(moveX * gain, moveY * gain);
    }

    /// <summary>Hand at center = still; push toward an edge to turn, faster further out.</summary>
    void MoveJoystick(double fx, double fy, double dt)
    {
        double cx = _cal.ScreenWidth / 2.0, cy = _cal.ScreenHeight / 2.0;
        double ox = fx - cx, oy = fy - cy;
        double d = Math.Sqrt(ox * ox + oy * oy);
        if (d <= _stickDeadzone || dt <= 0) return;

        double range = Math.Max(1, Math.Min(cx, cy) - _stickDeadzone);
        double t = Math.Clamp((d - _stickDeadzone) / range, 0, 1);
        double speed = _stickMaxSpeed * t * t;
        SendAccumulated(ox / d * speed * dt, oy / d * speed * dt);
    }

    /// <summary>Sends whole counts and keeps the fractions, so slow movement isn't rounded away to zero.</summary>
    void SendAccumulated(double dx, double dy)
    {
        _accX += dx;
        _accY += dy;
        int mx = (int)_accX, my = (int)_accY;
        _accX -= mx;
        _accY -= my;
        NativeMouse.MoveRelative(mx, my);
    }

    /// <summary>Fist = all four fingertips curled in close to the wrist.</summary>
    static bool IsFist(IReadOnlyList<Point2f> lm, double handSize)
    {
        if (handSize < 1e-3) return false;
        foreach (int tip in FingerTips)
            if (Dist(lm[tip], lm[0]) / handSize > 1.3) return false; // open finger reaches ~1.8-2.0
        return true;
    }

    public void Release()
    {
        if (!_buttonDown) return;
        NativeMouse.LeftUp();
        _buttonDown = false;
    }

    static double Dist(Point2f a, Point2f b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
