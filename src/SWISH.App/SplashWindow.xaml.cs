using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Swish.App;

/// <summary>
/// Animated title card shown before the main window. Pieces of the SWISH artwork fly in along their
/// own directions, the title builds up, everything holds with a slight drift, then flows back out of
/// frame and the window closes.
///
/// Every animation is a pure function of time (<see cref="Apply"/>) rather than a WPF storyboard,
/// so any moment can be rendered on demand: `SWISH.exe --render-splash folder` writes frames to PNG.
/// Click or press any key to skip.
/// </summary>
public partial class SplashWindow : Window
{
    // ---- Timeline (seconds) ----
    const double HoldStart = 1.6;    // everything is in place
    const double ExitStart = 3.1;    // pieces start leaving
    const double End = 3.9;          // window closes

    /// <summary>A shape that slides in from an offset, drifts while holding, then slides out.</summary>
    sealed record Piece(UIElement Element, Vector From, double InAt, double InFor,
                        Vector To, double OutAt, double OutFor, double SpinFrom = 0, double SpinTo = 0,
                        double Drift = 3)
    {
        public readonly TranslateTransform Move = new();
        public readonly RotateTransform Spin = new();
    }

    /// <summary>A line that draws itself on, then draws itself off.</summary>
    sealed record Stroke(Shape Shape, double Length, double InAt, double InFor, double OutAt, double OutFor);

    readonly List<Piece> _pieces = new();
    readonly List<Stroke> _strokes = new();
    readonly List<UIElement> _letters = new();

    // Title box in design units, measured from the artwork.
    static readonly Rect TitleRect = new(260, 121, 380, 135);
    readonly Stopwatch _clock = new();
    const string SubtitleText = "SERIAL · WIRELESS · INTERACTIVE ·\nSYSTEM · FOR · HUMANS";
    const string TaglineText = "A touchless control system\nfor your gaming experience";
    bool _finished;

    /// <summary>Raised once, when the animation ends or is skipped.</summary>
    public event Action? Finished;

    public SplashWindow()
    {
        InitializeComponent();

        // 60% of the screen width, 16:9.
        Width = SystemParameters.PrimaryScreenWidth * 0.6;
        Height = Width * 508 / 902;

        BuildTitle();
        BuildTimeline();
        Apply(0);

        Loaded += (_, _) =>
        {
            _clock.Start();
            CompositionTarget.Rendering += OnFrame;
        };
        MouseDown += (_, _) => Finish();
        KeyDown += (_, _) => Finish();
    }

    /// <summary>
    /// Builds "SWISH" from the font's letter outlines and fits their ink (not the line box, which adds
    /// space above and below) exactly into <see cref="TitleRect"/>. One path per letter so each can animate.
    /// WPF can't reach Bahnschrift's variable weights via FontWeight/FontStretch, only by these instance names.
    /// </summary>
    void BuildTitle()
    {
        var typeface = new Typeface(new FontFamily("Bahnschrift Bold SemiCondensed"), FontStyles.Normal,
                                    FontWeights.Normal, FontStretches.Normal);
        const double size = 100, tracking = 7;
        // The heaviest Bahnschrift is still lighter than the artwork, so each letter also gets a white
        // outline. That thickens every stroke evenly without changing the letter shapes.
        var thicken = new Pen(Brushes.White, 7) { LineJoin = PenLineJoin.Miter };
        double x = 0;
        var glyphs = new List<Geometry>();
        foreach (char c in "SWISH")
        {
            var text = new FormattedText(c.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0);
            glyphs.Add(text.BuildGeometry(new Point(x, 0)));
            x += text.WidthIncludingTrailingWhitespace + tracking;
        }

        var ink = Rect.Empty;
        foreach (var g in glyphs) ink.Union(g.GetRenderBounds(thicken));

        // Map the letters' combined ink box onto the design's title box.
        var fit = new TransformGroup();
        fit.Children.Add(new TranslateTransform(-ink.X, -ink.Y));
        fit.Children.Add(new ScaleTransform(TitleRect.Width / ink.Width, TitleRect.Height / ink.Height));
        fit.Children.Add(new TranslateTransform(TitleRect.X, TitleRect.Y));
        TitleLetters.RenderTransform = fit;

        foreach (var g in glyphs)
        {
            var letter = new System.Windows.Shapes.Path
            {
                Data = g, Fill = Brushes.White,
                Stroke = Brushes.White, StrokeThickness = thicken.Thickness, StrokeLineJoin = PenLineJoin.Miter,
                RenderTransform = new TranslateTransform(),
            };
            _letters.Add(letter);
            TitleLetters.Children.Add(letter);
        }

        // Subtitle: fixed-width cells give the tracked-out monospace look.
        var mono = new FontFamily("Cascadia Mono, Consolas");
        const double subSize = 12.5, cell = 9.6;
        foreach (var line in SubtitleText.Split('\n'))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 19 };
            foreach (char ch in line)
                row.Children.Add(new TextBlock
                {
                    Text = ch.ToString(), Width = cell, TextAlignment = TextAlignment.Center,
                    FontFamily = mono, FontSize = subSize, Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED)),
                });
            Subtitle.Children.Add(row);
        }
    }

    void BuildTimeline()
    {
        // Enter from one direction, leave towards another. Offsets are in design units (canvas 902 x 508).
        var upLeft = new Vector(-1, -1);
        var downLeft = new Vector(-1, 1);
        var upRight = new Vector(1, -1);
        var downRight = new Vector(1, 1);
        var left = new Vector(-1, 0);
        var right = new Vector(1, 0);

        // Left cluster: the big pink stripe slides down along its own diagonal, the blue pieces come from the left.
        // (The big pink ribbon isn't a sliding piece: it stretches in and out, see ApplyRibbon.)
        Add(TopLeftBlock,    from: upLeft * 260,   inAt: 0.08, to: upLeft * 300,   outAt: 0.00);
        Add(LeftTriA,        from: left * 260,     inAt: 0.22, to: left * 300,     outAt: 0.06);
        Add(LeftTriB,        from: left * 300,     inAt: 0.30, to: left * 340,     outAt: 0.12);
        Add(LeftTriC,        from: left * 280,     inAt: 0.38, to: left * 320,     outAt: 0.02);
        Add(PinkStripeSmall, from: downLeft * 260, inAt: 0.18, to: downLeft * 300, outAt: 0.08);

        // Right cluster: the big pink triangle rises in from the corner, its blue triangles slide in after it.
        Add(RightBlock,      from: downRight * 460, inAt: 0.05, to: downRight * 520, outAt: 0.10);
        Add(RightTriA,       from: right * 360,     inAt: 0.30, to: right * 420,     outAt: 0.00);
        Add(RightTriB,       from: right * 300,     inAt: 0.40, to: right * 360,     outAt: 0.06);
        Add(RightTriC,       from: right * 320,     inAt: 0.48, to: right * 380,     outAt: 0.12);
        Add(Cross,           from: upRight * 300,   inAt: 0.15, to: upRight * 340,   outAt: 0.04, spinFrom: -120, spinTo: 120);

        // Lines draw themselves on, then off.
        _strokes.Add(new Stroke(ThickLine, PolylineLength(ThickLine.Points), InAt: 0.25, InFor: 0.9, OutAt: ExitStart, OutFor: 0.55));
        _strokes.Add(new Stroke(Outline, PathLength(Outline.Data), InAt: 0.45, InFor: 1.0, OutAt: ExitStart + 0.05, OutFor: 0.5));
    }

    void Add(UIElement element, Vector from, double inAt, Vector to, double outAt,
             double spinFrom = 0, double spinTo = 0)
    {
        var piece = new Piece(element, from, inAt, 0.75, to, ExitStart + outAt, 0.6, spinFrom, spinTo);
        var group = new TransformGroup();
        group.Children.Add(piece.Spin);
        group.Children.Add(piece.Move);
        element.RenderTransform = group;
        _pieces.Add(piece);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        Apply(t);
        if (t >= End) Finish();
    }

    /// <summary>Puts every element where it belongs at time <paramref name="t"/> (seconds).</summary>
    void Apply(double t)
    {
        foreach (var p in _pieces)
        {
            double pIn = EaseOutCubic(Progress(t, p.InAt, p.InFor));
            double pOut = EaseInCubic(Progress(t, p.OutAt, p.OutFor));
            // Gentle drift while holding, so the card doesn't look frozen.
            double drift = Math.Sin((t + p.InAt * 7) * 1.7) * p.Drift * pIn * (1 - pOut);
            var offset = p.From * (1 - pIn) + p.To * pOut + new Vector(drift, -drift * 0.6);
            p.Move.X = offset.X;
            p.Move.Y = offset.Y;

            if (p.SpinFrom != 0 || p.SpinTo != 0)
                p.Spin.Angle = p.SpinFrom * (1 - pIn) + p.SpinTo * pOut;
        }

        ApplyRibbon(t);

        foreach (var s in _strokes)
        {
            // Dash units are multiples of the stroke thickness.
            double units = s.Length / s.Shape.StrokeThickness;
            s.Shape.StrokeDashArray = new DoubleCollection { units, units };
            double on = EaseOutCubic(Progress(t, s.InAt, s.InFor));
            double off = EaseInCubic(Progress(t, s.OutAt, s.OutFor));
            s.Shape.StrokeDashOffset = units * (1 - on) - units * off;
        }

        // Title letters rise in one after another, then the whole title lifts and fades on exit.
        double exit = EaseInCubic(Progress(t, ExitStart - 0.05, 0.45));
        for (int i = 0; i < _letters.Count; i++)
        {
            double p = EaseOutBack(Progress(t, 0.55 + i * 0.07, 0.55));
            var move = (TranslateTransform)_letters[i].RenderTransform;
            move.Y = (1 - p) * 90 - exit * 50; // in font units (the title is scaled to fit its box)
            _letters[i].Opacity = Math.Clamp(p, 0, 1) * (1 - exit);
        }

        // Rules grow out from the text toward the edges.
        double rules = EaseOutCubic(Progress(t, 0.95, 0.6)) * (1 - exit);
        RuleLeft.X1 = 257 - 210 * rules;
        RuleRight.X2 = 644 + 213 * rules;
        RuleLeft.Opacity = RuleRight.Opacity = rules > 0.001 ? 1 : 0;

        // Subtitle fades up; tagline types out.
        double sub = EaseOutCubic(Progress(t, 1.0, 0.5));
        Subtitle.Opacity = sub * (1 - exit);
        Subtitle.RenderTransform = new TranslateTransform(0, (1 - sub) * 10);

        int chars = (int)Math.Round(TaglineText.Length * Progress(t, 1.15, 0.65));
        Tagline.Text = TaglineText[..chars];
        Tagline.Opacity = 1 - exit;

        // Whole card fades at the very end so the main window doesn't pop in over a hard cut.
        Root.Opacity = 1 - Progress(t, End - 0.2, 0.2);
    }

    // ---- The top-left ribbon ----
    // Its two long edges run down-left at 45°, from the top edge to the left side. Positions along it
    // are measured from where the upper edge crosses the top of the canvas. The ribbon is drawn
    // between a tail and a head along that axis: the head grows down from above the frame (stretch in),
    // then the tail chases it out past the bottom-left (stretch out).
    static readonly Point RibbonTop = new(208, -10);              // upper edge, at the top of the canvas
    static readonly Vector RibbonAxis = new(-Math.Sqrt(0.5), Math.Sqrt(0.5));   // down-left
    static readonly Vector RibbonAcross = new(Math.Sqrt(0.5), Math.Sqrt(0.5));  // upper edge -> lower edge
    const double RibbonWidth = 109.6;                              // 155 px apart horizontally at 45°
    const double RibbonStart = -110;                               // whole cut is above the frame
    const double RibbonEnd = 420;                                  // whole cut is past the left edge
    const double RibbonInAt = 0.0, RibbonInFor = 0.85, RibbonOutAt = ExitStart + 0.05, RibbonOutFor = 0.6;

    void ApplyRibbon(double t)
    {
        double head = RibbonStart + (RibbonEnd - RibbonStart) * EaseOutCubic(Progress(t, RibbonInAt, RibbonInFor));
        double tail = RibbonStart + (RibbonEnd - RibbonStart) * EaseInCubic(Progress(t, RibbonOutAt, RibbonOutFor));
        if (head <= tail)
        {
            PinkStripe.Visibility = Visibility.Hidden;
            return;
        }
        PinkStripe.Visibility = Visibility.Visible;
        Point Upper(double s) => RibbonTop + RibbonAxis * s;
        Point Lower(double s) => Upper(s) + RibbonAcross * RibbonWidth;
        // Square ends (cut across the ribbon), like a strip of tape.
        PinkStripe.Points = new PointCollection { Upper(tail), Upper(head), Lower(head), Lower(tail) };
    }

    void Finish()
    {
        if (_finished) return;
        _finished = true;
        CompositionTarget.Rendering -= OnFrame;
        Close();
        Finished?.Invoke();
    }

    /// <summary>Renders the animation at the given times to PNGs (for checking it against the design).</summary>
    public void RenderFrames(string folder, IEnumerable<double> times, int width = 1600)
    {
        Directory.CreateDirectory(folder);
        int height = width * 508 / 902;
        foreach (var t in times)
        {
            Apply(t);
            Root.Measure(new Size(width, height));
            Root.Arrange(new Rect(0, 0, width, height));
            Root.UpdateLayout();
            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(Root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var file = File.Create(System.IO.Path.Combine(folder, $"splash_{t * 1000:0000}ms.png"));
            png.Save(file);
        }
    }

    // ---- Helpers ----

    static double Progress(double t, double start, double duration) => Math.Clamp((t - start) / duration, 0, 1);
    static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);
    static double EaseInCubic(double x) => x * x * x;
    static double EaseOutBack(double x)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return x <= 0 ? 0 : 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }

    static double PolylineLength(PointCollection points)
    {
        double sum = 0;
        for (int i = 1; i < points.Count; i++) sum += (points[i] - points[i - 1]).Length;
        return sum;
    }

    static double PathLength(Geometry geometry)
    {
        var flat = geometry.GetFlattenedPathGeometry();
        double sum = 0;
        foreach (var figure in flat.Figures)
        {
            var last = figure.StartPoint;
            foreach (var seg in figure.Segments)
                if (seg is PolyLineSegment poly)
                    foreach (var p in poly.Points) { sum += (p - last).Length; last = p; }
                else if (seg is LineSegment line) { sum += (line.Point - last).Length; last = line.Point; }
        }
        return sum;
    }
}
