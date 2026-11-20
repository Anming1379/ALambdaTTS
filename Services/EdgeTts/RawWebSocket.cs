using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ALambdaTTS.Services.EdgeTts;

public enum RawWebSocketOpcode
{
    Continuation = 0,
    Text = 1,
    Binary = 2,
    Close = 8,
    Ping = 9,
    Pong = 10,
}

/// <summary>
/// 极小号 WebSocket 客户端（TLS + 帧读写）。
///
/// 为什么不用 System.Net.WebSockets.ClientWebSocket：微软的 Edge 朗读接口
/// 对它发出的帧不买账（回 "Bad request" 然后断开），用自己写的帧读写
/// 与官方 python 客户端走完全相同的字节，行为稳定可预期。
/// </summary>
public sealed class RawWebSocket : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private RawWebSocket(TcpClient tcp, SslStream ssl)
    {
        _tcp = tcp;
        _ssl = ssl;
    }

    public static async Task<RawWebSocket> ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(uri.Host, uri.Port, cancellationToken).ConfigureAwait(false);

        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = uri.Host },
            cancellationToken).ConfigureAwait(false);

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var builder = new StringBuilder();
        builder.Append("GET ").Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
        builder.Append("Host: ").Append(uri.Host).Append("\r\n");
        foreach (var (name, value) in headers)
        {
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        builder.Append("Connection: Upgrade\r\n");
        builder.Append("Upgrade: websocket\r\n");
        builder.Append("Sec-WebSocket-Version: 13\r\n");
        builder.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n\r\n");

        var request = Encoding.ASCII.GetBytes(builder.ToString());
        await ssl.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await ssl.FlushAsync(cancellationToken).ConfigureAwait(false);

        var status = await ReadHandshakeAsync(ssl, cancellationToken).ConfigureAwait(false);
        if (!status.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
        {
            ssl.Dispose();
            tcp.Dispose();
            throw new EdgeTtsException($"语音服务握手失败：{status.Trim()}");
        }

        return new RawWebSocket(tcp, ssl);
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        await SendFrameAsync(RawWebSocketOpcode.Text, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读一条完整消息；控制帧（ping/pong/close）在这里就地处理。</summary>
    public async Task<(RawWebSocketOpcode Opcode, byte[] Payload)> ReceiveMessageAsync(
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream(64 * 1024);
        var opcode = RawWebSocketOpcode.Text;

        while (true)
        {
            var (fin, frameOpcode, payload) = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);

            switch (frameOpcode)
            {
                case RawWebSocketOpcode.Ping:
                    await SendFrameAsync(RawWebSocketOpcode.Pong, payload, cancellationToken).ConfigureAwait(false);
                    continue;

                case RawWebSocketOpcode.Pong:
                    continue;

                case RawWebSocketOpcode.Close:
                    return (RawWebSocketOpcode.Close, payload);

                case RawWebSocketOpcode.Continuation:
                    message.Write(payload, 0, payload.Length);
                    break;

                default:
                    opcode = frameOpcode;
                    message.Write(payload, 0, payload.Length);
                    break;
            }

            if (fin)
            {
                return (opcode, message.ToArray());
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _ssl.Dispose();
        }
        catch
        {
        }

        _tcp.Dispose();
        _sendLock.Dispose();
    }

    private async Task SendFrameAsync(RawWebSocketOpcode opcode, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[14];
        var headerLength = 0;

        header[headerLength++] = (byte)(0x80 | (byte)opcode);

        if (payload.Length < 126)
        {
            header[headerLength++] = (byte)(0x80 | payload.Length);
        }
        else if (payload.Length <= 0xFFFF)
        {
            header[headerLength++] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(headerLength), (ushort)payload.Length);
            headerLength += 2;
        }
        else
        {
            header[headerLength++] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(headerLength), (ulong)payload.Length);
            headerLength += 8;
        }

        var mask = RandomNumberGenerator.GetBytes(4);
        mask.CopyTo(header, headerLength);
        headerLength += 4;

        var masked = new byte[payload.Length];
        for (var index = 0; index < payload.Length; index++)
        {
            masked[index] = (byte)(payload[index] ^ mask[index % 4]);
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _ssl.WriteAsync(header.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
            if (masked.Length > 0)
            {
                await _ssl.WriteAsync(masked, cancellationToken).ConfigureAwait(false);
            }

            await _ssl.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<(bool Fin, RawWebSocketOpcode Opcode, byte[] Payload)> ReadFrameAsync(
        CancellationToken cancellationToken)
    {
        var head = new byte[2];
        await ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);

        var fin = (head[0] & 0x80) != 0;
        var opcode = (RawWebSocketOpcode)(head[0] & 0x0F);
        var masked = (head[1] & 0x80) != 0;
        long length = head[1] & 0x7F;

        if (length == 126)
        {
            var extended = new byte[2];
            await ReadExactlyAsync(extended, cancellationToken).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReadExactlyAsync(extended, cancellationToken).ConfigureAwait(false);
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(extended);
        }

        byte[]? mask = null;
        if (masked)
        {
            mask = new byte[4];
            await ReadExactlyAsync(mask, cancellationToken).ConfigureAwait(false);
        }

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        if (mask is not null)
        {
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] ^= mask[index % 4];
            }
        }

        return (fin, opcode, payload);
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _ssl.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EdgeTtsException("语音服务提前关闭了连接。");
            }

            offset += read;
        }
    }

    private static async Task<string> ReadHandshakeAsync(SslStream ssl, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];

        while (true)
        {
            var read = await ssl.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EdgeTtsException("语音服务在握手阶段关闭了连接。");
            }

            buffer.Add(single[0]);
            var count = buffer.Count;
            if (count >= 4
                && buffer[count - 4] == '\r'
                && buffer[count - 3] == '\n'
                && buffer[count - 2] == '\r'
                && buffer[count - 1] == '\n')
            {
                break;
            }

            if (count > 16 * 1024)
            {
                throw new EdgeTtsException("语音服务握手响应过长。");
            }
        }

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        var firstLine = text.Split("\r\n", 2, StringSplitOptions.None)[0];
        return firstLine;
    }
}
