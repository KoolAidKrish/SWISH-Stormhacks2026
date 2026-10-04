using System.Windows;

namespace Swish.App.Controls;

/// <summary>
/// Puts a <c>CardButton</c> into its hovered look (lifted, pink border, blue fill, shadow) without the pointer,
/// e.g. the tutorial walking through its cards one by one.
/// </summary>
public static class CardHighlight
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(CardHighlight), new PropertyMetadata(false));

    public static bool GetIsOn(DependencyObject d) => (bool)d.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject d, bool value) => d.SetValue(IsOnProperty, value);
}
