using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watching.Mobile;

/// <summary>一个变化分块：位置 + JPEG 数据。</summary>
public sealed class TileChunk
{
    public int X { get; init; }
    public int Y { get; init; }
    public byte[] Jpeg { get; init; }
}

/// <summary>
/// 服务端帧协议（与 Windows 端完全一致，含分块增量）：
///
///   整帧（meta.mode="full"）：
///     偏移 0   4 字节  "WF01"
///     偏移 4   8 字节  帧序号（小端 int64）
///     偏移 12  256 字节 JSON 元数据（\0 补齐）
///     偏移 268 之后    JPEG 数据
///
///   增量帧（meta.mode="delta"）：
///     偏移 268 4 字节  "DT01"
///     偏移 272 4 字节  分块数 n
///     之后     n × 8 字节  每块 x(2) + y(2) + 长度(4)
///     之后     各分块 JPEG 依次排列
///
/// 增量帧只包含「变化的块」，客户端需要把它们贴到上一帧的对应位置。
/// </summary>
public static class FramePacket
{
    public const int MetaSize = 256;
    public const int HeaderSize = 4 + 8 + MetaSize;
    public const int TileTableHeader = 8;
    public const int TileEntrySize = 8;

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
        public int tile { get; set; }
        public int tx { get; set; }
        public int ty { get; set; }

        public bool IsDelta => string.Equals(mode, "delta", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析结果：整帧给 FullJpeg，增量帧给 Tiles。</summary>
    public sealed class Parsed
    {
        public long Sequence { get; init; }
        public Meta Meta { get; init; }
        public byte[] FullJpeg { get; init; }
        public List<TileChunk> Tiles { get; init; } = new();
        public bool IsDelta => FullJpeg == null;
        public int ByteLength { get; init; }
    }

    public static bool TryParse(byte[] data, out Parsed parsed)
    {
        parsed = null;
        if (data == null || data.Length < HeaderSize) return false;
        if (data[0] != 'W' || data[1] != 'F' || data[2] != '0' || data[3] != '1') return false;

        long seq = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4, 8));

        int len = 0;
        while (len < MetaSize && data[12 + len] != 0) len++;

        Meta meta;
        try
        {
            meta = JsonSerializer.Deserialize<Meta>(Encoding.UTF8.GetString(data, 12, len), Opts);
        }
        catch
        {
            return false;
        }
        if (meta == null) return false;

        if (!meta.IsDelta)
        {
            int offset = HeaderSize;
            var jpeg = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, jpeg, 0, jpeg.Length);
            parsed = new Parsed { Sequence = seq, Meta = meta, FullJpeg = jpeg, ByteLength = data.Length };
            return true;
        }

        // 增量帧
        if (data.Length < HeaderSize + TileTableHeader) return false;
        if (data[HeaderSize] != 'D' || data[HeaderSize + 1] != 'T' ||
            data[HeaderSize + 2] != '0' || data[HeaderSize + 3] != '1') return false;

        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(HeaderSize + 4, 4));
        if (count < 0 || count > 4096) return false;

        int p = HeaderSize + TileTableHeader;
        if (data.Length < p + count * TileEntrySize) return false;

        var entries = new List<(int x, int y, int len)>(count);
        for (int i = 0; i < count; i++)
        {
            int x = (data[p] << 8) | data[p + 1];
            int y = (data[p + 2] << 8) | data[p + 3];
            int l = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4, 4));
            p += TileEntrySize;
            entries.Add((x, y, l));
        }

        var tiles = new List<TileChunk>(count);
        foreach (var (x, y, l) in entries)
        {
            if (l < 0 || p + l > data.Length) return false;
            var jpeg = new byte[l];
            Buffer.BlockCopy(data, p, jpeg, 0, l);
            p += l;
            tiles.Add(new TileChunk { X = x, Y = y, Jpeg = jpeg });
        }

        parsed = new Parsed { Sequence = seq, Meta = meta, Tiles = tiles, ByteLength = data.Length };
        return true;
    }
}

/// <summary>客户端 → 服务端的控制消息。</summary>
public sealed class ClientMessage
{
    [JsonPropertyName("t")] public string Type { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("quality")] public int? Quality { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("maxWidth")] public int? MaxWidth { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; }
    [JsonPropertyName("delta")] public int? Delta { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);
}

/// <summary>服务端 → 客户端的控制消息。</summary>
public sealed class ServerMessage
{
    [JsonPropertyName("t")] public string Type { get; set; }
    [JsonPropertyName("sw")] public int ScreenWidth { get; set; }
    [JsonPropertyName("sh")] public int ScreenHeight { get; set; }
    [JsonPropertyName("q")] public int Quality { get; set; }
    [JsonPropertyName("fps")] public int Fps { get; set; }
    [JsonPropertyName("remote")] public bool RemoteControl { get; set; }
    [JsonPropertyName("name")] public string MachineName { get; set; }
    [JsonPropertyName("msg")] public string Message { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ServerMessage Parse(string json)
    {
        try { return JsonSerializer.Deserialize<ServerMessage>(json, Opts); }
        catch { return null; }
    }
}
