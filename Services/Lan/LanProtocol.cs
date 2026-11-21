using System.IO;
using System.Text;
using System.Text.Json;

namespace ALambdaTTS.Services.Lan;

/// <summary>
/// 局域网帧：TCP 上跑的就是它，4 字节长度前缀 + UTF-8 JSON。
/// 字段故意留宽一点，用不到的填 null，两端都是同一个程序所以不怕冗余。
/// </summary>
public sealed class LanFrame
{
    public const int ProtocolVersion = 1;

    /// <summary>hello / pair / pair-ok / pair-failed / message / ping / pong / bye / discover / discover-reply</summary>
    public string Type { get; set; } = string.Empty;

    public int Protocol { get; set; } = ProtocolVersion;

    public string? DeviceId { get; set; }

    public string? Name { get; set; }

    /// <summary>配对码（明文只在局域网里传，够用了）。</summary>
    public string? Code { get; set; }

    public string? Id { get; set; }

    public string? Text { get; set; }

    public string? Voice { get; set; }

    public int Rate { get; set; }

    public int Pitch { get; set; }

    public int Volume { get; set; } = 100;

    public long SentAt { get; set; }

    /// <summary>监听端口（发现报文里告诉对方连哪）。</summary>
    public int Port { get; set; }
}

/// <summary>局域网帧的编解码。</summary>
public static class LanCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(LanFrame frame)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, Options);
        var buffer = new byte[payload.Length + 4];
        var length = payload.Length;
        buffer[0] = (byte)(length >> 24);
        buffer[1] = (byte)(length >> 16);
        buffer[2] = (byte)(length >> 8);
        buffer[3] = (byte)length;
        Buffer.BlockCopy(payload, 0, buffer, 4, payload.Length);
        return buffer;
    }

    public static LanFrame? Decode(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<LanFrame>(payload, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读一帧；对端正常关闭返回 null。</summary>
    public static async Task<LanFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length is <= 0 or > 4 * 1024 * 1024)
        {
            return null;
        }

        var payload = new byte[length];
        if (!await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Decode(payload);
    }

    public static async Task WriteAsync(Stream stream, LanFrame frame, CancellationToken cancellationToken)
    {
        var buffer = Encode(frame);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    /// <summary>发现报文用 UDP 发，同样是这一套编码。</summary>
    public static byte[] EncodeDatagram(LanFrame frame) => JsonSerializer.SerializeToUtf8Bytes(frame, Options);
}

/// <summary>发现 / 配对 / 已连接的对端信息。</summary>
public sealed record LanPeer(string DeviceId, string Name, string Host, int Port)
{
    public string Label => $"{Name}（{Host}:{Port}）";
}

/// <summary>收到的一条局域网消息。</summary>
public sealed record LanChatMessage(
    string Id,
    string FromName,
    string Text,
    string? Voice,
    int Rate,
    int Pitch,
    int Volume,
    DateTimeOffset SentAt);

public enum LanState
{
    Stopped,
    Listening,
    Pairing,
    Connected,
}

/// <summary>状态变化（供界面显示）。</summary>
public sealed record LanStatus(LanState State, string Detail, LanPeer? Peer = null);
