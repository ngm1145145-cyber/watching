using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Watching.Common;

namespace Watching.Server;

/// <summary>服务端总控：抓屏流管理、客户端登记、全局设置广播。</summary>
public sealed class ServerHost : IDisposable
{
    private readonly AppConfig _config;
    private readonly CaptureHub _hub = new();
    private readonly NetServer _net;
    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private readonly object _settingsGate = new();

    private int _quality;
    private int _fps;
    private int _maxWidth;
    private bool _remote;

    public event Action Changed;

    public AppConfig Config => _config;
    public int Port => _config.Port;
    public CaptureHub Hub => _hub;
    public bool IsRunning { get; private set; }

    public int Quality { get { lock (_settingsGate) return _quality; } }
    public int Fps { get { lock (_settingsGate) return _fps; } }
    public int MaxWidth { get { lock (_settingsGate) return _maxWidth; } }
    public bool RemoteControlEnabled { get { lock (_settingsGate) return _remote; } }

    public int ClientCount => _clients.Count;

    public ServerHost(AppConfig config)
    {
        _config = config;
        _quality = config.Quality;
        _fps = config.Fps;
        _maxWidth = config.MaxWidth;
        _remote = config.RemoteControlEnabled;
        _net = new NetServer(config, _hub, this);
    }

    public void Start()
    {
        _net.Start();
        IsRunning = true;
        Log.Write($"监听 0.0.0.0:{_config.Port}");
    }

    internal void RegisterClient(ClientConnection client)
    {
        _clients[client.Id] = client;
        Changed?.Invoke();
    }

    internal void OnClientClosed(ClientConnection client)
    {
        if (_clients.TryRemove(client.Id, out var removed))
        {
            Log.Write($"客户端断开 [{removed.Kind}] {removed.RemoteAddress}");
            removed.Dispose();
            Changed?.Invoke();
        }
    }

    public IReadOnlyList<ClientConnection> Clients => _clients.Values.ToList();

    public string DescribeClients()
    {
        var list = Clients;
        if (list.Count == 0) return "当前没有客户端连接";
        return $"{list.Count} 个客户端已连接：" + string.Join("、", list.Select(c => c.Describe()));
    }

    /// <summary>本机所有可用的局域网地址（用于显示给用户 / 生成连接二维码文本）。</summary>
    public List<string> LocalUrls()
    {
        var urls = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    urls.Add($"http://{ip}:{_config.Port}/");
                }
            }
        }
        catch { }

        if (urls.Count == 0) urls.Add($"http://127.0.0.1:{_config.Port}/");
        return urls.Distinct().ToList();
    }

    public List<string> LocalHostNames()
    {
        var names = new List<string> { "127.0.0.1" };
        try { names.Add(Dns.GetHostName()); } catch { }
        return names;
    }

    // ---------------- 设置 ----------------

    /// <summary>更新全局设置并广播给所有客户端（不会写入 config.json，由调用方决定）。</summary>
    public void ApplySettings(int? quality = null, int? fps = null, int? maxWidth = null, bool? remote = null, bool save = true)
    {
        lock (_settingsGate)
        {
            if (quality.HasValue) _quality = Math.Clamp(quality.Value, 20, 95);
            if (fps.HasValue) _fps = Math.Clamp(fps.Value, 1, 60);
            if (maxWidth.HasValue) _maxWidth = maxWidth.Value <= 0 ? 0 : Math.Clamp(maxWidth.Value, 200, 7680);
            if (remote.HasValue) _remote = remote.Value;

            _config.Quality = _quality;
            _config.Fps = _fps;
            _config.MaxWidth = _maxWidth;
            _config.RemoteControlEnabled = _remote;
        }

        if (save) _config.Save();

        var msg = new ServerMessage
        {
            Type = "settings",
            Quality = _quality,
            Fps = _fps,
            MaxWidth = _maxWidth,
            RemoteControl = _remote
        };
        BroadcastText(msg);
        Changed?.Invoke();
    }

    public void BroadcastText(ServerMessage msg)
    {
        foreach (var c in Clients) c.QueueText(msg);
    }

    /// <summary>把某个客户端新请求的画质/裁剪同步到它自己的抓屏流。</summary>
    public void OnClientSettingsChanged(ClientConnection client)
    {
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var c in Clients) c.Close();
        _clients.Clear();
        _net.Dispose();
        _hub.Dispose();
        IsRunning = false;
    }
}
