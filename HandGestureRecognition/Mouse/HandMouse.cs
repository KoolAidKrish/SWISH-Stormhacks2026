using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// Drives the mouse from hand landmarks:
///   thumb + index pinch  = left button  (hold to drag)
///   thumb + middle pinch = right button (hold to keep it pressed)
///   thumb on both fingertips = both buttons (e.g. aim + shoot), when simultaneousButtons is on
///   thumb + ring pinch   = scroll: while held the cursor stays put and moving the hand scrolls
///                          (up/down, and left/right for sideways scrolling)
/// In Relative mode, make a fist to "lift the mouse" so you can reposition your hand.
///
/// Smoothness: the camera only updates ~30 times a second. Instead of jumping the cursor
/// once per camera frame, a background thread glides it at ~500 Hz between frames.
///
/// Landmarks: the 21 MediaPipe hand points, in camera pixel coordinates.
/// </summary>
public sealed class HandMouse : IDisposable
{
    static readonly int[] PalmPoints = { 0, 5, 9, 13, 17 };   // wrist + knuckles: stable, low noise
    static readonly int[] FingerTips = { 8, 12, 16, 20 };

    readonly CalibrationData _cal;
    readonly OneEuroFilter _fx, _fy;
    readonly double _deadzonePx;
    readonly double _pinchOn, _pinchOff, _clickFreezeSeconds;
    readonly bool _simultaneousButtons;
    readonly double _sensitivity, _acceleration;
    readonly double _stickDeadzone, _stickMaxSpeed;
    readonly double _glideSeconds;
    readonly double _scrollGain;

    // Main-thread state
    bool _buttonDown;       // left
    bool _rightDown;
    bool _scrolling;
    double _freezeUntil;
    bool _enabled = true;
    bool _hasCursor;
    double _cursorX, _cursorY;
    double _prevX, _prevY;
    double _lastTime = -1;

    // Shared with the output thread (guarded by _lock)
    readonly object _lock = new();
    double _pendX, _pendY;            // Relative: counts still to be sent
    double _pendWheelX, _pendWheelY;  // Scroll: wheel units still to be sent
    double _stickVX, _stickVY;        // Joystick: counts per second
    bool _hasTarget;                  // Absolute: where the cursor should end up
    double _targetX, _targetY;

    // Output-thread-only state
    readonly Thread _thread;
    volatile bool _running = true;
    double _accX, _accY;
    double _accWheelX, _accWheelY;
    bool _hasOut;
    double _outX, _outY;
    int _lastAx = int.MinValue, _lastAy = int.MinValue;

    public MouseMode Mode { get; private set; } = MouseMode.Relative;
    public bool IsPinching => _buttonDown;
    public bool IsRightPinching => _rightDown;
    public bool IsClutched { get; private set; }
    /// <summary>Thumb + ring pinch held: hand movement scrolls instead of moving the cursor.</summary>
    public bool IsScrolling => _scrolling;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) { Release(); StopOutput(); } }
    }

    /// <param name="minCutoff">Smoothing at rest. Lower = steadier but laggier (try 0.1 to 1.0).</param>
    /// <param name="beta">Responsiveness during fast moves. Higher = less lag (try 0.001 to 0.02).</param>
    /// <param name="deadzonePx">Screen px of wobble ignored while holding still. 0 disables it.</param>
    /// <param name="sensitivity">Relative mode: cursor speed multiplier.</param>
    /// <param name="acceleration">Relative mode: extra gain for fast moves. 0 = none.</param>
    /// <param name="glideSeconds">How long the cursor takes to glide to each new camera reading.
    /// Higher = silkier but slightly laggier (try 0.02 to 0.06).</param>
    /// <param name="simultaneousButtons">True = left and right can be held together (aim + shoot in FPS games).
    /// False = one button at a time, closer pinch wins (fewer accidental double-clicks on the desktop).</param>
    /// <param name="stickDeadzone">Joystick mode: distance from center (screen px) before turning starts.</param>
    /// <param name="stickMaxSpeed">Joystick mode: turn speed (counts/s) at the edge.</param>
    /// <param name="scrollGain">Wheel units (120 = one notch) per screen pixel of hand movement while scroll-pinching.</param>
    public HandMouse(CalibrationData calibration,
                     double minCutoff = 0.4, double beta = 0.004, double deadzonePx = 6,
                     double sensitivity = 1.5, double acceleration = 0.0005,
                     double glideSeconds = 0.03,
                     double pinchOn = 0.25, double pinchOff = 0.40, double clickFreezeSeconds = 0.15,
                     bool simultaneousButtons = true,
                     double stickDeadzone = 120, double stickMaxSpeed = 2500,
                     double scrollGain = 2.5)
    {
        _cal = calibration;
        _fx = new OneEuroFilter(minCutoff, beta);
        _fy = new OneEuroFilter(minCutoff, beta);
        _deadzonePx = deadzonePx;
        _sensitivity = sensitivity;
        _acceleration = acceleration;
        _glideSeconds = Math.Max(0.001, glideSeconds);
        _pinchOn = pinchOn;
        _pinchOff = pinchOff;
        _simultaneousButtons = simultaneousButtons;
        _clickFreezeSeconds = clickFreezeSeconds;
        _stickDeadzone = stickDeadzone;
        _stickMaxSpeed = stickMaxSpeed;
        _scrollGain = scrollGain;

        _thread = new Thread(OutputLoop) { IsBackground = true, Name = "HandMouse output", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
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
        StopOutput();
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
            IsClutched = false;
            StopOutput();
            return;
        }

        // 1. Map to screen units, 2. One Euro filter
        var screen = _cal.Map(Anchor(landmarks));
        double fx = _fx.Filter(screen.X, now);
        double fy = _fy.Filter(screen.Y, now);

        // 3. Soft deadzone: tiny wobble is ignored, real movement passes through smoothly.
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

        double moveX = _cursorX - _prevX, moveY = _cursorY - _prevY;
        _prevX = _cursorX;
        _prevY = _cursorY;

        double handSize = Dist(landmarks[0], landmarks[9]);
        IsClutched = Mode == MouseMode.Relative && IsFist(landmarks, handSize);

        // Pinch = left button. Never start a click from a fist.
        double leftPinch = handSize > 1e-3 ? Dist(landmarks[4], landmarks[8]) / handSize : 1.0;   // thumb-index
        double rightPinch = handSize > 1e-3 ? Dist(landmarks[4], landmarks[12]) / handSize : 1.0; // thumb-middle
        double scrollPinch = handSize > 1e-3 ? Dist(landmarks[4], landmarks[16]) / handSize : 1.0; // thumb-ring

        // Scroll pinch: ends once the fingers are clearly apart (or on a fist); starts only when no button is
        // held and the ring finger is the one the thumb is touching (so a sloppy click doesn't scroll).
        if (_scrolling && (scrollPinch >= _pinchOff || IsClutched))
        {
            _scrolling = false;
            _freezeUntil = now + _clickFreezeSeconds;   // don't let the release nudge the cursor
        }
        else if (!_scrolling && !IsClutched && !_buttonDown && !_rightDown
                 && scrollPinch < _pinchOn && scrollPinch < leftPinch && scrollPinch < rightPinch)
        {
            _scrolling = true;
        }

        // Releases (hysteresis: let go only once the fingers are clearly apart)
        if (_buttonDown && leftPinch >= _pinchOff)
        {
            NativeMouse.LeftUp();
            _buttonDown = false;
            _freezeUntil = now + _clickFreezeSeconds;
        }
        if (_rightDown && rightPinch >= _pinchOff)
        {
            NativeMouse.RightUp();
            _rightDown = false;
            _freezeUntil = now + _clickFreezeSeconds;
        }

        // Presses (never from a fist, nor while scrolling)
        if (!IsClutched && !_scrolling)
        {
            bool left = !_buttonDown && leftPinch < _pinchOn;
            bool right = !_rightDown && rightPinch < _pinchOn;

            if (!_simultaneousButtons)
            {
                // One button at a time: nothing new while either is held, and the closer pinch wins
                if (_buttonDown || _rightDown) left = right = false;
                else if (left && right)
                {
                    left = leftPinch <= rightPinch;
                    right = !left;
                }
            }

            if (left)
            {
                NativeMouse.LeftDown();
                _buttonDown = true;
                _freezeUntil = now + _clickFreezeSeconds;
            }
            if (right)
            {
                NativeMouse.RightDown();
                _rightDown = true;
                _freezeUntil = now + _clickFreezeSeconds;
            }
        }

        bool frozen = now < _freezeUntil;

        lock (_lock)
        {
            if (_scrolling)
            {
                // Hand up = scroll up (wheel is positive upwards, screen y grows downwards); right = scroll right.
                _pendWheelY += -moveY * _scrollGain;
                _pendWheelX += moveX * _scrollGain;
                _stickVX = _stickVY = 0;   // the cursor itself stays put
                return;
            }
            switch (Mode)
            {
                case MouseMode.Relative:
                    if (!frozen && !IsClutched && (moveX != 0 || moveY != 0))
                    {
                        double gain = _sensitivity;
                        if (_acceleration > 0 && dt > 0)
                        {
                            double speed = Math.Sqrt(moveX * moveX + moveY * moveY) / dt;
                            gain *= Math.Min(1 + _acceleration * speed, 4);
                        }
                        _pendX += moveX * gain;   // the output thread glides this out over the next few ms
                        _pendY += moveY * gain;
                    }
                    break;

                case MouseMode.Absolute:
                    if (!frozen)
                    {
                        _targetX = Math.Clamp(_cursorX, 0, _cal.ScreenWidth - 1);
                        _targetY = Math.Clamp(_cursorY, 0, _cal.ScreenHeight - 1);
                        _hasTarget = true;
                    }
                    break;

                case MouseMode.Joystick:
                    (_stickVX, _stickVY) = frozen ? (0, 0) : JoystickVelocity(fx, fy);
                    break;
            }
        }
    }

    /// <summary>Hand at center = still; push toward an edge to turn, faster further out.</summary>
    (double Vx, double Vy) JoystickVelocity(double fx, double fy)
    {
        double cx = _cal.ScreenWidth / 2.0, cy = _cal.ScreenHeight / 2.0;
        double ox = fx - cx, oy = fy - cy;
        double d = Math.Sqrt(ox * ox + oy * oy);
        if (d <= _stickDeadzone) return (0, 0);

        double range = Math.Max(1, Math.Min(cx, cy) - _stickDeadzone);
        double t = Math.Clamp((d - _stickDeadzone) / range, 0, 1);
        double speed = _stickMaxSpeed * t * t;
        return (ox / d * speed, oy / d * speed);
    }

    /// <summary>
    /// Runs at ~500 Hz. Glides the cursor toward the latest camera reading instead of jumping
    /// once per camera frame: this is what removes the 30 fps "stepping".
    /// </summary>
    void OutputLoop()
    {
        timeBeginPeriod(1); // default Windows sleep granularity is ~15 ms; ask for 1 ms
        try
        {
            var sw = Stopwatch.StartNew();
            double last = 0;
            while (_running)
            {
                Thread.Sleep(2);
                double t = sw.Elapsed.TotalSeconds;
                double dt = t - last;
                last = t;
                if (dt <= 0) continue;

                double a = 1 - Math.Exp(-dt / _glideSeconds); // fraction of the remaining distance to cover this tick
                int mx, my, wheelX, wheelY;
                bool moveAbs = false;
                int ax = 0, ay = 0;

                lock (_lock)
                {
                    double sx = _pendX * a + _stickVX * dt;
                    double sy = _pendY * a + _stickVY * dt;
                    _pendX -= _pendX * a;
                    _pendY -= _pendY * a;

                    // Scrolling glides out the same way, so it's smooth rather than one jump per camera frame.
                    _accWheelX += _pendWheelX * a;
                    _accWheelY += _pendWheelY * a;
                    _pendWheelX -= _pendWheelX * a;
                    _pendWheelY -= _pendWheelY * a;
                    wheelX = (int)_accWheelX;
                    wheelY = (int)_accWheelY;
                    _accWheelX -= wheelX;
                    _accWheelY -= wheelY;

                    if (_hasTarget)
                    {
                        if (!_hasOut) { _outX = _targetX; _outY = _targetY; _hasOut = true; }
                        else
                        {
                            _outX += (_targetX - _outX) * a;
                            _outY += (_targetY - _outY) * a;
                        }
                        ax = (int)Math.Round(_outX);
                        ay = (int)Math.Round(_outY);
                        moveAbs = true;
                    }
                    else
                    {
                        _hasOut = false;
                    }

                    _accX += sx;
                    _accY += sy;
                    mx = (int)_accX;
                    my = (int)_accY;
                    _accX -= mx;
                    _accY -= my;
                }

                if (mx != 0 || my != 0) NativeMouse.MoveRelative(mx, my);
                if (wheelY != 0) NativeMouse.Wheel(wheelY);
                if (wheelX != 0) NativeMouse.Wheel(wheelX, horizontal: true);

                // Only move when the position changes, so a still hand doesn't fight a real mouse
                if (moveAbs && (ax != _lastAx || ay != _lastAy))
                {
                    NativeMouse.MoveTo(ax, ay);
                    _lastAx = ax;
                    _lastAy = ay;
                }
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    void StopOutput()
    {
        lock (_lock)
        {
            _pendX = _pendY = 0;
            _pendWheelX = _pendWheelY = 0;
            _stickVX = _stickVY = 0;
            _hasTarget = false;
        }
    }

    static bool IsFist(IReadOnlyList<Point2f> lm, double handSize)
    {
        if (handSize < 1e-3) return false;
        foreach (int tip in FingerTips)
            if (Dist(lm[tip], lm[0]) / handSize > 1.3) return false;
        return true;
    }

    public void Release()
    {
        _scrolling = false;
        if (_buttonDown)
        {
            NativeMouse.LeftUp();
            _buttonDown = false;
        }
        if (_rightDown)
        {
            NativeMouse.RightUp();
            _rightDown = false;
        }
    }

    /// <summary>Stops the output thread and releases the button. Call before replacing or discarding the mouse.</summary>
    public void Dispose()
    {
        Release();
        StopOutput();
        _running = false;
        _thread.Join(100);
    }

    static double Dist(Point2f a, Point2f b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
}
