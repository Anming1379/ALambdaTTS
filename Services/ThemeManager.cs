using System.Windows;
using Microsoft.Win32;

namespace ALambdaTTS.Services;

/// <summary>
/// 深浅主题。色板整本替换 Application.Resources[0]，XAML 里全部用
/// DynamicResource，切主题时整窗即时换肤。
/// </summary>
public static class ThemeManager
{
    private static bool _watching;

    public static AppThemeMode Mode { get; private set; } = AppThemeMode.Auto;

    public static bool IsDark { get; private set; }

    /// <summary>主题变化（包括跟随系统时的自动切换）。</summary>
    public static event EventHandler? Changed;

    public static bool SystemUsesDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(AppThemeMode mode)
    {
        Mode = mode;
        IsDark = mode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            _ => SystemUsesDark(),
        };

        var app = Application.Current;
        if (app is not null)
        {
            var dictionaries = app.Resources.MergedDictionaries;
            var tokens = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ALambdaTTS;component/Themes/Tokens.{(IsDark ? "Dark" : "Light")}.xaml",
                    UriKind.Absolute),
            };

            var index = -1;
            for (var i = 0; i < dictionaries.Count; i++)
            {
                var source = dictionaries[i].Source?.OriginalString;
                if (source is not null && source.Contains("Tokens.", StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                dictionaries[index] = tokens;
            }
            else
            {
                dictionaries.Insert(0, tokens);
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>跟随系统主题：注册表里的桌面/应用主题变化时重算一次。</summary>
    public static void WatchSystemChanges()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
                    && Mode == AppThemeMode.Auto)
                {
                    Apply(AppThemeMode.Auto);
                }
            };
        }
        catch
        {
            // 某些受限环境拿不到系统事件，忽略即可
        }
    }
}
