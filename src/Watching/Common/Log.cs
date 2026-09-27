using System;
using System.Diagnostics;
using System.IO;

namespace Watching.Common;

/// <summary>极简日志：写到 %AppData%\Watching\watching.log，并可选输出到控制台。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly bool HasConsole = TryGetConsole();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Watching");

    public static string LogPath => Path.Combine(Dir, "watching.log");

    private static bool TryGetConsole()
    {
        try { return Console.Out != null; }
        catch { return false; }
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Debug.WriteLine(line);
        if (HasConsole)
        {
            try { Console.WriteLine(line); } catch { }
        }
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    File.Delete(LogPath);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { }
    }

    public static void Error(string message, Exception ex = null)
        => Write("[ERROR] " + message + (ex == null ? "" : $" :: {ex.GetType().Name}: {ex.Message}"));
}
