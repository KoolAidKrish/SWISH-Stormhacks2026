using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

/// <summary>Which calibration to run.</summary>
public enum CalibrationKind
{
    /// <summary>Hold your hand still on 9 targets (ScreenCalibrator).</summary>
    Points,
    /// <summary>Follow a moving dot for ~20 s (PursuitCalibrator).</summary>
    Pursuit,
}

/// <summary>
/// A full-screen calibration that turns hand positions into a camera → screen mapping.
/// Drive it from the frame loop: Update() then Render() every frame, and keep calling
/// Cv2.WaitKey() so its window repaints.
/// </summary>
public interface ICalibrator : IDisposable
{
    bool IsComplete { get; }
    CalibrationData? Result { get; }

    /// <param name="anchor">Hand point in camera pixel coordinates (use HandMouse.Anchor), or null if no hand.</param>
    /// <param name="now">Time in seconds.</param>
    void Update(Point2f? anchor, double now);

    void Render(Mat? cameraFrame = null);
}
