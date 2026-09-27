using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;

namespace Watching.Desktop;

/// <summary>
/// 全屏窗口：把主窗口里的画面控件临时“挂”到这里，退出后归还。
/// 用同一个控件避免了重复解码和多份 WebSocket。
/// </summary>
public partial class FullscreenWindow : Window
{
    private readonly ScreenView _view;
    private readonly object _originalParent;
    private readonly bool _remote;

    public FullscreenWindow(ScreenView view, bool remote)
    {
        InitializeComponent();
        _view = view;
        _remote = remote;

        // 记下原来的位置
        _originalParent = VisualTreeHelper.GetParent(view);

        Loaded += (_, _) =>
        {
            try
            {
                if (_originalParent is System.Windows.Controls.Panel panel)
                    panel.Children.Remove(_view);

                Host.Children.Add(_view);
                _view.IsFullscreenHosted = true;
                _view.ShowHudWhenFullscreen = true;
                _view.Focus();
            }
            catch (Exception ex)
            {
                Common.Log.Error("进入全屏失败", ex);
                Close();
            }
        };

        Closed += (_, _) => Restore();
    }

    private void Restore()
    {
        try
        {
            if (Host.Children.Contains(_view)) Host.Children.Remove(_view);
            if (_originalParent is System.Windows.Controls.Panel panel && !panel.Children.Contains(_view))
                panel.Children.Add(_view);
            _view.IsFullscreenHosted = false;
        }
        catch (Exception ex)
        {
            Common.Log.Error("退出全屏恢复失败", ex);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.F11)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (_remote)
        {
            _view.ForwardKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        }
    }
}
