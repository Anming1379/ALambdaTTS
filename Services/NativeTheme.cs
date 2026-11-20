using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ALambdaTTS.Services;

/// <summary>
/// 让 Windows 11 原生标题栏跟随应用主题：深浅标志 + 标题栏底色、边框色、文字色。
/// 只改颜色，不动窗口圆角（保持系统默认）。
/// </summary>
public static class NativeTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = ThemeManager.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        // 这三个颜色属性要 Windows 11；Windows 10 上只保留深色标志
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            SetColor(handle, DwmwaCaptionColor, "Surface");
            SetColor(handle, DwmwaBorderColor, "Border");
            SetColor(handle, DwmwaTextColor, "Text");
        }
    }

    private static void SetColor(IntPtr handle, int attribute, string brushKey)
    {
        if (Application.Current?.TryFindResource(brushKey) is not SolidColorBrush solid)
        {
            return;
        }

        var color = solid.Color;
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
