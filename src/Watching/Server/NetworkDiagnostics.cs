using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Watching.Common;

namespace Watching.Server;

public sealed class NetworkAdapterInfo
{
    public string Name { get; set; }
    public string Ip { get; set; }
    public string Description { get; set; }
    public NetworkInterfaceType Type { get; set; }
    /// <summary>是否像是能连手机的地址（排除虚拟机 / 回环 / 自动私有地址）。</summary>
    public bool LooksUsable { get; set; }
    /// <summary>是否虚拟机网卡（VirtualBox / VMware / Hyper-V）。</summary>
    public bool LooksVirtual { get; set; }
    /// <summary>这块网卡有没有默认网关（有网关的通常才是真正连路由器的那个）。</summary>
    public bool HasGateway { get; set; }
    /// <summary>是不是 Windows 实际用来对外发包的那块网卡（最可能被手机连上的就是它）。</summary>
    public bool IsPreferred { get; set; }
}

/// <summary>
/// 服务端网络自检：列出可用网卡、判断防火墙是否放行、给出可操作的结论。
/// 托盘菜单和设置界面都用它，避免「手机连不上」时只能瞎猜。
/// </summary>
public sealed class NetworkDiagnostics
{
    public List<NetworkAdapterInfo> Adapters { get; private set; } = new();
    public bool TcpRuleExists { get; private set; }
    public bool UdpRuleExists { get; private set; }
    public string PrimaryIp { get; private set; }
    public bool AnyUsableAdapter { get; private set; }

    public static NetworkDiagnostics Run()
    {
        var d = new NetworkDiagnostics();
        d.FillAdapters();
        d.TcpRuleExists = FirewallHelper.RuleExists(FirewallHelper.TcpRuleName);
        d.UdpRuleExists = FirewallHelper.RuleExists(FirewallHelper.UdpRuleName);
        d.PrimaryIp = d.BestAddresses().Select(a => a.Ip).FirstOrDefault();
        d.AnyUsableAdapter = d.Adapters.Any(a => a.LooksUsable);
        return d;
    }

    /// <summary>
    /// 按「哪个地址最可能连得上」排序。多网卡机器（比如又插网线又连 WiFi、
    /// 或者装了 VirtualBox 虚拟机网卡）以前会把 192.168.56.1 这种客户端根本
    /// 连不上的地址排在最前面，用户照着填当然连不上。
    ///   1. Windows 实际对外发包用的那块网卡
    ///   2. 其它有默认网关的物理网卡 —— 手机和别的电脑通常就在这个网段
    ///   3. 其它物理网卡
    ///   4. 虚拟机网卡（最后才列）
    /// </summary>
    public List<NetworkAdapterInfo> BestAddresses()
    {
        return Adapters
            .OrderByDescending(a => a.IsPreferred)
            .ThenByDescending(a => a.LooksUsable)
            .ThenByDescending(a => a.HasGateway)
            .ThenByDescending(a => !a.LooksVirtual)
            .ToList();
    }

    private void FillAdapters()
    {
        try
        {
            var preferred = PreferredOutboundIp();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                bool virt = LooksLikeVirtual(ni.Name + " " + ni.Description);
                bool hasGateway = false;

                try
                {
                    hasGateway = ni.GetIPProperties().GatewayAddresses.Any(g =>
                        g.Address != null &&
                        g.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !g.Address.Equals(IPAddress.Any));
                }
                catch { }

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;   // 自动私有地址，多半没连上

                    Adapters.Add(new NetworkAdapterInfo
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        Ip = ip,
                        Type = ni.NetworkInterfaceType,
                        LooksVirtual = virt,
                        LooksUsable = !virt && !IPAddress.IsLoopback(addr.Address),
                        HasGateway = hasGateway,
                        IsPreferred = preferred != null && preferred.Equals(addr.Address)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举网卡失败", ex);
        }
    }

    /// <summary>
    /// 问一下系统：如果要往公网发包，会用本机哪个 IP？
    /// 这是「哪块网卡是主网卡」最可靠的判断，而且 UDP connect 不会真的发数据。
    /// </summary>
    private static IPAddress PreferredOutboundIp()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
            if (probe.LocalEndPoint is IPEndPoint local && !IPAddress.IsLoopback(local.Address))
                return local.Address;
        }
        catch { }
        return null;
    }

    private static bool LooksLikeVirtual(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("virtualbox") || t.Contains("vmware") || t.Contains("hyper-v") ||
               t.Contains("vethernet") || t.Contains("virtual") || t.Contains("loopback") ||
               t.Contains("tap-") || t.Contains("tun") || t.Contains("wsl");
    }

    /// <summary>一句话结论，直接显示给用户。</summary>
    public string Summary()
    {
        if (!AnyUsableAdapter)
            return "没有检测到可用的局域网网卡（可能没连 WiFi / 网线）";

        if (!TcpRuleExists)
            return $"防火墙未放行！手机和别的电脑连不上。点「一键放行」即可（需要管理员）";

        if (!UdpRuleExists)
            return "TCP 已放行，但 UDP 未放行：能连，但客户端的「自动发现」搜不到本机";

        return "网络正常：已放行，客户端可以通过 IP 或自动发现连接";
    }

    public bool NeedsFix => !TcpRuleExists || !UdpRuleExists;

    /// <summary>
    /// 尝试从本机用指定 IP 连一下自己（验证监听与端口占用；不经过防火墙）。
    /// </summary>
    public static bool TestLocalConnect(string ip, int port, out string error)
    {
        error = null;
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(ip, port);
            if (!task.Wait(2500))
            {
                error = "连接超时";
                return false;
            }
            return client.Connected;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 列出提示用的地址（推荐的在最前面）。
    /// 有真实局域网网卡时只列它们，虚拟机网卡（192.168.56.x 这种）不显示，
    /// 免得用户照着填然后连不上；一块可用网卡都没有时才全部列出。
    /// </summary>
    public List<string> Urls(int port)
    {
        var ordered = BestAddresses();
        var usable = ordered.Where(a => a.LooksUsable).ToList();
        if (usable.Count == 0) usable = ordered;

        var list = usable.Select(a => $"http://{a.Ip}:{port}/").ToList();
        if (list.Count == 0) list.Add($"http://127.0.0.1:{port}/");
        return list.Distinct().ToList();
    }
}
