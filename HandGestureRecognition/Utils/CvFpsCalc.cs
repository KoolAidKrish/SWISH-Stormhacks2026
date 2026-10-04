using OpenCvSharp;

namespace HandGestureRecognition.Utils;

/// <summary>Port of utils/cvfpscalc.py.</summary>
public sealed class CvFpsCalc
{
    private long _startTick;
    private readonly double _freq;
    private readonly BoundedQueue<double> _diffTimes;

    public CvFpsCalc(int bufferLen = 1)
    {
        _startTick = Cv2.GetTickCount();
        _freq = 1000.0 / Cv2.GetTickFrequency();
        _diffTimes = new BoundedQueue<double>(bufferLen);
    }

    public double Get()
    {
        long currentTick = Cv2.GetTickCount();
        double differentTime = (currentTick - _startTick) * _freq;
        _startTick = currentTick;

        _diffTimes.Enqueue(differentTime);

        double fps = 1000.0 / (_diffTimes.Sum() / _diffTimes.Count);
        return Math.Round(fps, 2, MidpointRounding.ToEven);
    }
}
