using System.ComponentModel;
using ALambdaTTS.Themes;
using System.IO;
using System.Runtime.CompilerServices;

namespace ALambdaTTS.Models;

public enum TtsState
{
    Synthesizing,
    Ready,
    Failed,
    Canceled,
}

/// <summary>一张历史卡片 = 一次合成请求（可能包含多个 mp3 片段）。</summary>
public sealed class TtsItem : INotifyPropertyChanged
{
    private TtsState _state = TtsState.Synthesizing;
    private double _progress;
    private bool _isPlaying;
    private TimeSpan _duration;
    private string? _errorMessage;

    public TtsItem(string text, VoiceEntry voice, int rate, int pitch, int volume)
    {
        Text = text;
        Voice = voice;
        Rate = rate;
        Pitch = pitch;
        Volume = volume;
        Preview = BuildPreview(text);
    }

    public string Text { get; }

    /// <summary>卡片创建时间。重复点下载时文件名不变，直接覆盖原文件。</summary>
    public DateTime CreatedAt { get; } = DateTime.Now;

    public string Preview { get; }

    public VoiceEntry Voice { get; }

    public int Rate { get; }

    /// <summary>语调：相对原音高的偏移（Hz）。</summary>
    public int Pitch { get; }

    public int Volume { get; }

    public List<string> Files { get; } = new();

    public TtsState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanPlay));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(PlayIcon));
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(ShowStatus));
                OnPropertyChanged(nameof(MetaText));
                OnPropertyChanged(nameof(MetaBrushKey));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressLabel));
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (Set(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayIcon));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (Set(ref _duration, value))
            {
                OnPropertyChanged(nameof(MetaText));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (Set(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsBusy => State == TtsState.Synthesizing;

    /// <summary>只有非就绪状态才在卡片上显示状态文字。</summary>
    public bool ShowStatus => State != TtsState.Ready;

    public bool CanPlay => State == TtsState.Ready && Files.Count > 0;

    /// <summary>失败后显示重试按钮。</summary>
    public bool CanRetry => State == TtsState.Failed;

    /// <summary>合成中的卡片先不给删，免得删到一半回调还在写。</summary>
    public bool CanDelete => State != TtsState.Synthesizing;

    public string PlayIcon => IsPlaying ? IconGlyphs.Pause : IconGlyphs.PlayArrow;

    public string StatusLabel => State switch
    {
        TtsState.Synthesizing => $"合成中 {Progress:P0}",
        TtsState.Failed => ErrorMessage is { Length: > 0 } message ? $"失败：{message}" : "合成失败",
        TtsState.Canceled => "已取消",
        _ => IsPlaying ? "播放中" : "已就绪",
    };

    public string ProgressLabel => $"{Progress:P0}";

    /// <summary>卡片底部强调色小字：音色 · 语速 · 时长 · 片段数。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string> { $"{Voice.DisplayName} · {Voice.GenderLabel}" };

            if (Rate != 0)
            {
                parts.Add($"语速 {Rate:+0;-0}%");
            }

            if (Pitch != 0)
            {
                parts.Add($"语调 {Pitch:+0;-0}Hz");
            }

            if (Volume != 100)
            {
                parts.Add($"音量 {Volume}%");
            }

            if (State == TtsState.Ready && Duration > TimeSpan.Zero)
            {
                parts.Add(FormatDuration(Duration));
            }

            if (Files.Count > 1)
            {
                parts.Add($"{Files.Count} 段");
            }

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>底部小字颜色：失败用警示色，其余用强调色。</summary>
    public string MetaBrushKey => State == TtsState.Failed ? "Danger" : "Accent";

    private static string BuildPreview(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    public static string FormatDuration(TimeSpan span)
    {
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
