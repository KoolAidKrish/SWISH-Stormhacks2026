using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandGestureRecognition;
using HandGestureRecognition.Custom;
using VoiceKeys;

namespace Swish.App;

/// <summary>
/// Create and edit custom functions: record a gesture (the same pose from several angles), give it
/// voice phrases, and write the action it runs. Shows the camera with a recording guide, and live
/// recognition of the saved gestures.
/// </summary>
public partial class CustomFunctionsWindow : Window
{
    static readonly Brush Good = Frozen("#8FD694"), Warn = Frozen("#E8C46A"), Bad = Frozen("#F07178");

    readonly App _app;
    readonly CustomFunctionManager _manager;
    readonly ObservableCollection<CustomFunction> _items;
    readonly DispatcherTimer _timer;
    WriteableBitmap? _bitmap;

    CustomFunction? _current;          // null = a new, unsaved function
    CustomGesture? _draftGesture;      // the gesture as edited (recorded but not yet saved)
    HandsFrame? _latest;

    public CustomFunctionsWindow(App app, CustomFunctionManager manager)
    {
        InitializeComponent();
        _app = app;
        _manager = manager;
        _items = new ObservableCollection<CustomFunction>(manager.Functions);
        FunctionList.ItemsSource = _items;

        _app.Gestures.HandsUpdated += OnHands;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Render, (_, _) => OnTick(), Dispatcher);
        Loaded += (_, _) => { _app.ClaimPreview(this); _timer.Start(); };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _app.Gestures.HandsUpdated -= OnHands;
            _app.ReleasePreview(this);
        };
        HandCombo.SelectionChanged += (_, _) => ShowGestureStatus();

        if (_items.Count > 0) FunctionList.SelectedIndex = 0; else LoadEditor(null);
    }

    // ---- List ----

    void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (FunctionList.SelectedItem is CustomFunction fn) LoadEditor(fn);
    }

    void OnNew(object sender, RoutedEventArgs e)
    {
        FunctionList.SelectedItem = null;
        LoadEditor(null);
        NameBox.Focus();
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current is null) { LoadEditor(null); return; }
        if (MessageBox.Show(this, $"Delete \"{_current.Name}\"?", "SWISH", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _manager.Functions.Remove(_current);
        _manager.Save();
        _items.Remove(_current);
        LoadEditor(null);
        Say($"Deleted.", Good);
    }

    void LoadEditor(CustomFunction? fn)
    {
        _current = fn;
        _draftGesture = fn?.Gesture;
        NameBox.Text = fn?.Name ?? "";
        PhrasesBox.Text = fn is null ? "" : string.Join(", ", fn.VoicePhrases);
        ActionBox.Text = fn?.Action ?? "";
        ModeCombo.SelectedIndex = fn?.Mode == TriggerMode.Hold ? 1 : 0;
        HandCombo.SelectedIndex = (fn?.Gesture?.Hand ?? GestureHand.Either) switch
        {
            GestureHand.Right => 0, GestureHand.Left => 1, _ => 2,
        };
        DeleteButton.IsEnabled = fn is not null;
        MessageText.Text = fn is null ? "New function: name it, add a gesture and/or voice phrases, and an action." : "";
        MessageText.Foreground = Brushes.Gray;
        ShowGestureStatus();
    }

    // ---- Save / test ----

    void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Say("Give it a name first.", Bad); return; }
        if (_manager.Functions.Any(f => f != _current && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { Say($"There's already a function called \"{name}\".", Bad); return; }

        var draft = new CustomFunction
        {
            Name = name,
            VoicePhrases = PhrasesBox.Text.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList(),
            Action = ActionBox.Text.Trim(),
            Mode = SelectedMode(),
            Gesture = _draftGesture,
        };
        if (draft.Gesture is null && draft.VoicePhrases.Count == 0)
        { Say("Add a gesture or at least one voice phrase, or it can never run.", Bad); return; }
        try { draft.Compile(); }
        catch (FormatException ex) { Say(ex.Message, Bad); return; }

        if (_current is null)
        {
            _manager.Functions.Add(draft);
            _items.Add(draft);
            _current = draft;
        }
        else
        {
            _current.Name = draft.Name;
            _current.VoicePhrases = draft.VoicePhrases;
            _current.Action = draft.Action;
            _current.Mode = draft.Mode;
            _current.Gesture = draft.Gesture;
            FunctionList.Items.Refresh(); // same object, new name/summary
        }
        _manager.Save();
        FunctionList.SelectedItem = _current;
        DeleteButton.IsEnabled = true;
        Say($"Saved. {(_current.Gesture is null ? "" : "Make the gesture to try it.")}", Good);
    }

    async void OnTest(object sender, RoutedEventArgs e)
    {
        var fn = new CustomFunction { Action = ActionBox.Text, Mode = SelectedMode(), Gesture = _draftGesture };
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
            await Task.Delay(1000);   // "hold" mode: keys held for a second
            KeySender.Run(end);
        }
        Say("Ran the action.", Good);
    }

    // ---- Recording (guided, in its own window) ----

    void OnRecord(object sender, RoutedEventArgs e)
    {
        var others = _manager.Functions
            .Where(f => f != _current && f.Gesture is not null)
            .Select(f => f.Gesture!)
            .ToList();
        var recorder = new GestureRecorderWindow(_app, NameBox.Text.Trim(), SelectedHand(), others) { Owner = this };
        if (recorder.ShowDialog() == true && recorder.Result is { } gesture)
        {
            _draftGesture = gesture;
            ShowGestureStatus();
            Say("Recorded. Click Save to keep it.", Good);
        }
    }

    /// <summary>Engine thread: keep the latest frame for the live readout.</summary>
    void OnHands(HandsFrame frame) => Volatile.Write(ref _latest, frame);

    // ---- Refresh ----

    void OnTick()
    {
        BlitPreview();

        var f = Volatile.Read(ref _latest);
        LiveText.Text = f is null ? "Waiting for the camera…" :
            $"Right hand: {Describe(f.Right, f.RightMatch, f.RightConfidence)}\nLeft hand:  {Describe(f.Left, f.LeftMatch, f.LeftConfidence)}";
    }

    static string Describe(object? hand, string? match, double confidence) =>
        hand is null ? "not in view" : match is null ? "no custom gesture" : $"{match} ({confidence:P0})";

    void BlitPreview()
    {
        if (!_app.OwnsPreview(this)) return;
        using var frame = _app.Gestures.TakePreview();
        if (frame is null) return;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
            PreviewImage.Source = _bitmap;
        }
        int stride = (int)frame.Step();
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, stride * frame.Height, stride);
    }

    void ShowGestureStatus()
    {
        RemoveGestureButton.IsEnabled = _draftGesture is not null;
        if (_draftGesture is null)
        {
            GestureStatus.Text = "No gesture: voice only. Pick a hand and record one to add it.";
            return;
        }
        var g = _draftGesture;
        string text = $"{g.Samples.Count} samples, {g.Hand.ToString().ToLowerInvariant()} hand, match distance {g.Threshold:F2}";
        if (SelectedHand() != g.Hand) text += $"  ·  hand changed to {SelectedHand()}: record again to apply";
        if (g != _current?.Gesture) text += "  ·  not saved yet";
        GestureStatus.Text = text;
    }

    void OnRemoveGesture(object sender, RoutedEventArgs e)
    {
        _draftGesture = null;
        ShowGestureStatus();
        Say("Gesture removed. Save to keep the change.", Warn);
    }

    // ---- Helpers ----

    GestureHand SelectedHand() => Enum.Parse<GestureHand>((string)((ComboBoxItem)HandCombo.SelectedItem).Tag);
    TriggerMode SelectedMode() => Enum.Parse<TriggerMode>((string)((ComboBoxItem)ModeCombo.SelectedItem).Tag);

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
}
