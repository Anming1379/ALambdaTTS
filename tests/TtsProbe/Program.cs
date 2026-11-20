using System.Diagnostics;
using System.IO;
using System.Text;
using ALambdaTTS.Models;
using ALambdaTTS.Services;
using ALambdaTTS.Services.EdgeTts;

Console.OutputEncoding = Encoding.UTF8;

var failures = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        Console.WriteLine($"  [OK]   {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : " -> " + detail)}");
    }
}

// ---------------------------------------------------------------
// 1. 分段器
// ---------------------------------------------------------------
Console.WriteLine("1) TextChunker");
var shortText = "短句测试。";
var shortChunks = TextChunker.Split(shortText);
Check("短文本只分一段", shortChunks.Count == 1, $"实际 {shortChunks.Count}");
Check("短文本内容保留", shortChunks[0] == "短句测试。", shortChunks[0]);

var mixed = string.Concat(Enumerable.Repeat("这是第一句话。这是第二句话！这是第三句话？", 40));
var mixedChunks = TextChunker.Split(mixed, 200);
Check("长文本切段", mixedChunks.Count > 1, $"实际 {mixedChunks.Count}");
Check("每段不超上限", mixedChunks.All(c => c.Length <= 200), string.Join(",", mixedChunks.Select(c => c.Length)));
Check("切段不丢内容", mixedChunks.Sum(c => c.Length) >= mixed.Length - 3, $"{mixedChunks.Sum(c => c.Length)} vs {mixed.Length}");

var noPunctuation = new string('啊', 900);
var hardChunks = TextChunker.Split(noPunctuation, 200);
Check("无标点也能硬切", hardChunks.Count == 5 && hardChunks.All(c => c.Length <= 200), $"段数 {hardChunks.Count}");

var paragraphs = string.Join("\n\n", Enumerable.Repeat("段落一。", 300));
var paraChunks = TextChunker.Split(paragraphs, 300);
Check("多段落切段", paraChunks.Count > 1 && paraChunks.All(c => c.Length <= 300), $"段数 {paraChunks.Count}");

// ---------------------------------------------------------------
// 2. 参数格式
// ---------------------------------------------------------------
Console.WriteLine("2) 参数格式");
Check("语调 0 -> +0Hz", TtsService.BuildPitch(0) == "+0Hz", TtsService.BuildPitch(0));
Check("语调 +20 -> +20Hz", TtsService.BuildPitch(20) == "+20Hz", TtsService.BuildPitch(20));
Check("语调 -35 -> -35Hz", TtsService.BuildPitch(-35) == "-35Hz", TtsService.BuildPitch(-35));
Check("语速 +0%", TtsService.BuildRate(0) == "+0%", TtsService.BuildRate(0));
Check("语速 +25%", TtsService.BuildRate(25) == "+25%", TtsService.BuildRate(25));
Check("语速 -40%", TtsService.BuildRate(-40) == "-40%", TtsService.BuildRate(-40));
Check("音量 100 -> +0%", TtsService.BuildVolume(100) == "+0%", TtsService.BuildVolume(100));
Check("音量 0 -> -100%", TtsService.BuildVolume(0) == "-100%", TtsService.BuildVolume(0));
Check("音量 60 -> -40%", TtsService.BuildVolume(60) == "-40%", TtsService.BuildVolume(60));

// ---------------------------------------------------------------
// 3. 文件命名
// ---------------------------------------------------------------
// ---------------------------------------------------------------
// 3.5 DRM 令牌（Sec-MS-GEC v2）
// ---------------------------------------------------------------
Console.WriteLine("3.5 DRM 令牌");
var tokenVectors = new (double Timestamp, string Expected)[]
{
    (1795000000.123456, "6521DCC55F99AAD7-6D04852628E3DF4D0EF33213239BDC3C48750FE37FE5E4591F28C209CE0A3FA2"),
    (1795000200.0, "71B5709CE53550C8-CF3A72B2171E88D676E0C1630AA686B8232F4DC38284D0F0EA1A574A4F1BF162"),
    (1795000299.999999, "71B5709CE53550C8-CF3A72B2171E88D676E0C1630AA686B8232F4DC38284D0F0EA1A574A4F1BF162"),
    (0.0, "52BC7A17B50DFEC9-28C4394775C8751CD2F6859C030C06DB1489ACAB3B3A7651831FC26895177ED6"),
    (1.5, "52BC7A17B50DFEC9-28C4394775C8751CD2F6859C030C06DB1489ACAB3B3A7651831FC26895177ED6"),
    (1750000000.75, "B029A7426816F097-4579D112DC47FC3BF432B499C0A282F427C659474AF9BF5FA33CE2C003F9E9C2"),
    (1900000000.5, "35CB2860F259268E-7C6AC7AAFAF9ECFDEF5C38802D91759C088D45E6E8BDBC2EC1C78C22D51F1DF6"),
};
foreach (var (timestamp, expected) in tokenVectors)
{
    var actual = SecMsGec.Generate(timestamp);
    Check($"令牌向量 t={timestamp}", actual == expected, actual);
}

Check("令牌版本 v2", SecMsGec.GecVersion.StartsWith("2-"), SecMsGec.GecVersion);
Check("当前时间令牌格式", SecMsGec.Generate().Split('-') is { Length: 2 } parts && parts[0].Length == 16 && parts[1].Length == 64, SecMsGec.Generate());

// ---------------------------------------------------------------
// 3.6 文件名清洗
// ---------------------------------------------------------------
Console.WriteLine("3.6 文件名清洗");
var sanitized = AppPaths.SanitizeFileName("你好：世界/测试*文件?\"<>|", 28);
Check("非法字符被替换", sanitized.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, sanitized);
Check("空文本有兜底", AppPaths.SanitizeFileName("   ") == "speech");
Check("长度受限", AppPaths.SanitizeFileName(new string('字', 100), 20).Length <= 20);

// ---------------------------------------------------------------
// 4. 真实合成
// ---------------------------------------------------------------
Console.WriteLine("4) 真实合成（需要联网）");
var cacheDir = Path.Combine(Path.GetTempPath(), "alambda-tts-probe");
if (Directory.Exists(cacheDir))
{
    Directory.Delete(cacheDir, recursive: true);
}

var sw = Stopwatch.StartNew();
var progressEvents = new List<double>();
var progressLock = new object();
var progress = new Progress<SynthesisProgress>(p =>
{
    lock (progressLock)
    {
        progressEvents.Add(p.Overall);
    }
});

try
{
    var result = await TtsService.SynthesizeAsync(
        "你好，这是 ALambda TTS 的第一次自检。Everything works fine.",
        "zh-CN-XiaoxiaoNeural",
        0,
        0,
        100,
        cacheDir,
        progress,
        CancellationToken.None);

    sw.Stop();
    Check("生成一个片段", result.Files.Count == 1, $"实际 {result.Files.Count}");
    var file = result.Files[0];
    var info = new FileInfo(file);
    Check("文件存在且非空", info.Exists && info.Length > 2000, $"{info.Length} bytes");
    Check("耗时合理", sw.Elapsed.TotalSeconds < 60, $"{sw.Elapsed.TotalSeconds:F1}s");

    var header = new byte[4];
    using (var fs = File.OpenRead(file))
    {
        fs.ReadExactly(header, 0, 4);
    }

    var isMp3 = (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0) || Encoding.ASCII.GetString(header, 0, 3) == "ID3";
    Check("MP3 头正确", isMp3, BitConverter.ToString(header));
    Check("有进度回调", progressEvents.Count > 0, $"回调 {progressEvents.Count} 次");
    Console.WriteLine($"     文件: {file} ({info.Length:N0} bytes, 用时 {sw.Elapsed.TotalSeconds:F1}s)");
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"  [FAIL] 真实合成抛异常 -> {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------
// 5. 长文本分段合成 + 取消
// ---------------------------------------------------------------
Console.WriteLine("5) 长文本分段合成");
try
{
    var longText = string.Concat(Enumerable.Repeat(
        "长文本分段测试，每一句都要落到独立的 mp3 文件里。", 60));
    var result = await TtsService.SynthesizeAsync(
        longText,
        "zh-CN-XiaoxiaoNeural",
        -10,
        15,
        80,
        cacheDir,
        progress: null,
        CancellationToken.None);

    Check("长文本产生多个片段", result.Files.Count >= 2, $"实际 {result.Files.Count}");
    Check("片段都非空", result.Files.All(f => new FileInfo(f).Length > 1000));
    Check("片段文件名带序号", result.Files.All(f => f.Contains('-')), string.Join(", ", result.Files.Select(Path.GetFileName)));
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"  [FAIL] 长文本合成抛异常 -> {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("6) 语调参数走真实服务");
try
{
    var result = await TtsService.SynthesizeAsync(
        "语调测试。",
        "zh-CN-XiaoxiaoNeural",
        0,
        40,
        60,
        cacheDir,
        progress: null,
        CancellationToken.None);
    var file = result.Files[0];
    Check("带语调参数能合成", result.Files.Count == 1 && new FileInfo(file).Length > 2000,
        new FileInfo(file).Length + " bytes");
}
catch (Exception ex)
{
    Check("带语调参数能合成", false, $"{ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("7) 取消");
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
    var longText = string.Concat(Enumerable.Repeat(
        "取消测试，用较长的文本保证一秒内做不完。", 300));
    await TtsService.SynthesizeAsync(
        longText,
        "zh-CN-XiaoxiaoNeural",
        0,
        0,
        100,
        cacheDir,
        progress: null,
        cts.Token);
    Check("取消应抛 OperationCanceledException", false, "居然正常完成了");
}
catch (OperationCanceledException)
{
    Check("取消抛 OperationCanceledException", true);
}
catch (Exception ex)
{
    Check("取消抛 OperationCanceledException", false, $"{ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------
// 7. 音色目录
// ---------------------------------------------------------------
Console.WriteLine("8) 音色目录");
try
{
    var catalog = await VoiceCatalog.Fallback().RefreshAsync(CancellationToken.None);
    Check("拉到 100 个以上音色", catalog.Voices.Count > 100, $"实际 {catalog.Voices.Count}");
    Check("有中文音色", catalog.Voices.Any(v => v.Locale.StartsWith("zh")), "无 zh 音色");
    Check("默认音色存在", catalog.Find("zh-CN-XiaoxiaoNeural") is not null);
    Check("语言大类非空", catalog.Languages.Count > 30, $"实际 {catalog.Languages.Count}");
    var chinese = catalog.Languages.FirstOrDefault(l => l.Code == "zh");
    Check("中文是所有 zh 音色合成的一个大类", chinese is not null && chinese.Count == 14,
        chinese is null ? "没有中文" : $"{chinese.Label} {chinese.Count}");
    Check("中文大类排在第一位", catalog.Languages[0].Code == "zh", catalog.Languages[0].Code);
    Check("英文大类合并了所有 en 音色", catalog.Languages.First(l => l.Code == "en").Count > 30);
    var zhCn = VoiceEntry.MakeLocaleDisplay("zh-CN");
    Check("zh-CN 显示名", zhCn.Contains("中文"), zhCn);
    Check("类别已 Trim", catalog.Voices.SelectMany(v => v.Categories).All(c => c == c.Trim()));

    var reloaded = VoiceCatalog.LoadCache();
    Check("本地缓存可重载", reloaded is not null && reloaded.Voices.Count == catalog.Voices.Count,
        reloaded is null ? "null" : $"{reloaded.Voices.Count} vs {catalog.Voices.Count}");
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"  [FAIL] 音色目录异常 -> {ex.GetType().Name}: {ex.Message}");
}

// ---------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过。" : $"失败 {failures} 项。");
Console.WriteLine($"缓存目录: {cacheDir}");
return failures == 0 ? 0 : 1;
