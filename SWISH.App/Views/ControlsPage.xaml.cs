using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandGestureRecognition.Custom;
using Swish.App.Controls;
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

    // The finger keyboard (left hand) is fixed: each finger holds one key. Order matches the mockup.
    static readonly (string Key, string Finger, string Asset)[] FingerKeys =
    [
        ("w", "MIDDLE FINGER", "finger-middle"), ("a", "RING FINGER", "finger-ring"), ("d", "INDEX FINGER", "finger-index"),
        ("space", "THUMB", "finger-thumb"), ("s", "PINKY", "finger-pinky"),
    ];

    public ControlsPage() => InitializeComponent();

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
        foreach (var (key, finger, asset) in FingerKeys)
            moves.Children.Add(GestureCard(asset, $"FOLD {finger}", Label(key, key.ToUpperInvariant()), $"{finger} ({key.ToUpperInvariant()})"));
        hand.Children.Add(moves);

        hand.Children.Add(SubLabel("↗  RIGHT HAND " + Label("_right", "ACTIONS").ToUpperInvariant()));
        var actions = new WrapPanel();
        actions.Children.Add(GestureCard("pinch-index", "PINCH THUMB + INDEX", Label("lmb", "Left click"), "LEFT CLICK"));
        actions.Children.Add(GestureCard("pinch-middle", "PINCH THUMB + MIDDLE", Label("rmb", "Right click"), "RIGHT CLICK"));
        actions.Children.Add(GestureCard("pinch-ring", "PINCH THUMB + RING", Label("scroll", "Scroll"), "MOVE HAND TO SCROLL"));
        actions.Children.Add(GestureCard("fist", "MAKE A FIST", "Lift mouse", "PAUSES THE CURSOR"));
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
        foreach (var c in commands.Where(c => c.Essential)) essentials.Children.Add(VoiceCard(c.Say[0], c.DisplayLabel, c));
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
            foreach (var c in inCategory) panel.Children.Add(VoiceCard(c.Say[0], c.DisplayLabel, c));
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

    /// <summary>A hand-control card: gesture art (or a placeholder) + what it does in this game.</summary>
    FrameworkElement GestureCard(string asset, string gesture, string label, string detail)
    {
        var art = new Grid { Height = 64 };
        if (MenuSidebar.LoadAsset($"Gestures/{asset}.png") is { } img)
        {
            art.Height = double.NaN;   // the art takes whatever the card leaves above its labels
            art.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4) });
            RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
        }
        else
        {
            art.Children.Add(new TextBlock { Text = "✋", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 36,
                Foreground = (Brush)FindResource("Gray"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
            art.Children.Add(new TextBlock { Text = gesture, Style = (Style)FindResource("Mono"), FontSize = 9.5,
                Foreground = (Brush)FindResource("Muted"), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom });
        }
        return Card(art, label, detail, onClick: null);
    }

    FrameworkElement VoiceCard(string phrase, string label, CommandDef command)
    {
        var top = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(new TextBlock { Text = "SAY", Style = (Style)FindResource("Mono"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        top.Children.Add(new TextBlock { Text = $"\"{phrase.ToUpperInvariant()}\"", Style = (Style)FindResource("Mono"), FontSize = 12.5,
            FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
        string also = command.Say.Count > 1 ? "Also: " + string.Join(", ", command.Say.Skip(1).Select(s => $"\"{s}\"")) : "";
        string does = command.Do is not null ? string.Join("; ", command.Do) : string.Join(" ", command.Keys);
        return Card(top, label, null, onClick: null, tooltip: $"{also}\nPresses: {does}".Trim());
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
