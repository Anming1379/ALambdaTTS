using System.Text;
using System.Text.RegularExpressions;

namespace ALambdaTTS.Services;

/// <summary>
/// 把长文本按句子切成若干段。Edge TTS 单次请求太长的文本容易失败，
/// 每段独立请求、独立落成 mp3，不做二进制拼接（因此不需要 ffmpeg）。
/// </summary>
public static class TextChunker
{
    /// <summary>单段上限。经验值：1200 字符以内请求稳定，语音节奏也自然。</summary>
    public const int DefaultMaxChars = 1200;

    private static readonly Regex SentenceSplitter = new(
        @"(?<=[。！？!?；;…\n])",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string text, int maxChars = DefaultMaxChars)
    {
        if (maxChars < 100)
        {
            maxChars = 100;
        }

        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var chunks = new List<string>();
        var buffer = new StringBuilder();

        foreach (var sentence in SentenceSplitter.Split(normalized))
        {
            if (sentence.Length == 0)
            {
                continue;
            }

            if (sentence.Length > maxChars)
            {
                if (buffer.Length > 0)
                {
                    chunks.Add(buffer.ToString().Trim());
                    buffer.Clear();
                }

                foreach (var piece in HardSplit(sentence, maxChars))
                {
                    chunks.Add(piece);
                }

                continue;
            }

            if (buffer.Length > 0 && buffer.Length + sentence.Length > maxChars)
            {
                chunks.Add(buffer.ToString().Trim());
                buffer.Clear();
            }

            buffer.Append(sentence);
        }

        if (buffer.Length > 0)
        {
            var tail = buffer.ToString().Trim();
            if (tail.Length > 0)
            {
                chunks.Add(tail);
            }
        }

        return chunks;
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 三个以上换行压成两个，避免把整段空白也发给合成服务
        normalized = Regex.Replace(normalized, "\n{3,}", "\n\n");
        return normalized.Trim();
    }

    /// <summary>单句超长时先找逗号之类的软断点，实在没有就硬切。</summary>
    private static IEnumerable<string> HardSplit(string sentence, int maxChars)
    {
        var rest = sentence;
        while (rest.Length > maxChars)
        {
            var window = rest[..maxChars];
            var cut = window.LastIndexOfAny(new[] { '，', ',', '、', '：', ':', ' ', '\n' });
            if (cut < maxChars * 0.5)
            {
                cut = maxChars - 1;
            }

            var piece = rest[..(cut + 1)].Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            rest = rest[(cut + 1)..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }
}
