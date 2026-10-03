using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Watching.Common;

namespace Watching.Server;

/// <summary>帧在 WebSocket 之外复用的一套自描述二进制封装（HTTP 抓图接口也用）。</summary>
public static class FramePacket
{
    public const int MetaSize = 256;
    public const int HeaderSize = 4 + 8 + MetaSize; // magic + seq + meta

    /// <summary>增量包的分块表魔数。</summary>
    public const string TileMagic = "DT01";
    /// <summary>分块表表头长度：4 字节魔数 + 4 字节分块数。</summary>
    public const int TileTableHeader = 8;
    /// <summary>每个分块表项长度：x(2) + y(2) + 长度(4)。</summary>
    public const int TileEntrySize = 8;

    public static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public sealed class Meta
    {
        public int w { get; set; }
        public int h { get; set; }
        public int sw { get; set; }
        public int sh { get; set; }
        public int q { get; set; }
        public bool crop { get; set; }
        public long ts { get; set; }
        public string mode { get; set; }
        public double capMs { get; set; }
        public double encMs { get; set; }
        /// <summary>分块边长（mode=delta 时客户端用它定位每块）。</summary>
        public int tile { get; set; }
        /// <summary>横向分块数。</summary>
        public int tx { get; set; }
        /// <summary>纵向分块数。</summary>
        public int ty { get; set; }

        /// <summary>是否是增量帧。</summary>
        public bool IsDelta => string.Equals(mode, "delta", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一个变化分块。</summary>
    public sealed class Tile
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Length { get; init; }
        public int JpegOffset { get; init; }
    }

    /// <summary>解析结果（两种帧统一表示）。</summary>
    public sealed class Parsed
    {
        public long Sequence { get; init; }
        public Meta Meta { get; init; }

        /// <summary>原始整包数据（分块 JPEG 是包内切片，解码要用）。</summary>
        public byte[] PacketData { get; init; }

        /// <summary>整帧 JPEG 的偏移；0 表示这是增量帧。</summary>
        public int FullJpegOffset { get; init; }
        public int FullJpegLength { get; init; }

        /// <summary>变化分块表。</summary>
        public List<Tile> Tiles { get; init; } = new();

        public bool IsDelta => FullJpegOffset == 0;
    }

    /// <summary>解析一个二进制帧（整帧或增量都支持）。</summary>
    public static bool TryParsePacket(byte[] data, out Parsed parsed)
    {
        parsed = null;
        if (!TryUnpack(data, out long seq, out Meta meta, out int bodyOffset)) return false;
        if (meta == null) return false;

        var result = new Parsed { Sequence = seq, Meta = meta, PacketData = data };

        if (!meta.IsDelta)
        {
            // 整帧：body 就是 JPEG
            parsed = new Parsed
            {
                Sequence = seq,
                Meta = meta,
                PacketData = data,
                FullJpegOffset = bodyOffset,
                FullJpegLength = data.Length - bodyOffset
            };
            return true;
        }

        // 增量：body = "DT01" + count + 表项 + 各分块 JPEG
        if (data.Length < bodyOffset + TileTableHeader) return false;
        if (data[bodyOffset] != 'D' || data[bodyOffset + 1] != 'T' ||
            data[bodyOffset + 2] != '0' || data[bodyOffset + 3] != '1') return false;

        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(bodyOffset + 4, 4));
        if (count < 0 || count > 4096) return false;

        int p = bodyOffset + TileTableHeader;
        if (data.Length < p + count * TileEntrySize) return false;

        var tiles = new List<Tile>(count);
        for (int i = 0; i < count; i++)
        {
            int x = (data[p] << 8) | data[p + 1];
            int y = (data[p + 2] << 8) | data[p + 3];
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4, 4));
            p += TileEntrySize;
            tiles.Add(new Tile { X = x, Y = y, Length = len, JpegOffset = 0 });
        }

        int dataOffset = p;
        var final = new List<Tile>(count);
        foreach (var t in tiles)
        {
            if (dataOffset + t.Length > data.Length) return false;
            final.Add(new Tile { X = t.X, Y = t.Y, Length = t.Length, JpegOffset = dataOffset });
            dataOffset += t.Length;
        }

        parsed = new Parsed { Sequence = seq, Meta = meta, PacketData = data, Tiles = final };
        return true;
    }

    /// <summary>打包一帧：4 字节魔数 + 8 字节序号 + 256 字节 JSON 元数据 + JPEG。</summary>
    public static byte[] Pack(long seq, Meta meta, byte[] jpeg)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(meta, Opts);
        if (json.Length > MetaSize) throw new InvalidOperationException("元数据过大");

        var buf = new byte[HeaderSize + jpeg.Length];
        buf[0] = (byte)'W'; buf[1] = (byte)'F';
        buf[2] = (byte)'0'; buf[3] = (byte)'1';
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4, 8), seq);
        Array.Copy(json, 0, buf, 12, json.Length);
        Array.Copy(jpeg, 0, buf, HeaderSize, jpeg.Length);
        return buf;
    }

    /// <summary>
    /// 组装 268 字节的帧头（4 字节魔数 + 8 字节序号 + 256 字节 JSON），写到调用方给的缓冲里。
    /// 复用同一个 byte[] 就不需要每帧分配。
    /// </summary>
    public static bool WriteMetaHeader(byte[] buffer, long seq, Meta meta)
    {
        if (buffer == null || buffer.Length < HeaderSize) return false;

        var json = JsonSerializer.SerializeToUtf8Bytes(meta, Opts);
        if (json.Length > MetaSize) return false;

        buffer[0] = (byte)'W'; buffer[1] = (byte)'F';
        buffer[2] = (byte)'0'; buffer[3] = (byte)'1';
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(4, 8), seq);
        Array.Clear(buffer, 12, MetaSize);
        Array.Copy(json, 0, buffer, 12, json.Length);
        return true;
    }

    public static bool TryUnpack(byte[] data, out long seq, out Meta meta, out int jpegOffset)
    {
        seq = 0; meta = null; jpegOffset = 0;
        if (data == null || data.Length < HeaderSize) return false;
        if (data[0] != 'W' || data[1] != 'F' || data[2] != '0' || data[3] != '1') return false;

        seq = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4, 8));
        int len = 0;
        while (len < MetaSize && data[12 + len] != 0) len++;
        try
        {
            meta = JsonSerializer.Deserialize<Meta>(Encoding.UTF8.GetString(data, 12, len), Opts);
        }
        catch
        {
            return false;
        }
        jpegOffset = HeaderSize;
        return meta != null;
    }
}

/// <summary>极简 WebSocket 服务端实现（RFC6455，仅服务端方向）。</summary>
public static class WebSocketProtocol
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public const int OpContinuation = 0x0;
    public const int OpText = 0x1;
    public const int OpBinary = 0x2;
    public const int OpClose = 0x8;
    public const int OpPing = 0x9;
    public const int OpPong = 0xA;

    public static string ComputeAccept(string key)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + Guid)));
    }

    public static async System.Threading.Tasks.Task WriteFrameAsync(NetworkStream stream, byte[] payload,
        int opCode = OpBinary, System.Threading.CancellationToken ct = default)
    {
        payload ??= Array.Empty<byte>();
        int len = payload.Length;
        int headerLen = len < 126 ? 2 : (len <= ushort.MaxValue ? 4 : 10);
        var header = new byte[headerLen];
        header[0] = (byte)(0x80 | (opCode & 0x0F));
        if (len < 126) header[1] = (byte)len;
        else if (len <= ushort.MaxValue)
        {
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)len);
        }
        else
        {
            header[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2, 8), (ulong)len);
        }

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (len > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    public static System.Threading.Tasks.Task WriteTextAsync(NetworkStream stream, string text,
        System.Threading.CancellationToken ct = default)
        => WriteFrameAsync(stream, Utf8.GetBytes(text ?? ""), OpText, ct);

    /// <summary>
    /// 直接把「268 字节帧头 + JPEG」写出去，不做中间拼接拷贝。
    /// 注意：WebSocket 帧长必须是 <b>头 + JPEG 的总长</b>（这里曾经漏算头导致客户端解析错位）。
    /// </summary>
    public static async System.Threading.Tasks.Task WritePacketAsync(NetworkStream stream, byte[] jpeg,
        byte[] header268, System.Threading.CancellationToken ct = default)
    {
        int payloadLen = header268.Length + jpeg.Length;

        int headerLen = 2 + (payloadLen < 126 ? 0 : (payloadLen <= ushort.MaxValue ? 2 : 8));
        var wsHeader = new byte[headerLen];

        wsHeader[0] = (byte)(0x80 | OpBinary);
        if (payloadLen < 126)
        {
            wsHeader[1] = (byte)payloadLen;
        }
        else if (payloadLen <= ushort.MaxValue)
        {
            wsHeader[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(wsHeader.AsSpan(2, 2), (ushort)payloadLen);
        }
        else
        {
            wsHeader[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(wsHeader.AsSpan(2, 8), (ulong)payloadLen);
        }

        await stream.WriteAsync(wsHeader, ct).ConfigureAwait(false);
        await stream.WriteAsync(header268, ct).ConfigureAwait(false);
        await stream.WriteAsync(jpeg, ct).ConfigureAwait(false);
    }

    public static async System.Threading.Tasks.Task<(int opCode, byte[] payload)> ReadFrameAsync(
        NetworkStream stream, System.Threading.CancellationToken ct = default)
    {
        var head = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
        int b0 = head[0], b1 = head[1];
        int op = b0 & 0x0F;
        bool masked = (b1 & 0x80) != 0;
        long len = b1 & 0x7F;

        if (len == 126)
        {
            var ext = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
            len = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (len == 127)
        {
            var ext = await ReadExactAsync(stream, 8, ct).ConfigureAwait(false);
            len = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
        }

        if (len > 16 * 1024 * 1024) throw new IOException("WebSocket 帧过大");

        byte[] mask = null;
        if (masked)
        {
            mask = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        }

        var payload = len == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, (int)len, ct).ConfigureAwait(false);
        if (masked)
        {
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        }

        return (op, payload);
    }

    private static async System.Threading.Tasks.Task<byte[]> ReadExactAsync(NetworkStream stream, int count,
        System.Threading.CancellationToken ct)
    {
        var buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("连接已关闭");
            off += n;
        }
        return buf;
    }
}
