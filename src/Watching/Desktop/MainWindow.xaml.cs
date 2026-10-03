using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Watching.Common;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using MessageBox = System.Windows.MessageBox;

namespace Watching.Desktop;

public partial class MainWindow : Window
{
    private FrameClient _client;
    private FullscreenWindow _fullscreen;
    private bool _remoteEnabled;

    public MainWindow(string host, int port, bool startFullscreen, string password = null)
    {
        InitializeComponent();

        View.FullscreenToggleRequested += ToggleFullscreen;
        View.ExitRequested += () =>
        {
            if (_fullscreen != null) { _fullscreen.Close(); _fullscreen = null; }
            Disconnect();
        };

        var cfg = App.Config;
        HostBox.Text = string.IsNullOrEmpty(host)
            ? (string.IsNullOrEmpty(cfg.LastHost) ? "" : cfg.LastHost)
            : host;
        PortBox.Text = (port > 0 ? port : (cfg.LastPort > 0 ? cfg.LastPort : cfg.Port)).ToString();
        PwdBox.Password = password ?? cfg.LastAccessPassword ?? "";

        Loaded += (_, _) =>
        {
            Common.Log.Write($"客户端窗口已加载：host='{HostBox.Text}' port='{PortBox.Text}' autoConnect={startFullscreen}");
            HostBox.Focus();
            if (startFullscreen && !string.IsNullOrEmpty(HostBox.Text)) Connect();
        };
        Closed += (_, _) => Cleanup();

        PreviewKeyDown += Window_PreviewKeyDown;
    }

    // ---------------- 连接 ----------------

    public void ConnectTo(string host, int port)
    {
        HostBox.Text = host;
        PortBox.Text = port.ToString();
        Connect();
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Connect();

    private void Local_Click(object sender, RoutedEventArgs e)
    {
        HostBox.Text = "127.0.0.1";
        PortBox.Text = App.Config.Port.ToString();
    }

    /// <summary>局域网自动搜索服务端（UDP 广播），用户不用再手抄 IP。</summary>
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SearchButton.IsEnabled = false;
            SearchButton.Content = "搜索中…";
            StateText.Text = "正在局域网内搜索服务端…";

            int port = int.TryParse(PortBox.Text, out var p) && p > 0 ? p : 8899;
            var servers = await DiscoveryClient.SearchAsync(port, 3000);

            if (servers.Count == 0)
            {
                StateText.Text = "没搜到服务端";
                MessageBox.Show(this,
                    "局域网里没有搜到 Watching 服务端。\n\n" +
                    "请确认：\n" +
                    "  1. 服务端那台电脑已经启动 Watching 服务端\n" +
                    "  2. 两台机器在同一个 WiFi / 局域网\n" +
                    "  3. 服务端已放行防火墙（服务端托盘右键 → 网络自检 → 一键放行）\n\n" +
                    "也可以直接手动输入 IP 后点「连接」。",
                    "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (servers.Count > 1)
            {
                var lines = servers.Select((s, i) => $"{i + 1}. {s.Describe()}").ToArray();
                int pick = PromptPick(lines);
                if (pick < 0) { StateText.Text = "已取消"; return; }
                HostBox.Text = servers[pick].Host;
                PortBox.Text = servers[pick].Port.ToString();
            }
            else
            {
                HostBox.Text = servers[0].Host;
                PortBox.Text = servers[0].Port.ToString();
            }

            StateText.Text = $"找到 {servers.Count} 个服务端，正在连接 {HostBox.Text}:{PortBox.Text}";
            Connect();
        }
        catch (Exception ex)
        {
            Common.Log.Error("搜索服务端失败", ex);
            StateText.Text = "搜索失败：" + ex.Message;
        }
        finally
        {
            SearchButton.IsEnabled = true;
            SearchButton.Content = "搜索服务端";
        }
    }

    /// <summary>搜到多个服务端时让用户挑一个（返回索引，-1 表示取消）。</summary>
    private int PromptPick(string[] lines)
    {
        var win = new Window
        {
            Title = "选择要连接的服务端",
            Width = 460,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (System.Windows.Media.Brush)FindResource("Bg")
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "局域网里找到多个服务端，选一个：",
            Margin = new Thickness(0, 0, 0, 10)
        });

        var list = new System.Windows.Controls.ListBox { Height = 170 };
        foreach (var l in lines) list.Items.Add(l);
        list.SelectedIndex = 0;
        panel.Children.Add(list);

        int result = -1;
        var ok = new System.Windows.Controls.Button
        {
            Content = "连接",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(14, 7, 14, 7)
        };
        ok.Click += (_, _) =>
        {
            result = list.SelectedIndex;
            win.Close();
        };
        panel.Children.Add(ok);

        win.Content = panel;
        win.ShowDialog();
        return result;
    }

    private void Connect()
    {
        string host = (HostBox.Text ?? "").Trim();
        if (host.Length == 0)
        {
            MessageBox.Show(this, "请输入服务端 IP 地址。", "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
            HostBox.Focus();
            return;
        }

        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host.Substring(7);
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host.Substring(8);
        host = host.TrimEnd('/');
        if (host.Contains(':'))
        {
            var parts = host.Split(':');
            host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var p)) PortBox.Text = p.ToString();
        }

        if (!int.TryParse(PortBox.Text, out int port) || port <= 0 || port > 65535)
        {
            MessageBox.Show(this, "端口不正确。", "Watching", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Disconnect();

        _client = new FrameClient(Dispatcher) { DisplayName = Environment.MachineName, Kind = "pc" };
        _client.StateChanged += OnStateChanged;
        _client.MessageReceived += OnMessage;
        View.Attach(_client);
        View.AllowRemoteInput = _remoteEnabled;

        App.Config.LastHost = host;
        App.Config.LastPort = port;
        App.Config.LastAccessPassword = PwdBox.Password;
        App.Config.Save();

        _client.Connect(host, port, PwdBox.Password);
        ConnectButton.Content = "断开";
        ConnectButton.Click -= Connect_Click;
        ConnectButton.Click += Disconnect_Click;
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

    private void Disconnect()
    {
        if (_client != null)
        {
            _client.StateChanged -= OnStateChanged;
            _client.MessageReceived -= OnMessage;
            _client.Dispose();
            _client = null;
        }

        ConnectButton.Content = "连接";
        ConnectButton.Click -= Disconnect_Click;
        ConnectButton.Click += Connect_Click;
        StateText.Text = "未连接";
        RemoteCheck.IsChecked = false;
        RemoteCheck.IsEnabled = false;
        _remoteEnabled = false;
        View.AllowRemoteInput = false;
    }

    private void OnStateChanged(object sender, ClientState state)
    {
        StateText.Text = state switch
        {
            ClientState.Connecting => "正在连接…",
            ClientState.Connected => $"已连接 {HostBox.Text}:{PortBox.Text}",
            ClientState.Failed => "连接失败，正在重试…",
            ClientState.Closed => "已断开",
            _ => "未连接"
        };
    }

    private void OnMessage(object sender, ServerMessage msg)
    {
        if (msg.Type == "welcome")
        {
            _remoteEnabled = msg.RemoteControl;
            RemoteCheck.IsEnabled = msg.RemoteControl;
            RemoteCheck.IsChecked = msg.RemoteControl;
            View.AllowRemoteInput = msg.RemoteControl;

            var server = string.IsNullOrEmpty(msg.MachineName) ? HostBox.Text : msg.MachineName;
            StateText.Text = $"已连接 {server} ({HostBox.Text}:{PortBox.Text})" +
                             (msg.RemoteControl ? " · 可远程控制" : "");

            HintText.Text = msg.RemoteControl
                ? "远程控制已开启：移动鼠标即可操作对方 · 以管理员身份运行的窗口需要服务端也用管理员启动才能控制 · F11 全屏"
                : "F11 全屏 · Ctrl+滚轮 缩放 · Esc 退出全屏（远程控制未开启，只能看）";
        }
        else if (msg.Type == "state")
        {
            _remoteEnabled = msg.RemoteControl;
            RemoteCheck.IsEnabled = msg.RemoteControl;
            RemoteCheck.IsChecked = msg.RemoteControl;
            View.AllowRemoteInput = msg.RemoteControl;
        }
        else if (msg.Type == "error" && !string.IsNullOrEmpty(msg.Message))
        {
            StateText.Text = msg.Message;
        }
    }

    // ---------------- 全屏 ----------------

    public void EnterFullscreen() => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (_fullscreen != null)
        {
            _fullscreen.Close();
            _fullscreen = null;
            return;
        }

        if (_client == null)
        {
            MessageBox.Show(this, "请先连接到服务端。", "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _fullscreen = new FullscreenWindow(View, _remoteEnabled);
        _fullscreen.Closed += (_, _) =>
        {
            _fullscreen = null;
            Activate();
        };
        _fullscreen.Show();
        _fullscreen.Activate();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (_remoteEnabled && _client != null && Keyboard.FocusedElement is not System.Windows.Controls.TextBox
            && Keyboard.FocusedElement is not System.Windows.Controls.PasswordBox)
        {
            View.ForwardKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void Cleanup()
    {
        try { if (_fullscreen != null) { _fullscreen.Close(); _fullscreen = null; } } catch { }
        Disconnect();
        App.Config.Save();
    }
}
