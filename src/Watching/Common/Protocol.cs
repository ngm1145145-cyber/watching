using System;
using System.Text.Json.Serialization;

namespace Watching.Common;

/// <summary>本机 / 服务端信息。</summary>
public sealed class AppInfo
{
    [JsonPropertyName("app")] public string App { get; set; } = "Watching";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("name")] public string MachineName { get; set; } = Environment.MachineName;
    [JsonPropertyName("user")] public string UserName { get; set; } = Environment.UserName;
    [JsonPropertyName("os")] public string OS { get; set; } = Environment.OSVersion.VersionString;
    [JsonPropertyName("screen")] public string Screen { get; set; }
    [JsonPropertyName("remote")] public bool RemoteControl { get; set; }
    [JsonPropertyName("clients")] public int Clients { get; set; }
}

/// <summary>服务端 → 客户端的文本控制消息。</summary>
public sealed class ServerMessage
{
    [JsonPropertyName("t")] public string Type { get; set; }
    [JsonPropertyName("w")] public int Width { get; set; }
    [JsonPropertyName("h")] public int Height { get; set; }
    [JsonPropertyName("sw")] public int ScreenWidth { get; set; }
    [JsonPropertyName("sh")] public int ScreenHeight { get; set; }
    [JsonPropertyName("q")] public int Quality { get; set; }
    [JsonPropertyName("fps")] public double Fps { get; set; }
    [JsonPropertyName("kbps")] public double Kbps { get; set; }
    [JsonPropertyName("ts")] public long Timestamp { get; set; }
    [JsonPropertyName("crop")] public bool Cropped { get; set; }
    [JsonPropertyName("remote")] public bool RemoteControl { get; set; }
    [JsonPropertyName("maxw")] public int MaxWidth { get; set; }
    [JsonPropertyName("capms")] public double CaptureMs { get; set; }
    [JsonPropertyName("name")] public string MachineName { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("msg")] public string Message { get; set; }
}

/// <summary>客户端 → 服务端的文本控制消息。</summary>
public sealed class ClientMessage
{
    [JsonPropertyName("t")] public string Type { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("quality")] public int? Quality { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("maxWidth")] public int? MaxWidth { get; set; }
    [JsonPropertyName("x0")] public double? X0 { get; set; }
    [JsonPropertyName("y0")] public double? Y0 { get; set; }
    [JsonPropertyName("x1")] public double? X1 { get; set; }
    [JsonPropertyName("y1")] public double? Y1 { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("button")] public string Button { get; set; }
    [JsonPropertyName("delta")] public int? Delta { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; }
    [JsonPropertyName("ctrl")] public bool? Ctrl { get; set; }
    [JsonPropertyName("alt")] public bool? Alt { get; set; }
    [JsonPropertyName("shift")] public bool? Shift { get; set; }
    [JsonPropertyName("win")] public bool? Win { get; set; }
}
