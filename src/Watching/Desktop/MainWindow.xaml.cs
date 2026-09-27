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
