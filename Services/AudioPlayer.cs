using System.IO;
using System.Windows.Media;

namespace ALambdaTTS.Services;

/// <summary>
/// 播放一组 mp3 片段：MediaPlayer 顺序播放，MediaEnded 自动切下一段。
/// 同一时刻只播一条请求，切换卡片前调用 Stop 即可。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private IReadOnlyList<string> _queue = Array.Empty<string>();
    private int _index;
    private bool _openPending;

    public AudioPlayer()
    {
        _player.MediaEnded += OnMediaEnded;
        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
    }

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// 不在这里调音量：MediaPlayer.Volume 在部分音频链路上不生效，
    /// 音量统一在合成时按 Edge 的 volume 参数处理（见 TtsItem.Volume）。
    /// </summary>
    public static bool VolumeHandledBySynthesis => true;

    /// <summary>播放状态变化（true = 正在播放）。</summary>
    public event EventHandler<bool>? PlayingChanged;

    /// <summary>某一段的时长解析出来了，界面据此累计总时长。</summary>
    public event EventHandler<TimeSpan>? DurationResolved;

    public event EventHandler<string>? Failed;

    public event EventHandler? Finished;

    public void Play(IReadOnlyList<string> files)
    {
        Stop();

        if (files.Count == 0)
        {
            return;
        }

        _queue = files;
        _index = 0;
        _openPending = true;
        _player.Open(new Uri(Path.GetFullPath(files[0]), UriKind.Absolute));
    }

    public void Pause()
    {
        if (!IsPlaying)
        {
            return;
        }

        _player.Pause();
        SetPlaying(false);
    }

    public void Resume()
    {
        if (IsPlaying || _queue.Count == 0)
        {
            return;
        }

        if (_player.Source is not null)
        {
            _player.Play();
            SetPlaying(true);
        }
        else
        {
            Play(_queue);
        }
    }

    public void Stop()
    {
        _queue = Array.Empty<string>();
        _index = 0;
        _openPending = false;
        _player.Stop();
        _player.Close();

        if (IsPlaying)
        {
            SetPlaying(false);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (!_openPending)
        {
            return;
        }

        _openPending = false;

        if (_player.NaturalDuration.HasTimeSpan)
        {
            DurationResolved?.Invoke(this, _player.NaturalDuration.TimeSpan);
        }

        _player.Play();
        SetPlaying(true);
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_index + 1 < _queue.Count)
        {
            _index++;
            _openPending = true;
            _player.Open(new Uri(Path.GetFullPath(_queue[_index]), UriKind.Absolute));
            return;
        }

        Stop();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        var message = e.ErrorException?.Message ?? "播放失败";
        Stop();
        Failed?.Invoke(this, message);
    }

    private void SetPlaying(bool playing)
    {
        IsPlaying = playing;
        PlayingChanged?.Invoke(this, playing);
    }
}
