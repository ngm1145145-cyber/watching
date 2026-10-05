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

    private DiscoveryService _discovery;

    /// <summary>局域网自动发现服务（UDP），可能为空（端口被占等情况）。</summary>
    public DiscoveryService Discovery => _discovery;
    public bool DiscoveryRunning => _discovery?.IsRunning == true;

    public ServerHost(AppConfig config)
    {
        _config = config;
        _quality = config.Quality;
        _fps = config.Fps;
        _maxWidth = config.MaxWidth;
        _remote = config.RemoteControlEnabled;
        _hub.DrawCursor = config.DrawCursor;
        _net = new NetServer(config, _hub, this);
    }

    public void Start()
    {
        _net.Start();
        IsRunning = true;
        Log.Write($"监听 0.0.0.0:{_config.Port}");

        if (_config.DiscoveryEnabled)
        {
            _discovery = new DiscoveryService(
                _config,
                () => ClientCount,
                () => Environment.MachineName,
                () => _config.AccessControlActive);
            _discovery.Start();
        }

        // 启动时顺手体检一次：防火墙没放行是「手机连不上」的头号原因
        var diag = NetworkDiagnostics.Run();
        Log.Write("网络自检：" + diag.Summary());
    }

    /// <summary>重新体检网络（用户点「刷新」或改完防火墙后调用）。</summary>
    public NetworkDiagnostics RefreshDiagnostics() => NetworkDiagnostics.Run();

    /// <summary>尝试添加防火墙放行规则（会弹一次 UAC）。</summary>
    public bool FixFirewall(out string message)
    {
        bool ok = FirewallHelper.AddRulesWithElevation(_config.Port, out message);

        // 规则变化后重启发现服务，让 UDP 监听也吃到新规则
        try
        {
            _discovery?.Dispose();
            _discovery = null;
            if (_config.DiscoveryEnabled && ok)
            {
                _discovery = new DiscoveryService(_config, () => ClientCount,
                    () => Environment.MachineName, () => _config.AccessControlActive);
                _discovery.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error("重启发现服务失败", ex);
        }

        Changed?.Invoke();
        return ok;
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

    /// <summary>
    /// 本机所有可用的局域网地址（用于显示给用户 / 生成连接二维码文本）。
    /// 走 NetworkDiagnostics 的过滤与排序：排除 169.254 和虚拟机网卡，
    /// 把「Windows 实际对外发包用的那块网卡」排在最前面。
    /// </summary>
    public List<string> LocalUrls()
    {
        try
        {
            var urls = NetworkDiagnostics.Run().Urls(_config.Port);
            if (urls.Count > 0) return urls;
        }
        catch (Exception ex)
        {
            Log.Error("枚举本机地址失败", ex);
        }
        return new List<string> { $"http://127.0.0.1:{_config.Port}/" };
    }

    /// <summary>推荐给用户填的那个地址（多网卡时最可能是对的那个）。</summary>
    public string PrimaryUrl()
    {
        var urls = LocalUrls();
        return urls.Count > 0 ? urls[0] : $"http://127.0.0.1:{_config.Port}/";
    }

    public List<string> LocalHostNames()
    {
        var names = new List<string> { "127.0.0.1" };
        try { names.Add(Dns.GetHostName()); } catch { }
        return names;
    }

    // ---------------- 设置 ----------------

    /// <summary>更新全局设置并广播给所有客户端（不会写入 config.json，由调用方决定）。</summary>
    public void ApplySettings(int? quality = null, int? fps = null, int? maxWidth = null,
        bool? remote = null, bool? drawCursor = null, bool save = true)
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
            if (drawCursor.HasValue) _config.DrawCursor = drawCursor.Value;
        }

        if (drawCursor.HasValue) _hub.ApplyDrawCursor(drawCursor.Value);

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
        try { _discovery?.Dispose(); } catch { }
        _discovery = null;
        foreach (var c in Clients) c.Close();
        _clients.Clear();
        _net.Dispose();
        _hub.Dispose();
        IsRunning = false;
    }
}
