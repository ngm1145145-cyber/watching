using System;
using Microsoft.Win32;
using Watching.Common;

namespace Watching.Server;

/// <summary>开机自启动（写入当前用户的 Run 键，不需要管理员权限）。</summary>
public static class AutoStartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WatchingServer";

    public static string ExecutablePath
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) return exe;
                return System.Reflection.Assembly.GetEntryAssembly()?.Location;
            }
            catch { return null; }
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return false;

            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --server");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机启动失败", ex);
            return false;
        }
    }
}
