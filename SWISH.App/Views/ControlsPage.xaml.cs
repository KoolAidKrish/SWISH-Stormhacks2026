using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandGestureRecognition.Custom;
using Swish.App.Controls;
using Swish.App.Services;
using VoiceKeys;

namespace Swish.App.Views;

/// <summary>
/// "My games" + the controls for the selected game, all driven by data: the game list and hand-control
/// labels come from the voice presets (game / handLabels), the voice cards from the preset's commands
/// (essential ones up front, the rest grouped by category), plus the user's own custom functions.
/// Selecting a game switches the active voice preset.
/// </summary>
public partial class ControlsPage : UserControl, ISwishPage
{
    App? _app;
    ShellWindow? _shell;

    public ControlsPage() => InitializeComponent();

    public bool ShowsCameraCorner => true;

    public IInputElement? DefaultFocus => Sidebar.FocusTarget;

    public void OnShown(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        DefaultToggle.IsChecked = true;
        Sidebar.GameChanged -= Build;
        Sidebar.GameChanged += Build;
        Sidebar.Attach(app, shell, MenuSection.Controls);

        if (Sidebar.SelectedGame is not { } preset)
        {
            Notice.Text = app.VoiceProblem ?? "No game presets found.";
            Notice.Visibility = Visibility.Visible;
            return;
        }
        Build(preset);
    }

    void OnTutorial(object sender, RoutedEventArgs e) => _shell?.ShowOverlay(new TutorialPage());

    void OnCustomize(object sender, RoutedEventArgs e)
    {
        DefaultToggle.IsChecked = true;   // it's an action, the view stays on the defaults
        _app?.ShowCommandEditor(game: _app.CurrentGame);
    }

    // ---- Content ----

    void Build(Preset preset)
    {
        Sections.Children.Clear();
        GameTitle.Text = preset.Game?.Title.ToUpperInvariant();
        Hero.Source = preset.Game is { } info ? MenuSidebar.LoadAsset($"Games/{info.Tile}") : null;
        Notice.Visibility = _app?.Voice is null ? Visibility.Visible : Visibility.Collapsed;
        Notice.Text = _app?.Voice is null ? $"Voice is off ({_app?.VoiceProblem}); voice controls are shown but won't respond." : "";

        var labels = preset.HandLabels;
        string Label(string key, string fallback) => labels.TryGetValue(key, out var l) ? l : fallback;

        // ---- Default hand controls ----
        var hand = new StackPanel();
        hand.Children.Add(SubLabel("✥  LEFT HAND " + Label("_left", "MOVEMENTS").ToUpperInvariant()));
        var moves = new WrapPanel();
        foreach (var control in HandControl.All.Where(c => c.Hand == "LEFT")) moves.Children.Add(HandCard(control, preset, Label));
        hand.Children.Add(moves);

        hand.Children.Add(SubLabel("↗  RIGHT HAND " + Label("_right", "ACTIONS").ToUpperInvariant()));
        var actions = new WrapPanel();
        foreach (var control in HandControl.All.Where(c => c.Hand == "RIGHT")) actions.Children.Add(HandCard(control, preset, Label));
        actions.Children.Add(GestureCard("fist", "MAKE A FIST", "Lift mouse", "PAUSES THE CURSOR",
            onClick: () => SwishDialog.Inform(Window.GetWindow(this), "LIFT MOUSE",
                "Making a fist lifts the mouse so you can move your hand back without moving the cursor. It can't be rebound, or there'd be no way to reposition your hand.")));
        hand.Children.Add(actions);

        var myGestures = _app!.CustomFunctions.Functions
            .Where(f => f.Gesture is not null && (f.Game is null || f.Game == preset.Id)).ToList();
        hand.Children.Add(SubLabel("✋  YOUR GESTURES"));
        var mine = new WrapPanel();
        foreach (var f in myGestures) mine.Children.Add(CustomCard(f, $"✋ {f.Gesture!.Hand.ToString().ToUpperInvariant()} HAND"));
        mine.Children.Add(AddCard(preset.Id, null, "NEW GESTURE", gesture: true));
        hand.Children.Add(mine);
        Sections.Children.Add(Section("✋  DEFAULT HAND CONTROLS", hand));

        // ---- Voice controls ----
        var voice = new StackPanel();
        var commands = preset.Commands.Where(c => c.Say.Count > 0).ToList();
        var custom = _app.CustomFunctions.Functions
            .Where(f => f.VoicePhrases.Count > 0 && (f.Game is null || f.Game == preset.Id)).ToList();

        voice.Children.Add(SubLabel("↗  ESSENTIALS"));
        var essentials = new WrapPanel();
        foreach (var c in commands.Where(c => c.Essential)) essentials.Children.Add(VoiceCard(c.Say[0], c.DisplayLabel, c, preset.Id));
        voice.Children.Add(essentials);

        // Every category, collapsed: all its commands plus "+ add" for a new one in that category.
        var categories = commands.Select(c => c.Category ?? "Other")
            .Concat(custom.Select(f => f.Category).OfType<string>())
            .Distinct().ToList();
        voice.Children.Add(SubLabel("☰  ALL COMMANDS BY CATEGORY"));
        foreach (var category in categories)
        {
            var inCategory = commands.Where(c => (c.Category ?? "Other") == category).ToList();
            var mineHere = custom.Where(f => f.Category == category).ToList();
            var panel = new WrapPanel();
            foreach (var c in inCategory) panel.Children.Add(VoiceCard(c.Say[0], c.DisplayLabel, c, preset.Id));
            foreach (var f in mineHere) panel.Children.Add(CustomCard(f, $"SAY \"{f.VoicePhrases[0].ToUpperInvariant()}\""));
            panel.Children.Add(AddCard(preset.Id, category, "ADD TO " + category.ToUpperInvariant()));
            voice.Children.Add(new Expander
            {
                Style = (Style)FindResource("CategoryExpander"),
                Header = $"{category.ToUpperInvariant()}  ({inCategory.Count + mineHere.Count})",
                Content = panel,
            });
        }

        // The user's own commands without a category.
        var loose = custom.Where(f => f.Category is null).ToList();
        var yours = new WrapPanel();
        foreach (var f in loose) yours.Children.Add(CustomCard(f, $"SAY \"{f.VoicePhrases[0].ToUpperInvariant()}\""));
        yours.Children.Add(AddCard(preset.Id, null, "NEW VOICE COMMAND"));
        voice.Children.Add(new Expander
        {
            Style = (Style)FindResource("CategoryExpander"),
            Header = $"YOUR COMMANDS  ({loose.Count})",
            Content = yours,
            IsExpanded = loose.Count > 0,
        });
        Sections.Children.Add(Section("🔊  CUSTOM VOICE CONTROLS", voice));
        Scroller.ScrollToTop();
    }

    // ---- Building blocks ----

    Expander Section(string title, UIElement content) => new()
    {
        Style = (Style)FindResource("SectionExpander"),
        Header = title,
        Content = content,
    };

    TextBlock SubLabel(string text) => new() { Text = text, Style = (Style)FindResource("SubLabel") };

    /// <summary>
    /// A built-in hand control: what it does in this game (its preset label, or the key the user rebound it to).
    /// Clicking it opens the rebind page for this game.
    /// </summary>
    FrameworkElement HandCard(HandControl control, Preset preset, Func<string, string, string> label)
    {
        var custom = _app!.HandBindings.Get(preset.Id, control.Id);
        string defaultKey = control.DefaultChord is null ? "Scroll" : KeyCaptureBox.Pretty(control.DefaultChord);
        string what = custom is not null ? KeyCaptureBox.Pretty(custom) : label(control.LabelKey, defaultKey);
        string name = control.Gesture.Replace("FOLD ", "").Replace("PINCH ", "");
        string detail = custom is not null ? $"{name} (YOURS)"
                      : control.DefaultChord is null ? "MOVE HAND TO SCROLL"
                      : control.Finger is not null ? $"{name} ({defaultKey})" : defaultKey.Replace("LMB", "LEFT CLICK").Replace("RMB", "RIGHT CLICK");
        return GestureCard(control.Asset, control.Gesture, what, detail,
            onClick: () => _shell?.Navigate(new BindGesturePage(control.Id, preset.Id)));
    }

    /// <summary>A hand-control card: gesture art (or a placeholder) + what it does; on hover the hand turns blue.</summary>
    FrameworkElement GestureCard(string asset, string gesture, string label, string detail, Action onClick)
    {
        var art = new Grid { Height = 64 };
        FrameworkElement? blue = null;
        if (MenuSidebar.LoadAsset($"Gestures/{asset}.png") is { } img)
        {
            art.Height = double.NaN;   // the art takes whatever the card leaves above its labels
            art.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4) });
            // The same hand in blue (dark tones = the hover blue, light ones = white), faded in on hover.
            blue = new Image { Source = img is BitmapSource bmp ? BlueDuotone(bmp) : img, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4), Opacity = 0, IsHitTestVisible = false };
            art.Children.Add(blue);
            RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
        }
        else
        {
            art.Children.Add(new TextBlock { Text = "✋", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 36,
                Foreground = (Brush)FindResource("Gray"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
            art.Children.Add(new TextBlock { Text = gesture, Style = (Style)FindResource("Mono"), FontSize = 9.5,
                Foreground = (Brush)FindResource("Muted"), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom });
        }
        var card = Card(art, label, detail, onClick);
        if (blue is not null)
        {
            void Fade(double to) => blue.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));
            card.MouseEnter += (_, _) => Fade(1);
            card.MouseLeave += (_, _) => { if (!card.IsKeyboardFocused) Fade(0); };
            card.GotKeyboardFocus += (_, _) => Fade(1);
            card.LostKeyboardFocus += (_, _) => { if (!card.IsMouseOver) Fade(0); };
        }
        return card;
    }

    static readonly Dictionary<ImageSource, BitmapSource> _blueCache = new();

    /// <summary>
    /// The grey dithered hand art recoloured like the mockup's hovered card: black → the hover blue (#092390),
    /// mid grey → pale blue (#BFD3FF), white → white; transparency kept. Made once per image.
    /// </summary>
    internal static BitmapSource BlueDuotone(BitmapSource source)
    {
        if (_blueCache.TryGetValue(source, out var cached)) return cached;
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        (double R, double G, double B) dark = (9, 35, 144), mid = (191, 211, 255), light = (255, 255, 255);
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] == 0) continue;
            double l = (0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]) / 255;   // luminance (BGR order)
            var (a, b, t) = l < 0.6 ? (dark, mid, l / 0.6) : (mid, light, (l - 0.6) / 0.4);
            px[i] = (byte)(a.B + (b.B - a.B) * t);
            px[i + 1] = (byte)(a.G + (b.G - a.G) * t);
            px[i + 2] = (byte)(a.R + (b.R - a.R) * t);
        }
        var result = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return _blueCache[source] = result;
    }

    /// <summary>A voice-command card. Clicking it starts a custom command copied from this one (phrases, keys, category).</summary>
    FrameworkElement VoiceCard(string phrase, string label, CommandDef command, string game)
    {
        var top = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(new TextBlock { Text = "SAY", Style = (Style)FindResource("Mono"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        top.Children.Add(new TextBlock { Text = $"\"{phrase.ToUpperInvariant()}\"", Style = (Style)FindResource("Mono"), FontSize = 12.5,
            FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
        string also = command.Say.Count > 1 ? "Also: " + string.Join(", ", command.Say.Skip(1).Select(s => $"\"{s}\"")) : "";
        string does = command.Do is not null ? string.Join("; ", command.Do) : string.Join(" ", command.Keys);
        var template = new CustomFunction
        {
            Name = label, VoicePhrases = command.Say.ToList(), Action = ActionOf(command), Game = game, Category = command.Category,
        };
        return Card(top, label, null, onClick: () => _app?.ShowCommandEditor(template: template),
                    tooltip: $"{also}\nPresses: {does}\nClick to make your own version".Trim());
    }

    /// <summary>A preset command's keys as editor steps: its step program, or its keys tapped in order, then any text.</summary>
    static string ActionOf(CommandDef c)
    {
        var lines = c.Do is not null ? c.Do.ToList()
                  : c.Keys.Select(k => c.HoldMs is { } ms ? $"tap {k} {ms}" : k).ToList();
        if (!string.IsNullOrEmpty(c.Text)) lines.Add("type " + c.Text.Replace("\r", "").Replace("\n", " "));
        return string.Join("\n", lines);
    }

    FrameworkElement CustomCard(CustomFunction f, string top) =>
        Card(new TextBlock { Text = top, Style = (Style)FindResource("Mono"), FontSize = 12, FontWeight = FontWeights.Bold,
                 TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
             f.Name, "CLICK TO EDIT", onClick: () => _app?.ShowCommandEditor(edit: f));

    FrameworkElement AddCard(string game, string? category, string what, bool gesture = false) =>
        Card(new TextBlock { Text = "+", Style = (Style)FindResource("Display"), FontSize = 42, HorizontalAlignment = HorizontalAlignment.Center,
                 VerticalAlignment = VerticalAlignment.Center },
             "Add", what, onClick: () => _app?.ShowCommandEditor(game: game, category: category, gesture: gesture));

    /// <summary>A chamfered card. Clickable cards are buttons (focusable); read-only ones are not tab stops.</summary>
    /// <remarks>Every card on this screen is clickable now: built-in ones start a custom copy, yours open for editing.</remarks>
    FrameworkElement Card(UIElement top, string label, string? detail, Action? onClick, string? tooltip = null)
    {
        var body = new DockPanel();
        var bottom = new StackPanel();
        bottom.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = (Style)FindResource("Mono"), FontSize = 11.5,
            FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        if (detail is not null)
            bottom.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Mono"), FontSize = 9.5, Foreground = (Brush)FindResource("Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(top);

        var button = new Button
        {
            Style = (Style)FindResource("CardButton"), Content = body, ToolTip = tooltip,
            Focusable = onClick is not null, IsTabStop = onClick is not null,
            Cursor = onClick is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
        };
        AutomationProperties_SetName(button, detail is null ? label : $"{label}, {detail}");
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    static void AutomationProperties_SetName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);
}
