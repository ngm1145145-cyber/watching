using System;
using System.Collections.Generic;
using System.Linq;

namespace Watching.Common;

/// <summary>
/// 「局域网到底有没有包进来」的活动记录。
///
/// 排查「手机连不上」时最有用的一条信息是：本机到底有没有收到那个设备的包。
/// 如果这里一条记录都没有，说明包根本没到本机（IP 填错 / 不在同一个网段 /
/// 路由器开了 AP 隔离），再怎么改服务端都没用。
/// </summary>
public static class NetworkActivity
{
    public sealed class Entry
    {
        public DateTime Time { get; init; }
        public string Kind { get; init; }
        public string Remote { get; init; }
        public string Detail { get; init; }

        public override string ToString()
        {
            var text = $"{Time:MM-dd HH:mm:ss}  {Kind,-8} 来自 {Remote}";
            if (!string.IsNullOrEmpty(Detail)) text += "  " + Detail;
            return text;
        }
    }

    private const int MaxEntries = 40;
    private static readonly object Gate = new();
    private static readonly LinkedList<Entry> Items = new();

    /// <summary>记录一条。同一个来源 3 秒内重复的同类事件只记第一次，避免刷屏。</summary>
    public static void Record(string kind, string remote, string detail = null)
    {
        if (string.IsNullOrEmpty(remote)) remote = "?";
        try
        {
            lock (Gate)
            {
                var last = Items.Last?.Value;
                if (last != null &&
                    last.Kind == kind && last.Remote == remote &&
                    (DateTime.Now - last.Time).TotalSeconds < 3)
                {
                    return;
                }

                Items.AddLast(new Entry { Time = DateTime.Now, Kind = kind, Remote = remote, Detail = detail });
                while (Items.Count > MaxEntries) Items.RemoveFirst();
            }
        }
        catch
        {
            // 诊断信息记不上不该影响主流程
        }
    }

    public static List<Entry> Recent(int max = 12)
    {
        lock (Gate)
        {
            return Items.Reverse().Take(max).ToList();
        }
    }

    public static bool HasAny
    {
        get { lock (Gate) { return Items.Count > 0; } }
    }

    /// <summary>有没有收到过「非本机」的活动（用来判断外面的设备到底通不通）。</summary>
    public static bool HasRemote
    {
        get
        {
            lock (Gate)
            {
                return Items.Any(e => !IsLocal(e.Remote));
            }
        }
    }

    private static bool IsLocal(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return true;
        return ip.StartsWith("127.") || ip == "::1" || ip.StartsWith("169.254.");
    }
}
