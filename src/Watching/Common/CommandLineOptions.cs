using System;
using System.Globalization;

namespace Watching.Common;

/// <summary>命令行参数解析。</summary>
public sealed class CommandLineOptions
{
    public bool Server { get; private set; }
    public string Host { get; private set; }
    public int Port { get; private set; } = 8899;
    public int Fps { get; private set; }
    public int Quality { get; private set; }
    public int MaxWidth { get; private set; }
    public bool Fullscreen { get; private set; }

    /// <summary>自定义配置文件路径（--config）。</summary>
    public string ConfigPath { get; private set; }

    /// <summary>客户端访问密码（--password）。</summary>
    public string Password { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].Trim();
            string next = i + 1 < args.Length ? args[i + 1] : null;

            switch (a.ToLowerInvariant())
            {
                case "--server":
                case "-s":
                case "server":
                case "/server":
                    o.Server = true;
                    break;

                case "--client":
                case "-c":
                case "client":
                case "/client":
                    o.Server = false;
                    break;

                case "--fullscreen":
                case "-f":
                    o.Fullscreen = true;
                    break;

                case "--config":
                    if (next != null) { o.ConfigPath = next; i++; }
                    break;

                case "--password":
                case "--pwd":
                    if (next != null) { o.Password = next; i++; }
                    break;

                case "--connect":
                    if (next != null) { o.ApplyHost(next); i++; }
                    break;

                case "--host":
                    if (next != null) { o.Host = next; i++; }
                    break;

                case "--port":
                case "-p":
                    if (next != null && int.TryParse(next, out var p)) { o.Port = p; i++; }
                    break;

                case "--fps":
                    if (next != null && int.TryParse(next, out var f)) { o.Fps = f; i++; }
                    break;

                case "--quality":
                case "-q":
                    if (next != null && int.TryParse(next, out var q)) { o.Quality = q; i++; }
                    break;

                case "--width":
                case "-w":
                    if (next != null && int.TryParse(next, out var w)) { o.MaxWidth = w; i++; }
                    break;

                default:
                    // 允许直接写 IP 或 IP:端口
                    if (!a.StartsWith("-") && !a.StartsWith("/") && LooksLikeAddress(a))
                        o.ApplyHost(a);
                    break;
            }
        }
        return o;
    }

    private void ApplyHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var s = value.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
        s = s.TrimEnd('/');
        if (s.Contains('/')) s = s.Substring(0, s.IndexOf('/'));

        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            Host = s.Substring(0, colon);
            Port = port;
        }
        else
        {
            Host = s;
        }
    }

    private static bool LooksLikeAddress(string s)
    {
        foreach (var c in s)
        {
            bool ok = char.IsDigit(c) || c == '.' || c == ':' || char.IsLetter(c) || c == '-';
            if (!ok) return false;
        }
        return s.Length > 0 && s.Contains('.');
    }
}
