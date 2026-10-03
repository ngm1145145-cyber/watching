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

namespace Watching.Desktop;

/// <summary>搜到的服务端。</summary>
public sealed class DiscoveredServer
{
    [JsonPropertyName("magic")] public string Magic { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("host")] public string Host { get; set; }
    [JsonPropertyName("name")] public string MachineName { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("version")] public string AppVersion { get; set; }
    [JsonPropertyName("pwd")] public int PasswordRequired { get; set; }
    [JsonPropertyName("clients")] public int Clients { get; set; }
    [JsonPropertyName("os")] public string OS { get; set; }

    public string Describe() =>
        $"{MachineName ?? "?"} ({Host}:{Port})" +
        (PasswordRequired == 1 ? " · 需要密码" : "") +
        (Clients > 0 ? $" · {Clients} 人在看" : "");
}

/// <summary>
/// 局域网搜索服务端：向广播地址发 WATCHING_DISCOVER，收 UDP 回包。
/// 用户就不用再去查 IP 了。
/// </summary>
public static class DiscoveryClient
{
    private const string ProbeText = "WATCHING_DISCOVER";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>搜索局域网内的服务端。timeoutMs 内收到几个就返回几个。</summary>
    public static async Task<List<DiscoveredServer>> SearchAsync(int port = 8899, int timeoutMs = 2500,
        CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredServer>();

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probe = Encoding.UTF8.GetBytes(ProbeText);

        // 广播地址 + 各网段广播 + 回环（本机测试用）
        var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var b in SubnetBroadcasts()) targets.Add(b);

        foreach (var target in targets.Distinct())
        {
            try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, port)).ConfigureAwait(false); }
            catch { }
        }

        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            int remain = (int)Math.Max(1, deadline - Environment.TickCount64);
            var receiveTask = udp.ReceiveAsync();
            var done = await Task.WhenAny(receiveTask, Task.Delay(Math.Min(remain, 400), ct)).ConfigureAwait(false);
            if (done != receiveTask) continue;

            try
            {
                var result = await receiveTask.ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                var server = JsonSerializer.Deserialize<DiscoveredServer>(text, JsonOpts);
                if (server == null || string.IsNullOrEmpty(server.Host)) continue;
                if (!string.Equals(server.Magic, "WATCHING/1", StringComparison.OrdinalIgnoreCase)) continue;
                if (server.Port <= 0) server.Port = port;

                found[$"{server.Host}:{server.Port}"] = server;
            }
            catch { }
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
                    if (!ua.Address.ToString().StartsWith("169.254.")) list.Add(new IPAddress(bytes));
                }
            }
        }
        catch { }
        return list;
    }
}
