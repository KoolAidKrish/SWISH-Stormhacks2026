using System.Globalization;
using HandGestureRecognition.Mouse;

namespace HandGestureRecognition;

/// <summary>Command-line options (same flags as the Python argparse setup).</summary>
internal sealed record AppArgs(
    int Device,
    string Image,
    int Width,
    int Height,
    double MinDetectionConfidence,
    bool DisableImageFlip,
    bool Calibrate,
    bool CalibratePursuit,
    bool ShowWindow,
    bool Record)
{
    public static AppArgs Parse(string[] argv)
    {
        int device = 0;
        string image = "";
        int width = 640;
        int height = 480;
        double minDetectionConfidence = 0.6;
        bool disableImageFlip = false;
        bool calibrate = false;
        bool calibratePursuit = false;
        bool showWindow = false;
        bool record = false;

        for (int i = 0; i < argv.Length; i++)
        {
            string arg = argv[i];
            string? inlineValue = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith('-') && eq > 0)
            {
                inlineValue = arg.Substring(eq + 1);
                arg = arg.Substring(0, eq);
            }

            string NextValue() => inlineValue ?? (i + 1 < argv.Length
                ? argv[++i]
                : throw new ArgumentException($"Missing value for {arg}"));

            switch (arg)
            {
                case "-sw":
                case "--show_window":
                    showWindow = true;
                    break;
                case "-rec":
                case "--record":
                    record = true;
                    break;
                case "-cal":
                case "--calibrate":
                    calibrate = true;
                    break;
                case "-calp":
                case "--calibrate_pursuit":
                    calibrate = true;
                    calibratePursuit = true;
                    break;
                case "-d":
                case "--device":
                    device = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "-im":
                case "--image":
                    image = NextValue();
                    break;
                case "-wi":
                case "--width":
                    width = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "-he":
                case "--height":
                    height = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "-mdc":
                case "--min_detection_confidence":
                    minDetectionConfidence = double.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "-dif":
                case "--disable_image_flip":
                    disableImageFlip = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argv[i]}");
            }
        }

        return new AppArgs(device, image, width, height, minDetectionConfidence, disableImageFlip, calibrate, calibratePursuit, showWindow, record);
    }
}

/// <summary>
/// Console version: runs the frame loop (GestureEngine) on the main thread with the OpenCV
/// debug window. SWISH.App hosts the same engine behind a WPF window instead.
/// </summary>
internal static class Program
{
    private static int Main(string[] argv)
    {
        NativeMouse.EnableDpiAwareness();   // must run before any window is created
        GestureEngine.DisableBackgroundThrottling();

        // Argument parsing ######################################################
        AppArgs args;
        try
        {
            args = AppArgs.Parse(argv);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        using var engine = new GestureEngine(new GestureOptions
        {
            Device = args.Device,
            Image = args.Image,
            Width = args.Width,
            Height = args.Height,
            MinDetectionConfidence = args.MinDetectionConfidence,
            DisableImageFlip = args.DisableImageFlip,
            Calibrate = args.Calibrate,
            CalibrationKind = args.CalibratePursuit ? CalibrationKind.Pursuit : CalibrationKind.Points,
            ShowWindow = args.ShowWindow,
            UseOpenCvWindow = true,
            // Recording the annotated feed is opt-in: it re-encodes every frame and writes ~9 MB/min.
            RecordPath = args.Record ? "output.mp4" : null,
            // Relative to the working directory, as before: model/, calibration.json, output.mp4
        });
        engine.Log += Console.WriteLine;
        engine.Run();
        return 0;
    }
}
