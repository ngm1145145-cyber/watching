using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageSource = System.Windows.Media.ImageSource;
using System.Windows.Threading;
using Watching.Common;
using UserControl = System.Windows.Controls.UserControl;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using Point = System.Windows.Point;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using Keyboard = System.Windows.Input.Keyboard;
using Cursors = System.Windows.Input.Cursors;

namespace Watching.Desktop;

/// <summary>
/// 画面显示控件：负责显示最新帧、缩放/拖动、全屏切换、远程输入、统计信息。
/// 主窗口和全屏窗口共用它。
/// </summary>
public partial class ScreenView : UserControl
{
    private FrameClient _client;
    private System.Windows.Media.Imaging.BitmapSource _image;
    private DispatcherTimer _hudTimer;
    private DispatcherTimer _statTimer;
    private Point _dragStart;
    private Point _offsetStart;
    private bool _dragging;
    private bool _hudVisible = true;

    public bool AllowRemoteInput { get; set; }
    public bool IsFullscreenHosted { get; set; }

    public event Action FullscreenToggleRequested;
    public event Action ExitRequested;

    public ScreenView()
    {
        InitializeComponent();

        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _hudTimer.Tick += (_, _) => { _hudTimer.Stop(); SetHud(!IsFullscreenHosted || ShowHudWhenFullscreen); };

        _statTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _statTimer.Tick += (_, _) => UpdateStatusPill();
        _statTimer.Start();

        MouseMove += OnImageMouseMove;
        MouseLeftButtonDown += OnImageMouseDown;
        MouseLeftButtonUp += OnImageMouseUp;
        MouseRightButtonDown += OnImageMouseRightDown;
        MouseRightButtonUp += OnImageMouseRightUp;
        MouseWheel += OnImageWheel;
        MouseMove += (_, _) => ShowHud();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    public bool ShowHudWhenFullscreen { get; set; } = true;

    public System.Windows.Media.Imaging.BitmapSource CurrentImage => _image;

    public void Attach(FrameClient client)
    {
        _client = client;
        _client.FrameReceived += OnFrame;
        _client.StatsUpdated += OnStats;
        _client.MessageReceived += OnMessage;
    }

    // ---------------- 渲染 ----------------

    private void OnFrame(object sender, FrameEventArgs e)
    {
        _image = e.Image;
        ScreenImage.Source = _image;
        UpdateLayoutMode();
    }
    private void OnStats(object sender, ClientStats stats)
    {
        _lastStats = stats;
        UpdateStatusPill();
    }

    private ClientStats _lastStats;

    private void OnMessage(object sender, ServerMessage msg)
    {
        if (msg.Type == "error" && !string.IsNullOrEmpty(msg.Message))
        {
            ErrorPill.Text = msg.Message;
            ErrorPill.Visibility = Visibility.Visible;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) => { t.Stop(); ErrorPill.Visibility = Visibility.Collapsed; };
            t.Start();
        }
    }

    private void UpdateStatusPill()
    {
        var s = _lastStats;
        if (s == null)
        {
            StatusPill.Text = "等待画面…";
            return;
        }
        string res = s.Width > 0 ? $" · {s.Width}×{s.Height}" : "";
        string tip = FitToggle.IsChecked == true ? "适应窗口" : $"{(int)(ZoomSlider.Value * 100)}%";
        string delta = s.DeltaRatio > 0.01 ? $" · 增量 {s.DeltaRatio:P0}" : "";
        StatusPill.Text = $"{s.Fps:F0} fps · {s.Kbps:F0} KB/s{res}{delta} · {tip}";
        StatusPill.Visibility = Visibility.Visible;

        if (IsFullscreenHosted)
        {
            MirrorStatus.Text = StatusPill.Text;
        }
    }

    private void UpdateLayoutMode()
    {
        bool fit = FitToggle.IsChecked == true;
        if (fit)
        {
            ScreenImage.Stretch = Stretch.Uniform;
            ScreenImage.Width = double.NaN;
            ScreenImage.Height = double.NaN;
            ScreenImage.RenderTransform = null;
            ZoomSlider.IsEnabled = false;
        }
        else
        {
            ScreenImage.Stretch = Stretch.Fill;
            ScreenImage.Width = _image?.PixelWidth ?? 0;
            ScreenImage.Height = _image?.PixelHeight ?? 0;
            ScreenImage.RenderTransform = new ScaleTransform(ZoomSlider.Value, ZoomSlider.Value);
            ScreenImage.RenderTransformOrigin = new Point(0.5, 0.5);
            ZoomSlider.IsEnabled = true;
        }
    }

    // ---------------- HUD ----------------

    private void ShowHud()
    {
        SetHud(true);
        if (IsFullscreenHosted && ShowHudWhenFullscreen)
        {
            _hudTimer.Stop();
            _hudTimer.Start();
        }
    }

    private void SetHud(bool visible)
    {
        _hudVisible = visible;
        Hud.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Cursor = visible ? Cursors.Arrow : Cursors.None;
    }

    private void Hud_MouseEnter(object sender, MouseEventArgs e) => SetHud(true);

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        FitToggle.IsChecked = true;
        ZoomSlider.Value = 1.0;
        UpdateLayoutMode();
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        if (FitToggle.IsChecked == true && e.NewValue != 1.0)
            FitToggle.IsChecked = false;
        UpdateLayoutMode();
        UpdateStatusPill();
    }

    private void Fit_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateLayoutMode();
        UpdateStatusPill();
    }

    private int _qualityLevel = 1;

    private void Quality_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        _qualityLevel = (_qualityLevel + 1) % 3;
        switch (_qualityLevel)
        {
            case 0: _client.SetQuality(45, 12, 1080); QualityButton.Content = "画质:流畅"; break;
            case 1: _client.SetQuality(65, 20, 1600); QualityButton.Content = "画质:中"; break;
            default: _client.SetQuality(85, 30, 0); QualityButton.Content = "画质:高清"; break;
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => FullscreenToggleRequested?.Invoke();

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_image == null) return;
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Watching");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"watching-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_image));
            using var fs = System.IO.File.Create(file);
            encoder.Save(fs);

            ErrorPill.Text = "已保存到 " + file;
            ErrorPill.Visibility = Visibility.Visible;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) => { t.Stop(); ErrorPill.Visibility = Visibility.Collapsed; };
            t.Start();
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
        }
    }

    // ---------------- 远程输入 ----------------

    private bool TryMapPoint(Point p, out double nx, out double ny)
    {
        nx = ny = 0;
        if (_image == null || !AllowRemoteInput) return false;

        double iw = _image.PixelWidth, ih = _image.PixelHeight;
        double cw = ActualWidth, ch = ActualHeight;
        if (iw <= 0 || ih <= 0 || cw <= 0) return false;

        bool fit = FitToggle.IsChecked == true;
        double scale = fit ? Math.Min(cw / iw, ch / ih) : ZoomSlider.Value;
        double dw = iw * scale, dh = ih * scale;
        double dx = (cw - dw) / 2, dy = (ch - dh) / 2;

        double x = (p.X - dx) / dw;
        double y = (p.Y - dy) / dh;
        if (x < -0.05 || x > 1.05 || y < -0.05 || y > 1.05) return false;

        nx = Math.Clamp(x, 0, 1);
        ny = Math.Clamp(y, 0, 1);
        return true;
    }

    private long _lastMoveSentTick;
    private double _lastMoveX = -1, _lastMoveY = -1;

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_dragging && FitToggle.IsChecked != true)
        {
            ZoomSlider.Value = Math.Max(0.2, Math.Min(6, ZoomSlider.Value));
            return;
        }

        if (!AllowRemoteInput) return;
        if (!TryMapPoint(p, out var nx, out var ny)) return;

        // 限流到 50Hz，并且位置没实质变化就不发（鼠标事件本身可能几百 Hz）
        long now = Environment.TickCount64;
        if (now - _lastMoveSentTick < 20) return;
        if (Math.Abs(nx - _lastMoveX) < 0.0008 && Math.Abs(ny - _lastMoveY) < 0.0008) return;

        _lastMoveSentTick = now;
        _lastMoveX = nx;
        _lastMoveY = ny;

        _client?.Send(new ClientMessage
        {
            Type = "input",
            Kind = "move",
            X = Math.Round(nx, 4),
            Y = Math.Round(ny, 4)
        });
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (AllowRemoteInput && TryMapPoint(p, out var nx, out var ny))
        {
            _client?.Send(new ClientMessage { Type = "input", Kind = "down", Button = "left", X = nx, Y = ny });
        }
        else if (FitToggle.IsChecked != true)
        {
            _dragging = true;
            _dragStart = p;
            _offsetStart = new Point(0, 0);
            CaptureMouse();
        }
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            return;
        }

        var p = e.GetPosition(this);
        if (AllowRemoteInput && TryMapPoint(p, out var nx, out var ny))
            _client?.Send(new ClientMessage { Type = "input", Kind = "up", Button = "left", X = nx, Y = ny });
    }

    private void OnImageMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (AllowRemoteInput && TryMapPoint(p, out var nx, out var ny))
            _client?.Send(new ClientMessage { Type = "input", Kind = "down", Button = "right", X = nx, Y = ny });
    }

    private void OnImageMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (AllowRemoteInput && TryMapPoint(p, out var nx, out var ny))
            _client?.Send(new ClientMessage { Type = "input", Kind = "up", Button = "right", X = nx, Y = ny });
    }

    private void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        if (AllowRemoteInput &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) == false &&
            _client != null)
        {
            var p = e.GetPosition(this);
            if (TryMapPoint(p, out var nx, out var ny))
            {
                _client.Send(new ClientMessage
                {
                    Type = "input", Kind = "wheel", Delta = e.Delta, X = nx, Y = ny
                });
                return;
            }
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FitToggle.IsChecked = false;
            ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * (e.Delta > 0 ? 1.1 : 0.9), 0.2, 6);
            UpdateLayoutMode();
            e.Handled = true;
        }
    }

    /// <summary>键盘转发（仅在服务端开启远程控制时）。</summary>
    public void ForwardKey(Key key, ModifierKeys modifiers)
    {
        if (!AllowRemoteInput || _client == null) return;

        string name = key switch
        {
            Key.Enter => "enter",
            Key.Space => "space",
            Key.Back => "backspace",
            Key.Escape => "esc",
            Key.Tab => "tab",
            Key.Left => "left",
            Key.Right => "right",
            Key.Up => "up",
            Key.Down => "down",
            Key.Delete => "delete",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            _ => key.ToString()
        };

        _client.Send(new ClientMessage
        {
            Type = "input",
            Kind = "key",
            Key = name,
            Ctrl = modifiers.HasFlag(ModifierKeys.Control),
            Alt = modifiers.HasFlag(ModifierKeys.Alt),
            Shift = modifiers.HasFlag(ModifierKeys.Shift),
            Win = modifiers.HasFlag(ModifierKeys.Windows)
        });
    }
}
