using System;
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
    [JsonPropertyName("version")] public string AppVersion { get; set; } = "1.0.1";
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

                bool isProbe = text.Contains(ProbeText, StringComparison.OrdinalIgnoreCase);
                bool isMagic = text.Contains(Magic, StringComparison.OrdinalIgnoreCase);
                if (!isProbe && !isMagic) continue;

                var reply = BuildReply(result.RemoteEndPoint.Address);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, JsonOpts));
                await _listener.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).ConfigureAwait(false);
                ProbesAnswered++;
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
        string host = null;
        try
        {
            var local = PickLocalAddress(remote);
            if (local != null) host = local.ToString();
        }
        catch { }

        if (string.IsNullOrEmpty(host))
        {
            var diag = NetworkDiagnostics.Run();
            host = diag.PrimaryIp;
        }

        return new DiscoveryMessage
        {
            Type = "reply",
            Host = host,
            MachineName = Safe(_machineName),
            Port = _config.Port,
            Clients = SafeInt(_clientCount),
            PasswordRequired = SafeBool(() => _passwordRequired()) ? 1 : 0,
            OS = Environment.OSVersion.VersionString
        };
    }

    /// <summary>定时向局域网广播公告，客户端可以只监听不探测。</summary>
    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        if (!_config.DiscoveryEnabled) return;

        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, _config.Port) };

                // 顺便给各网段的广播地址也发一份，兼容部分不转发 255.255.255.255 的路由器
                foreach (var seg in SubnetBroadcasts())
                    targets.Add(new IPEndPoint(seg, _config.Port));

                foreach (var ep in targets)
                {
                    try
                    {
                        var reply = BuildReply(ep.Address);
                        reply.Type = "announce";
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, JsonOpts));
                        await _listener.SendAsync(bytes, bytes.Length, ep).ConfigureAwait(false);
                    }
                    catch { }
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

    private static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ua.IPv4Mask;
                    if (mask == null) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    if (ip.Length != 4 || m.Length != 4) continue;

                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | (byte)~m[i]);

                    var broadcast = new IPAddress(b);
                    if (!IPAddress.IsLoopback(ua.Address) && !ua.Address.ToString().StartsWith("169.254."))
                        result.Add(broadcast);
                }
            }
        }
        catch { }
        return result;
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
