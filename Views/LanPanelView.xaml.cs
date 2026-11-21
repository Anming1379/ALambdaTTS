using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ALambdaTTS.Services.Lan;

namespace ALambdaTTS.Views;

/// <summary>局域网面板：开关、本机名、配对码、发现设备（点一下就配对）、上次连接、断开。</summary>
public partial class LanPanelView : UserControl
{
    private readonly ObservableCollection<LanPeer> _peers = new();
    private bool _suppress;
    private string? _rememberedHost;
    private string? _rememberedName;
    private int _rememberedPort = LanService.DefaultPort;

    public LanPanelView()
    {
        InitializeComponent();
        PeerList.ItemsSource = _peers;
        RefreshPeerHint();
    }

    /// <summary>开关 / 名称 / 配对码 / 自动连接 改了。</summary>
    public event EventHandler<LanPanelValues>? ValuesChanged;

    /// <summary>点了「发现」。</summary>
    public event EventHandler? DiscoverRequested;

    /// <summary>点了某台发现的设备（点一下就直接配对）。</summary>
    public event EventHandler<LanPeer>? PairRequested;

    /// <summary>点了「直接连接」。</summary>
    public event EventHandler<LanManualTarget>? ManualPairRequested;

    /// <summary>点了「连接上次」。</summary>
    public event EventHandler<LanManualTarget>? ReconnectRequested;

    /// <summary>点了「忘记」。</summary>
    public event EventHandler? ForgetRequested;

    /// <summary>点了「断开」。</summary>
    public event EventHandler? DisconnectRequested;

    /// <summary>面板里按了 Esc。</summary>
    public event EventHandler? CloseRequested;

    public LanPanelValues Values => new(
        EnableToggle.IsChecked == true,
        NameBox.Text.Trim(),
        CodeBox.Text.Trim(),
        ParsePort(PortBox.Text),
        _rememberedHost,
        AutoToggle.IsChecked == true);

    /// <summary>把设置填进控件（打开面板时调用）。</summary>
    public void Load(LanPanelValues values, string? peerName, bool connected)
    {
        _suppress = true;
        EnableToggle.IsChecked = values.Enabled;
        AutoToggle.IsChecked = values.AutoConnect;
        NameBox.Text = values.DeviceName;
        CodeBox.Text = values.PairCode;
        PortBox.Text = values.Port.ToString();
        _suppress = false;

        SetRemembered(values.RememberedHost, values.Port, values.RememberedName);
        if (string.IsNullOrWhiteSpace(HostBox.Text) && !string.IsNullOrWhiteSpace(values.RememberedHost))
        {
            HostBox.Text = values.RememberedHost!;
        }

        SetPeer(peerName, connected);
        RefreshPeerHint();
    }

    /// <summary>服务状态变了。</summary>
    public void SetStatus(LanStatus status)
    {
        StatusText.Text = status.State switch
        {
            LanState.Connected => "已连接 · " + status.Peer?.Name,
            _ => status.Detail,
        };

        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            status.State == LanState.Connected ? "Accent" : "TextMuted");

        SetPeer(status.Peer?.Name, status.State == LanState.Connected);
    }

    public void AddPeer(LanPeer peer)
    {
        if (_peers.Any(p => p.DeviceId == peer.DeviceId || (p.Host == peer.Host && p.Port == peer.Port)))
        {
            return;
        }

        _peers.Add(peer);
        RefreshPeerHint();
    }

    public void ClearPeers()
    {
        _peers.Clear();
        RefreshPeerHint();
    }

    public void SetPeer(string? peerName, bool connected)
    {
        PeerText.Text = connected && !string.IsNullOrWhiteSpace(peerName)
            ? "当前连接：" + peerName
            : "还没有配对";
        DisconnectButton.IsEnabled = connected;
        UpdateRememberedRow(connected);
    }

    /// <summary>记住的对端（配对成功或启动时从设置里恢复）。</summary>
    public void SetRemembered(string? host, int port, string? name)
    {
        _rememberedHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        _rememberedPort = port is >= 1024 and <= 65535 ? port : LanService.DefaultPort;
        _rememberedName = name;
        UpdateRememberedRow(DisconnectButton.IsEnabled);
    }

    public void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void UpdateRememberedRow(bool connected)
    {
        var hasRemembered = !string.IsNullOrWhiteSpace(_rememberedHost);

        if (connected)
        {
            ReconnectButton.Visibility = Visibility.Collapsed;
            ForgetButton.Visibility = Visibility.Collapsed;
            return;
        }

        ReconnectButton.Visibility = hasRemembered ? Visibility.Visible : Visibility.Collapsed;
        ForgetButton.Visibility = hasRemembered ? Visibility.Visible : Visibility.Collapsed;
        ReconnectButton.Content = hasRemembered && !string.IsNullOrWhiteSpace(_rememberedName)
            ? $"连接 {_rememberedName}"
            : "连接上次";
        ReconnectButton.ToolTip = hasRemembered ? $"{_rememberedHost}:{_rememberedPort}" : null;

        if (hasRemembered && string.IsNullOrWhiteSpace(PeerText.Text.Replace("还没有配对", string.Empty, StringComparison.Ordinal)))
        {
            PeerText.Text = $"上次连接：{_rememberedName ?? _rememberedHost}（{_rememberedHost}:{_rememberedPort}）";
        }
    }

    private void RefreshPeerHint()
    {
        EmptyPeerHint.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnableToggle_Click(object sender, RoutedEventArgs e) => RaiseValues();

    private void AutoToggle_Click(object sender, RoutedEventArgs e) => RaiseValues();

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => RaiseValues();

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e) => RaiseValues();

    private void RaiseValues()
    {
        if (!_suppress)
        {
            ValuesChanged?.Invoke(this, Values);
        }
    }

    private void Discover_Click(object sender, RoutedEventArgs e)
    {
        ClearPeers();
        StatusText.Text = "正在搜索…";
        DiscoverRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>整行就是按钮：点哪儿都能连。</summary>
    private void PeerRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LanPeer peer)
        {
            StatusText.Text = $"正在连接 {peer.Name}…";
            PairRequested?.Invoke(this, peer);
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            StatusText.Text = "填一下对方的 IP";
            return;
        }

        ManualPairRequested?.Invoke(this, new LanManualTarget(host, ParsePort(PortBox.Text)));
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_rememberedHost))
        {
            ReconnectRequested?.Invoke(this, new LanManualTarget(_rememberedHost!, _rememberedPort));
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        SetRemembered(null, LanService.DefaultPort, null);
        PeerText.Text = "还没有配对";
        ForgetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
        => DisconnectRequested?.Invoke(this, EventArgs.Empty);

    private static int ParsePort(string text)
        => int.TryParse(text, out var port) && port is >= 1024 and <= 65535 ? port : LanService.DefaultPort;
}

/// <summary>面板上可以改的那几项。</summary>
public sealed record LanPanelValues(
    bool Enabled,
    string DeviceName,
    string PairCode,
    int Port,
    string? RememberedHost,
    bool AutoConnect,
    string? RememberedName = null);

/// <summary>手动连接目标。</summary>
public sealed record LanManualTarget(string Host, int Port);
