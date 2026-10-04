using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandGestureRecognition;
using HandGestureRecognition.Custom;
using Swish.App.Controls;
using VoiceKeys;

namespace Swish.App.Views;

/// <summary>
/// Make or edit a custom command in three steps: 1) how it's triggered (a phrase, a gesture, or both),
/// 2) what it does (steps built from recorded keys, or the step language as text for power users),
/// 3) its name and category. The live camera shows whether saved gestures are recognised, voice phrases
/// can be tried out loud, and a summary says in plain words what the command will do.
/// Voice commands are suspended on this page, so trying a phrase never presses a key.
/// </summary>
public partial class CommandEditorPage : UserControl, ISwishPage
{
    enum StepKind { Press, Hold, Wait, Type }

    sealed class Step
    {
        public StepKind Kind = StepKind.Press;
        public string Chord = "";
        public int Ms = 500;
        public string Text = "";
    }

    static readonly string[] KindNames = ["PRESS", "HOLD", "WAIT", "TYPE"];

    readonly App _app = (App)Application.Current;
    ShellWindow? _shell;
    readonly CustomFunction? _editing;
    readonly List<string> _phrases = new();
    List<Step> _steps = new();
    bool _scriptMode;
    CustomGesture? _draftGesture;
    HandsFrame? _latest;
    readonly DispatcherTimer _liveTimer;
    string _savedSnapshot = "";
    bool _loading = true;

    /// <param name="template">Start a new command from this one's settings (it isn't changed or saved).</param>
    public CommandEditorPage(CustomFunction? edit = null, string? game = null, string? category = null, bool gesture = false,
                             CustomFunction? template = null)
    {
        InitializeComponent();
        _editing = edit;
        _liveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => ShowLive(), Dispatcher);

        GameCombo.ItemsSource = new[] { new ComboBoxItem { Content = "ALL GAMES", Tag = null } }
            .Concat((_app.Config?.Presets ?? []).Where(p => p.Game is not null).OrderBy(p => p.Game!.Order)
                .Select(p => new ComboBoxItem { Content = p.Game!.Title.ToUpperInvariant(), Tag = p.Id }))
            .ToList();
        PhraseBox.TextChanged += (_, _) => PhraseHint.Visibility = PhraseBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryBox.TextChanged += (_, _) => UpdateSummary();
        ScriptBox.TextChanged += (_, _) => UpdateSummary();

        if ((edit ?? template) is { } source)
        {
            TitleText.Text = edit is not null ? "EDIT COMMAND" : "NEW COMMAND";
            SubtitleText.Text = edit is not null ? edit.Name.ToUpperInvariant() : $"BASED ON {source.Name.ToUpperInvariant()}";
            NameBox.Text = edit is not null ? edit.Name : "";   // a copy gets its own (default) name
            _phrases.AddRange(source.VoicePhrases);
            _draftGesture = source.Gesture;
            VoiceToggle.IsChecked = source.VoicePhrases.Count > 0;
            GestureToggle.IsChecked = source.Gesture is not null || gesture;
            if (VoiceToggle.IsChecked != true && GestureToggle.IsChecked != true) VoiceToggle.IsChecked = true;
            SelectGame(source.Game ?? game);
            CategoryBox.Text = source.Category ?? category ?? "";
            (source.Mode == TriggerMode.Hold ? ModeHold : ModeOnce).IsChecked = true;
            (source.Gesture?.Hand switch { GestureHand.Left => HandLeft, GestureHand.Right => HandRight, _ => HandEither }).IsChecked = true;
            if (FromScript(source.Action) is { } steps) _steps = steps.Count > 0 ? steps : [new Step()];
            else { _scriptMode = true; ScriptBox.Text = source.Action; }
        }
        else
        {
            VoiceToggle.IsChecked = !gesture;
            GestureToggle.IsChecked = gesture;
            SelectGame(game);
            CategoryBox.Text = category ?? "";
            ModeOnce.IsChecked = true;
            HandEither.IsChecked = true;
            _steps.Add(new Step());
        }
        DeleteButton.Visibility = edit is null ? Visibility.Collapsed : Visibility.Visible;

        _loading = false;
        ShowScriptMode();
        RebuildPhrases();
        RebuildSteps();
        RebuildCategories();
        ShowTriggers();
        ShowGestureStatus();
        UpdateSummary();
        _savedSnapshot = Snapshot();
    }

    public IInputElement? DefaultFocus => _editing is not null ? SaveButton : VoiceToggle.IsChecked == true ? PhraseBox : RecordButton;

    public void OnShown(App app, ShellWindow shell)
    {
        _shell = shell;
        Sidebar.Attach(app, shell, MenuSection.Editor, canLeave: ConfirmLeave);
        app.Gestures.HandsUpdated += OnHands;
        if (app.Voice is { } voice)
        {
            voice.Suspended = true;   // trying a phrase out loud mustn't press keys in this window
            voice.Heard += OnHeard;
            voice.SetExtraHints(_phrases);
        }
        else HeardText.Text = "Voice is off: " + (app.VoiceProblem ?? "unavailable");
        _liveTimer.Start();
        PreviewKeyDown += OnPageKey;
    }

    public void OnHidden()
    {
        _liveTimer.Stop();
        PreviewKeyDown -= OnPageKey;
        _app.Gestures.HandsUpdated -= OnHands;
        if (_app.Voice is { } voice)
        {
            voice.Heard -= OnHeard;
            voice.SetExtraHints([]);
            voice.Suspended = false;
        }
    }

    // ---- Navigation ----

    void OnPageKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.FocusedElement is KeyCaptureBox { IsListening: true } || GameCombo.IsDropDownOpen) return;
        e.Handled = true;
        OnBack(this, new RoutedEventArgs());
    }

    void OnBack(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeave()) _shell?.Navigate(new ControlsPage());
    }

    /// <summary>OK to leave? Asks first if there are unsaved changes.</summary>
    bool ConfirmLeave() =>
        Snapshot() == _savedSnapshot ||
        SwishDialog.Confirm(Window.GetWindow(this), "UNSAVED CHANGES",
                            "You've changed this command but haven't saved it. Leave and lose those changes?",
                            confirm: "LEAVE", cancel: "KEEP EDITING", danger: true);

    // ---- 1. Trigger ----

    void OnTriggerToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (VoiceToggle.IsChecked != true && GestureToggle.IsChecked != true)
        {
            ((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked = true;
            Say("It needs at least one trigger.", Warn);
        }
        ShowTriggers();
        UpdateNameHint();   // the default name depends on the triggers (also refreshes the summary)
    }

    void ShowTriggers()
    {
        VoicePanel.Visibility = VoiceToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GesturePanel.Visibility = GestureToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HoldNote.Visibility = HoldMode ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnPhraseKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddPhrase(sender, e);
    }

    void OnAddPhrase(object sender, RoutedEventArgs e)
    {
        foreach (var phrase in PhraseBox.Text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            if (!_phrases.Contains(phrase, StringComparer.OrdinalIgnoreCase)) _phrases.Add(phrase);
        PhraseBox.Clear();
        PhraseBox.Focus();
        RebuildPhrases();
    }

    void RebuildPhrases()
    {
        PhraseChips.Children.Clear();
        foreach (var phrase in _phrases)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = $"\"{phrase.ToUpperInvariant()}\"   ×" };
            AutomationProperties_SetName(chip, $"Remove phrase {phrase}");
            chip.Click += (_, _) => { _phrases.Remove(phrase); RebuildPhrases(); PhraseBox.Focus(); };
            PhraseChips.Children.Add(chip);
        }
        PhraseChips.Margin = new Thickness(0, 0, 0, _phrases.Count > 0 ? 4 : 0);
        _app.Voice?.SetExtraHints(_phrases);
        UpdateNameHint();
        UpdateSummary();
    }

    /// <summary>WebSocket thread: something was said. Show whether it matches a phrase.</summary>
    void OnHeard(string transcript) => Dispatcher.BeginInvoke(() =>
    {
        string said = Normalize(transcript);
        if (said.Length == 0) return;
        var match = _phrases.FirstOrDefault(p => Normalize(p) is { Length: > 0 } n && $" {said} ".Contains($" {n} "));
        if (_phrases.Count == 0)
        {
            HeardText.Text = $"Heard \"{transcript.Trim()}\". Add it as a phrase above to use it.";
            HeardText.Foreground = (Brush)FindResource("White");
        }
        else if (match is not null)
        {
            HeardText.Text = $"✓  Heard \"{transcript.Trim()}\": matches \"{match}\"";
            HeardText.Foreground = (Brush)FindResource("Success");
        }
        else
        {
            HeardText.Text = $"✗  Heard \"{transcript.Trim()}\": no match. Try adding it as a phrase.";
            HeardText.Foreground = Warn;
        }
    });

    static string Normalize(string text) =>
        Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}\s']", " "), @"\s+", " ").Trim();

    // Gesture

    void OnHandChanged(object sender, RoutedEventArgs e) { if (!_loading) { ShowGestureStatus(); UpdateSummary(); } }

    void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ShowTriggers();
        UpdateSummary();
    }

    void OnRecord(object sender, RoutedEventArgs e)
    {
        var others = _app.CustomFunctions.Functions
            .Where(f => f != _editing && f.Gesture is not null)
            .Select(f => f.Gesture!)
            .ToList();
        var recorder = new GestureRecorderWindow(_app, CommandName(), SelectedHand(), others) { Owner = Window.GetWindow(this) };
        if (recorder.ShowDialog() == true && recorder.Result is { } gesture)
        {
            _draftGesture = gesture;
            Say("Gesture recorded. Save to keep it.", Good);
        }
        ShowGestureStatus();
        UpdateSummary();
    }

    void OnRemoveGesture(object sender, RoutedEventArgs e)
    {
        _draftGesture = null;
        ShowGestureStatus();
        UpdateSummary();
    }

    void ShowGestureStatus()
    {
        RemoveGestureButton.Visibility = _draftGesture is null ? Visibility.Collapsed : Visibility.Visible;
        RecordText.Text = _draftGesture is null ? "●  RECORD GESTURE" : "●  RECORD AGAIN";
        if (_draftGesture is not { } g)
        {
            GestureStatus.Text = "Not recorded yet. Recording takes about 20 seconds.";
            GestureStatus.Foreground = (Brush)FindResource("Muted");
            return;
        }
        var text = $"✓  Recorded ({g.Hand.ToString().ToLowerInvariant()} hand)";
        if (SelectedHand() != g.Hand) text += ". Hand changed: record again to apply it";
        GestureStatus.Text = text;
        GestureStatus.Foreground = SelectedHand() != g.Hand ? Warn : (Brush)FindResource("Success");
    }

    /// <summary>Engine thread: keep the latest frame for the live readout.</summary>
    void OnHands(HandsFrame frame) => Volatile.Write(ref _latest, frame);

    void ShowLive()
    {
        var f = Volatile.Read(ref _latest);
        LiveText.Text = f is null ? "Waiting for the camera…" :
            $"RIGHT HAND  {Describe(f.Right, f.RightMatch, f.RightConfidence)}\nLEFT HAND   {Describe(f.Left, f.LeftMatch, f.LeftConfidence)}";
    }

    static string Describe(object? hand, string? match, double confidence) =>
        hand is null ? "not in view" : match is null ? "no custom gesture" : $"✓ {match} ({confidence:P0})";

    // ---- 2. Action ----

    void OnAddStep(object sender, RoutedEventArgs e)
    {
        _steps.Add(new Step());
        RebuildSteps();
        // Straight into recording the new step's key.
        if (StepRows.Children[^1] is DockPanel row && row.Children.OfType<KeyCaptureBox>().FirstOrDefault() is { } box)
            box.Listen();
    }

    void RebuildSteps()
    {
        StepRows.Children.Clear();
        for (int i = 0; i < _steps.Count; i++)
            StepRows.Children.Add(StepRow(_steps[i], i));
        UpdateSummary();
    }

    DockPanel StepRow(Step step, int index)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        string n = $"Step {index + 1}";

        var grip = Grip(row, index, n);
        DockPanel.SetDock(grip, Dock.Left);
        row.Children.Add(grip);

        // Remove, on the right
        foreach (var (glyph, name, action, enabled) in new (string, string, Action, bool)[]
        {
            ("×", $"Remove {n}", () => _steps.Remove(step), _steps.Count > 1),
        })
        {
            var b = new Button { Content = glyph, Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0),
                BorderBrush = (Brush)FindResource("Line"), IsEnabled = enabled };
            AutomationProperties_SetName(b, name);
            b.Click += (_, _) => { action(); RebuildSteps(); };
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
        }

        var kind = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 10, 0), ItemsSource = KindNames, SelectedIndex = (int)step.Kind };
        AutomationProperties_SetName(kind, $"{n} type");
        kind.SelectionChanged += (_, _) => { step.Kind = (StepKind)kind.SelectedIndex; RebuildSteps(); };
        DockPanel.SetDock(kind, Dock.Left);
        row.Children.Add(kind);

        if (step.Kind is StepKind.Press or StepKind.Hold)
        {
            var key = new KeyCaptureBox { Chord = step.Chord, Margin = new Thickness(0, 0, 10, 0) };
            AutomationProperties_SetName(key, $"{n} key");
            key.ChordChanged += k => { step.Chord = k.Chord; UpdateSummary(); };
            DockPanel.SetDock(key, Dock.Left);
            row.Children.Add(key);
        }
        if (step.Kind is StepKind.Hold or StepKind.Wait)
        {
            var label = new TextBlock { Text = step.Kind == StepKind.Hold ? "FOR" : "", Style = (Style)FindResource("Mono"), FontSize = 12,
                Foreground = (Brush)FindResource("Gray"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var ms = new TextBox { Text = step.Ms.ToString(), Width = 72, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties_SetName(ms, $"{n} milliseconds");
            ms.TextChanged += (_, _) => { if (int.TryParse(ms.Text, out var v) && v >= 0) step.Ms = v; UpdateSummary(); };
            var unit = new TextBlock { Text = "MS", Style = (Style)FindResource("Mono"), FontSize = 12, Foreground = (Brush)FindResource("Gray"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var el in new FrameworkElement[] { label, ms, unit }) { DockPanel.SetDock(el, Dock.Left); row.Children.Add(el); }
        }
        if (step.Kind == StepKind.Type)
        {
            var text = new TextBox { Text = step.Text, MinWidth = 260, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties_SetName(text, $"{n} text to type");
            text.TextChanged += (_, _) => { step.Text = text.Text; UpdateSummary(); };
            DockPanel.SetDock(text, Dock.Left);
            row.Children.Add(text);
        }
        return row;
    }

    void Move(int from, int to)
    {
        var step = _steps[from];
        _steps.RemoveAt(from);
        _steps.Insert(to, step);
    }

    // ---- Drag to reorder ----
    // Each step has a grip of six dots. Drag it (the row follows the pointer and the others slide aside), or
    // focus it and press Up/Down. The dots go pink under the pointer and while dragging.

    DockPanel? _dragRow;
    int _dragFrom, _dragTo;
    double _dragStartY, _pitch;

    FrameworkElement Grip(DockPanel row, int index, string name)
    {
        var grip = new System.Windows.Controls.Primitives.Thumb { Style = (Style)FindResource("GripThumb") };
        AutomationProperties_SetName(grip, $"{name}: drag to reorder, or press Up or Down");

        // The Thumb captures the mouse itself; positions are read against the step list, so the row can move under it.
        grip.DragStarted += (_, _) =>
        {
            if (_steps.Count < 2) { grip.CancelDrag(); return; }
            _dragRow = row;
            _dragFrom = _dragTo = index;
            _dragStartY = Mouse.GetPosition(StepRows).Y;
            _pitch = row.ActualHeight + row.Margin.Bottom;
            Panel.SetZIndex(row, 10);
            row.RenderTransform = new TranslateTransform();
            row.Opacity = 0.9;
        };
        grip.DragDelta += (_, _) =>
        {
            if (_dragRow != row) return;
            double dy = Mouse.GetPosition(StepRows).Y - _dragStartY;   // absolute, not the Thumb's own (moving) coordinates
            ((TranslateTransform)row.RenderTransform).Y = dy;
            int to = Math.Clamp((int)Math.Round(_dragFrom + dy / _pitch), 0, _steps.Count - 1);
            if (to == _dragTo) return;
            _dragTo = to;
            // Slide the rows between the old and new place out of the way.
            for (int i = 0; i < StepRows.Children.Count; i++)
            {
                if (StepRows.Children[i] is not DockPanel other || other == row) continue;
                double shift = _dragFrom < _dragTo && i > _dragFrom && i <= _dragTo ? -_pitch
                             : _dragFrom > _dragTo && i >= _dragTo && i < _dragFrom ? _pitch : 0;
                if (other.RenderTransform is not TranslateTransform t) other.RenderTransform = t = new TranslateTransform();
                t.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(shift, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
                });
            }
        };
        grip.DragCompleted += (_, e) =>
        {
            if (_dragRow != row) return;
            _dragRow = null;
            if (!e.Canceled && _dragTo != _dragFrom) Move(_dragFrom, _dragTo);
            RebuildSteps();   // drop (or snap back if cancelled, e.g. Alt+Tab mid-drag)
        };
        grip.KeyDown += (_, e) =>
        {
            int to = e.Key == Key.Up ? index - 1 : e.Key == Key.Down ? index + 1 : -1;
            if (to < 0 || to >= _steps.Count) return;
            e.Handled = true;
            Move(index, to);
            RebuildSteps();
            if (StepRows.Children[to] is DockPanel moved) moved.Children[0].Focus();   // keep the grip focused
        };
        return grip;
    }

    void OnToggleScript(object sender, RoutedEventArgs e)
    {
        if (!_scriptMode)
        {
            ScriptBox.Text = ToScript(_steps);
            _scriptMode = true;
        }
        else if (FromScript(ScriptBox.Text) is { } steps)
        {
            _steps = steps.Count > 0 ? steps : [new Step()];
            _scriptMode = false;
            RebuildSteps();
        }
        else
        {
            Say("This action uses steps the editor can't show (like latch or release), so it stays as text.", Warn);
            return;
        }
        ShowScriptMode();
        UpdateSummary();
    }

    void ShowScriptMode()
    {
        var steps = _scriptMode ? Visibility.Collapsed : Visibility.Visible;
        var text = _scriptMode ? Visibility.Visible : Visibility.Collapsed;
        StepRows.Visibility = StepButtons.Visibility = steps;
        ScriptBox.Visibility = ScriptHelp.Visibility = text;
        ModeSwitchText.Text = _scriptMode ? "BACK TO STEP EDITOR" : "EDIT AS TEXT (ADVANCED)";
    }

    static string ToScript(List<Step> steps) => string.Join("\n", steps.Select(s => s.Kind switch
    {
        StepKind.Press when s.Chord.Length > 0 => s.Chord,
        StepKind.Hold when s.Chord.Length > 0 => $"hold {s.Chord} {s.Ms}",
        StepKind.Wait => $"wait {s.Ms}",
        StepKind.Type when s.Text.Length > 0 => $"type {s.Text}",
        _ => null,
    }).OfType<string>());   // unfinished steps (no key yet, nothing to type) are left out

    /// <summary>The action as editable steps, or null if it uses something the step editor doesn't show.</summary>
    static List<Step>? FromScript(string action)
    {
        var steps = new List<Step>();
        foreach (var raw in action.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var verb = parts[0].ToLowerInvariant();
            bool Ms(int i, out int ms) { ms = 0; return parts.Length == i + 1 && int.TryParse(parts[i], out ms) && ms >= 0; }

            if (verb == "type") steps.Add(new Step { Kind = StepKind.Type, Text = line[4..].TrimStart() });
            else if (verb == "tap" && parts.Length == 2) steps.Add(new Step { Chord = parts[1].ToLowerInvariant() });
            else if (verb is "tap" or "hold" && parts.Length == 3 && Ms(2, out var held))
                steps.Add(new Step { Kind = StepKind.Hold, Chord = parts[1].ToLowerInvariant(), Ms = held });
            else if (verb == "wait" && Ms(1, out var wait)) steps.Add(new Step { Kind = StepKind.Wait, Ms = wait });
            else if (parts.Length == 1 && !line.StartsWith("//") &&
                     verb is not ("down" or "up" or "latch" or "unlatch" or "release" or "lift" or "resume" or "tap" or "hold" or "wait"))
                steps.Add(new Step { Chord = verb });
            else return null;
        }
        return steps;
    }

    string ActionText => _scriptMode ? ScriptBox.Text.Trim() : ToScript(_steps);

    // ---- 3. Name and category ----

    void OnGameChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RebuildCategories();
        UpdateSummary();
    }

    void SelectGame(string? game) =>
        GameCombo.SelectedItem = GameCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == game) ?? GameCombo.Items[0];

    string? SelectedGame => (string?)((ComboBoxItem?)GameCombo.SelectedItem)?.Tag;

    /// <summary>Suggest the selected game's categories (and ones the user made) as one-click chips.</summary>
    void RebuildCategories()
    {
        CategoryChips.Children.Clear();
        var game = SelectedGame;
        var preset = game is null ? null : _app.Config?.FindPreset(game);
        var categories = (preset?.Commands.Select(c => c.Category) ?? [])
            .Concat(_app.CustomFunctions.Functions.Where(f => f.Game == game).Select(f => f.Category))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = category.ToUpperInvariant() };
            AutomationProperties_SetName(chip, $"Use category {category}");
            chip.Click += (_, _) => CategoryBox.Text = category;
            CategoryChips.Children.Add(chip);
        }
    }

    void OnNameChanged(object sender, TextChangedEventArgs e) => UpdateNameHint();

    void UpdateNameHint()
    {
        NameHint.Text = NameBox.Text.Length == 0 ? $"Leave empty to call it \"{DefaultName()}\"" : "";
        if (!_loading) UpdateSummary();
    }

    string DefaultName()
    {
        var name = _phrases.Count > 0 ? char.ToUpperInvariant(_phrases[0][0]) + _phrases[0][1..] : GestureToggle.IsChecked == true ? "My gesture" : "My command";
        string candidate = name;
        for (int i = 2; NameTaken(candidate); i++) candidate = $"{name} {i}";
        return candidate;
    }

    bool NameTaken(string name) =>
        _app.CustomFunctions.Functions.Any(f => f != _editing && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    string CommandName() => NameBox.Text.Trim() is { Length: > 0 } name ? name : DefaultName();

    // ---- Summary ----

    bool HoldMode => GestureToggle.IsChecked == true && ModeHold.IsChecked == true;
    GestureHand SelectedHand() => HandLeft.IsChecked == true ? GestureHand.Left : HandRight.IsChecked == true ? GestureHand.Right : GestureHand.Either;

    void UpdateSummary()
    {
        if (_loading) return;
        var when = new List<string>();
        if (VoiceToggle.IsChecked == true)
            when.Add(_phrases.Count == 0 ? "say a phrase (add one)" : "say " + string.Join(" or ", _phrases.Select(p => $"\"{p}\"")));
        if (GestureToggle.IsChecked == true)
        {
            var hand = SelectedHand() == GestureHand.Either ? "either hand" : $"your {SelectedHand().ToString().ToLowerInvariant()} hand";
            when.Add(_draftGesture is null ? "make your gesture (record it first)" : $"make your gesture with {hand}");
        }

        string does;
        if (_scriptMode)
            does = $"run {ScriptBox.Text.Split('\n').Count(l => l.Trim().Length > 0)} step(s) written as text";
        else
        {
            var parts = _steps.Select(s => s.Kind switch
            {
                StepKind.Press when s.Chord.Length > 0 => HoldMode ? KeyCaptureBox.Pretty(s.Chord) : $"press {KeyCaptureBox.Pretty(s.Chord)}",
                StepKind.Hold when s.Chord.Length > 0 => $"hold {KeyCaptureBox.Pretty(s.Chord)} for {s.Ms} ms",
                StepKind.Wait => $"wait {s.Ms} ms",
                StepKind.Type when s.Text.Length > 0 => $"type \"{s.Text}\"",
                _ => null,
            }).OfType<string>().ToList();
            does = parts.Count == 0 ? "(nothing yet: record a key in step 2)"
                 : HoldMode ? $"hold {string.Join(" + ", parts)} down until you let go"
                 : string.Join(", then ", parts);
        }

        var game = (GameCombo.SelectedItem as ComboBoxItem)?.Content as string;
        SummaryText.Text = $"\"{CommandName()}\"\n\nWhen you {string.Join(" or ", when)}, SWISH will {does}."
                         + (SelectedGame is null ? "\n\nWorks in every game." : $"\n\nOnly while {game} is selected.");
    }

    // ---- Save / test / delete ----

    CustomFunction? BuildDraft()
    {
        if (VoiceToggle.IsChecked == true && _phrases.Count == 0) { Say("Add at least one phrase in step 1.", Bad); PhraseBox.Focus(); return null; }
        if (GestureToggle.IsChecked == true && _draftGesture is null) { Say("Record the gesture in step 1 first.", Bad); RecordButton.Focus(); return null; }
        var name = CommandName();
        if (NameTaken(name)) { Say($"There's already a command called \"{name}\".", Bad); NameBox.Focus(); return null; }

        var draft = new CustomFunction
        {
            Name = name,
            VoicePhrases = VoiceToggle.IsChecked == true ? _phrases.ToList() : [],
            Gesture = GestureToggle.IsChecked == true ? _draftGesture : null,
            Mode = HoldMode ? TriggerMode.Hold : TriggerMode.Once,
            Action = ActionText,
            Game = SelectedGame,
            Category = CategoryBox.Text.Trim() is { Length: > 0 } cat ? cat : null,
        };
        try { draft.Compile(); }
        catch (FormatException ex) { Say(ex.Message, Bad); return null; }
        return draft;
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (BuildDraft() is not { } draft) return;
        var target = _editing;
        if (target is null)
        {
            _app.CustomFunctions.Functions.Add(draft);
        }
        else
        {
            target.Name = draft.Name;
            target.VoicePhrases = draft.VoicePhrases;
            target.Gesture = draft.Gesture;
            target.Mode = draft.Mode;
            target.Action = draft.Action;
            target.Game = draft.Game;
            target.Category = draft.Category;
        }
        _app.CustomFunctions.Save();
        // Re-open as an edit of the saved command, so further changes update it.
        var next = new CommandEditorPage(target ?? draft);
        _shell?.Navigate(next);
        next.Say(draft.Gesture is null ? "Saved." : "Saved. Make the gesture to try it.", Good);
    }

    async void OnTest(object sender, RoutedEventArgs e)
    {
        var fn = new CustomFunction { Action = ActionText, Mode = HoldMode ? TriggerMode.Hold : TriggerMode.Once,
                                      Gesture = GestureToggle.IsChecked == true ? _draftGesture : null };
        IReadOnlyList<KeySender.Step> start, end;
        try { (start, end) = fn.Compile(); }
        catch (FormatException ex) { Say(ex.Message, Bad); return; }

        for (int i = 3; i > 0; i--)
        {
            Say($"Running in {i}… switch to where it should go.", Warn);
            await Task.Delay(1000);
        }
        KeySender.Run(start);
        if (end.Count > 0)
        {
            await Task.Delay(1000);   // hold mode: keys held for a second
            KeySender.Run(end);
        }
        Say("Ran the action.", Good);
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        if (!SwishDialog.Confirm(Window.GetWindow(this), "DELETE COMMAND",
                                 $"Delete \"{_editing.Name}\"? Its gesture and phrases stop working straight away.",
                                 confirm: "DELETE", cancel: "KEEP IT", danger: true)) return;
        _app.CustomFunctions.Functions.Remove(_editing);
        _app.CustomFunctions.Save();
        _shell?.Navigate(new ControlsPage());
    }

    // ---- Helpers ----

    /// <summary>Everything the user can change, to tell whether leaving would lose edits.</summary>
    string Snapshot() => string.Join("\u001f", CommandName(), SelectedGame, CategoryBox.Text, string.Join(",", _phrases),
        VoiceToggle.IsChecked, GestureToggle.IsChecked, _draftGesture?.GetHashCode(), HoldMode, SelectedHand(), ActionText);

    static readonly Brush Good = Frozen("#7BE09A"), Warn = Frozen("#E8C46A"), Bad = Frozen("#FF6B6B");

    void Say(string text, Brush color)
    {
        MessageText.Text = text;
        MessageText.Foreground = color;
    }

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static void AutomationProperties_SetName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);
}
