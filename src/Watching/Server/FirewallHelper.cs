using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using Watching.Common;

namespace Watching.Server;

/// <summary>
/// Windows 防火墙放行管理。
///
/// 局域网连不上的头号原因就是防火墙：三个配置文件默认都是 BlockInbound，
/// 没有入站规则时手机 / 别的电脑根本连不上。这里负责：
///   1. 查询是否已有放行规则
///   2. 用 netsh 添加规则（需要管理员权限，会弹一次 UAC）
/// </summary>
public static class FirewallHelper
{
    public const string TcpRuleName = "Watching Server (TCP)";
    public const string UdpRuleName = "Watching Server (UDP)";

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>查询指定名字的防火墙规则是否存在。</summary>
    public static bool RuleExists(string ruleName)
    {
        var output = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
        return output != null && output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>两条规则（TCP + UDP）是否都已存在。</summary>
    public static bool BothRulesExist() => RuleExists(TcpRuleName) && RuleExists(UdpRuleName);

    /// <summary>
    /// 静默添加规则（仅当本进程已是管理员时才会成功）。
    /// 返回 true 表示添加后规则确实存在。
    /// </summary>
    public static bool TryAddRulesQuietly(int port, out string message)
    {
        message = null;
        if (!IsElevated())
        {
            message = "需要管理员权限才能添加防火墙规则";
            return false;
        }

        AddRule(TcpRuleName, port, "TCP");
        AddRule(UdpRuleName, port, "UDP");

        bool ok = BothRulesExist();
        message = ok ? "已放行" : "规则添加后仍查询不到，可能被安全软件拦截";
        return ok;
    }

    /// <summary>
    /// 弹出 UAC 提权对话框来添加规则（服务端设置里的「一键放行」按钮）。
    /// </summary>
    public static bool AddRulesWithElevation(int port, out string message)
    {
        message = null;

        if (IsElevated())
            return TryAddRulesQuietly(port, out message);

        try
        {
            // 用 cmd 跑一段 netsh；这样只提权一次，TCP+UDP 一起加
            var script =
                $"netsh advfirewall firewall delete rule name=\"{TcpRuleName}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall delete rule name=\"{UdpRuleName}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" dir=in action=allow protocol=TCP localport={port} & " +
                $"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" dir=in action=allow protocol=UDP localport={port}";

            var psi = new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                message = "无法启动提权进程";
                return false;
            }

            if (!proc.WaitForExit(30000))
            {
                message = "提权操作超时（可能在等你点 UAC 确认框）";
                return false;
            }

            if (proc.ExitCode != 0)
            {
                message = $"netsh 返回错误码 {proc.ExitCode}";
                return false;
            }

            System.Threading.Thread.Sleep(600);
            bool ok = BothRulesExist();
            message = ok ? "已放行" : "命令执行完成但查不到规则，可能被安全软件拦截";
            return ok;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "你在 UAC 提示框上点了「否」，没有获得管理员权限";
            return false;
        }
        catch (Exception ex)
        {
            message = "添加规则失败：" + ex.Message;
            Log.Error("添加防火墙规则失败", ex);
            return false;
        }
    }

    /// <summary>删除自己的规则（卸载/排查用）。</summary>
    public static void RemoveRules(int port)
    {
        AddRule(TcpRuleName, port, "TCP", remove: true);
        AddRule(UdpRuleName, port, "UDP", remove: true);
    }

    private static void AddRule(string name, int port, string protocol, bool remove = false)
    {
        if (remove)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
            return;
        }
        RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={port}");
    }

    private static string RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            return stdout + stderr;
        }
        catch (Exception ex)
        {
            Log.Error("执行 netsh 失败", ex);
            return null;
        }
    }
}
