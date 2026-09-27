using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Watching.Common;

namespace Watching.Server;

public partial class LogWindow : Window
{
    private readonly DispatcherTimer _timer;

    public LogWindow()
    {
        InitializeComponent();
        PathText.Text = Log.LogPath;
        Refresh();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        try
        {
            if (!File.Exists(Log.LogPath))
            {
                LogBox.Text = "（暂无日志）";
                return;
            }

            using var fs = new FileStream(Log.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();

            const int max = 200_000;
            if (text.Length > max) text = "…（已省略较早内容）…\r\n" + text.Substring(text.Length - max);

            if (LogBox.Text != text)
            {
                LogBox.Text = text;
                LogBox.ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            LogBox.Text = "读取日志失败：" + ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Log.LogPath + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
