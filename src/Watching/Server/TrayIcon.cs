using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using Watching.Common;
using Clipboard = System.Windows.Clipboard;

namespace Watching.Server;

/// <summary>
/// 托盘图标：服务端唯一的可见界面（一个托盘小图标），右键可以进设置或退出。
/// 服务端本身不会显示任何被截屏的内容。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly ServerHost _host;
    private readonly AppConfig _config;
    private SettingsWindow _settings;
    private LogWindow _logWindow;

    public TrayIcon(ServerHost host, AppConfig config)
    {
        _host = host;
        _config = config;

        _icon = new WinForms.NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Watching 服务端 - 静默运行中",
            Visible = true
        };

        _icon.DoubleClick += (_, _) => ShowSettings();
        _icon.ContextMenuStrip = BuildMenu();
        _host.Changed += UpdateTooltip;
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        menu.Opening += (_, _) => Rebuild(menu);
        Rebuild(menu);
        return menu;
    }

    private void Rebuild(WinForms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var title = new WinForms.ToolStripMenuItem("Watching 服务端 · 运行中")
        {
            Enabled = false,
            Font = new Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold)
        };
        menu.Items.Add(title);

        menu.Items.Add(new WinForms.ToolStripMenuItem(_host.DescribeClients()) { Enabled = false });

        var urls = _host.LocalUrls();
        foreach (var url in urls)
        {
            var item = new WinForms.ToolStripMenuItem("手机访问：" + url);
            item.Click += (_, _) => OpenUrl(url);
            menu.Items.Add(item);
        }

        var copy = new WinForms.ToolStripMenuItem("复制本机地址");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, urls)); } catch { }
        };
        menu.Items.Add(copy);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var settings = new WinForms.ToolStripMenuItem("设置…（需要密码）");
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        var state = new WinForms.ToolStripMenuItem(_host.RemoteControlEnabled
            ? "远程控制：已开启（点击关闭）"
            : "远程控制：已关闭（点击开启）");
        state.Click += (_, _) =>
        {
            if (!_host.RemoteControlEnabled && !PasswordGate.HasPassword)
            {
                WinForms.MessageBox.Show("开启远程控制前，请先在设置里设置一个密码。",
                    "Watching", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                ShowSettings();
                return;
            }

            if (_host.RemoteControlEnabled || PasswordGate.Verify(PromptPassword()))
                _host.ApplySettings(remote: !_host.RemoteControlEnabled);
        };
        menu.Items.Add(state);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var log = new WinForms.ToolStripMenuItem("查看日志");
        log.Click += (_, _) => ShowLog();
        menu.Items.Add(log);

        var exit = new WinForms.ToolStripMenuItem("退出服务端");
        exit.Click += (_, _) =>
        {
            if (WinForms.MessageBox.Show("确定要退出 Watching 服务端吗？退出后客户端将无法连接。",
                    "Watching", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Question)
                == WinForms.DialogResult.Yes)
            {
                ExitApp();
            }
        };
        menu.Items.Add(exit);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开浏览器失败", ex);
        }
    }

    private string PromptPassword()
    {
        var dlg = new PasswordDialog(PasswordDialogMode.Verify, "请输入设置密码");
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    public void ShowSettings()
    {
        try
        {
            if (!PasswordGate.HasPassword)
            {
                // 从未设置密码：允许直接进入设置，并在里面引导设置密码
                Log.Write("尚未设置密码，直接进入设置");
            }
            else
            {
                var dlg = new PasswordDialog(PasswordDialogMode.Verify, "进入设置需要密码");
                if (dlg.ShowDialog() != true) return;
            }

            if (_settings != null && _settings.IsVisible)
            {
                _settings.Activate();
                return;
            }

            _settings = new SettingsWindow(_host, _config);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
            _settings.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("打开设置窗口失败", ex);
        }
    }

    private void ShowLog()
    {
        if (_logWindow != null && _logWindow.IsVisible)
        {
            _logWindow.Activate();
            return;
        }
        _logWindow = new LogWindow();
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
        _logWindow.Activate();
    }

    private void ExitApp()
    {
        Log.Write("用户从托盘退出服务端");
        try { _host.Dispose(); } catch { }
        Dispose();
        App.ShutdownApp();
    }

    private void UpdateTooltip()
    {
        try
        {
            var text = _host.ClientCount == 0
                ? "Watching 服务端 - 静默运行中"
                : $"Watching 服务端 - {_host.ClientCount} 个客户端正在观看";
            if (text.Length > 62) text = text.Substring(0, 62);
            _icon.Text = text;
        }
        catch { }
    }

    // ---------------- 托盘图标绘制 ----------------

    private static Icon BuildIcon()
    {
        try
        {
            int size = 32;
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var bg = new SolidBrush(Color.FromArgb(255, 30, 41, 59));
                using var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), 7);
                g.FillPath(bg, path);

                using var pen = new Pen(Color.FromArgb(255, 96, 165, 250), 2.2f);
                // 眼睛轮廓
                g.DrawArc(pen, 5, 11, 22, 14, 200, 140);
                g.DrawArc(pen, 5, 11, 22, 14, 20, 140);
                // 瞳孔
                using var iris = new SolidBrush(Color.FromArgb(255, 226, 232, 240));
                g.FillEllipse(iris, 13, 14, 6, 6);
                using var pupil = new SolidBrush(Color.FromArgb(255, 37, 99, 235));
                g.FillEllipse(pupil, 14.5f, 15.5f, 3, 3);
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(h);
                return (Icon)tmp.Clone();
            }
            finally
            {
                NativeDestroyIcon(h);
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static void NativeDestroyIcon(IntPtr h) => DestroyIcon(h);

    public void Show()
    {
        _icon.Visible = true;
    }

    public void Dispose()
    {
        try { _host.Changed -= UpdateTooltip; } catch { }
        try { _icon.Visible = false; _icon.Dispose(); } catch { }
    }
}
