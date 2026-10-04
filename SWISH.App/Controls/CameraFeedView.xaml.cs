using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandGestureRecognition;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace Swish.App.Controls;

public enum FeedLook
{
    /// <summary>Camera colours as they are.</summary>
    Color,
    /// <summary>Black and white.</summary>
    Grayscale,
    /// <summary>Grayscale with an ordered-dither texture (the "How to calibrate" look).</summary>
    Dithered,
}

/// <summary>
/// The live camera picture with SWISH styling: grayscale / dithered / blurred, and the tracked hands
/// drawn as cyan points joined by thin white lines. Frames come from the gesture engine's preview slot
/// (it claims the preview while visible, so only one view takes frames at a time); the conversion runs
/// on this control's UI timer, never on the frame-processing thread.
/// </summary>
public partial class CameraFeedView : UserControl
{
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(FeedLook), typeof(CameraFeedView), new PropertyMetadata(FeedLook.Grayscale));
    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(
        nameof(Blur), typeof(double), typeof(CameraFeedView), new PropertyMetadata(0.0, (d, _) => ((CameraFeedView)d).ApplyBlur()));
    public static readonly DependencyProperty ShowSkeletonProperty = DependencyProperty.Register(
        nameof(ShowSkeleton), typeof(bool), typeof(CameraFeedView), new PropertyMetadata(true));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(bool), typeof(CameraFeedView), new PropertyMetadata(true, (d, e) =>
            ((CameraFeedView)d).Fit.Stretch = (bool)e.NewValue ? Stretch.UniformToFill : Stretch.Uniform));

    public FeedLook Look { get => (FeedLook)GetValue(LookProperty); set => SetValue(LookProperty, value); }
    /// <summary>Gaussian blur radius in screen pixels (0 = sharp). Done on the GPU by WPF.</summary>
    public double Blur { get => (double)GetValue(BlurProperty); set => SetValue(BlurProperty, value); }
    public bool ShowSkeleton { get => (bool)GetValue(ShowSkeletonProperty); set => SetValue(ShowSkeletonProperty, value); }
    /// <summary>True = cover the whole control (cropping), false = fit inside it (letterboxing).</summary>
    public bool Fill { get => (bool)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Draw extra shapes here in camera-pixel coordinates (the frame is <see cref="FrameSize"/>).</summary>
    public Canvas Overlay => OverlayHost;
    public System.Windows.Size FrameSize => new(Frame.Width, Frame.Height);

    readonly DispatcherTimer _timer;
    WriteableBitmap? _bitmap;
    Mat? _gray, _dither, _bayer;
    HandsFrame? _hands;
    App? _app;

    public CameraFeedView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => OnTick(), Dispatcher);
        Loaded += (_, _) =>
        {
            _app = Application.Current as App;
            if (_app is null) return;
            _app.Gestures.HandsUpdated += OnHands;
            _app.ClaimPreview(this);
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            if (_app is null) return;
            _app.Gestures.HandsUpdated -= OnHands;
            _app.ReleasePreview(this);
        };
    }

    void OnHands(HandsFrame frame) => Volatile.Write(ref _hands, frame);   // engine thread: just keep the latest

    void ApplyBlur() => Picture.Effect = Blur > 0 ? new BlurEffect { Radius = Blur, KernelType = KernelType.Gaussian } : null;

    void OnTick()
    {
        if (_app is null || !_app.OwnsPreview(this)) return;
        using (var frame = _app.Gestures.TakePreview())
            if (frame is not null) Show(frame);

        Skeleton.Hands = ShowSkeleton ? Volatile.Read(ref _hands) : null;
    }

    void Show(Mat frame)
    {
        if (Frame.Width != frame.Width || Frame.Height != frame.Height)
        {
            Frame.Width = frame.Width;
            Frame.Height = frame.Height;
        }
        Placeholder.Visibility = Visibility.Collapsed;

        if (Look == FeedLook.Color)
        {
            Blit(frame, PixelFormats.Bgr24);
            return;
        }

        _gray ??= new Mat();
        Cv2.CvtColor(frame, _gray, ColorConversionCodes.BGR2GRAY);
        if (Look == FeedLook.Dithered)
        {
            // Ordered (Bayer 4x4) dither, blended back over the darkened picture for a halftone texture.
            _bayer = BayerTile(frame.Width, frame.Height, _bayer);
            _dither ??= new Mat();
            Cv2.Compare(_gray, _bayer, _dither, CmpType.GT);
            Cv2.AddWeighted(_gray, 0.55, _dither, 0.22, 0, _gray);
        }
        Blit(_gray, PixelFormats.Gray8);
    }

    void Blit(Mat image, PixelFormat format)
    {
        if (_bitmap is null || _bitmap.PixelWidth != image.Width || _bitmap.PixelHeight != image.Height || _bitmap.Format != format)
        {
            _bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, format, null);
            Picture.Source = _bitmap;
        }
        int stride = (int)image.Step();
        _bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), image.Data, stride * image.Height, stride);
    }

    static Mat BayerTile(int w, int h, Mat? existing)
    {
        if (existing is not null && existing.Width == w && existing.Height == h) return existing;
        existing?.Dispose();
        byte[,] m = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
        var tile = new Mat(h, w, MatType.CV_8UC1);
        var row = new byte[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) row[x] = (byte)(m[y % 4, x % 4] * 16 + 8);
            System.Runtime.InteropServices.Marshal.Copy(row, 0, tile.Ptr(y), w);
        }
        return tile;
    }
}
