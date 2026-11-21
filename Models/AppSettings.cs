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

    /// <summary>音色面板上次的性别筛选：all / female / male。</summary>
    public string? VoiceFilterGender { get; set; }

    /// <summary>音色面板上次的语言大类代码（zh、en ...），空表示全部语言。</summary>
    public string? VoiceFilterLanguage { get; set; }

    /// <summary>语速百分比，-50 ~ +100。</summary>
    public int Rate { get; set; }

    /// <summary>语调偏移（Hz），-50 ~ +50。</summary>
    public int Pitch { get; set; }

    /// <summary>音量百分比，0 ~ 100（合成时映射成 -100% ~ +0%）。</summary>
    public int Volume { get; set; } = 100;

    // ---------- 局域网模式 ----------

    /// <summary>启动时是否自动开启局域网监听。</summary>
    public bool LanEnabled { get; set; }

    /// <summary>本机在局域网里显示的名字，空则用计算机名。</summary>
    public string? LanDeviceName { get; set; }

    /// <summary>本机监听端口。</summary>
    public int LanPort { get; set; } = 45790;

    /// <summary>配对码：两边一致才能配对。</summary>
    public string? LanPairCode { get; set; }

    /// <summary>上次配对的对端名字（界面上显示）。</summary>
    public string? LanPeerName { get; set; }

    /// <summary>上次配对的对端地址，用来重连。</summary>
    public string? LanPeerHost { get; set; }

    public int LanPeerPort { get; set; } = 45790;

    /// <summary>启动后自动连接上次的对端。</summary>
    public bool LanAutoConnect { get; set; } = true;

    public double WindowWidth { get; set; } = 600;

    public double WindowHeight { get; set; } = 760;

    /// <summary>默认下载目录：系统音乐文件夹下的 ATTS。每次现算，跟着系统设置走。</summary>
    public static string DefaultDownloadFolder
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(music))
            {
                music = Path.Combine(AppPaths.Root, "Audio");
            }

            return Path.Combine(music, "ATTS");
        }
    }

    public string ResolveDownloadFolder()
    {
        if (!string.IsNullOrWhiteSpace(DownloadFolder))
        {
            return DownloadFolder.Trim();
        }

        return DefaultDownloadFolder;
    }

    /// <summary>
    /// 路径是否就是默认目录。是的话设置里存 null，别把绝对路径焊死——
    /// 否则以后用户把「音乐」挪到别的盘，程序还往老地方存。
    /// </summary>
    public static bool IsDefaultDownloadFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            var fallback = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultDownloadFolder));
            return string.Equals(candidate, fallback, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
