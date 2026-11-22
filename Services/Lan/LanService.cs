using System.Net.Sockets;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ALambdaTTS.Services.Lan;

/// <summary>
/// 局域网直连：UDP 广播发现 + TCP 配对 + 双向消息。
/// 走的是普通 IP，所以 Radmin VPN / ZeroTier / 热点 这类虚拟局域网一样能用；
/// 广播不通时还可以手填对方 IP 直接连。
/// </summary>
public sealed class LanService : IAsyncDisposable
{
    public const int DefaultPort = 45790;
    public const int DefaultDiscoveryPort = 45789;

    private readonly string _deviceId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private UdpClient? _discoveryListener;
    private TcpClient? _peerClient;
    private Stream? _peerStream;
    private LanPeer? _peer;
    private LanState _state = LanState.Stopped;
    private string _name = "ALambda TTS";
    private string _code = string.Empty;
    private int _port = DefaultPort;
    private long _lastPongTicks;

    /// <summary>发现了局域网里同样开着本程序的设备。</summary>
    public event EventHandler<LanPeer>? PeerDiscovered;

    /// <summary>状态变化（未启动 / 等待配对 / 配对中 / 已连接）。</summary>
    public event EventHandler<LanStatus>? StatusChanged;

    /// <summary>收到对端发来的消息。</summary>
    public event EventHandler<LanChatMessage>? MessageReceived;

    public LanState State
    {
        get { lock (_gate) { return _state; } }
    }

    public LanPeer? Peer
    {
        get { lock (_gate) { return _peer; } }
    }

    public string DeviceName
    {
        get { lock (_gate) { return _name; } }
    }

    public int Port
    {
        get { lock (_gate) { return _port; } }
    }

    public string DeviceId => _deviceId;

    public bool IsConnected => State == LanState.Connected && _peerStream is not null;

    /// <summary>启动监听（幂等）。name 是本机显示名，code 是配对码。</summary>
    public void Start(string name, int port, string code)
    {
        lock (_gate)
        {
            _name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
            _code = code.Trim();
            if (port is < 1024 or > 65535)
            {
                port = DefaultPort;
            }

            if (_cts is not null && port == _port)
            {
                return;
            }
        }

        if (_cts is not null)
        {
            StopAsync().GetAwaiter().GetResult();
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _port = port;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            SetState(LanState.Listening, "等待配对");
            _ = AcceptLoopAsync(listener, cts.Token);
            _ = DiscoveryResponderAsync(cts.Token);
        }
        catch (SocketException error)
        {
            SetState(LanState.Stopped, $"端口 {port} 被占用：{error.Message}");
            cts.Dispose();
            lock (_gate)
            {
                _cts = null;
            }
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
        }

        await SafeSayByeAsync().ConfigureAwait(false);
        DropPeer("已断开");
        StopListener();
        cts.Dispose();
        SetState(LanState.Stopped, "未启用");
    }

    /// <summary>UDP 广播找一圈设备，timeout 内收到的都返回。</summary>
    public async Task<IReadOnlyList<LanPeer>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var found = new List<LanPeer>();
        int port;
        string name;
        lock (_gate)
        {
            port = _port;
            name = _name;
        }

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = LanCodec.EncodeDatagram(new LanFrame
        {
            Type = "discover",
            DeviceId = _deviceId,
            Name = name,
            Port = port,
        });

        foreach (var target in BroadcastTargets())
        {
            try
            {
                await udp.SendAsync(request, target, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 某个网卡发不出去不影响别的
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = deadline - DateTime.UtcNow;
            receiveTimeout.CancelAfter(remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : remaining);

            try
            {
                var result = await udp.ReceiveAsync(receiveTimeout.Token).ConfigureAwait(false);
                var frame = LanCodec.Decode(result.Buffer);
                if (frame is null || frame.Type != "discover-reply" || frame.DeviceId == _deviceId)
                {
                    continue;
                }

                var peer = new LanPeer(
                    frame.DeviceId ?? result.RemoteEndPoint.Address.ToString(),
                    string.IsNullOrWhiteSpace(frame.Name) ? result.RemoteEndPoint.Address.ToString() : frame.Name!,
                    result.RemoteEndPoint.Address.ToString(),
                    frame.Port > 0 ? frame.Port : DefaultPort);

                if (found.Any(p => p.DeviceId == peer.DeviceId || (p.Host == peer.Host && p.Port == peer.Port)))
                {
                    continue;
                }

                found.Add(peer);
                PeerDiscovered?.Invoke(this, peer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>主动配对：连上去、报名字和配对码，等对方回 pair-ok。</summary>
    public async Task<bool> PairAsync(string host, int port, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (port is < 1024 or > 65535)
        {
            port = DefaultPort;
        }

        SetState(LanState.Pairing, $"正在连接 {host}:{port}");

        TcpClient? client = null;
        try
        {
            client = new TcpClient { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            string name;
            lock (_gate)
            {
                name = _name;
            }

            var hello = new LanFrame
            {
                Type = "hello",
                DeviceId = _deviceId,
                Name = name,
                Port = Port,
            };

            await LanCodec.WriteAsync(stream, hello, timeout.Token).ConfigureAwait(false);
            await LanCodec.WriteAsync(
                stream,
                new LanFrame { Type = "pair", DeviceId = _deviceId, Name = name, Code = code, Port = Port },
                timeout.Token).ConfigureAwait(false);

            var reply = await LanCodec.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (reply is null || reply.Type != "pair-ok")
            {
                client.Dispose();
                SetState(LanState.Listening, reply?.Type == "pair-failed" ? "配对码不对" : "对方没有回应");
                return false;
            }

            var peer = new LanPeer(
                reply.DeviceId ?? host,
                string.IsNullOrWhiteSpace(reply.Name) ? host : reply.Name!,
                host,
                port);

            AttachPeer(client, stream, peer);
            return true;
        }
        catch (Exception error)
        {
            client?.Dispose();
            SetState(LanState.Listening, $"连接失败：{Trim(error.Message)}");
            return false;
        }
    }

    /// <summary>给对端发一条消息。没连上返回 false。</summary>
    public async Task<bool> SendAsync(LanChatMessage message, CancellationToken cancellationToken = default)
    {
        var stream = _peerStream;
        if (stream is null)
        {
            return false;
        }

        var frame = new LanFrame
        {
            Type = "message",
            Id = message.Id,
            DeviceId = _deviceId,
            Name = message.FromName,
            Text = message.Text,
            Voice = message.Voice,
            Rate = message.Rate,
            Pitch = message.Pitch,
            Volume = message.Volume,
            SentAt = message.SentAt.ToUnixTimeMilliseconds(),
            Port = Port,
        };

        return await WriteAsync(stream, frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>主动断开（会让对端回到等待配对状态）。</summary>
    public async Task DisconnectAsync()
    {
        await SafeSayByeAsync().ConfigureAwait(false);
        DropPeer("已断开");
        if (_cts is not null)
        {
            SetState(LanState.Listening, "等待配对");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ================= 内部 =================

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = HandleIncomingAsync(client, cancellationToken);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            if (IsConnected)
            {
                client.Dispose();
                return;
            }

            client.NoDelay = true;
            var stream = client.GetStream();

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));

            var hello = await LanCodec.ReadAsync(stream, handshake.Token).ConfigureAwait(false);
            if (hello is null || hello.Type != "hello")
            {
                client.Dispose();
                return;
            }

            var pair = await LanCodec.ReadAsync(stream, handshake.Token).ConfigureAwait(false);
            string code;
            string name;
            lock (_gate)
            {
                code = _code;
                name = _name;
            }

            var accepted = pair is { Type: "pair" }
                && (string.IsNullOrEmpty(code) || string.Equals(pair.Code, code, StringComparison.Ordinal));

            if (!accepted)
            {
                await WriteAsync(stream, new LanFrame { Type = "pair-failed", Name = name }, handshake.Token)
                    .ConfigureAwait(false);
                client.Dispose();
                SetState(LanState.Listening, "有一台设备配对码不对，已拒绝");
                return;
            }

            var remoteHost = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            var peer = new LanPeer(
                pair!.DeviceId ?? hello.DeviceId ?? remoteHost,
                string.IsNullOrWhiteSpace(pair.Name) ? hello.Name ?? remoteHost : pair.Name!,
                remoteHost,
                pair.Port > 0 ? pair.Port : DefaultPort);

            await WriteAsync(
                stream,
                new LanFrame { Type = "pair-ok", DeviceId = _deviceId, Name = name, Port = Port },
                handshake.Token).ConfigureAwait(false);

            AttachPeer(client, stream, peer);
        }
        catch
        {
            client.Dispose();
        }
    }

    private void AttachPeer(TcpClient client, Stream stream, LanPeer peer)
    {
        lock (_gate)
        {
            _peerClient = client;
            _peerStream = stream;
            _peer = peer;
            _lastPongTicks = Environment.TickCount64;
        }

        SetState(LanState.Connected, $"已连接 {peer.Name}", peer);

        var token = _cts?.Token ?? CancellationToken.None;
        _ = ReadLoopAsync(stream, token);
        _ = HeartbeatAsync(token);
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await LanCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Type)
                {
                    case "message":
                        HandleMessage(frame);
                        break;
                    case "ping":
                        await WriteAsync(stream, new LanFrame { Type = "pong", Name = DeviceName }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case "pong":
                        lock (_gate)
                        {
                            _lastPongTicks = Environment.TickCount64;
                        }

                        break;
                    case "bye":
                        DropPeer("对端断开了");
                        return;
                }
            }
        }
        catch
        {
        }

        DropPeer("连接中断");
    }

    private void HandleMessage(LanFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.Text))
        {
            return;
        }

        var peer = Peer;
        var message = new LanChatMessage(
            frame.Id ?? Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(frame.Name) ? peer?.Name ?? "对端" : frame.Name!,
            frame.Text!,
            frame.Voice,
            frame.Rate,
            frame.Pitch,
            Math.Clamp(frame.Volume, 0, 100),
            frame.SentAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(frame.SentAt)
                : DateTimeOffset.Now);

        MessageReceived?.Invoke(this, message);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (!IsConnected)
            {
                return;
            }

            var idle = Environment.TickCount64 - Interlocked.Read(ref _lastPongTicks);
            if (idle > 20_000)
            {
                DropPeer("心跳超时");
                return;
            }

            var stream = _peerStream;
            if (stream is not null)
            {
                await WriteAsync(stream, new LanFrame { Type = "ping", Name = DeviceName }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>回应局域网里的广播（这样对面点「发现设备」就能看到我）。</summary>
    private async Task DiscoveryResponderAsync(CancellationToken cancellationToken)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultDiscoveryPort));
            udp.EnableBroadcast = true;
        }
        catch
        {
            return;
        }

        _discoveryListener = udp;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var frame = LanCodec.Decode(result.Buffer);
                if (frame is null || frame.Type != "discover" || frame.DeviceId == _deviceId)
                {
                    continue;
                }

                var reply = LanCodec.EncodeDatagram(new LanFrame
                {
                    Type = "discover-reply",
                    DeviceId = _deviceId,
                    Name = DeviceName,
                    Port = Port,
                });

                await udp.SendAsync(reply, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
        }

        try
        {
            udp.Dispose();
        }
        catch
        {
        }
    }

    private async Task<bool> WriteAsync(Stream stream, LanFrame frame, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LanCodec.WriteAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SafeSayByeAsync()
    {
        var stream = _peerStream;
        if (stream is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await WriteAsync(stream, new LanFrame { Type = "bye", Name = DeviceName }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void DropPeer(string detail)
    {
        TcpClient? client;
        lock (_gate)
        {
            client = _peerClient;
            _peerClient = null;
            _peerStream = null;
            _peer = null;
        }

        try
        {
            client?.Dispose();
        }
        catch
        {
        }

        if (_cts is not null)
        {
            SetState(LanState.Listening, detail);
        }
    }

    private void StopListener()
    {
        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        _listener = null;

        try
        {
            _discoveryListener?.Dispose();
        }
        catch
        {
        }

        _discoveryListener = null;
    }

    private void SetState(LanState state, string detail, LanPeer? peer = null)
    {
        lock (_gate)
        {
            _state = state;
        }

        StatusChanged?.Invoke(this, new LanStatus(state, detail, peer));
    }

    /// <summary>255.255.255.255 加上各网卡的定向广播（Radmin 这类虚拟网卡要靠后者）。</summary>
    private static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, DefaultDiscoveryPort);

        foreach (var nic in SafeNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask is [0, 0, 0, 0])
                {
                    continue;
                }

                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    broadcast[i] = (byte)(address[i] | (mask[i] ^ 0xFF));
                }

                var target = new IPAddress(broadcast);
                if (!target.Equals(unicast.Address))
                {
                    yield return new IPEndPoint(target, DefaultDiscoveryPort);
                }
            }
        }
    }

    private static IEnumerable<NetworkInterface> SafeNetworkInterfaces()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            yield return nic;
        }
    }

    private static string Trim(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 60 ? single : single[..60] + "…";
    }
}
