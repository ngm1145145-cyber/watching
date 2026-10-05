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
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Log = Android.Util.Log;

namespace Watching.Mobile;

public sealed class DiscoveredServer
{
    [JsonPropertyName("magic")] public string Magic { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("host")] public string Host { get; set; }
    [JsonPropertyName("name")] public string MachineName { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("pwd")] public int PasswordRequired { get; set; }
    [JsonPropertyName("clients")] public int Clients { get; set; }

    public string Describe() =>
        $"{MachineName ?? "?"}  ({Host}:{Port})" +
        (PasswordRequired == 1 ? "  需要密码" : "") +
        (Clients > 0 ? $"  {Clients} 人在看" : "");
}

/// <summary>
/// 手机端局域网搜索服务端。
/// 安卓要收到 UDP 广播必须持有 WifiManager.MulticastLock，否则系统会把广播包丢掉。
/// </summary>
public sealed class UdpDiscovery
{
    private const string ProbeText = "WATCHING_DISCOVER";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>搜索局域网内的服务端。返回按机器名排序的结果。</summary>
    public static async Task<List<DiscoveredServer>> SearchAsync(Context context, int port = 8899,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredServer>();
        WifiManager.MulticastLock lockObj = null;

        try
        {
            // 1) 申请多播锁，否则安卓会过滤广播
            try
            {
                if (context?.GetSystemService(Context.WifiService) is WifiManager wifi)
                {
                    lockObj = wifi.CreateMulticastLock("watching-discovery");
                    lockObj.SetReferenceCounted(true);
                    lockObj.Acquire();
                }
            }
            catch { }

            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var probe = Encoding.UTF8.GetBytes(ProbeText);
            var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            foreach (var b in SubnetBroadcasts()) targets.Add(b);

            foreach (var target in targets.Distinct())
            {
                try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, port)).ConfigureAwait(false); }
                catch { }
            }

            long deadline = System.Environment.TickCount64 + timeoutMs;

            // 整段搜索窗口只挂一个接收任务。
            // 以前的写法是每 400ms 超时就重新 ReceiveAsync 一次，被丢下的那些接收任务
            // 仍然挂在同一个 socket 上：服务端的回包要是晚到一点，就被某个没人读的
            // 接收任务吃掉丢掉了 —— 表现就是「服务端明明回了，手机却搜不到」。
            var receive = udp.ReceiveAsync();

            while (System.Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
            {
                int remain = (int)Math.Max(1, deadline - System.Environment.TickCount64);
                var done = await Task.WhenAny(receive, Task.Delay(Math.Min(remain, 400), ct)).ConfigureAwait(false);
                if (done != receive) continue;

                try
                {
                    var result = await receive.ConfigureAwait(false);
                    var server = JsonSerializer.Deserialize<DiscoveredServer>(
                        Encoding.UTF8.GetString(result.Buffer), JsonOpts);

                    if (server != null && !string.IsNullOrEmpty(server.Host) &&
                        string.Equals(server.Magic, "WATCHING/1", StringComparison.OrdinalIgnoreCase))
                    {
                        if (server.Port <= 0) server.Port = port;
                        found[$"{server.Host}:{server.Port}"] = server;
                    }
                }
                catch { }

                if (ct.IsCancellationRequested) break;
                receive = udp.ReceiveAsync();   // 收到一条再挂下一条
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Watching", "搜索服务端失败: " + ex.Message);
        }
        finally
        {
            try { lockObj?.Release(); } catch { }
            try { lockObj?.Dispose(); } catch { }
        }

        return found.Values.OrderBy(s => s.MachineName).ToList();
    }

    private static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        var list = new List<IPAddress>();
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

                    var bytes = new byte[4];
                    for (int i = 0; i < 4; i++) bytes[i] = (byte)(ip[i] | (byte)~m[i]);
                    list.Add(new IPAddress(bytes));
                }
            }
        }
        catch { }
        return list;
    }
}
