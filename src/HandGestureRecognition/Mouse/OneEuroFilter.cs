namespace HandGestureRecognition.Mouse;

/// <summary>
/// One Euro filter (Casiez et al. 2012): heavy smoothing when the hand is still (kills jitter),
/// light smoothing when it moves fast (keeps latency low).
///   minCutoff: lower = smoother at rest, but laggier.
///   beta:      higher = less lag during fast motion.
/// </summary>
public sealed class OneEuroFilter
{
    readonly double _minCutoff, _beta, _dCutoff;
    double _x, _dx, _tPrev;
    bool _initialized;

    public OneEuroFilter(double minCutoff = 1.0, double beta = 0.007, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    public double Filter(double x, double t)
    {
        if (!_initialized)
        {
            _x = x; _dx = 0; _tPrev = t; _initialized = true;
            return x;
        }

        double dt = t - _tPrev;
        if (dt <= 0) return _x;
        _tPrev = t;

        double rawDx = (x - _x) / dt;
        double aD = Alpha(dt, _dCutoff);
        _dx = aD * rawDx + (1 - aD) * _dx;

        double cutoff = _minCutoff + _beta * Math.Abs(_dx);
        double a = Alpha(dt, cutoff);
        _x = a * x + (1 - a) * _x;
        return _x;
    }

    public void Reset() => _initialized = false;

    static double Alpha(double dt, double cutoff)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }
}
