using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ALambdaTTS.Services.EdgeTts;

namespace ALambdaTTS.Services;

public sealed record SynthesisProgress(int DoneSegments, int TotalSegments, double SegmentFraction)
{
    public double Overall => TotalSegments <= 0
        ? 0
        : Math.Clamp((DoneSegments + SegmentFraction) / TotalSegments, 0, 1);
}

public sealed record SynthesisResult(IReadOnlyList<string> Files);

/// <summary>
/// Edge TTS 合成管线：分段 -> 逐段流式写盘 -> 汇报进度 -> 支持取消。
/// 每段一个独立 mp3，播放和下载都按片段列表处理。
/// </summary>
public static class TtsService
{
    /// <summary>语速：-50 ~ +100 直接映射成 Edge 的 "+0%"/"-20%" 写法。</summary>
    public static string BuildRate(int percent)
    {
        var clamped = Math.Clamp(percent, -100, 100);
        return $"{clamped:+0;-0;+0}%";
    }

    /// <summary>语调：界面 -50 ~ +50，映射成 Edge 的 "-20Hz"/"+0Hz" 写法（相对原音高的偏移）。</summary>
    public static string BuildPitch(int hertz)
    {
        var clamped = Math.Clamp(hertz, -100, 100);
        return $"{clamped:+0;-0;+0}Hz";
    }

    /// <summary>音量：界面 0 ~ 100，映射成 Edge 的 -100% ~ +0%（+0% 即原始音量）。</summary>
    public static string BuildVolume(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return $"{clamped - 100:+0;-0;+0}%";
    }

    public static async Task<SynthesisResult> SynthesizeAsync(
        string text,
        string voiceShortName,
        int rate,
        int pitch,
        int volume,
        string cacheDirectory,
        IProgress<SynthesisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunks = TextChunker.Split(text);
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("没有可合成的文本。");
        }

        Directory.CreateDirectory(cacheDirectory);
        var session = Guid.NewGuid().ToString("n")[..8];
        var files = new List<string>(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = chunks[i];
            var path = Path.Combine(
                cacheDirectory,
                chunks.Count == 1 ? $"{session}.mp3" : $"{session}-{i + 1:00}.mp3");

            var segmentIndex = i;
            var lastReport = -1;

            IProgress<SynthesisProgress>? localProgress = progress is null
                ? null
                : new Progress<SynthesisProgress>(value =>
                {
                    // 同一百分比不必反复通知界面（回调可能并发，用原子操作去重）
                    var percent = (int)(value.Overall * 100);
                    if (Interlocked.Exchange(ref lastReport, percent) != percent)
                    {
                        progress.Report(value);
                    }
                });

            await SynthesizeSegmentAsync(
                chunk,
                voiceShortName,
                rate,
                pitch,
                volume,
                path,
                seen =>
                {
                    var fraction = chunk.Length == 0 ? 0 : Math.Min(0.97, (double)seen / chunk.Length);
                    localProgress?.Report(new SynthesisProgress(segmentIndex, chunks.Count, fraction));
                },
                cancellationToken);

            files.Add(path);
            localProgress?.Report(new SynthesisProgress(files.Count, chunks.Count, 0));
        }

        progress?.Report(new SynthesisProgress(chunks.Count, chunks.Count, 0));
        return new SynthesisResult(files);
    }

    private static async Task SynthesizeSegmentAsync(
        string text,
        string voice,
        int rate,
        int pitch,
        int volume,
        string path,
        Action<int> onProgress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        const int maxAttempts = 3;

        // 这台机器走 TUN 代理，TLS 偶发 EOF；多试几次基本都能过
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await EdgeTtsClient.SynthesizeToFileAsync(
                    text,
                    voice,
                    BuildRate(rate),
                    BuildPitch(pitch),
                    BuildVolume(volume),
                    path,
                    onProgress,
                    cancellationToken).ConfigureAwait(false);

                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    throw new EdgeTtsException("服务没有返回音频数据。");
                }

                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(path);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                TryDelete(path);
                LogFailure(text, attempt, ex);
            }
        }

        if (lastError is null)
        {
            throw new EdgeTtsException($"合成失败（已重试 {maxAttempts} 次）");
        }

        throw new EdgeTtsException(Describe(lastError) + $"（已重试 {maxAttempts} 次）", lastError);
    }

    /// <summary>把底层异常翻译成用户能看懂的一句话。</summary>
    private static string Describe(Exception? error)
    {
        if (error is null)
        {
            return "合成失败";
        }

        if (error is EdgeTtsException)
        {
            return error.Message;
        }

        var root = error;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        if (error is AuthenticationException || root is AuthenticationException)
        {
            return "TLS 握手失败，网络或代理不稳定";
        }

        if (error is SocketException || root is SocketException)
        {
            return "网络连接失败，请检查网络";
        }

        if (error is IOException || root is IOException)
        {
            return "网络连接被中断";
        }

        return string.IsNullOrWhiteSpace(error.Message) ? "合成失败" : error.Message;
    }

    private static void LogFailure(string text, int attempt, Exception error)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var head = text.Length <= 40 ? text : text[..40] + "…";
            File.AppendAllText(
                AppPaths.ErrorLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] attempt {attempt + 1}: {error.GetType().Name}: {error.Message}"
                + Environment.NewLine
                + "  text: " + head
                + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
