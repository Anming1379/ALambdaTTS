using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALambdaTTS.Services.EdgeTts;

/// <summary>
/// 直连微软 Edge 朗读接口的最小客户端。
///
/// 为什么不用现成的 EdgeTTS.DotNet 合成：它的 DRM 令牌还是 v1 格式，
/// 服务端现在只认 v2（见 SecMsGec），握手直接 403。
/// 传输层也没用 ClientWebSocket：它对这边发出的帧一律回 "Bad request"，
/// 所以换成自己写的 RawWebSocket，与官方 python 客户端字节一致。
/// </summary>
public static class EdgeTtsClient
{
    private const string WssHost = "speech.platform.bing.com";

    private const string WssPath = "/consumer/speech/synthesize/readaloud/edge/v1";

    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    /// <summary>合成配置。注意：这里不能用插值字符串，否则 }} 会被当成转义。</summary>
    private const string SynthesisConfig =
        "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":" +
        "{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"}," +
        "\"outputFormat\":\"" + OutputFormat + "\"}}}}";

    /// <summary>
    /// 服务端只认完整音色名：zh-CN-XiaoxiaoNeural ->
    /// Microsoft Server Speech Text to Speech Voice (zh-CN, XiaoxiaoNeural)。
    /// </summary>
    private static readonly Regex ShortVoicePattern = new(
        @"^([a-z]{2,})-([A-Z]{2,})-(.+Neural)$",
        RegexOptions.Compiled);

    private static bool DebugEnabled =>
        Environment.GetEnvironmentVariable("ALAMBDA_TTS_DEBUG") == "1";

    /// <summary>合成一段文本并写入 mp3 文件；onProgress 收到"已播报的字符数"。</summary>
    public static async Task SynthesizeToFileAsync(
        string text,
        string voice,
        string rate,
        string pitch,
        string volume,
        string path,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var token = timeout.Token;

        var connectionId = Guid.NewGuid().ToString("N");
        var requestId = Guid.NewGuid().ToString("N");
        var uri = new Uri(
            $"wss://{WssHost}{WssPath}?TrustedClientToken={SecMsGec.TrustedClientToken}" +
            $"&ConnectionId={connectionId}" +
            $"&Sec-MS-GEC={SecMsGec.Generate()}" +
            $"&Sec-MS-GEC-Version={SecMsGec.GecVersion}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Origin"] = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold",
            ["Pragma"] = "no-cache",
            ["Cache-Control"] = "no-cache",
            ["Accept-Encoding"] = "gzip, deflate, br",
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["Sec-CH-UA"] = "\" Not;A Brand\";v=\"99\", \"Microsoft Edge\";v=\"143\", \"Chromium\";v=\"143\"",
            ["Cookie"] = "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";",
        };

        Debug($"connect {SecMsGec.GecVersion}");

        using var socket = await RawWebSocket.ConnectAsync(uri, headers, token).ConfigureAwait(false);
        Debug("connected");

        var timestamp = DateTime.UtcNow.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            CultureInfo.InvariantCulture);

        var config = SynthesisConfig + "\r\n";

        await SendWithLogAsync(
            socket,
            $"X-Timestamp:{timestamp}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n{config}",
            token).ConfigureAwait(false);

        var ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{Escape(ToServerVoiceName(voice))}'><prosody pitch='{Escape(pitch)}' rate='{Escape(rate)}' " +
            $"volume='{Escape(volume)}'>{Escape(text)}</prosody></voice></speak>";

        await SendWithLogAsync(
            socket,
            $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{timestamp}Z\r\nPath:ssml\r\n\r\n{ssml}",
            token).ConfigureAwait(false);

        await ReceiveAudioAsync(socket, path, onProgress, token).ConfigureAwait(false);
    }

    private static async Task ReceiveAudioAsync(
        RawWebSocket socket,
        string path,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        var seenChars = 0;

        while (true)
        {
            var (opcode, payload) = await socket.ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);

            if (opcode == RawWebSocketOpcode.Close)
            {
                Debug($"close payload={Encoding.UTF8.GetString(payload)}");
                return;
            }

            if (opcode == RawWebSocketOpcode.Text)
            {
                var text = Encoding.UTF8.GetString(payload);
                Debug($"text len={payload.Length} head={Head(text, 140)}");

                if (text.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    return;
                }

                if (text.Contains("Path:audio.metadata", StringComparison.Ordinal))
                {
                    seenChars += CountMetadataChars(text);
                    onProgress?.Invoke(seenChars);
                }

                continue;
            }

            // 二进制帧：2 字节大端头长度 + 头 + 音频数据
            if (payload.Length < 2)
            {
                continue;
            }

            var headerLength = (payload[0] << 8) | payload[1];
            if (payload.Length < 2 + headerLength)
            {
                Debug($"bin short len={payload.Length} headerLen={headerLength}");
                continue;
            }

            var header = Encoding.ASCII.GetString(payload, 2, headerLength);
            if (!header.Contains("Path:audio", StringComparison.Ordinal))
            {
                Debug($"bin skip header={header.Replace("\r\n", " | ", StringComparison.Ordinal)}");
                continue;
            }

            await file.WriteAsync(payload.AsMemory(2 + headerLength), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>从 metadata JSON 里累计 WordBoundary 的字符数，用于进度显示。</summary>
    private static int CountMetadataChars(string message)
    {
        var jsonStart = message.IndexOf('{');
        if (jsonStart < 0)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(message[jsonStart..]);
            if (!document.RootElement.TryGetProperty("Metadata", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var total = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("Type", out var type)
                    || type.GetString() is not "WordBoundary")
                {
                    continue;
                }

                if (item.TryGetProperty("Data", out var data)
                    && data.TryGetProperty("text", out var textElement)
                    && textElement.TryGetProperty("Length", out var length)
                    && length.TryGetInt32(out var count))
                {
                    total += count;
                }
            }

            return total;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static async Task SendWithLogAsync(RawWebSocket socket, string message, CancellationToken token)
    {
        Debug("SEND " + message.Replace("\r\n", " | ", StringComparison.Ordinal));

        if (DebugEnabled)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "tts-payloads.txt"),
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(message)) + Environment.NewLine);
            }
            catch
            {
            }
        }

        await socket.SendTextAsync(message, token).ConfigureAwait(false);
    }

    private static string ToServerVoiceName(string voice)
    {
        var match = ShortVoicePattern.Match(voice);
        if (!match.Success)
        {
            return voice;
        }

        var lang = match.Groups[1].Value;
        var region = match.Groups[2].Value;
        var name = match.Groups[3].Value;

        var dash = name.IndexOf('-');
        if (dash >= 0)
        {
            region = $"{region}-{name[..dash]}";
            name = name[(dash + 1)..];
        }

        return $"Microsoft Server Speech Text to Speech Voice ({lang}-{region}, {name})";
    }

    private static string Escape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string Head(string value, int length)
        => value.Length <= length ? value : value[..length];

    private static void Debug(string line)
    {
        if (!DebugEnabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "tts-debug.log"),
                line + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
        }
    }
}
