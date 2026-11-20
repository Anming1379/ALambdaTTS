using System.IO;
using System.Text;
using System.Text.Json;

namespace ALambdaTTS.Services;

/// <summary>设置持久化。写临时文件再替换，避免中途退出把配置写坏。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时静默回到默认值，不影响启动
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch
        {
            // 保存失败不打断使用
        }
    }
}
