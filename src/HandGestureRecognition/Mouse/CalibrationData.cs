using System.Text.Json;
using OpenCvSharp;

namespace HandGestureRecognition.Mouse;

/// <summary>Camera-to-screen mapping produced by ScreenCalibrator. Saved as JSON so you don't recalibrate every run.</summary>
public sealed class CalibrationData
{
    /// <summary>Row-major 3x3 homography: camera pixel -> screen pixel.</summary>
    public double[] Homography { get; set; } = new double[9];
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    /// <summary>Average distance (screen px) between targets and where the calibration points map to. Lower is better.</summary>
    public double MeanErrorPx { get; set; }

    public Point2d Map(Point2f p)
    {
        var h = Homography;
        double w = h[6] * p.X + h[7] * p.Y + h[8];
        if (Math.Abs(w) < 1e-9) w = 1e-9;
        return new Point2d(
            (h[0] * p.X + h[1] * p.Y + h[2]) / w,
            (h[3] * p.X + h[4] * p.Y + h[5]) / w);
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Returns null if the file is missing, invalid, or was made for a different screen resolution.</summary>
    public static CalibrationData? TryLoad(string path, int screenWidth, int screenHeight)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<CalibrationData>(File.ReadAllText(path));
            if (data is null || data.Homography.Length != 9) return null;
            if (data.ScreenWidth != screenWidth || data.ScreenHeight != screenHeight) return null;
            return data;
        }
        catch
        {
            return null;
        }
    }
}
