using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Swish.App;

/// <summary>A screen shown in the shell. Pages get told when they appear/disappear so they can start and stop work.</summary>
public interface ISwishPage
{
    /// <summary>Calibration screens cover the whole primary monitor (the path is in screen space).</summary>
    bool Fullscreen => false;
    /// <summary>Control that gets keyboard focus first (the primary action), so Enter works straight away.</summary>
    IInputElement? DefaultFocus => null;
    void OnShown(App app, ShellWindow shell) { }
    void OnHidden() { }
}

/// <summary>
/// The main SWISH window: hosts one page at a time (Home → How to calibrate → Countdown → Calibrating →
/// Complete → Controls, plus Settings). Closing hides it to the tray; the engines keep running.
/// </summary>
public partial class ShellWindow : Window
{
    readonly App _app;
    ISwishPage? _current;
    bool _isFullscreen;
    (WindowState State, WindowStyle Style, ResizeMode Resize, Rect Bounds, bool Topmost) _windowed;

    public ShellWindow(App app)
    {
        InitializeComponent();
        _app = app;
        StateChanged += (_, _) => FitMaximized();
        // The title bar's "PAUSED" chip follows the pause state, however it was changed (menu, tray, voice).
        var poll = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, _) => PausedChip.Visibility = _app.IsPaused ? Visibility.Visible : Visibility.Collapsed, Dispatcher);
        IsVisibleChanged += (_, _) => { if (IsVisible) poll.Start(); else poll.Stop(); };
    }

    // ---- Title bar ----

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();   // hides to the tray (OnClosing)

    /// <summary>A chromeless maximized window hangs over the screen edge by the resize frame; inset by that much.</summary>
    void FitMaximized()
    {
        bool max = WindowState == WindowState.Maximized && !_isFullscreen;
        MaxButton.Content = max ? "" : "";   // Segoe MDL2: restore / maximize
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
        if (!max) { Root.Margin = new Thickness(0); return; }
        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double frame = (GetSystemMetrics(32 /* SM_CXSIZEFRAME */) + GetSystemMetrics(92 /* SM_CXPADDEDBORDER */)) / scale;
        Root.Margin = new Thickness(frame);
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    public void Navigate(UserControl page)
    {
        _current?.OnHidden();
        Host.Content = page;
        _current = page as ISwishPage;
        SetFullscreen(_current?.Fullscreen == true);
        _current?.OnShown(_app, this);

        // Put keyboard focus on the page's main action once it's laid out.
        Dispatcher.BeginInvoke(() =>
        {
            if (_current?.DefaultFocus is { } target) Keyboard.Focus(target);
            else page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Re-shows the current page (e.g. after custom functions changed).</summary>
    public void RefreshPage()
    {
        if (Host.Content is UserControl page && page is ISwishPage p)
        {
            p.OnHidden();
            p.OnShown(_app, this);
        }
    }

    void SetFullscreen(bool on)
    {
        if (on == _isFullscreen) return;
        TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            _windowed = (WindowState, WindowStyle, ResizeMode, new Rect(Left, Top, Width, Height), Topmost);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Primary monitor, where the hand mouse works (the calibration maps to it).
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true;
        }
        else
        {
            WindowStyle = _windowed.Style;
            ResizeMode = _windowed.Resize;
            Topmost = _windowed.Topmost;
            Left = _windowed.Bounds.Left;
            Top = _windowed.Bounds.Top;
            Width = _windowed.Bounds.Width;
            Height = _windowed.Bounds.Height;
            WindowState = _windowed.State;
        }
        _isFullscreen = on;
        FitMaximized();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true;   // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _current?.OnHidden();
        base.OnClosed(e);
    }
}
