using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ALambdaTTS.Models;
using EdgeTTS.DotNet;

namespace ALambdaTTS.Services;

/// <summary>
/// 音色目录：在线拉取一次，成功后写入本地 json；下次启动先在本地秒开、
/// 后台再刷新。断网时回退到缓存，缓存也没有就只给一个默认音色。
/// </summary>
public sealed class VoiceCatalog
{
    public const string DefaultVoice = "zh-CN-XiaoxiaoNeural";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<VoiceEntry> _voices;

    private VoiceCatalog(List<VoiceEntry> voices)
    {
        _voices = voices;

        // 语言筛选按"大类"合并：zh-CN / zh-HK / zh-CN-liaoning 都归到「中文」
        Languages = voices
            .GroupBy(v => v.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LanguageGroup(
                group.Key,
                VoiceEntry.MakeLanguageLabel(group.Key),
                group.Count()))
            .OrderBy(group => group.Code.Equals("zh", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(group => group.Label, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>语言大类（中文排最前），带该语言下的音色数量。</summary>
    public IReadOnlyList<LanguageGroup> Languages { get; }

    public IReadOnlyList<VoiceEntry> Voices => _voices;

    public static VoiceCatalog Fallback()
    {
        return new VoiceCatalog(new List<VoiceEntry>
        {
            new(
                DefaultVoice,
                VoiceEntry.MakeDisplayName(DefaultVoice),
                "Female",
                "zh-CN",
                VoiceEntry.MakeLocaleDisplay("zh-CN"),
                new[] { "General" },
                new[] { "Warm" }),
        });
    }

    public async Task<VoiceCatalog> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var voices = await EdgeTTS.DotNet.Voices.ListVoicesAsync(proxy: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var entries = voices
                .Where(v => !string.IsNullOrWhiteSpace(v.ShortName))
                .Select(ToEntry)
                .ToList();

            if (entries.Count == 0)
            {
                return this;
            }

            SaveCache(entries);
            return new VoiceCatalog(entries);
        }
        catch
        {
            return this;
        }
    }

    public VoiceEntry? Find(string? shortName)
    {
        if (string.IsNullOrWhiteSpace(shortName))
        {
            return null;
        }

        return _voices.FirstOrDefault(v => string.Equals(v.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
    }

    public VoiceEntry DefaultEntry()
    {
        return Find(DefaultVoice)
               ?? _voices.FirstOrDefault(v => v.Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
               ?? _voices.FirstOrDefault()
               ?? Fallback()._voices[0];
    }

    public string LocaleDisplay(string locale) => VoiceEntry.MakeLocaleDisplay(locale);

    /// <summary>启动时同步读取本地缓存（毫秒级），没有就返回 null。</summary>
    public static VoiceCatalog? LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.VoicesFile))
            {
                return null;
            }

            var json = File.ReadAllText(AppPaths.VoicesFile, Encoding.UTF8);
            var cached = JsonSerializer.Deserialize<VoiceCacheFile>(json, JsonOptions);
            if (cached?.Voices is null || cached.Voices.Count == 0)
            {
                return null;
            }

            var entries = cached.Voices
                .Where(v => !string.IsNullOrWhiteSpace(v.ShortName))
                .Select(v => new VoiceEntry(
                    v.ShortName!,
                    string.IsNullOrWhiteSpace(v.DisplayName) ? VoiceEntry.MakeDisplayName(v.ShortName!) : v.DisplayName!,
                    v.Gender ?? "Female",
                    v.Locale ?? "zh-CN",
                    string.IsNullOrWhiteSpace(v.LocaleDisplay) ? VoiceEntry.MakeLocaleDisplay(v.Locale ?? "zh-CN") : v.LocaleDisplay!,
                    v.Categories ?? new List<string>(),
                    v.Personalities ?? new List<string>()))
                .ToList();

            return entries.Count == 0 ? null : new VoiceCatalog(entries);
        }
        catch
        {
            return null;
        }
    }

    private static VoiceEntry ToEntry(EdgeTTS.DotNet.Models.Voice voice)
    {
        var categories = (voice.VoiceTag?.ContentCategories ?? new List<string>())
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct()
            .ToList();

        var personalities = (voice.VoiceTag?.VoicePersonalities ?? new List<string>())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

        var locale = string.IsNullOrWhiteSpace(voice.Locale) ? "zh-CN" : voice.Locale.Trim();

        return new VoiceEntry(
            voice.ShortName.Trim(),
            VoiceEntry.MakeDisplayName(voice.ShortName.Trim()),
            string.IsNullOrWhiteSpace(voice.Gender) ? "Female" : voice.Gender.Trim(),
            locale,
            VoiceEntityLocale(locale),
            categories,
            personalities);
    }

    private static string VoiceEntityLocale(string locale)
    {
        var display = VoiceEntry.MakeLocaleDisplay(locale);
        if (!display.Equals(locale, StringComparison.OrdinalIgnoreCase))
        {
            return display;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            return culture.EnglishName;
        }
        catch
        {
            return locale;
        }
    }

    private static void SaveCache(List<VoiceEntry> entries)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var file = new VoiceCacheFile
            {
                FetchedAt = DateTimeOffset.Now,
                Voices = entries.Select(v => new VoiceCacheEntry
                {
                    ShortName = v.ShortName,
                    DisplayName = v.DisplayName,
                    Gender = v.Gender,
                    Locale = v.Locale,
                    LocaleDisplay = v.LocaleDisplay,
                    Categories = v.Categories.ToList(),
                    Personalities = v.Personalities.ToList(),
                }).ToList(),
            };

            var json = JsonSerializer.Serialize(file, JsonOptions);
            File.WriteAllText(AppPaths.VoicesFile, json, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public sealed record LanguageGroup(string Code, string Label, int Count);

    private sealed class VoiceCacheFile
    {
        public DateTimeOffset FetchedAt { get; set; }

        public List<VoiceCacheEntry>? Voices { get; set; }
    }

    private sealed class VoiceCacheEntry
    {
        public string? ShortName { get; set; }

        public string? DisplayName { get; set; }

        public string? Gender { get; set; }

        public string? Locale { get; set; }

        public string? LocaleDisplay { get; set; }

        public List<string>? Categories { get; set; }

        public List<string>? Personalities { get; set; }
    }
}
