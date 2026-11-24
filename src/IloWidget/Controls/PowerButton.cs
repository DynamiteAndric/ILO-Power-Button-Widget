using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IloWidget.Controls;

/// <summary>
/// Big round power button. It must be pressed and held until the ring is fully charged,
/// so a stray click cannot start the server.
/// </summary>
public sealed class PowerButton : FrameworkElement
{
    private static readonly Duration HoldTime = TimeSpan.FromSeconds(1.0);

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(PowerButton),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChargeProperty = DependencyProperty.Register(
        nameof(Charge), typeof(double), typeof(PowerButton),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True while the server is on: the glyph is filled-in bright, without being clickable.</summary>
    public static readonly DependencyProperty IsLitProperty = DependencyProperty.Register(
        nameof(IsLit), typeof(bool), typeof(PowerButton),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Charge { get => (double)GetValue(ChargeProperty); set => SetValue(ChargeProperty, value); }
    public bool IsLit { get => (bool)GetValue(IsLitProperty); set => SetValue(IsLitProperty, value); }

    /// <summary>Raised once when the button has been held long enough.</summary>
    public event EventHandler? Activated;

    /// <summary>Raised when a new hold begins (before it can complete).</summary>
    public event EventHandler? HoldStarted;

    private int _holdId;
    private bool _holding;

    static PowerButton()
    {
        FocusableProperty.OverrideMetadata(typeof(PowerButton), new FrameworkPropertyMetadata(true));
        IsEnabledProperty.OverrideMetadata(typeof(PowerButton),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((PowerButton)d).OnEnabledChanged()));
    }

    public PowerButton()
    {
        Cursor = Cursors.Hand;
    }

    private void OnEnabledChanged()
    {
        if (!IsEnabled)
            CancelHold();
        Cursor = IsEnabled ? Cursors.Hand : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true; // never start a window drag from the button
        if (!IsEnabled)
            return;
        Focus();
        CaptureMouse();
        BeginHold();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        CancelHold();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => CancelHold();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter && IsEnabled)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                BeginHold();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            CancelHold();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => CancelHold();

    private void BeginHold()
    {
        if (_holding)
            return;
        _holding = true;
        var id = ++_holdId;
        HoldStarted?.Invoke(this, EventArgs.Empty);
        var anim = new DoubleAnimation(0, 1, HoldTime);
        anim.Completed += (_, _) =>
        {
            if (!_holding || id != _holdId)
                return;
            _holding = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            BeginAnimation(ChargeProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)));
            Activated?.Invoke(this, EventArgs.Empty);
        };
        BeginAnimation(ChargeProperty, anim);
    }

    private void CancelHold()
    {
        if (!_holding)
            return;
        _holding = false;
        _holdId++;
        BeginAnimation(ChargeProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var r = Math.Min(w, h) / 2 - 4;
        if (r < 20)
            return;
        var c = new Point(w / 2, h / 2);
        var accent = Accent;
        var live = IsEnabled || IsLit;

        dc.PushOpacity(live ? 1.0 : 0.45);

        // hit-test area + inner disc
        var disc = new RadialGradientBrush(Color.FromArgb(0xE6, 0x10, 0x30, 0x48), Color.FromArgb(0xE6, 0x04, 0x0C, 0x14));
        disc.Freeze();
        dc.DrawEllipse(Brushes.Transparent, null, c, r, r);
        dc.DrawEllipse(disc, new Pen(accent, 1.5), c, r - 16, r - 16);

        // outer ring and tick ring
        dc.DrawEllipse(null, new Pen(accent, 2), c, r, r);
        var ticks = new Pen(accent, 5) { DashStyle = new DashStyle(new double[] { 0.35, 1.6 }, 0) };
        dc.PushOpacity(0.35);
        dc.DrawEllipse(null, ticks, c, r - 8, r - 8);
        dc.Pop();

        // charge arc (grows clockwise from 12 o'clock while held)
        var charge = Math.Clamp(Charge, 0, 1);
        if (charge > 0.001)
        {
            var pen = new Pen(accent, 6) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            if (charge >= 0.999)
                dc.DrawEllipse(null, pen, c, r - 8, r - 8);
            else
                dc.DrawGeometry(null, pen, Arc(c, r - 8, -90, charge * 360));
        }

        // power glyph: open ring with a bar through the gap
        var g = r * 0.34;
        var glyphPen = new Pen(accent, Math.Max(4, r * 0.085)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (IsLit || charge > 0.001)
        {
            dc.PushOpacity(IsLit ? 0.22 : 0.22 * charge);
            dc.DrawEllipse(accent, null, c, r - 18, r - 18);
            dc.Pop();
        }
        dc.DrawGeometry(null, glyphPen, Arc(c, g, -90 + 32, 360 - 64));
        dc.DrawLine(glyphPen, new Point(c.X, c.Y - g * 1.22), new Point(c.X, c.Y - g * 0.18));

        dc.Pop();
    }

    private static Geometry Arc(Point c, double radius, double startDeg, double sweepDeg)
    {
        Point At(double deg)
        {
            var a = deg * Math.PI / 180;
            return new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(startDeg), false, false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }
}
