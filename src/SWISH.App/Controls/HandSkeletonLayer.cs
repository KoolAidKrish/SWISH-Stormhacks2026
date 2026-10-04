using System.Windows;
using System.Windows.Media;
using HandGestureRecognition;
using Point2f = OpenCvSharp.Point2f;

namespace Swish.App.Controls;

/// <summary>
/// Draws tracked hands the SWISH way: thin white bones and cyan joints, in camera-pixel coordinates
/// (it sits inside the same scaled frame as the picture). Vector drawing, so it stays crisp at any size.
/// </summary>
public sealed class HandSkeletonLayer : FrameworkElement
{
    // MediaPipe hand connections (same as the engine's debug drawing).
    static readonly (int A, int B)[] Bones =
    [
        (0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8), (5, 9), (9, 10), (10, 11), (11, 12),
        (9, 13), (13, 14), (14, 15), (15, 16), (13, 17), (17, 18), (18, 19), (19, 20), (0, 17),
    ];

    static readonly Pen BonePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 2.2));
    static readonly Pen JointOutline = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(160, 10, 30, 40)), 1.2));
    static readonly Brush Joint = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2EC4F0")));

    HandsFrame? _hands;

    public HandsFrame? Hands
    {
        get => _hands;
        set
        {
            if (ReferenceEquals(value, _hands)) return;
            _hands = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_hands is null) return;
        Draw(dc, _hands.Right);
        Draw(dc, _hands.Left);
    }

    static void Draw(DrawingContext dc, Point2f[]? lm)
    {
        if (lm is null || lm.Length < 21) return;
        foreach (var (a, b) in Bones)
            dc.DrawLine(BonePen, new Point(lm[a].X, lm[a].Y), new Point(lm[b].X, lm[b].Y));
        foreach (var p in lm)
            dc.DrawEllipse(Joint, JointOutline, new Point(p.X, p.Y), 4.2, 4.2);
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
