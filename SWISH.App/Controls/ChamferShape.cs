using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Swish.App.Controls;

/// <summary>Which corner is cut off.</summary>
public enum ChamferCorner { TopLeft, TopRight, BottomRight, BottomLeft }

/// <summary>
/// A rectangle with one corner clipped at 45°, the SWISH section-bar and card shape. Sizes itself to its
/// layout slot, so it's used as the background layer of templates (fill and/or outline via Fill/Stroke).
/// </summary>
public sealed class ChamferShape : Shape
{
    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferShape),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerProperty = DependencyProperty.Register(
        nameof(Corner), typeof(ChamferCorner), typeof(ChamferShape),
        new FrameworkPropertyMetadata(ChamferCorner.BottomRight, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    public ChamferCorner Corner { get => (ChamferCorner)GetValue(CornerProperty); set => SetValue(CornerProperty, value); }

    public ChamferShape() => Stretch = Stretch.None;

    protected override Size MeasureOverride(Size constraint) => new(0, 0);   // fills whatever slot it's given

    protected override Geometry DefiningGeometry
    {
        get
        {
            double t = StrokeThickness / 2, w = Math.Max(0, ActualWidth - t), h = Math.Max(0, ActualHeight - t);
            double c = Math.Min(Chamfer, Math.Min(w, h) / 2);
            Point[] p = Corner switch
            {
                ChamferCorner.TopLeft => [new(t + c, t), new(w, t), new(w, h), new(t, h), new(t, t + c)],
                ChamferCorner.TopRight => [new(t, t), new(w - c, t), new(w, t + c), new(w, h), new(t, h)],
                ChamferCorner.BottomLeft => [new(t, t), new(w, t), new(w, h), new(t + c, h), new(t, h - c)],
                _ => [new(t, t), new(w, t), new(w, h - c), new(w - c, h), new(t, h)],
            };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p[0], isFilled: true, isClosed: true);
                ctx.PolyLineTo(p[1..], isStroked: true, isSmoothJoin: false);
            }
            g.Freeze();
            return g;
        }
    }
}
