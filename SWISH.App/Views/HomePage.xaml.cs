using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Swish.App.Views;

/// <summary>
/// Landing screen: SWISH wordmark, tagline, "START CALIBRATING →", decorative corner shapes. Like the splash,
/// everything slides in from its own direction (the letters one after another) and then floats gently.
/// </summary>
public partial class HomePage : UserControl, ISwishPage
{
    App? _app;
    ShellWindow? _shell;

    // Floating: each piece slides in from an offset at its own moment, then drifts on a slow sine.
    sealed record Floater(UIElement Element, TranslateTransform Move, Vector From, double InAt, double Drift, double Phase);
    readonly List<Floater> _floaters = new();
    readonly Stopwatch _clock = new();
    readonly DispatcherTimer _floatTimer;   // ~30 fps is plenty for a drift, and stops while the page is hidden

    public HomePage()
    {
        InitializeComponent();
        BuildDecorations();
        BuildWordmark();
        _floatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Float(), Dispatcher);

        Add(TopLeft, new(-160, -120), 0.00, 5, 0.0);
        Add(TopRight, new(160, -140), 0.10, 6, 1.3);
        Add(BottomRight, new(180, 120), 0.18, 5, 2.1);
        Add(BottomLeft, new(-180, 80), 0.26, 4, 3.4);
        Add(BottomCenter, new(0, 140), 0.32, 4, 4.2);
        for (int i = 0; i < Wordmark.Children.Count; i++)   // letters drop in one after another
            Add(Wordmark.Children[i], new(0, -70 - 18 * i), 0.25 + 0.09 * i, 3.5, i * 1.1);
        Add(Tagline, new(0, 30), 0.80, 2, 5.0);
        Add(Pitch, new(60, 0), 0.90, 2.5, 2.6);
    }

    void Add(UIElement element, Vector from, double inAt, double drift, double phase)
    {
        var move = new TranslateTransform();
        element.RenderTransform = move;
        element.Opacity = 0;
        _floaters.Add(new Floater(element, move, from, inAt, drift, phase));
    }

    void BuildWordmark()
    {
        foreach (char c in "SWISH")
            Wordmark.Children.Add(new TextBlock
            {
                Text = c.ToString(), Style = (Style)FindResource("Display"), FontSize = 236, LineHeight = 200,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            });
    }

    void Float()
    {
        double t = _clock.Elapsed.TotalSeconds;
        foreach (var f in _floaters)
        {
            double p = Math.Clamp((t - f.InAt) / 0.75, 0, 1);
            double eased = 1 - Math.Pow(1 - p, 3);   // ease-out cubic, like the splash
            double drift = Math.Sin(t * 1.1 + f.Phase) * f.Drift * eased;
            f.Move.X = f.From.X * (1 - eased) + drift * 0.5;
            f.Move.Y = f.From.Y * (1 - eased) - drift;
            f.Element.Opacity = eased;
        }
    }

    public IInputElement? DefaultFocus => StartButton;

    public void OnShown(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        SkipHint.Visibility = app.HasCalibration() ? Visibility.Visible : Visibility.Collapsed;
        _clock.Restart();
        Float();
        _floatTimer.Start();
    }

    public void OnHidden() => _floatTimer.Stop();

    void OnStart(object sender, RoutedEventArgs e) => _shell?.Navigate(new HowToCalibratePage());
    void OnSkip(object sender, RoutedEventArgs e) => _shell?.Navigate(new ControlsPage());

    // ---- Decorations (drawn in each corner layer's own coordinates) ----

    static readonly Brush Pink = (Brush)Application.Current.FindResource("Pink");
    static readonly Brush Teal = (Brush)Application.Current.FindResource("Teal");

    void BuildDecorations()
    {
        // Top-left: teal block, two pink X's cut off by the top edge, a pink triangle, a thin pink rule.
        TopLeft.Children.Add(Poly(Teal, (0, 0), (150, 0), (0, 150)));
        TopLeft.Children.Add(Cross(Pink, 150, 22, 150, 52));
        TopLeft.Children.Add(Cross(Pink, 330, 22, 150, 52));
        TopLeft.Children.Add(Poly(Pink, (40, 150), (175, 150), (40, 280)));
        TopLeft.Children.Add(Line(Pink, 1.5, (0, 112), (420, 112)));

        // Top-right: big pink ring arcs dropping in from the top edge.
        TopRight.Children.Add(Ring(Pink, 12, 330, -70, 220));
        TopRight.Children.Add(Ring(Pink, 2.5, 330, -70, 186));

        // Right / bottom-right: teal circuit trace stepping down the right edge, a thin pink trace beside it,
        // and a large pink triangle in the corner.
        BottomRight.Children.Add(Trace(Teal, 16, (420, 0), (420, 230), (380, 270), (380, 430), (330, 480), (0, 480)));
        BottomRight.Children.Add(Trace(Pink, 1.5, (395, 0), (395, 220), (355, 260), (355, 420), (310, 465), (0, 465)));
        BottomRight.Children.Add(Poly(Pink, (190, 520), (440, 520), (440, 260)));

        // Bottom-left: teal trace along the bottom with a step, thin pink trace above it.
        // (ends 40 px / 55 px above the bottom, the same heights as the right-hand traces, so they line up)
        BottomLeft.Children.Add(Trace(Teal, 16, (0, 120), (520, 120), (540, 100), (760, 100)));
        BottomLeft.Children.Add(Trace(Pink, 1.5, (0, 105), (500, 105), (520, 85), (760, 85)));

        // Bottom-centre: a pink X sitting on the bottom edge.
        BottomCenter.Children.Add(Cross(Pink, 100, 140, 190, 60));
    }

    static Polygon Poly(Brush fill, params (double X, double Y)[] pts) =>
        new() { Fill = fill, Points = new PointCollection(pts.Select(p => new Point(p.X, p.Y))) };

    static Polyline Trace(Brush stroke, double thickness, params (double X, double Y)[] pts) =>
        new() { Stroke = stroke, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Miter,
                Points = new PointCollection(pts.Select(p => new Point(p.X, p.Y))) };

    static Line Line(Brush stroke, double thickness, (double X, double Y) a, (double X, double Y) b) =>
        new() { Stroke = stroke, StrokeThickness = thickness, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y };

    static Ellipse Ring(Brush stroke, double thickness, double cx, double cy, double r)
    {
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = stroke, StrokeThickness = thickness };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        return e;
    }

    /// <summary>A thick X: two bars crossing at right angles, centred on (cx, cy).</summary>
    static Path Cross(Brush fill, double cx, double cy, double size, double bar)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var angle in new[] { 45.0, -45.0 })
        {
            var rect = new RectangleGeometry(new Rect(cx - size / 2, cy - bar / 2, size, bar))
            {
                Transform = new RotateTransform(angle, cx, cy),
            };
            g.Children.Add(rect);
        }
        return new Path { Fill = fill, Data = g };
    }
}
