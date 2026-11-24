using System.Windows;
using System.Windows.Media;

namespace IloWidget.Controls;

/// <summary>Segmented energy-shield style meter.</summary>
public sealed class ShieldBar : FrameworkElement
{
    private const int Segments = 14;
    private const double Gap = 3;
    private const double Skew = 6;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(ShieldBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(ShieldBar),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= Skew || h <= 0)
            return;

        var segWidth = (w - Skew - Gap * (Segments - 1)) / Segments;
        var lit = (int)Math.Round(Math.Clamp(Level, 0, 1) * Segments);

        for (var i = 0; i < Segments; i++)
        {
            var x = i * (segWidth + Gap);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(x + Skew, 0), true, true);
                g.LineTo(new Point(x + Skew + segWidth, 0), true, false);
                g.LineTo(new Point(x + segWidth, h), true, false);
                g.LineTo(new Point(x, h), true, false);
            }
            geo.Freeze();

            if (i < lit)
            {
                dc.DrawGeometry(Accent, null, geo);
            }
            else
            {
                dc.PushOpacity(0.18);
                dc.DrawGeometry(Accent, null, geo);
                dc.Pop();
            }
        }
    }
}
