using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ALambdaTTS.Models;
using ALambdaTTS.Services;
using ALambdaTTS.Services.Lan;
using ALambdaTTS.Themes;
using ALambdaTTS.Views;

namespace ALambdaTTS;

public partial class MainWindow : Window
{
    private const double ComposerCollapsedHeight = 98;
    private const double ComposerExpandedHeight = 150;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<TtsItem> _items = new();
    private readonly AudioPlayer _player = new();

    private VoiceCatalog _catalog = VoiceCatalog.Fallback();
    private VoiceEntry _voice;
    private TtsItem? _playingItem;
    private CancellationTokenSource? _cts;
    private bool _busy;

    private readonly LanService _lan = new();
    private readonly Queue<LanChatMessage> _remoteQueue = new();
    private readonly DispatcherTimer _lanTimer;
    private bool _remotePumping;
    private bool _reconnecting;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        _voice = _catalog.DefaultEntry();

        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);

        HistoryList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => UpdateEmptyState();

        RateKnob.Value = Math.Clamp(settings.Rate, -50, 100);
        PitchKnob.Value = Math.Clamp(settings.Pitch, -50, 50);
        VolumeKnob.Value = Math.Clamp(settings.Volume, 0, 100);

        _player.PlayingChanged += Player_PlayingChanged;
        _player.DurationResolved += Player_DurationResolved;
        _player.Finished += Player_Finished;
        _player.Failed += Player_Failed;

        VoicePicker.VoiceChosen += VoicePicker_VoiceChosen;
        VoicePicker.CloseRequested += (_, _) => CloseVoicePanel();
        VoicePicker.FilterChanged += (_, _) =>
        {
            _settings.VoiceFilterGender = VoicePicker.SelectedGender;
            _settings.VoiceFilterLanguage = VoicePicker.SelectedLanguage;
            SettingsStore.Save(_settings);
        };
        ThemeManager.Changed += ThemeManager_Changed;

        LanPanel.ValuesChanged += LanPanel_ValuesChanged;
        LanPanel.DiscoverRequested += LanPanel_DiscoverRequested;
        LanPanel.PairRequested += LanPanel_PairRequested;
        LanPanel.ManualPairRequested += LanPanel_ManualPairRequested;
        LanPanel.DisconnectRequested += LanPanel_DisconnectRequested;
        LanPanel.ReconnectRequested += LanPanel_ReconnectRequested;
        LanPanel.ForgetRequested += LanPanel_ForgetRequested;
        LanPanel.CloseRequested += (_, _) => CloseLanPanel();

        _lan.StatusChanged += (_, status) => OnUi(() =>
        {
            LanPanel.SetStatus(status);
            UpdateLanButton();

            if (status.State == LanState.Connected && status.Peer is { } peer)
            {
                RememberPeer(peer);
            }
        });
        _lan.PeerDiscovered += (_, peer) => OnUi(() => LanPanel.AddPeer(peer));
        _lan.MessageReceived += (_, message) => OnUi(() => OnLanMessage(message));

        // 断了就自己连回去（同一台对端、同一个配对码）
        _lanTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _lanTimer.Tick += LanTimer_Tick;
        _lanTimer.Start();

        SourceInitialized += (_, _) =>
        {
            NativeTheme.Apply(this);
            UpdateDownloadFolder();
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        Deactivated += (_, _) =>
        {
            CloseVoicePanel();
            CloseLanPanel();
        };

        UpdateVoiceButton();
        UpdateDownloadFolder();
        UpdateEmptyState();
        UpdateSendState();

        if (_settings.LanEnabled)
        {
            StartLan();
            if (_settings.LanAutoConnect && !string.IsNullOrWhiteSpace(_settings.LanPeerHost))
            {
                _ = PairWithAsync(_settings.LanPeerHost!, _settings.LanPeerPort, announce: false);
            }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var cached = VoiceCatalog.LoadCache();
        if (cached is not null)
        {
            _catalog = cached;
            ResolveVoice();
        }

        StatusText.Text = "正在获取音色列表…";
        var refreshed = await _catalog.RefreshAsync(CancellationToken.None);
        if (!ReferenceEquals(refreshed, _catalog) && refreshed.Voices.Count > 0)
        {
            _catalog = refreshed;
            ResolveVoice();
            StatusText.Text = $"音色列表已更新，共 {_catalog.Voices.Count} 个";
        }
        else
        {
            StatusText.Text = _catalog.Voices.Count > 1
                ? $"使用本地音色缓存，共 {_catalog.Voices.Count} 个"
                : "音色列表获取失败，仅可用默认音色";
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _lanTimer.Stop();
        _lan.StopAsync().GetAwaiter().GetResult();
        _player.Dispose();

        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.Rate = (int)RateKnob.Value;
        _settings.Pitch = (int)PitchKnob.Value;
        _settings.Volume = (int)VolumeKnob.Value;
        _settings.VoiceShortName = _voice.ShortName;
        SettingsStore.Save(_settings);
    }

    private void ResolveVoice()
    {
        _voice = _catalog.Find(_settings.VoiceShortName) ?? _catalog.DefaultEntry();
        UpdateVoiceButton();
    }

    private void UpdateVoiceButton()
    {
        VoiceNameText.Text = _voice.DisplayName;
        VoiceMetaText.Text = $"{_voice.LocaleLabel} · {_voice.GenderLabel}";
        VoiceButton.ToolTip = $"{_voice.DisplayName}\n{_voice.LocaleLabel} · {_voice.GenderLabel}";
    }

    private void UpdateDownloadFolder()
    {
        DownloadFolderText.Text = _settings.ResolveDownloadFolder();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeManager_Changed(object? sender, EventArgs e)
    {
        NativeTheme.Apply(this);
    }

    // ================= 音色面板 =================

    private bool IsVoicePanelOpen => VoiceOverlay.Visibility == Visibility.Visible;

    private void OpenVoicePanel()
    {
        VoicePicker.Initialize(_catalog, _voice, _settings.VoiceFilterGender, _settings.VoiceFilterLanguage);
        VoiceOverlay.Visibility = Visibility.Visible;
        // 等布局完成再抢焦点，否则搜索框还没生成
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(VoicePicker.FocusSearch));
    }

    private void CloseVoicePanel()
    {
        if (IsVoicePanelOpen)
        {
            VoiceOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>点面板外面的遮罩：收起面板。</summary>
    private void VoiceOverlay_Dismiss(object sender, MouseButtonEventArgs e)
    {
        CloseVoicePanel();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 窗口级快捷键。单独抽出来是因为测试进程里没法伪造修饰键状态（Keyboard.Modifiers 读的是真实键盘）。
    /// </summary>
    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        var control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (key == Key.L && control)
        {
            CloseVoicePanel();
            CloseLanPanel();
            FocusComposer();
            return true;
        }

        if (key == Key.Escape)
        {
            if (IsVoicePanelOpen)
            {
                CloseVoicePanel();
                return true;
            }

            if (IsLanPanelOpen)
            {
                CloseLanPanel();
                return true;
            }

            if (_busy)
            {
                CancelSynthesis();
                return true;
            }
        }

        return false;
    }

    /// <summary>把光标送回输入框末尾（Ctrl+L）。</summary>
    public void FocusComposer()
    {
        if (!InputBox.IsKeyboardFocusWithin)
        {
            InputBox.Focus();
        }

        InputBox.CaretIndex = InputBox.Text.Length;
        InputBox.ScrollToEnd();
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsVoicePanelOpen)
        {
            CloseVoicePanel();
            return;
        }

        OpenVoicePanel();
    }

    private void VoicePicker_VoiceChosen(object? sender, VoiceEntry entry)
    {
        _voice = entry;
        _settings.VoiceShortName = entry.ShortName;
        UpdateVoiceButton();
        CloseVoicePanel();
        SettingsStore.Save(_settings);
        StatusText.Text = $"音色已切换为 {entry.DisplayName}";
    }

    // ================= 局域网 =================

    /// <summary>本机在局域网里的显示名。</summary>
    public string LocalDeviceName => string.IsNullOrWhiteSpace(_settings.LanDeviceName)
        ? Environment.MachineName
        : _settings.LanDeviceName!.Trim();

    private bool IsLanPanelOpen => LanOverlay.Visibility == Visibility.Visible;

    /// <summary>局域网是否正在监听（自检用）。</summary>
    public bool IsLanListening => _lan.State != LanState.Stopped;

    /// <summary>当前是否已和另一台电脑配对成功（自检用）。</summary>
    public bool IsLanConnected => _lan.IsConnected;

    private void LanButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsLanPanelOpen)
        {
            CloseLanPanel();
            return;
        }

        OpenLanPanel();
    }

    private void OpenLanPanel()
    {
        CloseVoicePanel();
        EnsurePairCode();
        LanPanel.Load(BuildLanValues(), _settings.LanPeerName, _lan.IsConnected);
        LanPanel.SetRemembered(_settings.LanPeerHost, _settings.LanPeerPort, _settings.LanPeerName);
        LanPanel.SetStatus(new LanStatus(
            _lan.State,
            _lan.State == LanState.Stopped ? "未启用" : "等待配对",
            _lan.Peer));
        LanOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(LanPanel.FocusName));
    }

    private void CloseLanPanel()
    {
        if (IsLanPanelOpen)
        {
            LanOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void LanOverlay_Dismiss(object sender, MouseButtonEventArgs e)
    {
        CloseLanPanel();
        e.Handled = true;
    }

    private LanPanelValues BuildLanValues() => new(
        _settings.LanEnabled,
        LocalDeviceName,
        _settings.LanPairCode ?? string.Empty,
        _settings.LanPort,
        _settings.LanPeerHost,
        _settings.LanAutoConnect,
        _settings.LanPeerName);

    private void EnsurePairCode()
    {
        if (string.IsNullOrWhiteSpace(_settings.LanPairCode))
        {
            _settings.LanPairCode = Random.Shared
                .Next(100000, 1000000)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            SettingsStore.Save(_settings);
        }
    }

    private void StartLan()
    {
        EnsurePairCode();
        _lan.Start(LocalDeviceName, _settings.LanPort, _settings.LanPairCode ?? string.Empty);
        UpdateLanButton();
    }

    private void LanPanel_ValuesChanged(object? sender, LanPanelValues values)
    {
        var enabling = values.Enabled && !_settings.LanEnabled;
        var disabling = !values.Enabled && _settings.LanEnabled;

        _settings.LanDeviceName = values.DeviceName;
        _settings.LanPairCode = values.PairCode;
        _settings.LanPort = values.Port;
        _settings.LanEnabled = values.Enabled;
        _settings.LanAutoConnect = values.AutoConnect;

        if (disabling)
        {
            _ = _lan.StopAsync();
        }
        else if (values.Enabled)
        {
            // Start 幂等：名字和配对码即时生效，端口变了才重新绑定
            _lan.Start(values.DeviceName, values.Port, values.PairCode);
            if (enabling)
            {
                StatusText.Text = $"局域网已开启，配对码 {values.PairCode}";
                if (_settings.LanAutoConnect && !string.IsNullOrWhiteSpace(_settings.LanPeerHost))
                {
                    _ = PairWithAsync(_settings.LanPeerHost!, _settings.LanPeerPort, announce: false);
                }
            }
        }

        SettingsStore.Save(_settings);
        UpdateLanButton();
    }

    private async void LanPanel_DiscoverRequested(object? sender, EventArgs e)
    {
        if (!_settings.LanEnabled)
        {
            StatusText.Text = "先打开局域网开关";
            return;
        }

        StatusText.Text = "正在搜索局域网里的设备…";
        var peers = await _lan.DiscoverAsync(TimeSpan.FromSeconds(2));
        foreach (var peer in peers)
        {
            LanPanel.AddPeer(peer);
        }

        StatusText.Text = peers.Count == 0
            ? "没搜到设备，试试直接填对方 IP 连接"
            : $"发现 {peers.Count} 台设备";
    }

    private async void LanPanel_PairRequested(object? sender, LanPeer peer)
        => await PairWithAsync(peer.Host, peer.Port, announce: true);

    private async void LanPanel_ManualPairRequested(object? sender, LanManualTarget target)
        => await PairWithAsync(target.Host, target.Port, announce: true);

    private async Task<bool> PairWithAsync(string host, int port, bool announce)
    {
        var code = _settings.LanPairCode ?? string.Empty;
        if (announce)
        {
            StatusText.Text = $"正在连接 {host}:{port}…";
        }

        var paired = await _lan.PairAsync(host, port, code);
        if (paired)
        {
            _settings.LanPeerHost = host;
            _settings.LanPeerPort = port;
            _settings.LanPeerName = _lan.Peer?.Name;
            SettingsStore.Save(_settings);
            LanPanel.SetPeer(_lan.Peer?.Name, true);
            StatusText.Text = $"已连接 {_lan.Peer?.Name} · 消息会同时在这台电脑朗读";
        }
        else if (announce)
        {
            StatusText.Text = "配对失败：检查配对码，或确认对方也开着局域网模式";
        }

        UpdateLanButton();
        return paired;
    }

    /// <summary>把对端记下来（两边都会记，下次启动直接连回去）。</summary>
    private void RememberPeer(LanPeer peer)
    {
        if (string.IsNullOrWhiteSpace(peer.Host))
        {
            return;
        }

        _settings.LanPeerHost = peer.Host;
        _settings.LanPeerPort = peer.Port;
        _settings.LanPeerName = peer.Name;
        SettingsStore.Save(_settings);
        LanPanel.SetRemembered(peer.Host, peer.Port, peer.Name);
    }

    private async void LanPanel_ReconnectRequested(object? sender, LanManualTarget target)
        => await PairWithAsync(target.Host, target.Port, announce: true);

    private void LanPanel_ForgetRequested(object? sender, EventArgs e)
    {
        _settings.LanPeerHost = null;
        _settings.LanPeerPort = Services.Lan.LanService.DefaultPort;
        _settings.LanPeerName = null;
        SettingsStore.Save(_settings);
        StatusText.Text = "已忘记上次的对端";
    }

    private async void LanPanel_DisconnectRequested(object? sender, EventArgs e)
    {
        await _lan.DisconnectAsync();
        LanPanel.SetPeer(null, false);
        StatusText.Text = "已断开局域网连接";
        UpdateLanButton();
    }

    /// <summary>断了就悄悄重连，不刷状态栏。</summary>
    private async void LanTimer_Tick(object? sender, EventArgs e)
    {
        if (!_settings.LanEnabled || !_settings.LanAutoConnect || _lan.IsConnected
            || _lan.State != LanState.Listening)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.LanPeerHost) || _reconnecting)
        {
            return;
        }

        _reconnecting = true;
        try
        {
            await _lan.PairAsync(
                _settings.LanPeerHost!,
                _settings.LanPeerPort,
                _settings.LanPairCode ?? string.Empty);
            UpdateLanButton();
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void UpdateLanButton()
    {
        var connected = _lan.IsConnected;
        LanDot.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        LanIcon.Foreground = (System.Windows.Media.Brush)FindResource(connected ? "Accent" : "TextMuted");
        LanButton.ToolTip = connected
            ? $"局域网：已连接 {_lan.Peer?.Name}"
            : _settings.LanEnabled ? "局域网模式（等待配对）" : "局域网模式（配对另一台电脑）";
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>收到对端消息：入队后串行合成播放，避免和本机请求抢设备。</summary>
    public void OnLanMessage(LanChatMessage message)
    {
        _remoteQueue.Enqueue(message);
        if (!_remotePumping)
        {
            _ = PumpRemoteQueueAsync();
        }
    }

    private async Task PumpRemoteQueueAsync()
    {
        _remotePumping = true;
        try
        {
            while (_remoteQueue.Count > 0)
            {
                while (_busy && _remoteQueue.Count > 0)
                {
                    await Task.Delay(200);
                }

                if (_remoteQueue.Count == 0)
                {
                    break;
                }

                var message = _remoteQueue.Dequeue();
                var voice = _catalog.Find(message.Voice ?? string.Empty) ?? _voice;
                var item = new TtsItem(
                    message.Text,
                    voice,
                    message.Rate,
                    message.Pitch,
                    message.Volume,
                    $"来自 {message.FromName}");

                _items.Add(item);
                ScrollToEnd();
                StatusText.Text = $"收到 {message.FromName} 的消息，正在合成…";

                await RunSynthesisAsync(item, clearInput: false);
            }
        }
        finally
        {
            _remotePumping = false;
        }
    }

    private async Task SendLanAsync(string text)
    {
        var message = new LanChatMessage(
            Guid.NewGuid().ToString("N"),
            LocalDeviceName,
            text,
            _voice.ShortName,
            (int)RateKnob.Value,
            (int)PitchKnob.Value,
            (int)VolumeKnob.Value,
            DateTimeOffset.Now);

        if (!await _lan.SendAsync(message))
        {
            StatusText.Text = "局域网消息没发出去，只在本机朗读了";
        }
    }

    // ================= 合成 =================

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CancelSynthesis();
            return;
        }

        await StartSynthesisAsync();
    }

    private async Task StartSynthesisAsync()
    {
        if (_busy)
        {
            return;
        }

        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var lanConnected = _lan.IsConnected;
        var item = new TtsItem(
            text,
            _voice,
            (int)RateKnob.Value,
            (int)PitchKnob.Value,
            (int)VolumeKnob.Value,
            lanConnected ? $"由 {LocalDeviceName} 发送" : null);

        _items.Add(item);
        ScrollToEnd();

        if (lanConnected)
        {
            _ = SendLanAsync(text);
        }

        await RunSynthesisAsync(item, clearInput: true);
    }

    /// <summary>失败卡片上的重试：用这张卡片自己的文本 / 音色 / 参数重来一次。</summary>
    private void CardRetry_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || item.State != TtsState.Failed)
        {
            return;
        }

        if (_busy)
        {
            StatusText.Text = "正在合成别的请求，稍等一下再重试";
            return;
        }

        if (ReferenceEquals(_playingItem, item))
        {
            _player.Stop();
            _playingItem = null;
        }

        item.Files.Clear();
        item.ErrorMessage = null;
        item.Progress = 0;
        item.State = TtsState.Synthesizing;

        _ = RunSynthesisAsync(item, clearInput: false);
    }

    private async Task RunSynthesisAsync(TtsItem item, bool clearInput)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "连接语音服务…";

        var stopwatch = Stopwatch.StartNew();

        void ApplyProgress(SynthesisProgress value)
        {
            // 分段的进度回调是排队投递的，结束后可能还有一次晚到，别覆盖最终状态
            if (item.State != TtsState.Synthesizing)
            {
                return;
            }

            item.Progress = value.Overall;
            StatusText.Text = value.TotalSegments > 1
                ? $"合成中 · 第 {Math.Min(value.DoneSegments + 1, value.TotalSegments)}/{value.TotalSegments} 段 · {value.Overall:P0}"
                : $"合成中 · {value.Overall:P0}";
        }

        // 不依赖 SynchronizationContext：回调线程一律回到界面线程再改状态
        var progress = new Progress<SynthesisProgress>(value =>
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyProgress(value);
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyProgress(value)));
            }
        });

        try
        {
            var result = await TtsService.SynthesizeAsync(
                item.Text,
                item.Voice.ShortName,
                item.Rate,
                item.Pitch,
                item.Volume,
                AppPaths.CacheDir,
                progress,
                _cts.Token);

            item.Files.Clear();
            item.Files.AddRange(result.Files);
            item.State = TtsState.Ready;
            item.Progress = 1;

            stopwatch.Stop();
            StatusText.Text = result.Files.Count > 1
                ? $"已生成 {result.Files.Count} 个片段 · 用时 {stopwatch.Elapsed.TotalSeconds:F1} 秒"
                : $"已生成 · 用时 {stopwatch.Elapsed.TotalSeconds:F1} 秒";

            if (clearInput)
            {
                InputBox.Clear();
            }

            StartPlayback(item);
        }
        catch (OperationCanceledException)
        {
            item.State = TtsState.Canceled;
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            item.State = TtsState.Failed;
            item.ErrorMessage = ex.Message;
            StatusText.Text = "合成失败：" + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void CancelSynthesis()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendIcon.Text = busy ? IconGlyphs.Stop : IconGlyphs.ArrowUpward;
        SendButton.ToolTip = busy ? "取消本次合成（Esc）" : "生成语音（Enter）";
        UpdateSendState();
    }

    private void UpdateSendState()
    {
        SendButton.IsEnabled = _busy || !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    // ================= 播放 =================

    private void CardPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            TogglePlay(item);
        }
    }

    private void TogglePlay(TtsItem item)
    {
        if (!item.CanPlay)
        {
            return;
        }

        if (ReferenceEquals(_playingItem, item))
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                StatusText.Text = "已暂停";
            }
            else
            {
                _player.Resume();
                StatusText.Text = "播放中：" + item.Voice.DisplayName;
            }

            return;
        }

        StartPlayback(item);
    }

    private void StartPlayback(TtsItem item)
    {
        if (!item.CanPlay)
        {
            return;
        }

        _player.Stop();

        if (_playingItem is not null)
        {
            _playingItem.IsPlaying = false;
        }

        _playingItem = item;
        item.Duration = TimeSpan.Zero;
        item.IsPlaying = true;

        _player.Play(item.Files);
        StatusText.Text = "播放中：" + item.Voice.DisplayName;
    }

    private void Player_PlayingChanged(object? sender, bool playing)
    {
        if (_playingItem is not null)
        {
            _playingItem.IsPlaying = playing;
        }
    }

    private void Player_DurationResolved(object? sender, TimeSpan duration)
    {
        if (_playingItem is not null)
        {
            _playingItem.Duration += duration;
        }
    }

    private void Player_Finished(object? sender, EventArgs e)
    {
        if (_playingItem is not null)
        {
            _playingItem.IsPlaying = false;
            _playingItem = null;
        }

        StatusText.Text = "播放完毕";
    }

    private void Player_Failed(object? sender, string message)
    {
        if (_playingItem is not null)
        {
            _playingItem.IsPlaying = false;
            _playingItem = null;
        }

        StatusText.Text = "播放失败：" + message;
    }

    // ================= 卡片操作 =================

    private static TtsItem? ItemOf(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as TtsItem;
    }

    private void CardDownload_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            DownloadItem(item);
        }
    }

    private void DownloadItem(TtsItem item)
    {
        if (!item.CanPlay)
        {
            return;
        }

        var folder = _settings.ResolveDownloadFolder();

        try
        {
            Directory.CreateDirectory(folder);

            // 用卡片创建时间做后缀：同一张卡片重复下载会覆盖同一批文件
            var baseName = AppPaths.SanitizeFileName(item.Text, 28) + "_"
                + item.CreatedAt.ToString("yyyyMMdd-HHmmss");
            var count = item.Files.Count;
            var overwritten = 0;

            for (var index = 0; index < count; index++)
            {
                var name = count > 1 ? $"{baseName}-{index + 1:00}" : baseName;
                var target = Path.Combine(folder, name + ".mp3");
                if (File.Exists(target))
                {
                    overwritten++;
                }

                File.Copy(item.Files[index], target, overwrite: true);
            }

            StatusText.Text = overwritten > 0
                ? $"已覆盖 {count} 个文件：{folder}"
                : $"已保存 {count} 个文件到 {folder}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void CardCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            try
            {
                Clipboard.SetText(item.Text);
                StatusText.Text = "原文已复制到剪贴板";
            }
            catch
            {
                StatusText.Text = "复制失败";
            }
        }
    }

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
        {
            return;
        }

        if (ReferenceEquals(_playingItem, item))
        {
            _player.Stop();
            _playingItem = null;
        }

        _items.Remove(item);

        // 只清程序自己的缓存；下载文件夹里已经存下来的文件一个都不碰
        var purged = DeleteCacheFiles(item);
        StatusText.Text = purged > 0
            ? "已删除这条记录（下载文件夹里的文件保留）"
            : "已删除这条记录";
    }

    private static int DeleteCacheFiles(TtsItem item)
    {
        var removed = 0;
        string cacheRoot;
        try
        {
            cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.CacheDir))
                + Path.DirectorySeparatorChar;
        }
        catch
        {
            return 0;
        }

        foreach (var file in item.Files)
        {
            try
            {
                var full = Path.GetFullPath(file);
                if (!full.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(full);
                removed++;
            }
            catch
            {
                // 正在播放的文件可能被占用，退出时统一清
            }
        }

        return removed;
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (_items.Count > 0)
                {
                    HistoryList.ScrollIntoView(_items[^1]);
                }
            }));
    }

    // ================= 设置 / 文件夹 =================

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings)
        {
            Owner = this,
        };

        window.ShowDialog();
        UpdateDownloadFolder();
        NativeTheme.Apply(this);
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _settings.ResolveDownloadFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开文件夹失败：" + ex.Message;
        }
    }

    // ================= 输入框 =================

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = string.IsNullOrEmpty(InputBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSendState();
    }

    private void InputBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AnimateComposer(ComposerExpandedHeight);
    }

    private void InputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            AnimateComposer(ComposerCollapsedHeight);
        }
    }

    private void AnimateComposer(double target)
    {
        var current = ComposerPanel.ActualHeight;
        if (double.IsNaN(current) || current <= 0)
        {
            current = ComposerPanel.Height;
        }

        if (Math.Abs(current - target) < 0.5)
        {
            return;
        }

        ComposerPanel.Height = current;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) => ComposerPanel.Height = target;
        ComposerPanel.BeginAnimation(HeightProperty, animation);
    }

    private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (await HandleComposerKeyAsync(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 输入框的按键语义：Enter 发送，Shift+Enter 换行，Ctrl+Enter 同样发送，Esc 取消合成。
    /// 返回 true 表示已经处理（调用方要 e.Handled = true，避免 TextBox 再插一个换行）。
    /// </summary>
    public async Task<bool> HandleComposerKeyAsync(Key key, ModifierKeys modifiers)
    {
        var control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (key == Key.Escape)
        {
            if (_busy)
            {
                CancelSynthesis();
                return true;
            }

            return false;
        }

        if (key == Key.Enter)
        {
            // Shift+Enter 交给 TextBox 自己插换行
            if (shift)
            {
                return false;
            }

            if (!_busy)
            {
                await StartSynthesisAsync();
            }

            return true;
        }

        if (key == Key.V && control)
        {
            // 粘贴超长文本时给出提示（分段合成已经兜底，这里只是让用户有数）
            if (Clipboard.ContainsText() && Clipboard.GetText().Length > 8000)
            {
                StatusText.Text = "文本较长，会自动按句子分段生成多个 mp3";
            }
        }

        return false;
    }

    // ================= 拖入 txt =================

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var targets = files
            .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .Take(10)
            .ToList();

        if (targets.Count == 0)
        {
            StatusText.Text = "只能拖入 .txt 文本文件";
            return;
        }

        var builder = new StringBuilder();
        foreach (var path in targets)
        {
            try
            {
                var content = await ReadTextFileAsync(path);
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.Append(content.Trim());
            }
            catch (Exception ex)
            {
                StatusText.Text = $"读取 {Path.GetFileName(path)} 失败：{ex.Message}";
                return;
            }
        }

        InputBox.Text = builder.ToString();
        InputBox.CaretIndex = InputBox.Text.Length;
        StatusText.Text = $"已载入 {targets.Count} 个文本文件";
        InputBox.Focus();
    }

    private static async Task<string> ReadTextFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 老式 GBK 文本：装了代码页提供程序才能解码，没有就按 UTF-8 尽力而为
            try
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
