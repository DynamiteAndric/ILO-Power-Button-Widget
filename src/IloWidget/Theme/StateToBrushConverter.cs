using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using IloWidget.Models;

namespace IloWidget.Theme;

/// <summary>HUD accent color for each state.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public static readonly Brush Cyan = Frozen(0x4F, 0xD8, 0xFF);
    public static readonly Brush Bright = Frozen(0x8C, 0xEC, 0xFF);
    public static readonly Brush Dim = Frozen(0x3F, 0x86, 0xB0);
    public static readonly Brush Amber = Frozen(0xFF, 0xB2, 0x38);
    public static readonly Brush Red = Frozen(0xFF, 0x4B, 0x4B);

    /// <summary>Returns a Brush, or a Color when the target is a Color (glow effects).</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var brush = value switch
        {
            HudState.Online => Bright,
            HudState.Booting => Cyan,
            HudState.Offline or HudState.Stopping => Amber,
            HudState.Error => Red,
            _ => Dim,
        };
        return targetType == typeof(Color) ? ((SolidColorBrush)brush).Color : brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
