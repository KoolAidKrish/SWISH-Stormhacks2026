using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Swish.App.Controls;

/// <summary>
/// SWISH's own message box: a chamfered dark panel with a pink outline over a dimmed window, instead of
/// the Windows one. Esc or the first button cancels; the safe choice has keyboard focus to start with.
/// </summary>
public sealed class SwishDialog : Window
{
    bool _confirmed;

    SwishDialog(Window? owner, string title, string message, string? confirm, string cancel, bool danger)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("MonoFont");
        Title = title;
        if (owner is { IsVisible: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(30, 26, 30, 24), MaxWidth = 440 };
        body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading"), FontSize = 28, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("Mono"), FontSize = 13.5, LineHeight = 21,
                                          Foreground = (Brush)FindResource("Gray") });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancelButton = new Button { Content = cancel, IsCancel = true, Margin = new Thickness(0, 0, confirm is null ? 0 : 12, 0), MinWidth = 110 };
        cancelButton.Click += (_, _) => Close();
        buttons.Children.Add(cancelButton);
        if (confirm is not null)
        {
            var confirmButton = new Button { Content = confirm, MinWidth = 110, Style = (Style)FindResource("PrimaryButton") };
            if (danger)
            {
                confirmButton.Background = (Brush)FindResource("Danger");
                confirmButton.BorderBrush = (Brush)FindResource("Danger");
                confirmButton.Foreground = (Brush)FindResource("Bg");
            }
            confirmButton.Click += (_, _) => { _confirmed = true; Close(); };
            buttons.Children.Add(confirmButton);
        }
        body.Children.Add(buttons);

        var panel = new Grid();
        panel.Children.Add(new ChamferShape { Fill = (Brush)FindResource("Bg2"), Stroke = (Brush)FindResource("Pink"), StrokeThickness = 2,
                                              Chamfer = 18, Corner = ChamferCorner.BottomRight });
        panel.Children.Add(new System.Windows.Shapes.Rectangle { Width = 6, HorizontalAlignment = HorizontalAlignment.Left,
                                                                 Fill = (Brush)FindResource("Pink") });
        panel.Children.Add(body);
        Content = panel;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => cancelButton.Focus();   // the safe choice first: Enter won't throw work away
    }

    /// <summary>Asks a yes/no question. Returns true only if <paramref name="confirm"/> was chosen.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirm, string cancel, bool danger = false)
    {
        var dialog = new SwishDialog(owner, title, message, confirm, cancel, danger);
        using var dim = Dim(owner);
        dialog.ShowDialog();
        return dialog._confirmed;
    }

    /// <summary>A message with a single OK button.</summary>
    public static void Inform(Window? owner, string title, string message)
    {
        var dialog = new SwishDialog(owner, title, message, null, "OK", danger: false);
        using var dim = Dim(owner);
        dialog.ShowDialog();
    }

    /// <summary>Darkens the owner window's content while the dialog is up.</summary>
    static IDisposable Dim(Window? owner)
    {
        if (owner?.Content is not UIElement content) return new Undo(() => { });
        double before = content.Opacity;
        content.Opacity = 0.45;
        return new Undo(() => content.Opacity = before);
    }

    sealed class Undo(Action undo) : IDisposable { public void Dispose() => undo(); }
}
