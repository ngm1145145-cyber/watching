using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watching.Common;

/// <summary>服务端设置，保存在 %AppData%\Watching\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>监听端口。</summary>
    public int Port { get; set; } = 8899;

    /// <summary>最大帧率。</summary>
    public int Fps { get; set; } = 20;

    /// <summary>JPEG 画质 30-95。</summary>
    public int Quality { get; set; } = 70;

    /// <summary>发送给客户端的最大宽度（0 = 原始分辨率）。</summary>
    public int MaxWidth { get; set; } = 1600;

    /// <summary>是否允许客户端远程控制鼠标键盘（默认关闭）。</summary>
    public bool RemoteControlEnabled { get; set; }

    /// <summary>客户端连接是否需要访问密码。</summary>
    public bool AccessPasswordEnabled { get; set; }

    /// <summary>客户端连接密码（仅在启用时使用）。</summary>
    public string AccessPassword { get; set; }

    /// <summary>是否开机自动启动服务端。</summary>
    public bool AutoStart { get; set; }

    /// <summary>是否启用局域网自动发现（UDP 广播，客户端不用手填 IP）。</summary>
    public bool DiscoveryEnabled { get; set; } = true;

    /// <summary>是否对完全没变化的画面跳过重复帧（省流量）。</summary>
    public bool SkipUnchangedFrames { get; set; } = true;

    /// <summary>网络拥塞时是否自动降画质/帧率（保证流畅不卡）。</summary>
    public bool AdaptiveQuality { get; set; } = true;

    /// <summary>客户端上次连接的地址（方便下次直接连）。</summary>
    public string LastHost { get; set; }

    /// <summary>客户端上次连接的端口。</summary>
    public int LastPort { get; set; } = 8899;

    /// <summary>客户端上次使用的访问密码。</summary>
    public string LastAccessPassword { get; set; }

    /// <summary>设置窗口密码的 PBKDF2 哈希（Base64）。</summary>
    public string PasswordHash { get; set; }

    /// <summary>密码盐（Base64）。</summary>
    public string PasswordSalt { get; set; }

    /// <summary>是否已设置过密码。</summary>
    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash) && !string.IsNullOrEmpty(PasswordSalt);

    /// <summary>客户端连接是否真的需要密码。</summary>
    [JsonIgnore]
    public bool AccessControlActive => AccessPasswordEnabled && !string.IsNullOrEmpty(AccessPassword);

    /// <summary>校验客户端访问密码（恒定时间比较，避免时序侧信道）。</summary>
    public bool CheckAccessPassword(string candidate)
    {
        if (!AccessControlActive) return true;
        if (candidate == null) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(candidate);
        var b = System.Text.Encoding.UTF8.GetBytes(AccessPassword);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---------- 持久化 ----------

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Watching");

    private static string _pathOverride;

    public static string ConfigPath => _pathOverride ?? Path.Combine(Dir, "config.json");

    /// <summary>用 --config 指定配置文件位置（用于多实例 / 隔离测试）。</summary>
    public static void SetPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) _pathOverride = path;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static AppConfig _current;

    public static AppConfig Load()
    {
        if (_current != null) return _current;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    _current = cfg;
                    return _current;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读取配置失败，使用默认值：" + ex.Message);
        }
        _current = new AppConfig();
        return _current;
    }

    public void Normalize()
    {
        Port = Math.Clamp(Port, 1, 65535);
        Fps = Math.Clamp(Fps, 1, 60);
        Quality = Math.Clamp(Quality, 20, 95);
        MaxWidth = MaxWidth <= 0 ? 0 : Math.Clamp(MaxWidth, 320, 7680);
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Write("保存配置失败：" + ex.Message);
        }
    }
}
