using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
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
        d.PrimaryIp = d.Adapters.Where(a => a.LooksUsable).Select(a => a.Ip).FirstOrDefault()
                      ?? d.Adapters.Select(a => a.Ip).FirstOrDefault();
        d.AnyUsableAdapter = d.Adapters.Any(a => a.LooksUsable);
        return d;
    }

    private void FillAdapters()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var desc = ni.Name + " —— " + ni.Description;
                bool virt = LooksLikeVirtual(ni.Name + " " + ni.Description);

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
                        LooksUsable = !virt && !IPAddress.IsLoopback(addr.Address)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举网卡失败", ex);
        }
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

    /// <summary>列出所有提示用的地址。</summary>
    public List<string> Urls(int port)
    {
        var list = new List<string>();
        foreach (var a in Adapters.Where(a => a.LooksUsable)) list.Add($"http://{a.Ip}:{port}/");
        if (list.Count == 0)
            foreach (var a in Adapters) list.Add($"http://{a.Ip}:{port}/");
        if (list.Count == 0) list.Add($"http://127.0.0.1:{port}/");
        return list.Distinct().ToList();
    }
}
