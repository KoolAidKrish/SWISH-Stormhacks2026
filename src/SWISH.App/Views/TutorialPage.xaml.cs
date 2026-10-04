using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Swish.App.Controls;
using Path = System.Windows.Shapes.Path;

namespace Swish.App.Views;

/// <summary>
/// "TUTORIAL:" walkthrough of the Desktop preset's hand controls. It highlights each card in turn (the hover look),
/// animates its hand (open ↔ the gesture) and shows what that does to a cursor on a pretend desktop underneath,
/// then moves on to the next card, looping. Clicking a card jumps to it. Pulled up over the current screen with
/// <see cref="ShellWindow.ShowOverlay"/>; Got it (button, Enter, Esc or "done") closes it.
/// </summary>
public partial class TutorialPage : UserControl, ISwishPage
{
    /// <summary>Seconds per card: highlight in, the demo, highlight out.</summary>
    const double StepSeconds = 4.6, LeadIn = 0.35, LeadOut = 4.25;

    sealed record Step(string Label, string Asset, string Hint, string Caption);

    static readonly Step[] Steps =
    [
        new("MOVE\nMOUSE", "open", "Open your hand.", "OPEN HAND: the cursor follows your palm."),
        new("FREEZE\nMOUSE", "fist", "Make a fist.", "MAKE A FIST: the cursor stays put while you move your hand back. Open it to carry on."),
        new("LEFT\nCLICK", "pinch-index", "Pinch thumb + index.", "PINCH THUMB + INDEX: left click."),
        new("RIGHT\nCLICK", "pinch-middle", "Pinch thumb + middle.", "PINCH THUMB + MIDDLE: right click."),
        new("SCROLL", "pinch-ring", "Pinch thumb + ring.", "PINCH THUMB + RING, then move your hand up or down: scroll."),
    ];

    /// <summary>One card's moving parts: the open hand and the gesture (grey and blue copies), its hint strip.</summary>
    sealed class CardParts
    {
        public required Button Button;
        public required TranslateTransform Move;
        public required UIElement Open, OpenBlue, Gesture, GestureBlue, Hint;
        public double Glow;   // 0..1, eases toward 1 while this card is highlighted
    }

    readonly List<CardParts> _cards = [];
    readonly Stopwatch _clock = new();
    TimeSpan _lastFrame;
    int _step;
    App? _app;
    ShellWindow? _shell;

    // Stage pieces
    readonly Canvas _canvas = new();
    readonly Canvas _cursor = new();
    readonly Border _okButton, _menu, _frozen, _scrollBadge;
    readonly TextBlock _okText;
    readonly StackPanel _file;
    readonly Border _doc;
    readonly TranslateTransform _docScroll = new();
    readonly Ellipse _ripple;
    readonly ScaleTransform _rippleScale = new();
    double _docOffset;
    Point _cursorAt = Home, _cursorFrom = Home;

    const double StageW = 798, StageH = 210;
    static readonly Point Home = new(StageW / 2, StageH / 2 + 6);
    static readonly Point OkAt = new(560, 112), FileAt = new(560, 96);

    public TutorialPage()
    {
        InitializeComponent();

        for (int i = 0; i < Steps.Length; i++) Cards.Children.Add(BuildCard(i));

        // ---- Stage: a dark "screen" with the bits each demo needs; only the current step's are shown ----
        Stage.Children.Add(new ChamferShape { Fill = Res("Bg2"), Stroke = Res("Line"), StrokeThickness = 1.2, Chamfer = 18, Corner = ChamferCorner.BottomRight });
        Stage.Children.Add(_canvas);
        _canvas.Children.Add(Place(new TextBlock { Text = "DESKTOP", Style = MonoStyle, FontSize = 10, Foreground = Res("Muted") }, 14, 10));

        _okText = new TextBlock { Text = "OK", Style = MonoStyle, FontSize = 13, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _okButton = new Border { Width = 120, Height = 40, BorderBrush = Res("White"), BorderThickness = new Thickness(1.5), Child = _okText };
        _canvas.Children.Add(Place(_okButton, OkAt.X - 60, OkAt.Y - 20));

        _file = new StackPanel { Width = 90 };
        _file.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 H22 L32,10 V40 H0 Z M22,0 V10 H32 M6,18 H26 M6,25 H26 M6,32 H18"),
            Stroke = Res("Gray"), StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center,
        });
        _file.Children.Add(new TextBlock { Text = "notes.txt", Style = MonoStyle, FontSize = 10, Foreground = Res("Gray"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        _canvas.Children.Add(Place(_file, FileAt.X - 45, FileAt.Y - 22));

        var menuRows = new StackPanel();
        foreach (var (item, on) in new[] { ("Open", false), ("Copy", true), ("Rename", false), ("Delete", false) })
            menuRows.Children.Add(new Border
            {
                Background = on ? Res("Blue") : Brushes.Transparent, Padding = new Thickness(10, 3, 30, 3),
                Child = new TextBlock { Text = item, Style = MonoStyle, FontSize = 11 },
            });
        _menu = new Border { Background = Res("Panel"), BorderBrush = Res("Muted"), BorderThickness = new Thickness(1), Padding = new Thickness(0, 4, 0, 4), Child = menuRows, RenderTransform = new ScaleTransform() };
        _canvas.Children.Add(_menu);

        var lines = new StackPanel { RenderTransform = _docScroll };
        lines.Children.Add(new TextBlock { Text = "A LONG PAGE", Style = (Style)FindResource("Display"), FontSize = 20, Margin = new Thickness(0, 0, 0, 8) });
        var rng = new Random(7);
        for (int i = 0; i < 26; i++)
            lines.Children.Add(new Rectangle { Height = 6, Width = 140 + rng.Next(140), HorizontalAlignment = HorizontalAlignment.Left, Fill = Res(i % 6 == 5 ? "Pink" : "Line"), Margin = new Thickness(0, 0, 0, 9) });
        _doc = new Border { Width = 320, Height = 160, ClipToBounds = true, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 0), Child = lines };
        _canvas.Children.Add(Place(_doc, Home.X - 160, 30));

        _frozen = Badge("❚❚  CURSOR FROZEN", "Pink");
        _scrollBadge = Badge("↕  SCROLLING", "Pink");
        _canvas.Children.Add(_frozen);
        _canvas.Children.Add(_scrollBadge);

        _ripple = new Ellipse { Width = 36, Height = 36, Stroke = Res("Pink"), StrokeThickness = 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _rippleScale, IsHitTestVisible = false };
        _canvas.Children.Add(_ripple);

        // The cursor, styled like the landing site's: blue arrow, white edge, pink offset shadow. Hotspot at (3,3).
        _cursor.Children.Add(new Path { Data = Geometry.Parse("M4.5 4.5v19l5.5-5.5L21.5 15.5z"), Fill = Res("Pink") });
        _cursor.Children.Add(new Path { Data = Geometry.Parse("M3 3v19l5.5-5.5L20 14z"), Fill = Res("Blue"), Stroke = Res("White"), StrokeThickness = 1.5 });
        _cursor.RenderTransform = new ScaleTransform(1.25, 1.25, 3, 3);
        _canvas.Children.Add(_cursor);

        Unloaded += (_, _) => Stop();
    }

    public IInputElement? DefaultFocus => ContinueButton;

    public void OnShown(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        if (app.Voice is { } v) { v.Suspended = true; v.SetExtraHints(["continue"]); v.Heard += OnHeard; }
        GoTo(0);
        _clock.Restart();
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    public void OnHidden()
    {
        Stop();
        if (_app?.Voice is { } v) { v.Heard -= OnHeard; v.SetExtraHints([]); v.Suspended = false; }
    }

    void Stop()
    {
        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
    }

    void OnHeard(string transcript)
    {
        if (Regex.IsMatch(transcript, @"\b(continue|done|got it|close)\b", RegexOptions.IgnoreCase)) Dispatcher.BeginInvoke(Continue);
    }

    void OnContinue(object sender, RoutedEventArgs e) => Continue();
    void Continue() => _shell?.CloseOverlay();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Continue(); e.Handled = true; }
    }

    // ---- Cards ----

    Button BuildCard(int index)
    {
        var step = Steps[index];
        var content = new Grid { ClipToBounds = true };
        var move = new TranslateTransform();
        var art = new Grid { Width = 96, HorizontalAlignment = HorizontalAlignment.Right, RenderTransform = move };
        RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);

        UIElement Hand(string asset, bool blue)
        {
            var img = MenuSidebar.LoadAsset($"Gestures/{asset}.png");
            if (blue && img is BitmapSource bmp) img = ControlsPage.BlueDuotone(bmp);
            return new Image { Source = img, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, IsHitTestVisible = false };
        }
        var open = Hand("open", false);
        var openBlue = Hand("open", true);
        var gesture = step.Asset == "open" ? new Grid() : Hand(step.Asset, false);
        var gestureBlue = step.Asset == "open" ? new Grid() : Hand(step.Asset, true);
        open.Opacity = 1;
        foreach (var e in new[] { open, gesture, openBlue, gestureBlue }) art.Children.Add(e);
        content.Children.Add(art);

        content.Children.Add(new TextBlock
        {
            Text = step.Label, Style = MonoStyle, FontSize = 11, FontWeight = FontWeights.Bold, LineHeight = 14,
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 2),
        });

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Padding = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, IsHitTestVisible = false,
            Child = new TextBlock { Text = step.Hint, Style = MonoStyle, FontSize = 9.5 },
        };
        content.Children.Add(hint);

        var button = new Button
        {
            Style = (Style)FindResource("CardButton"), Content = content, Margin = new Thickness(0, 0, index < Steps.Length - 1 ? 12 : 0, 0),
        };
        AutomationProperties_SetName(button, $"{step.Label.Replace('\n', ' ')}: {step.Caption}");
        button.Click += (_, _) => { GoTo(index); _clock.Restart(); _lastFrame = TimeSpan.Zero; };
        // The pointer leaving the highlighted card runs its "un-hover"; put the highlight straight back.
        button.MouseLeave += (_, _) =>
        {
            if (index != _step || !CardHighlight.GetIsOn(button)) return;
            CardHighlight.SetIsOn(button, false);
            CardHighlight.SetIsOn(button, true);
        };

        _cards.Add(new CardParts { Button = button, Move = move, Open = open, OpenBlue = openBlue, Gesture = gesture, GestureBlue = gestureBlue, Hint = hint });
        return button;
    }

    void GoTo(int step)
    {
        _step = step;
        Caption.Text = Steps[step].Caption;
        _docOffset = 0;
        _okText.Text = "OK";
        _cursorFrom = _cursorAt;   // glide from wherever the last demo left it
    }

    // ---- Animation ----

    void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        double dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        double t = now.TotalSeconds;
        if (t >= StepSeconds)
        {
            GoTo((_step + 1) % Steps.Length);
            _clock.Restart();
            _lastFrame = TimeSpan.Zero;
            t = 0;
        }

        var f = Frame(_step, t);

        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            bool on = i == _step && t >= 0.05 && t < LeadOut;
            if (CardHighlight.GetIsOn(card.Button) != on) CardHighlight.SetIsOn(card.Button, on);
            card.Glow = Approach(card.Glow, on ? 1 : 0, dt / 0.18);
            // At rest a card shows its gesture (the move card, its open hand); its demo opens the hand first,
            // folds into the gesture and back as needed, then settles on the gesture again.
            double rest = Steps[i].Asset == "open" ? 0 : 1;
            double pose = i != _step ? rest
                        : Lerp(Lerp(rest, f.Pose, Ramp(t, 0, LeadIn)), rest, Ramp(t, LeadOut, StepSeconds));
            var (hx, hy) = i == _step ? f.Hand : (0d, 0d);
            card.Move.X = hx;
            card.Move.Y = hy;
            card.Open.Opacity = (1 - pose) * (1 - card.Glow);
            card.OpenBlue.Opacity = (1 - pose) * card.Glow;
            card.Gesture.Opacity = pose * (1 - card.Glow);
            card.GestureBlue.Opacity = pose * card.Glow;
            card.Hint.Opacity = card.Glow;
        }

        // Stage
        double glide = Ramp(t, 0, LeadIn);
        _cursorAt = new Point(Lerp(_cursorFrom.X, f.Cursor.X, glide), Lerp(_cursorFrom.Y, f.Cursor.Y, glide));
        f = f with { Cursor = _cursorAt };
        Canvas.SetLeft(_cursor, f.Cursor.X - 3);
        Canvas.SetTop(_cursor, f.Cursor.Y - 3);
        _cursor.Opacity = f.Frozen > 0 ? 1 - 0.45 * f.Frozen : 1;

        _okButton.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        _okButton.Background = f.Pressed ? Res("Blue") : Brushes.Transparent;
        _file.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        _doc.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        _docScroll.Y = -_docOffset;

        _menu.Opacity = f.Menu;
        _menu.Visibility = f.Menu > 0 ? Visibility.Visible : Visibility.Collapsed;
        ((ScaleTransform)_menu.RenderTransform).ScaleX = ((ScaleTransform)_menu.RenderTransform).ScaleY = 0.85 + 0.15 * f.Menu;
        Canvas.SetLeft(_menu, FileAt.X + 14);
        Canvas.SetTop(_menu, FileAt.Y + 4);

        _frozen.Opacity = f.Frozen;
        Canvas.SetLeft(_frozen, f.Cursor.X + 22);
        Canvas.SetTop(_frozen, f.Cursor.Y + 22);
        _scrollBadge.Opacity = f.Scrolling;
        Canvas.SetLeft(_scrollBadge, _step == 4 ? Home.X + 175 : 0);
        Canvas.SetTop(_scrollBadge, Home.Y - 10);

        _ripple.Opacity = f.Ripple > 0 ? 1 - f.Ripple : 0;
        _rippleScale.ScaleX = _rippleScale.ScaleY = 0.3 + 1.2 * f.Ripple;
        Canvas.SetLeft(_ripple, f.Cursor.X - 18);
        Canvas.SetTop(_ripple, f.Cursor.Y - 18);
    }

    /// <summary>Everything that moves, at time <paramref name="t"/> into a step.</summary>
    record struct FrameState(double Pose, (double X, double Y) Hand, Point Cursor, double Frozen = 0, bool Pressed = false,
                             double Ripple = 0, double Menu = 0, double Scrolling = 0);

    FrameState Frame(int step, double t)
    {
        switch (step)
        {
            case 0:   // move: the hand drifts in a figure-eight, the cursor traces the same path, bigger
            {
                double u = Ease(Clamp01((t - LeadIn) / (LeadOut - LeadIn))) * Math.PI * 2;
                double sx = Math.Sin(u), sy = Math.Sin(2 * u);
                return new(0, (7 * sx, 4 * sy), new Point(Home.X + 170 * sx, Home.Y + 50 * sy));
            }
            case 1:   // freeze: move right, fist, move back (cursor stays), open, move right again
            {
                double hx = t < 1.2 ? Lerp(-3, 7, Ramp(t, LeadIn, 1.2))
                          : t < 1.5 ? 7
                          : t < 2.9 ? Lerp(7, -7, Ramp(t, 1.5, 2.9))
                          : t < 3.2 ? -7
                          : Lerp(-7, 0, Ramp(t, 3.2, LeadOut));
                double cx = t < 1.5 ? Home.X - 150 + 15 * (hx + 3)
                          : t < 3.2 ? Home.X
                          : Home.X + 15 * (hx + 7);
                double pose = Ramp(t, 1.2, 1.5) - Ramp(t, 2.9, 3.2);
                double frozen = Ramp(t, 1.4, 1.6) - Ramp(t, 2.9, 3.1);
                return new(pose, (hx, 0), new Point(cx, Home.Y), Frozen: frozen);
            }
            case 2:   // left click: glide onto the button, pinch (click), release, click again
            {
                double k = Ramp(t, LeadIn, 1.4);
                var cursor = new Point(Lerp(Home.X - 220, OkAt.X + 8, k), Lerp(Home.Y + 40, OkAt.Y + 6, k));
                double pose = Pulse(t, 1.55, 2.25) + Pulse(t, 2.85, 3.55);
                bool pressed = pose > 0.8;
                double ripple = RippleAt(t, 1.72) + RippleAt(t, 3.02);
                if (t > 1.72) _okText.Text = "✓ CLICKED";
                return new(pose, (Lerp(-6, 4, k), Lerp(3, 0, k)), cursor, Pressed: pressed, Ripple: ripple);
            }
            case 3:   // right click: glide onto the file, pinch, a menu pops up
            {
                double k = Ramp(t, LeadIn, 1.4);
                var cursor = new Point(Lerp(Home.X - 220, FileAt.X, k), Lerp(Home.Y + 40, FileAt.Y, k));
                double pose = Pulse(t, 1.6, 2.6);
                double menu = Ramp(t, 1.8, 1.95) - Ramp(t, 4.0, 4.2);
                return new(pose, (Lerp(-6, 4, k), Lerp(3, 0, k)), cursor, Ripple: RippleAt(t, 1.78), Menu: menu);
            }
            default:  // scroll: pinch over the page, move down then up, the page scrolls with the hand; release
            {
                double pose = Ramp(t, 0.6, 0.9) - Ramp(t, 3.3, 3.6);
                double hy = t < 2.1 ? Lerp(0, 8, Ramp(t, 0.9, 2.1)) : Lerp(8, 2, Ramp(t, 2.1, 3.2));
                if (pose > 0.8) _docOffset = Math.Max(0, 12 * hy);
                return new(pose, (0, hy), new Point(Home.X + 40, Home.Y + 10 + 2 * hy), Scrolling: pose > 0.8 ? 1 : 0);
            }
        }
    }

    // ---- Helpers ----

    Style MonoStyle => (Style)FindResource("Mono");
    Brush Res(string key) => (Brush)FindResource(key);

    static T Place<T>(T element, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }

    Border Badge(string text, string color) => new()
    {
        Background = Res("Panel"), BorderBrush = Res(color), BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 6, 2), Opacity = 0,
        Child = new TextBlock { Text = text, Style = MonoStyle, FontSize = 10, Foreground = Res(color) },
    };

    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static double Ease(double v) => v * v * (3 - 2 * v);
    static double Lerp(double a, double b, double k) => a + (b - a) * k;
    /// <summary>0 before <paramref name="a"/>, 1 after <paramref name="b"/>, eased in between.</summary>
    static double Ramp(double t, double a, double b) => Ease(Clamp01((t - a) / (b - a)));
    /// <summary>A pinch held between <paramref name="a"/> and <paramref name="b"/>: 0.2 s to close, 0.2 s to open.</summary>
    static double Pulse(double t, double a, double b) => Ramp(t, a, a + 0.2) - Ramp(t, b - 0.2, b);
    static double RippleAt(double t, double at) => t >= at && t < at + 0.5 ? (t - at) / 0.5 : 0;
    static double Approach(double v, double target, double step) => v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

    static void AutomationProperties_SetName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);
}
