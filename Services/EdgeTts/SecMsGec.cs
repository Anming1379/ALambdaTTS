using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ALambdaTTS.Services.EdgeTts;

/// <summary>
/// 微软 Edge 朗读接口的 DRM 令牌（Sec-MS-GEC v2）。
///
/// 2026 年起服务端只认 v2 令牌（格式 "16位CS64-64位SHA256"），
/// 现成的 C# 封装（EdgeTTS.DotNet 0.4.0）还在发 v1，握手会 403，
/// 因此这里按官方 python 实现（edge-tts 7.3.1 的 drm.py）逐行移植。
/// </summary>
public static class SecMsGec
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    /// <summary>与 python 实现保持一致的 Chromium 版本号。</summary>
    public const string ChromiumFullVersion = "143.0.3650.75";

    private const double WinEpoch = 11644473600.0;

    private const uint Cs64Factor = 0x82893933;
    private const uint Cs64Adjustment0 = 0xF2F90B1A;
    private const uint Cs64Adjustment1 = 0x0B1A0000;
    private const uint Sha256Adjustment = 0xC9DC06A7;
    private const uint Sigma0Adjustment = 0x059C0B80;
    private const uint Sigma1Adjustment = 0x000B92C0;

    /// <summary>查询参数 Sec-MS-GEC-Version 的值。</summary>
    public static string GecVersion => "2-" + ChromiumFullVersion;

    private static readonly uint[] Sha256Initial =
    [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    ];

    private static readonly uint[] Sha256State =
    [
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
    ];

    private static readonly uint[] Sha256Schedule =
    [
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
        0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564,
    ];

    private static readonly uint[] RoundConstants =
    [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ];

    /// <summary>首块从第 14 轮开始跑，第 15 个常量被替换过。</summary>
    private static readonly uint[] BlockConstants = BuildBlockConstants();

    /// <summary>CS64 系数与偏移量（来自 python 实现）。</summary>
    private static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
    [
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038),
        (0xC89C5933, 0xB328CBDF), (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C),
        (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB), (0x06064CF3, 0xFA51A3DA),
        (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    ];

    public static string Generate() => Generate(DateTimeOffset.UtcNow);

    public static string Generate(DateTimeOffset now) => Generate(now.ToUnixTimeMilliseconds() / 1000.0);

    /// <summary>按 unix 时间戳生成令牌（同一 5 分钟窗口内结果相同）。</summary>
    public static string Generate(double unixTimestamp)
    {
        var ticks = unixTimestamp + WinEpoch;
        ticks -= ticks % 300.0;
        ticks *= 1e7;

        var payload = ticks.ToString("F0", CultureInfo.InvariantCulture) + TrustedClientToken;
        var data = Encoding.ASCII.GetBytes(payload);
        var digest = Sha256Digest(data);
        var (cs64A, cs64B) = Cs64Digest(data, digest);

        Span<byte> cs64 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(cs64, cs64A);
        BinaryPrimitives.WriteUInt32LittleEndian(cs64[4..], cs64B);

        return Convert.ToHexString(cs64) + "-" + Convert.ToHexString(digest);
    }

    private static uint[] BuildBlockConstants()
    {
        var copy = (uint[])RoundConstants.Clone();
        copy[14] = Sha256Adjustment;
        return copy;
    }

    private static byte[] Sha256Digest(byte[] data)
    {
        if (data.Length < 7)
        {
            throw new ArgumentException("令牌数据至少需要 7 个字节。", nameof(data));
        }

        var length = 57 + data.Length;
        var padCount = ((55 - length) % 64 + 64) % 64;
        var padded = new byte[data.Length - 7 + 1 + padCount + 8];

        Array.Copy(data, 7, padded, 0, data.Length - 7);
        padded[data.Length - 7] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8), (ulong)(length * 8));

        var state = Sha256Init(data.AsSpan(0, 7));
        var words = new uint[64];

        for (var offset = 0; offset < padded.Length; offset += 64)
        {
            for (var index = 0; index < 16; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(offset + index * 4, 4));
            }

            ExpandSchedule(words, 16);
            var working = Rounds(state, words, 0);
            for (var index = 0; index < 8; index++)
            {
                state[index] = unchecked(state[index] + working[index]);
            }
        }

        var result = new byte[32];
        for (var index = 0; index < 8; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(index * 4, 4), state[index]);
        }

        return result;
    }

    private static uint[] Sha256Init(ReadOnlySpan<byte> head)
    {
        var words = new uint[64];
        words[14] = (uint)((head[0] << 16) | (head[1] << 8) | head[2]);
        words[15] = (uint)((head[3] << 24) | (head[4] << 16) | (head[5] << 8) | head[6]);

        for (var index = 16; index < 30; index++)
        {
            var value = Sha256Schedule[index - 16];

            if (index - 2 >= 14)
            {
                var sigma1 = Sigma1(words[index - 2]);
                value = unchecked(value + (index == 16 ? sigma1 ^ Sigma1Adjustment : sigma1));
            }

            if (index - 7 >= 14)
            {
                value = unchecked(value + words[index - 7]);
            }

            if (index - 15 >= 14)
            {
                var sigma0 = Sigma0(words[index - 15]);
                value = unchecked(value + (index == 29 ? sigma0 ^ Sigma0Adjustment : sigma0));
            }

            words[index] = value;
        }

        words[30] = unchecked(
            Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14] + Sha256Adjustment - RoundConstants[14]);

        ExpandSchedule(words, 31);

        var working = Rounds(Sha256State, words, 14);
        var state = new uint[8];
        for (var index = 0; index < 8; index++)
        {
            state[index] = unchecked(Sha256Initial[index] + working[index]);
        }

        return state;
    }

    private static void ExpandSchedule(uint[] words, int firstWord)
    {
        for (var index = firstWord; index < 64; index++)
        {
            var x = words[index - 2];
            var y = words[index - 15];
            var sigma1 = (x >> 17) ^ (x << 15) ^ (x >> 19) ^ (x << 13) ^ (x >> 10);
            var sigma0 = (y >> 7) ^ (y << 25) ^ (y >> 18) ^ (y << 14) ^ (y >> 3);
            words[index] = unchecked(sigma1 + words[index - 7] + sigma0 + words[index - 16]);
        }
    }

    private static uint[] Rounds(uint[] state, uint[] words, int firstRound)
    {
        var constants = firstRound == 14 ? BlockConstants : RoundConstants;

        var a = state[0];
        var b = state[1];
        var c = state[2];
        var d = state[3];
        var e = state[4];
        var f = state[5];
        var g = state[6];
        var h = state[7];

        for (var index = firstRound; index < 64; index++)
        {
            var s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
            var ch = g ^ (e & (f ^ g));
            var t1 = unchecked(h + s1 + ch + constants[index] + words[index]);
            var s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
            var maj = (a & b) | (c & (a | b));
            var t2 = unchecked(s0 + maj);

            h = g;
            g = f;
            f = e;
            e = unchecked(d + t1);
            d = c;
            c = b;
            b = a;
            a = unchecked(t1 + t2);
        }

        return [a, b, c, d, e, f, g, h];
    }

    private static (uint, uint) Cs64Digest(byte[] data, byte[] digest)
    {
        var keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(0, 4)) | 1u;
        var keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4, 4)) | 1u;

        var factorA = unchecked(keyA * Cs64Factor);
        var factorB = unchecked(keyB * Cs64Factor);
        uint[] factors = [factorA, factorB];

        var terms = new List<uint>(Cs64Coefficients.Length + 32);
        var offsets = new List<uint>(Cs64Coefficients.Length + 32);

        for (var index = 0; index < Cs64Coefficients.Length; index++)
        {
            terms.Add(unchecked(Cs64Coefficients[index].Coefficient * factors[index % 2]));
            offsets.Add(Cs64Coefficients[index].Offset);
        }

        foreach (var (index, word) in WordsLe4Even(data))
        {
            var term = unchecked(word * (index % 2 == 0 ? keyA : keyB));
            var offset = unchecked(Rol16(word) * (index % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu));

            if (index == 0)
            {
                term = unchecked(term + Cs64Adjustment0 * factorA);
                offset = unchecked(offset + Cs64Adjustment1);
            }

            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;

        for (var index = 0; index < terms.Count; index += 2)
        {
            var x = terms[index];
            var y = terms[index + 1];

            var t = Rol16(unchecked(offsets[index] + x));
            var q = unchecked(t * 0x76F50F34u);
            swapAcc = unchecked(swapAcc + unchecked(Rol16(q) * 0xC3EFEA97u + q));

            t = Rol16(unchecked(offsets[index + 1] + y));
            q = unchecked(t * 0x1881CE3Cu);
            swapLast = unchecked(Rol16(q) * 0x35BD1EC9u + q);
            swapAcc = unchecked(swapAcc + swapLast);

            state = unchecked(state * keyA + x);
            state = unchecked(Rol16(state) * 0xCF98B111u);
            state = unchecked(Rol16(state) * 0x87085B9Fu);
            state = unchecked(Rol16(state) * 0x12CEB96Du);
            state = unchecked(Rol16(state) * 0x257E1D83u);
            acc = unchecked(acc + state);

            t = unchecked(state * keyB + y);
            t = unchecked(Rol16(t) * 0xA27416F5u);
            t = unchecked(Rol16(t) * 0xD38396FFu);
            state = unchecked(Rol16(unchecked(Rol16(t) * 0x7C932B89u)) * 0xBFA49F69u);
            acc = unchecked(acc + state);
        }

        return (swapLast ^ state, swapAcc ^ acc);
    }

    /// <summary>数据前补一个 0 字节，按小端 32 位切词，词数为奇数时补 0。</summary>
    private static IEnumerable<(int Index, uint Word)> WordsLe4Even(byte[] data)
    {
        var buffer = new byte[data.Length + 1];
        data.CopyTo(buffer, 1);

        var padded = buffer.Length % 4 == 0 ? buffer : new byte[buffer.Length + 4 - buffer.Length % 4];
        buffer.CopyTo(padded, 0);

        var count = padded.Length / 4;
        var words = new uint[count % 2 == 0 ? count : count + 1];

        for (var index = 0; index < count; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(index * 4, 4));
        }

        for (var index = 0; index < words.Length; index++)
        {
            yield return (index, words[index]);
        }
    }

    private static uint Rol16(uint value) => (value << 16) | (value >> 16);

    private static uint Rotr(uint value, int bits) => (value >> bits) | (value << (32 - bits));

    private static uint Sigma0(uint value)
        => (value >> 7) ^ (value << 25) ^ (value >> 18) ^ (value << 14) ^ (value >> 3);

    private static uint Sigma1(uint value)
        => (value >> 17) ^ (value << 15) ^ (value >> 19) ^ (value << 13) ^ (value >> 10);
}
