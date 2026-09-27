using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watching.Mobile;

/// <summary>
/// 服务端帧协议：
///   偏移 0   4 字节  "WF01"
///   偏移 4   8 字节  帧序号（小端 int64）
///   偏移 12  256 字节 JSON 元数据（\0 补齐）
///   偏移 268 之后    JPEG 数据
/// </summary>
public static class FramePacket
{
    public const int MetaSize = 256;
    public const int HeaderSize = 4 + 8 + MetaSize;

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
    }

    public static bool TryParse(byte[] data, out long seq, out Meta meta, out int jpegOffset)
    {
        seq = 0;
        meta = null;
        jpegOffset = 0;

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

        if (meta == null) return false;
        jpegOffset = HeaderSize;
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
