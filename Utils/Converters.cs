using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ALambdaTTS.Utils;

/// <summary>旋钮数值显示：语速带正负号和百分号，音量只显示数字。</summary>
public sealed class KnobValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double number)
        {
            return string.Empty;
        }

        var mode = parameter as string;
        return mode switch
        {
            "rate" => $"{number:+0;-0;0}%",
            "pitch" => $"{number:+0;-0;0}Hz",
            "volume" => $"{number:0}",
            _ => number.ToString("0.##", culture),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return double.TryParse(value?.ToString(), NumberStyles.Any, culture, out var parsed) ? parsed : 0d;
    }
}

/// <summary>按模型里给的资源键取一个画刷（例如卡片小字在失败时变警示色）。</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string;
        if (!string.IsNullOrEmpty(key) && Application.Current?.TryFindResource(key) is System.Windows.Media.Brush brush)
        {
            return brush;
        }

        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
