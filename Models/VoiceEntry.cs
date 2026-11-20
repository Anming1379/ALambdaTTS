using System.Globalization;

namespace ALambdaTTS.Models;

/// <summary>一个可选的 Edge TTS 音色，界面上的显示名与筛选用字段都在这里算好。</summary>
public sealed class VoiceEntry
{
    // 语言大类的中文名（按 Edge 音色列表里实际出现的 75 种语言整理）
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["af"] = "南非荷兰语",
        ["am"] = "阿姆哈拉语",
        ["ar"] = "阿拉伯语",
        ["az"] = "阿塞拜疆语",
        ["bg"] = "保加利亚语",
        ["bn"] = "孟加拉语",
        ["bs"] = "波斯尼亚语",
        ["ca"] = "加泰罗尼亚语",
        ["cs"] = "捷克语",
        ["cy"] = "威尔士语",
        ["da"] = "丹麦语",
        ["de"] = "德语",
        ["el"] = "希腊语",
        ["en"] = "英语",
        ["es"] = "西班牙语",
        ["et"] = "爱沙尼亚语",
        ["fa"] = "波斯语",
        ["fil"] = "菲律宾语",
        ["fi"] = "芬兰语",
        ["fr"] = "法语",
        ["ga"] = "爱尔兰语",
        ["gl"] = "加利西亚语",
        ["gu"] = "古吉拉特语",
        ["he"] = "希伯来语",
        ["hi"] = "印地语",
        ["hr"] = "克罗地亚语",
        ["hu"] = "匈牙利语",
        ["id"] = "印度尼西亚语",
        ["is"] = "冰岛语",
        ["it"] = "意大利语",
        ["iu"] = "因纽特语",
        ["ja"] = "日语",
        ["jv"] = "爪哇语",
        ["ka"] = "格鲁吉亚语",
        ["kk"] = "哈萨克语",
        ["km"] = "高棉语",
        ["kn"] = "卡纳达语",
        ["ko"] = "韩语",
        ["lo"] = "老挝语",
        ["lt"] = "立陶宛语",
        ["lv"] = "拉脱维亚语",
        ["mk"] = "马其顿语",
        ["ml"] = "马拉雅拉姆语",
        ["mn"] = "蒙古语",
        ["mr"] = "马拉地语",
        ["ms"] = "马来语",
        ["mt"] = "马耳他语",
        ["my"] = "缅甸语",
        ["nb"] = "书面挪威语",
        ["ne"] = "尼泊尔语",
        ["nl"] = "荷兰语",
        ["pl"] = "波兰语",
        ["ps"] = "普什图语",
        ["pt"] = "葡萄牙语",
        ["ro"] = "罗马尼亚语",
        ["ru"] = "俄语",
        ["si"] = "僧伽罗语",
        ["sk"] = "斯洛伐克语",
        ["sl"] = "斯洛文尼亚语",
        ["so"] = "索马里语",
        ["sq"] = "阿尔巴尼亚语",
        ["sr"] = "塞尔维亚语",
        ["su"] = "巽他语",
        ["sv"] = "瑞典语",
        ["sw"] = "斯瓦希里语",
        ["ta"] = "泰米尔语",
        ["te"] = "泰卢固语",
        ["th"] = "泰语",
        ["tr"] = "土耳其语",
        ["uk"] = "乌克兰语",
        ["ur"] = "乌尔都语",
        ["uz"] = "乌兹别克语",
        ["vi"] = "越南语",
        ["zh"] = "中文",
        ["zu"] = "祖鲁语",
    };

    public VoiceEntry(
        string shortName,
        string displayName,
        string gender,
        string locale,
        string localeDisplay,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> personalities)
    {
        ShortName = shortName;
        DisplayName = displayName;
        Gender = gender;
        Locale = locale;
        LocaleDisplay = localeDisplay;
        Categories = categories;
        Personalities = personalities;
        LanguageCode = locale.Split('-', 2)[0].ToLowerInvariant();
        SearchText = string.Join(
            ' ',
            new[]
            {
                shortName, displayName, locale, localeDisplay,
                MakeLanguageLabel(LanguageCode), string.Join(' ', categories),
            });
    }

    public string ShortName { get; }

    public string DisplayName { get; }

    public string Gender { get; }

    public string Locale { get; }

    public string LocaleDisplay { get; }

    public IReadOnlyList<string> Categories { get; }

    public IReadOnlyList<string> Personalities { get; }

    public string SearchText { get; }

    /// <summary>语言大类代码，例如 zh-CN-liaoning 和 zh-HK 都是 "zh"（筛选用）。</summary>
    public string LanguageCode { get; }

    public bool IsFemale => Gender.Equals("Female", StringComparison.OrdinalIgnoreCase);

    public string GenderLabel => IsFemale ? "女声" : "男声";

    /// <summary>语言下拉里的显示名。</summary>
    public string LocaleLabel => LocaleDisplay;

    /// <summary>类别标签，数据里偶尔带多余空格，统一清一遍。</summary>
    public string CategoryLabel => string.Join(" / ", Categories);

    /// <summary>"zh-CN-XiaoxiaoNeural" -> "晓晓"；"en-US-EmmaMultilingualNeural" -> "Emma Multilingual"。</summary>
    public static string MakeDisplayName(string shortName)
    {
        var name = shortName;
        const string suffix = "Neural";
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
        }

        var dash = name.LastIndexOf('-');
        var part = dash >= 0 ? name[(dash + 1)..] : name;
        if (string.IsNullOrEmpty(part))
        {
            part = name;
        }

        // 驼峰/数字混排拆成带空格的样子，读起来更像人名
        var builder = new System.Text.StringBuilder(part.Length + 4);
        for (var i = 0; i < part.Length; i++)
        {
            var ch = part[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(part[i - 1]) && part[i - 1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>语言大类的显示名，例如 zh -> 中文、en -> 英语。</summary>
    public static string MakeLanguageLabel(string code)
    {
        if (LanguageNames.TryGetValue(code, out var name))
        {
            return name;
        }

        try
        {
            var display = CultureInfo.GetCultureInfo(code).DisplayName;
            if (!string.IsNullOrWhiteSpace(display) && !display.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }
        catch
        {
        }

        return code.ToUpperInvariant();
    }

    /// <summary>"zh-CN" -> "中文（中国）"；特殊 locale 走手工表。</summary>
    public static string MakeLocaleDisplay(string locale)
    {
        switch (locale)
        {
            case "zh-CN-liaoning":
                return "中文（辽宁话）";
            case "zh-CN-shaanxi":
                return "中文（陕西话）";
            case "zh-CN":
                return "中文（中国大陆）";
            case "zh-HK":
                return "中文（香港）";
            case "zh-TW":
                return "中文（台湾）";
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(locale);
            var display = culture.DisplayName;
            if (!string.IsNullOrWhiteSpace(display))
            {
                return display;
            }
        }
        catch
        {
        }

        return locale;
    }
}
