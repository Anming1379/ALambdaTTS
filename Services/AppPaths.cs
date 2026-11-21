using System.IO;
using System.Text;

namespace ALambdaTTS.Services;

/// <summary>应用目录与文件名工具。</summary>
public static class AppPaths
{
    /// <summary>
    /// 数据根目录：默认 %LOCALAPPDATA%\ALambdaTTS。
    /// 设置 ALAMBDA_TTS_HOME 可以改到别处（自检用，免得污染真实配置）。
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("ALAMBDA_TTS_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALambdaTTS");
    }

    /// <summary>本次会话的语音缓存：退出程序即清空。</summary>
    public static string CacheDir { get; } = Path.Combine(Root, "cache");

    public static string VoicesFile { get; } = Path.Combine(Root, "voices.json");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    /// <summary>合成失败的详细记录，方便排查网络问题。</summary>
    public static string ErrorLog { get; } = Path.Combine(Root, "error.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>清空缓存目录（启动和退出各一次）。</summary>
    public static void ClearSessionCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 正在播放的文件可能被占用，留到下次启动再清
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(CacheDir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 把用户文本变成安全的文件名：去掉非法字符，压缩空白，并限制长度。
    /// </summary>
    public static string SanitizeFileName(string text, int maxChars = 28)
    {
        var builder = new StringBuilder();
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? ' ' : ch);
        }

        var cleaned = builder.ToString().Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (cleaned.Length > maxChars)
        {
            cleaned = cleaned[..maxChars].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "speech" : cleaned;
    }
}
