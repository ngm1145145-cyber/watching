using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Watching.Common;

namespace Watching.Server;

/// <summary>发现服务广播/应答的消息体（字段名与客户端 JS / C# 两端约定一致）。</summary>
public sealed class DiscoveryMessage
{
    [JsonPropertyName("magic")] public string Magic { get; set; } = DiscoveryService.Magic;
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("ver")] public string Version { get; set; } = "1";
    [JsonPropertyName("app")] public string App { get; set; } = "Watching";
    [JsonPropertyName("host")] public string Host { get; set; }
    [JsonPropertyName("name")] public string MachineName { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("version")] public string AppVersion { get; set; } = Watching.Common.AppConfig.AppVersion;
    [JsonPropertyName("clients")] public int Clients { get; set; }
    [JsonPropertyName("pwd")] public int PasswordRequired { get; set; }
    [JsonPropertyName("os")] public string OS { get; set; }
}

/// <summary>
/// 局域网自动发现：监听 UDP 8899，收到 <c>WATCHING_DISCOVER</c> 就回一条 JSON，
/// 里面带本机在<strong>该网卡上</strong>的 IP 和端口，客户端点一下就能连，不用手抄 IP。
///
/// 顺带每 20 秒向 255.255.255.255 / 各网段广播地址发一次公告，方便客户端被动发现。
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const string Magic = "WATCHING/1";
    public const string ProbeText = "WATCHING_DISCOVER";
    public const int DiscoveryPort = 8899;

    private readonly AppConfig _config;
    private readonly Func<int> _clientCount;
    private readonly Func<string> _machineName;
    private readonly Func<bool> _passwordRequired;

    private UdpClient _listener;
    private CancellationTokenSource _cts;
    private Task _listenTask;
    private Task _announceTask;
    private volatile bool _running;

    public bool IsRunning => _running;
    public long ProbesAnswered { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DiscoveryService(AppConfig config, Func<int> clientCount, Func<string> machineName, Func<bool> passwordRequired)
    {
        _config = config;
        _clientCount = clientCount;
        _machineName = machineName;
        _passwordRequired = passwordRequired;
    }

    public void Start()
    {
        if (_running) return;

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.EnableBroadcast = true;
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, _config.Port));

            _cts = new CancellationTokenSource();
            _running = true;
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            _announceTask = Task.Run(() => AnnounceLoopAsync(_cts.Token));

            Log.Write($"发现服务已启动：UDP {_config.Port}（回应 {ProbeText}，并每 20 秒广播一次）");
        }
        catch (Exception ex)
        {
            // 端口被占（例如另一个实例）时不影响主服务，只是没有自动发现
            Log.Error("发现服务启动失败（不影响主服务）", ex);
            _running = false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);

                // 只回应客户端的探测文本。以前连公告里的 magic 也当探测，
                // 自己发的广播绕回来会被自己当成探测再回一次，日志和「最近收到」
                // 里全是本机地址，真正的问题反而看不见了。
                if (!text.Contains(ProbeText, StringComparison.OrdinalIgnoreCase)) continue;

                var remote = result.RemoteEndPoint.Address.ToString();

                var reply = BuildReply(result.RemoteEndPoint.Address);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, JsonOpts));

                // 用「回复里写的那个本机地址」把包发出去：这样回包的源 IP 和 JSON 里的
                // host 一定一致，而且是顺着探测进来的那块网卡出去。
                // 多网卡机器上（网线 + WiFi、双 WiFi）这一点很关键 —— 以前统一由
                // 绑定 0.0.0.0 的 socket 回，回包可能从另一块网卡出去，客户端永远收不到。
                await SendReplyAsync(reply.Host, bytes, result.RemoteEndPoint).ConfigureAwait(false);
                ProbesAnswered++;

                // 这条日志是排查「手机搜不到本机」的关键：能看到是谁在搜、回了哪个地址
                Log.Write($"收到自动发现探测：{remote} → 已回复 http://{reply.Host}:{reply.Port}/");
                NetworkActivity.Record("发现探测", remote, $"已回复 {reply.Host}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // 收到 ICMP 端口不可达之类的瞬时错误，忽略继续
                Log.Write("发现服务收包异常：" + ex.SocketErrorCode);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("发现服务异常", ex);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 从指定本机地址回包；地址解析不出来时退回主监听 socket。
    /// </summary>
    private async Task SendReplyAsync(string localIp, byte[] bytes, IPEndPoint target)
    {
        IPAddress local = null;
        if (!string.IsNullOrEmpty(localIp)) IPAddress.TryParse(localIp, out local);

        bool targetIsLoopback = target != null && IPAddress.IsLoopback(target.Address);

        // 目标是本机回环时不能绑局域网地址发（会 WSAEADDRNOTAVAIL），直接用主监听 socket
        if (local != null && !IPAddress.IsLoopback(local) && !targetIsLoopback)
        {
            try
            {
                using var socket = new UdpClient(new IPEndPoint(local, 0));
                await socket.SendAsync(bytes, bytes.Length, target).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"从 {local} 回包失败（改用默认网卡）：{ex.Message}");
            }
        }

        await _listener.SendAsync(bytes, bytes.Length, target).ConfigureAwait(false);
    }

    /// <summary>即使本机有多个网卡，也要告诉客户端「用哪个 IP 能连到我」。</summary>
    private IPAddress PickLocalAddress(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, _config.Port);
            if (probe.LocalEndPoint is IPEndPoint local && !IPAddress.IsLoopback(local.Address))
                return local.Address;
        }
        catch { }
        return null;
    }

    private DiscoveryMessage BuildReply(IPAddress remote)
    {
        IPAddress local = null;
        try { local = PickLocalAddress(remote); }
        catch { }

        if (local == null)
        {
            var diag = NetworkDiagnostics.Run();
            if (!string.IsNullOrEmpty(diag.PrimaryIp)) local = IPAddress.Parse(diag.PrimaryIp);
        }

        return BuildReplyFor(local);
    }

    /// <summary>
    /// 定时向局域网广播公告，客户端可以只监听不探测。
    ///
    /// 关键点：**每块网卡各发一次**。以前只用绑定在 0.0.0.0 的同一个 socket 发，
    /// Windows 只会从「主网卡」出去一次，多网卡（网线 + WiFi、双 WiFi）时
    /// 另一边的设备永远收不到公告。
    /// </summary>
    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        if (!_config.DiscoveryEnabled) return;

        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var nic in LocalAddresses())
                {
                    try
                    {
                        await AnnounceFromAsync(nic.Ip, nic.Broadcast).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"向 {nic.Ip} 所在网段广播失败：{ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("广播公告失败", ex);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>从指定网卡的 IP 发出公告：255.255.255.255 + 该网段的广播地址各一份。</summary>
    private async Task AnnounceFromAsync(IPAddress localIp, IPAddress subnetBroadcast)
    {
        using var socket = new UdpClient(new IPEndPoint(localIp, 0));
        socket.EnableBroadcast = true;

        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, _config.Port) };
        if (subnetBroadcast != null) targets.Add(new IPEndPoint(subnetBroadcast, _config.Port));

        foreach (var ep in targets)
        {
            var reply = BuildReplyFor(localIp);
            reply.Type = "announce";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, JsonOpts));
            try
            {
                await socket.SendAsync(bytes, bytes.Length, ep).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // 有些网卡不允许广播，跳过
            }
        }
    }

    private sealed class NicInfo
    {
        public IPAddress Ip { get; init; }
        public IPAddress Broadcast { get; init; }
    }

    /// <summary>本机所有可用的 IPv4 网卡地址 + 对应网段广播地址。</summary>
    private static List<NicInfo> LocalAddresses()
    {
        var list = new List<NicInfo>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                bool hasGateway = false;
                try
                {
                    hasGateway = ipProps.GatewayAddresses.Any(g =>
                        g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !g.Address.Equals(IPAddress.Any));
                }
                catch { }

                // 虚拟机网卡不发公告：那是宿主机/虚拟机的内部网段
                bool virt = LooksLikeVirtualNic(ni.Name + " " + ni.Description);

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    if (ua.Address.ToString().StartsWith("169.254.")) continue;
                    if (virt && !hasGateway) continue;

                    var mask = ua.IPv4Mask;
                    IPAddress broadcast = null;
                    if (mask != null)
                    {
                        var ip = ua.Address.GetAddressBytes();
                        var m = mask.GetAddressBytes();
                        if (ip.Length == 4 && m.Length == 4)
                        {
                            var b = new byte[4];
                            for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | (byte)~m[i]);
                            broadcast = new IPAddress(b);
                        }
                    }

                    list.Add(new NicInfo { Ip = ua.Address, Broadcast = broadcast });
                }
            }
        }
        catch { }
        return list;
    }

    private static bool LooksLikeVirtualNic(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("virtualbox") || t.Contains("vmware") || t.Contains("hyper-v") ||
               t.Contains("vethernet") || t.Contains("virtual") || t.Contains("tap-") ||
               t.Contains("tun") || t.Contains("wsl");
    }

    /// <summary>用指定的本机地址构造公告/应答（host 必须是对方真能连上的那个）。</summary>
    private DiscoveryMessage BuildReplyFor(IPAddress localIp)
    {
        return new DiscoveryMessage
        {
            Type = "reply",
            Host = localIp?.ToString(),
            MachineName = Safe(_machineName),
            Port = _config.Port,
            Clients = SafeInt(_clientCount),
            PasswordRequired = SafeBool(() => _passwordRequired()) ? 1 : 0,
            OS = Environment.OSVersion.VersionString
        };
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch { return null; }
    }

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }

    public void Dispose()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { _listenTask?.Wait(500); } catch { }
        try { _announceTask?.Wait(500); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}

