using System;
using System.Threading;
using System.Windows;
using Watching.Common;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Watching;

public partial class App : Application
{
    public static AppConfig Config { get; private set; }
    public static SingleInstance Instance { get; private set; }
    public static bool LaunchedAsServer { get; private set; }

    private Server.TrayIcon _tray;
    private Server.ServerHost _server;
    private Mutex _serverMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args ?? Array.Empty<string>());
        LaunchedAsServer = options.Server;

        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
            AppConfig.SetPath(options.ConfigPath);

        // 同一时间只允许一个服务端 / 一个客户端实例
        var instance = new SingleInstance(options.Server ? "Watching.Server" : "Watching.Client");
        if (!instance.IsPrimary)
        {
            if (options.Server)
            {
                MessageBox.Show("Watching 服务端已经在运行了。\n请在右下角托盘中找到它的图标。",
                    "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!string.IsNullOrEmpty(options.Host))
            {
                // 客户端重复启动时，把连接请求交给已经运行的实例
                NamedPipeHelper.TrySend($"connect|{options.Host}|{options.Port}");
            }
            Shutdown();
            return;
        }
        Instance = instance;

        Config = AppConfig.Load();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (options.Server) StartServer(options);
        else StartClient(options);
    }

    private void StartServer(CommandLineOptions options)
    {
        _serverMutex = new Mutex(true, @"Global\WatchingServerMutex", out _);

        if (options.Port > 0) Config.Port = options.Port;
        if (options.Fps > 0) Config.Fps = options.Fps;
        if (options.Quality > 0) Config.Quality = options.Quality;
        if (options.MaxWidth > 0) Config.MaxWidth = options.MaxWidth;
        Config.Save();

        _server = new Server.ServerHost(Config);
        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"服务端启动失败：{ex.Message}\n\n端口 {Config.Port} 可能已被占用。",
                "Watching 服务端", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _tray = new Server.TrayIcon(_server, Config);
        _tray.Show();

        Log.Write($"服务端已启动，端口 {Config.Port}");
        foreach (var url in _server.LocalUrls()) Log.Write("  可访问：" + url);
    }

    private void StartClient(CommandLineOptions options)
    {
        Log.Write($"启动电脑客户端：host='{options.Host ?? "(空)"}' port={options.Port} fullscreen={options.Fullscreen}");
        var win = new Desktop.MainWindow(options.Host, options.Port,
            options.Fullscreen || !string.IsNullOrEmpty(options.Host), options.Password);
        MainWindow = win;
        win.Show();
        if (options.Fullscreen) win.EnterFullscreen();

        try
        {
            var listener = new Thread(() =>
            {
                NamedPipeHelper.Serve("Watching.Client", msg =>
                {
                    var parts = msg.Split('|');
                    if (parts.Length >= 3 && parts[0] == "connect")
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is Desktop.MainWindow mw)
                                mw.ConnectTo(parts[1], int.TryParse(parts[2], out var p) ? p : 8899);
                            else
                                MainWindow?.Activate();
                        });
                    }
                });
            })
            { IsBackground = true, Name = "watching-client-pipe" };
            listener.Start();
        }
        catch { /* 管道不可用不影响主功能 */ }
    }

    public static void ShutdownApp()
    {
        try { Current?.Shutdown(); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        try { Config?.Save(); } catch { }
        try { _serverMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
