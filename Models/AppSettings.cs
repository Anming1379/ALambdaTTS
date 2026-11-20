using System.IO;
using System.Windows;

namespace ALambdaTTS.Services;

/// <summary>主题模式。.NET 10 自带 System.Windows.ThemeMode，这里换名避免撞车。</summary>
public enum AppThemeMode
{
    Auto,
    Light,
    Dark
}

/// <summary>应用设置，字段保持扁平，方便 System.Text.Json 持久化。</summary>
public sealed class AppSettings
{
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Auto;

    /// <summary>下载目录。为空时使用 系统音乐文件夹\ATTS。</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>最后使用的音色 ShortName。</summary>
    public string? VoiceShortName { get; set; }

    /// <summary>语速百分比，-50 ~ +100。</summary>
    public int Rate { get; set; }

    /// <summary>语调偏移（Hz），-50 ~ +50。</summary>
    public int Pitch { get; set; }

    /// <summary>音量百分比，0 ~ 100（合成时映射成 -100% ~ +0%）。</summary>
    public int Volume { get; set; } = 100;

    public double WindowWidth { get; set; } = 600;

    public double WindowHeight { get; set; } = 760;

    public string ResolveDownloadFolder()
    {
        if (!string.IsNullOrWhiteSpace(DownloadFolder))
        {
            return DownloadFolder.Trim();
        }

        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(music))
        {
            music = Path.Combine(AppPaths.Root, "Audio");
        }

        return Path.Combine(music, "ATTS");
    }
}
