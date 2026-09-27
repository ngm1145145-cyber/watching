using System;
using System.Collections.Generic;
using Watching.Common;

namespace Watching.Server;

/// <summary>
/// 把客户端发来的鼠标 / 键盘事件注入到本机。
/// 只有在设置里打开“允许远程控制”后才会被调用。
/// </summary>
public static class InputInjector
{
    private const int InputSize = 40; // sizeof(INPUT) on x64

    private static readonly Dictionary<string, ushort> NamedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D,
            ["shift"] = 0x10, ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12,
            ["pause"] = 0x13, ["capslock"] = 0x14, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["space"] = 0x20, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23,
            ["home"] = 0x24, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["lwin"] = 0x5B, ["win"] = 0x5B, ["rwin"] = 0x5C,
            ["num0"] = 0x60, ["num1"] = 0x61, ["num2"] = 0x62, ["num3"] = 0x63,
            ["num4"] = 0x64, ["num5"] = 0x65, ["num6"] = 0x66, ["num7"] = 0x67,
            ["num8"] = 0x68, ["num9"] = 0x69,
            ["multiply"] = 0x6A, ["add"] = 0x6B, ["subtract"] = 0x6D,
            ["decimal"] = 0x6E, ["divide"] = 0x6F,
            ["numlock"] = 0x90, ["scrolllock"] = 0x91,
            [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF,
            ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
        };

    private static bool _loggedFailure;

    // ---------------- 鼠标 ----------------

    /// <summary>x / y 为屏幕绝对像素坐标（虚拟桌面坐标系）。</summary>
    public static void MouseMove(double x, double y)
    {
        int vx = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        int vy = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN));

        // 归一化到 0..65535
        double nx = (x - vx) * 65535.0 / Math.Max(1, vw - 1);
        double ny = (y - vy) * 65535.0 / Math.Max(1, vh - 1);
        nx = Math.Clamp(nx, 0, 65535);
        ny = Math.Clamp(ny, 0, 65535);

        var input = new User32.INPUT
        {
            type = User32.INPUT_MOUSE,
            u = new User32.INPUTUNION
            {
                mi = new User32.MOUSEINPUT
                {
                    dx = (int)Math.Round(nx),
                    dy = (int)Math.Round(ny),
                    dwFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK
                }
            }
        };
        Send(input);
    }

    public static void MouseButton(string button, bool down)
    {
        uint flag = button?.ToLowerInvariant() switch
        {
            "right" => down ? User32.MOUSEEVENTF_RIGHTDOWN : User32.MOUSEEVENTF_RIGHTUP,
            "middle" => down ? User32.MOUSEEVENTF_MIDDLEDOWN : User32.MOUSEEVENTF_MIDDLEUP,
            _ => down ? User32.MOUSEEVENTF_LEFTDOWN : User32.MOUSEEVENTF_LEFTUP,
        };

        var input = new User32.INPUT
        {
            type = User32.INPUT_MOUSE,
            u = new User32.INPUTUNION { mi = new User32.MOUSEINPUT { dwFlags = flag } }
        };
        Send(input);
    }

    public static void MouseWheel(int delta, bool horizontal = false)
    {
        var input = new User32.INPUT
        {
            type = User32.INPUT_MOUSE,
            u = new User32.INPUTUNION
            {
                mi = new User32.MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = horizontal ? User32.MOUSEEVENTF_HWHEEL : User32.MOUSEEVENTF_WHEEL
                }
            }
        };
        Send(input);
    }

    // ---------------- 键盘 ----------------

    /// <summary>发送一个组合键/按键。key 可以是单个字符、名字（enter/f5）或 VK 十六进制（0x41）。</summary>
    public static void Key(string key, bool ctrl, bool alt, bool shift, bool win)
    {
        if (string.IsNullOrEmpty(key)) return;

        if (!TryResolveVirtualKey(key, out ushort vk, out ushort scan, out bool unicode, out char literal))
            return;

        var mods = new List<(ushort vk, ushort scan)>();
        if (ctrl) mods.Add((0x11, 0));
        if (alt) mods.Add((0x12, 0));
        if (shift) mods.Add((0x10, 0));
        if (win) mods.Add((0x5B, 0));

        var inputs = new List<User32.INPUT>(mods.Count * 2 + 2);

        foreach (var (mvk, mscan) in mods)
            inputs.Add(MakeKey(mvk, mscan, false, false));

        if (unicode)
        {
            inputs.Add(MakeUnicode(literal, false));
            inputs.Add(MakeUnicode(literal, true));
        }
        else
        {
            inputs.Add(MakeKey(vk, scan, false, false));
            inputs.Add(MakeKey(vk, scan, true, false));
        }

        for (int i = mods.Count - 1; i >= 0; i--)
            inputs.Add(MakeKey(mods[i].vk, mods[i].scan, true, false));

        Send(inputs.ToArray());
    }

    private static bool TryResolveVirtualKey(string key, out ushort vk, out ushort scan, out bool unicode, out char literal)
    {
        vk = 0; scan = 0; unicode = false; literal = '\0';
        string k = key.Trim();

        if (k.Length == 1)
        {
            char c = k[0];
            if (c >= 'a' && c <= 'z') { vk = (ushort)char.ToUpperInvariant(c); scan = 0; return true; }
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
            if (NamedKeys.TryGetValue(k, out vk)) return true;

            // 其它可打印字符用 Unicode 注入，布局无关
            unicode = true;
            literal = c;
            return true;
        }

        if (NamedKeys.TryGetValue(k, out vk)) return true;

        if (k.StartsWith("f", StringComparison.OrdinalIgnoreCase) && int.TryParse(k.AsSpan(1), out int fn)
            && fn >= 1 && fn <= 24)
        {
            vk = (ushort)(0x70 + fn - 1);
            return true;
        }

        if (k.StartsWith("0x") && ushort.TryParse(k.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
        {
            vk = hex;
            return true;
        }

        if (ushort.TryParse(k, out ushort code) && code > 0 && code < 256)
        {
            vk = code;
            return true;
        }

        return false;
    }

    private static User32.INPUT MakeKey(ushort vk, ushort scan, bool up, bool extended)
    {
        uint flags = 0;
        if (up) flags |= User32.KEYEVENTF_KEYUP;
        if (extended) flags |= User32.KEYEVENTF_EXTENDEDKEY;

        if (scan == 0 && vk != 0)
            scan = (ushort)User32.MapVirtualKey(vk, User32.MAPVK_VK_TO_VSC);

        return new User32.INPUT
        {
            type = User32.INPUT_KEYBOARD,
            u = new User32.INPUTUNION
            {
                ki = new User32.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
            }
        };
    }

    private static User32.INPUT MakeUnicode(char c, bool up)
    {
        return new User32.INPUT
        {
            type = User32.INPUT_KEYBOARD,
            u = new User32.INPUTUNION
            {
                ki = new User32.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = User32.KEYEVENTF_UNICODE | (up ? User32.KEYEVENTF_KEYUP : 0)
                }
            }
        };
    }

    private static void Send(params User32.INPUT[] inputs)
    {
        try
        {
            uint sent = User32.SendInput((uint)inputs.Length, inputs, InputSize);
            if (sent == 0 && !_loggedFailure)
            {
                _loggedFailure = true;
                Log.Write("SendInput 返回 0（可能被 UIPI 拦截：目标窗口以管理员身份运行时会拒绝普通权限注入）");
            }
        }
        catch (Exception ex)
        {
            Log.Error("输入注入失败", ex);
        }
    }
}
