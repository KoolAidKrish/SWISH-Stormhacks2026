using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Swish.App.Views;

/// <summary>"CALIBRATION COMPLETE!" over the live feed; Continue (button, Enter or "continue") → controls.</summary>
public partial class CalibrationCompletePage : UserControl, ISwishPage
{
    App? _app;
    ShellWindow? _shell;

    readonly Func<UserControl>? _exitTo;

    /// <param name="exitTo">The screen calibration was started from (e.g. Settings); default: the controls screen.</param>
    public CalibrationCompletePage(string summary, Func<UserControl>? exitTo = null)
    {
        _exitTo = exitTo;
        InitializeComponent();
        SummaryText.Text = summary;
    }

    public IInputElement? DefaultFocus => ContinueButton;

    public void OnShown(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        if (app.Voice is { } v) { v.Suspended = true; v.SetExtraHints(["continue"]); v.Heard += OnHeard; }
    }

    public void OnHidden()
    {
        if (_app?.Voice is { } v) { v.Heard -= OnHeard; v.SetExtraHints([]); v.Suspended = false; }
    }

    void OnHeard(string transcript)
    {
        if (Regex.IsMatch(transcript, @"\b(continue|next|done)\b", RegexOptions.IgnoreCase)) Dispatcher.BeginInvoke(Continue);
    }

    void OnContinue(object sender, RoutedEventArgs e) => Continue();
    void Continue() => _shell?.Navigate(_exitTo?.Invoke() ?? new ControlsPage());
}
