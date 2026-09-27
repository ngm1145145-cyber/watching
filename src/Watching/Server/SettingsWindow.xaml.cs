using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Watching.Common;
using MessageBox = System.Windows.MessageBox;

namespace Watching.Server;

public partial class SettingsWindow : Window
{
    private readonly ServerHost _host;
    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer;
    private bool _loading = true;
    private bool _remoteBackup;
    private string _accessBackup;

    public SettingsWindow(ServerHost host, AppConfig config)
    {
        InitializeComponent();
        _host = host;
        _config = config;

        FpsSlider.Value = config.Fps;
        QualitySlider.Value = config.Quality;
        WidthSlider.Value = config.MaxWidth <= 0 ? 1920 : config.MaxWidth;
        _remoteBackup = config.RemoteControlEnabled;
        RemoteCheck.IsChecked = config.RemoteControlEnabled;
        AccessCheck.IsChecked = config.AccessPasswordEnabled;
        AccessBox.Text = config.AccessPassword ?? "";
        _accessBackup = config.AccessPassword;
        AutoStartCheck.IsChecked = AutoStartHelper.IsEnabled();

        UpdatePasswordState();
        UpdateSubtitle();

        _loading = false;
        UpdateLabels();

        ListenText.Text = "局域网访问地址：" + string.Join("    ", host.LocalUrls());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshClients();
        _timer.Start();
        RefreshClients();

        Closed += (_, _) =>
        {
            _timer.Stop();
            // 被取消 / 直接关窗时回滚远程控制与访问密码
            if (RemoteCheck.IsChecked != _remoteBackup || (AccessBox.Text ?? "") != (_accessBackup ?? ""))
            {
                _config.RemoteControlEnabled = _remoteBackup;
                _config.AccessPassword = _accessBackup;
                _host.ApplySettings(remote: _remoteBackup, save: true);
            }
        };
    }

    private void UpdateSubtitle()
    {
        SubtitleText.Text = $"{Environment.MachineName} · 端口 {_config.Port} · 客户端可通过 IP 地址连接";
    }

    private void RefreshClients()
    {
        try
        {
            ClientsText.Text = _host.DescribeClients();
        }
        catch { }
    }

    private void UpdateLabels()
    {
        FpsText.Text = $"{(int)FpsSlider.Value} fps";
        QualityText.Text = $"{(int)QualitySlider.Value}";
        WidthText.Text = $"{(int)WidthSlider.Value} px";
    }

    private void UpdatePasswordState()
    {
        if (PasswordGate.HasPassword)
        {
            PwdStateText.Text = "已设置密码（进入设置需要输入）";
            PwdButton.Content = "修改密码";
        }
        else
        {
            PwdStateText.Text = "尚未设置密码 —— 现在任何人都能打开这个设置窗口";
            PwdButton.Content = "设置密码";
        }
    }

    private void Fps_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        UpdateLabels();
        _host.ApplySettings(fps: (int)FpsSlider.Value, save: false);
    }

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        UpdateLabels();
        _host.ApplySettings(quality: (int)QualitySlider.Value, save: false);
    }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        UpdateLabels();
        _host.ApplySettings(maxWidth: (int)WidthSlider.Value, save: false);
    }

    private void Access_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (AccessCheck.IsChecked == true && string.IsNullOrWhiteSpace(AccessBox.Text))
        {
            AccessBox.Text = RandomPassword();
        }

        _config.AccessPasswordEnabled = AccessCheck.IsChecked == true;
        _config.AccessPassword = AccessBox.Text?.Trim() ?? "";
        _config.Save();
    }

    private void RandomPwd_Click(object sender, RoutedEventArgs e)
    {
        AccessBox.Text = RandomPassword();
        _config.AccessPassword = AccessBox.Text;
        if (AccessCheck.IsChecked != true) AccessCheck.IsChecked = true;
        _config.Save();
        MessageBox.Show(this, "已生成新的访问密码：\n\n" + AccessBox.Text + "\n\n请在客户端输入这个密码。",
            "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string RandomPassword()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyz23456789";
        var rnd = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
    }

    private void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var mode = PasswordGate.HasPassword ? PasswordDialogMode.Change : PasswordDialogMode.Create;
        var dlg = new PasswordDialog(mode, mode == PasswordDialogMode.Change ? "修改密码" : "设置密码") { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            PasswordGate.SetPassword(dlg.Password);
            UpdatePasswordState();
            MessageBox.Show(this, "密码已保存。", "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        var win = new LogWindow { Owner = this };
        win.Show();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 远程控制：开启前必须有密码，并且要验证
        if (RemoteCheck.IsChecked == true && _remoteBackup == false)
        {
            if (!PasswordGate.HasPassword)
            {
                MessageBox.Show(this, "开启远程控制前，请先设置一个密码（上面的“安全”一栏）。",
                    "Watching", MessageBoxButton.OK, MessageBoxImage.Warning);
                RemoteCheck.IsChecked = false;
                return;
            }

            var dlg = new PasswordDialog(PasswordDialogMode.Verify, "开启远程控制需要验证密码") { Owner = this };
            if (dlg.ShowDialog() != true)
            {
                RemoteCheck.IsChecked = false;
                MessageBox.Show(this, "未通过密码验证，远程控制保持关闭。",
                    "Watching", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        _config.AccessPasswordEnabled = AccessCheck.IsChecked == true;
        _config.AccessPassword = AccessBox.Text?.Trim() ?? "";
        _config.RemoteControlEnabled = RemoteCheck.IsChecked == true;
        _config.Fps = (int)FpsSlider.Value;
        _config.Quality = (int)QualitySlider.Value;
        _config.MaxWidth = (int)WidthSlider.Value;
        _config.AutoStart = AutoStartCheck.IsChecked == true;

        if (AutoStartCheck.IsChecked == true)
        {
            if (!AutoStartHelper.Apply(true))
            {
                MessageBox.Show(this, "写入开机启动项失败（可能被安全软件拦截），其它设置已保存。",
                    "Watching", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            AutoStartHelper.Apply(false);
        }

        _remoteBackup = _config.RemoteControlEnabled;
        _accessBackup = _config.AccessPassword;

        _host.ApplySettings(_config.Quality, _config.Fps, _config.MaxWidth <= 0 ? 0 : _config.MaxWidth,
            _config.RemoteControlEnabled);
        _config.Save();

        Log.Write($"设置已保存：远程控制={_config.RemoteControlEnabled}，访问密码={_config.AccessControlActive}，" +
                  $"帧率={_config.Fps}，画质={_config.Quality}，宽度={_config.MaxWidth}");

        Close();
    }
}
