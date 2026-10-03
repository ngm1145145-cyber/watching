using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Watching.Mobile;
using Log = Android.Util.Log;
using Environment = System.Environment;

namespace Watching.Mobile;

[Activity(
    Name = "com.watching.mobile.MainActivity",
    Label = "Watching",
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.KeyboardHidden | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.UiMode | ConfigChanges.Density,
    Theme = "@style/WatchingTheme")]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher })]
public class MainActivity : Activity
{
    private const string Tag = "Watching";
    private const string PrefsName = "watching";
    private static readonly int[] QualityLevels = { 45, 65, 85 };
    private static readonly int[] FpsLevels = { 12, 20, 30 };
    private static readonly int[] WidthLevels = { 1080, 1600, 0 };
    private static readonly string[] QualityNames = { "流畅", "中", "高清" };

    private ISharedPreferences _prefs;

    // 登录界面
    private LinearLayout _loginPanel;
    private EditText _hostBox, _portBox, _pwdBox;
    private CheckBox _rememberBox;
    private TextView _loginStatus;
    private Button _searchButton;

    // 观看界面
    private FrameLayout _viewerPanel;
    private ScreenImageView _image;
    private View _hud;
    private TextView _statusText, _detailText;
    private Button _qualityButton, _fullscreenButton;

    private ScreenClient _client;
    private bool _fullscreen;
    private bool _hudVisible = true;
    private int _qualityLevel = 1;
    private long _lastStatsTick;
    private double _lastFps, _lastKbps;
    private int _frameWidth, _frameHeight;
    private volatile bool _decoding;
    private bool _searching;

    // 断线重连用（回到前台时恢复）
    private string _lastHost;
    private int _lastPort;
    private string _lastPassword;
    private bool _wasWatching;

    private void StartClient(string host, int port, string password)
    {
        Disconnect(keepUi: true);

        _lastHost = host;
        _lastPort = port;
        _lastPassword = password;

        _client = new ScreenClient(host, port, password)
        {
            ClientName = Build.Model ?? "安卓手机"
        };
        _client.StateChanged += OnStateChanged;
        _client.FrameReceived += OnFrame;
        _client.StatsUpdated += OnStats;
        _client.MessageReceived += OnMessage;

        _statusText.Text = "连接中…";
        _detailText.Text = $"{host}:{port}";
        _client.Start();
        ApplyQuality();
    }

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);

        BuildUi();
        RestorePrefs();

        // 支持从分享/链接带 IP 启动：watching://?ip=192.168.1.8&port=8899&pwd=xxx
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent intent)
    {
        var data = intent?.Data;
        if (data == null) return;

        var host = data.GetQueryParameter("ip") ?? data.GetQueryParameter("host");
        var port = data.GetQueryParameter("port");
        var pwd = data.GetQueryParameter("pwd");

        if (!string.IsNullOrEmpty(host)) _hostBox.Text = host;
        if (!string.IsNullOrEmpty(port)) _portBox.Text = port;
        if (!string.IsNullOrEmpty(pwd)) _pwdBox.Text = pwd;

        if (!string.IsNullOrEmpty(host)) Connect();
    }

    // ---------------- 界面 ----------------

    private int Dp(float value) => (int)(value * Resources.DisplayMetrics.Density + 0.5f);

    private static TextView Label(Context ctx, string text, float size = 13f)
    {
        var tv = new TextView(ctx) { Text = text };
        tv.SetTextSize(Android.Util.ComplexUnitType.Sp, size);
        tv.SetTextColor(Color.ParseColor("#98A2B3"));
        return tv;
    }

    private EditText Field(string hint)
    {
        var et = new EditText(this) { Hint = hint };
        et.SetTextColor(Color.ParseColor("#E6EAF2"));
        et.SetHintTextColor(Color.ParseColor("#5B6675"));
        et.SetTextSize(Android.Util.ComplexUnitType.Sp, 16f);
        et.SetSingleLine(true);
        et.SetBackgroundColor(Color.ParseColor("#0E1116"));
        et.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        return et;
    }

    private Button FlatButton(string text)
    {
        var b = new Button(this) { Text = text };
        b.SetTextColor(Color.ParseColor("#E6EAF2"));
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
        b.SetBackgroundColor(Color.ParseColor("#232A36"));
        b.SetPadding(Dp(14), Dp(8), Dp(14), Dp(8));
        b.SetAllCaps(false);
        return b;
    }

    private LinearLayout.LayoutParams Lp(int width, int height, float weight = 0f, int topMarginDp = 0)
    {
        var lp = new LinearLayout.LayoutParams(width, height);
        if (weight > 0) lp.Weight = weight;
        if (topMarginDp > 0) lp.TopMargin = Dp(topMarginDp);
        return lp;
    }

    private void BuildUi()
    {
        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.ParseColor("#0B0E14"));

        // ---------- 登录面板 ----------
        _loginPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _loginPanel.SetGravity(GravityFlags.Center);
        _loginPanel.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));

        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetBackgroundColor(Color.ParseColor("#161B26"));
        card.SetPadding(Dp(20), Dp(22), Dp(20), Dp(22));

        var logo = new TextView(this) { Text = "Watching" };
        logo.SetTextSize(Android.Util.ComplexUnitType.Sp, 26f);
        logo.SetTextColor(Color.ParseColor("#E6EAF2"));
        logo.SetTypeface(null, TypefaceStyle.Bold);
        card.AddView(logo);

        var sub = new TextView(this) { Text = "安卓手机客户端 · 连接电脑服务端查看屏幕" };
        sub.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        sub.SetTextColor(Color.ParseColor("#98A2B3"));
        card.AddView(sub, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 6));

        card.AddView(Label(this, "服务端 IP 地址"), Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 18));
        _hostBox = Field("例如 192.168.1.8");
        _hostBox.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri;
        card.AddView(_hostBox, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 6));

        card.AddView(Label(this, "端口"), Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 12));
        _portBox = Field("8899");
        _portBox.InputType = Android.Text.InputTypes.ClassNumber;
        _portBox.Text = "8899";
        card.AddView(_portBox, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 6));

        card.AddView(Label(this, "访问密码（没设置就留空）"), Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 12));
        _pwdBox = Field("服务端未开启访问密码时留空");
        _pwdBox.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword;
        card.AddView(_pwdBox, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 6));

        _rememberBox = new CheckBox(this) { Text = "记住这个地址", Checked = true };
        _rememberBox.SetTextColor(Color.ParseColor("#E6EAF2"));
        card.AddView(_rememberBox, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 12));

        var connect = new Button(this) { Text = "开始观看" };
        connect.SetTextColor(Color.White);
        connect.SetTextSize(Android.Util.ComplexUnitType.Sp, 16f);
        connect.SetBackgroundColor(Color.ParseColor("#3B82F6"));
        connect.SetAllCaps(false);
        connect.Click += (_, _) => Connect();

        // 「搜索服务端」：UDP 广播自动找，不用手抄 IP
        _searchButton = new Button(this) { Text = "搜索局域网服务端" };
        _searchButton.SetTextColor(Color.ParseColor("#E6EAF2"));
        _searchButton.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
        _searchButton.SetBackgroundColor(Color.ParseColor("#232A36"));
        _searchButton.SetAllCaps(false);
        _searchButton.Click += async (_, _) => await SearchServersAsync();

        var buttonRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var connectLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f };
        buttonRow.AddView(connect, connectLp);
        var searchLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent) { Weight = 1f };
        searchLp.LeftMargin = Dp(8);
        buttonRow.AddView(_searchButton, searchLp);
        card.AddView(buttonRow, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 8));

        _loginStatus = new TextView(this) { Text = "" };
        _loginStatus.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        _loginStatus.SetTextColor(Color.ParseColor("#FCA5A5"));
        card.AddView(_loginStatus, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 10));

        var hint = new TextView(this)
        {
            Text = "提示：手机和电脑要在同一个 WiFi 下。不知道 IP 就点「搜索局域网服务端」；\n" +
                   "地址也可以直接写 192.168.1.8:8899。连不上时请检查服务端是否已放行防火墙。"
        };
        hint.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        hint.SetTextColor(Color.ParseColor("#5B6675"));
        card.AddView(hint, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 10));

        var cardWrap = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        _loginPanel.AddView(card, cardWrap);
        root.AddView(_loginPanel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // ---------- 观看面板 ----------
        _viewerPanel = new FrameLayout(this);
        _viewerPanel.SetBackgroundColor(Color.Black);
        _viewerPanel.Visibility = ViewStates.Gone;

        _image = new ScreenImageView(this);
        _image.DoubleTapped += ToggleImmersive;
        _viewerPanel.AddView(_image, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 顶部信息条
        _hud = BuildHud();
        _viewerPanel.AddView(_hud, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

        root.AddView(_viewerPanel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);
    }

    private View BuildHud()
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Vertical };
        bar.SetBackgroundColor(Color.ParseColor("#CC0B0E14"));
        bar.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));

        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);

        _statusText = new TextView(this) { Text = "连接中…" };
        _statusText.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        _statusText.SetTextColor(Color.ParseColor("#E6EAF2"));
        top.AddView(_statusText, Lp(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1f));

        _qualityButton = FlatButton("画质:中");
        _qualityButton.Click += (_, _) => CycleQuality();
        top.AddView(_qualityButton, Lp(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 0, 0));

        var fit = FlatButton("适应");
        fit.Click += (_, _) => { _image.FitToScreen = true; };
        top.AddView(fit, Lp(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 0, 0));

        _fullscreenButton = FlatButton("全屏");
        _fullscreenButton.Click += (_, _) => ToggleImmersive();
        top.AddView(_fullscreenButton);

        var exit = FlatButton("断开");
        exit.SetTextColor(Color.ParseColor("#FCA5A5"));
        exit.Click += (_, _) => Disconnect();
        top.AddView(exit);

        bar.AddView(top);

        _detailText = new TextView(this) { Text = "" };
        _detailText.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
        _detailText.SetTextColor(Color.ParseColor("#98A2B3"));
        bar.AddView(_detailText, Lp(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 4));

        return bar;
    }

    // ---------------- 连接 ----------------

    private void RestorePrefs()
    {
        if (_prefs.GetBoolean("remember", true))
        {
            _hostBox.Text = _prefs.GetString("host", "") ?? "";
            _portBox.Text = _prefs.GetString("port", "8899") ?? "8899";
            _pwdBox.Text = _prefs.GetString("pwd", "") ?? "";
            _rememberBox.Checked = true;
        }
        else
        {
            _rememberBox.Checked = false;
        }
    }

    private void SavePrefs(string host, int port, string pwd)
    {
        var editor = _prefs.Edit();
        if (_rememberBox.Checked)
        {
            editor.PutBoolean("remember", true);
            editor.PutString("host", host);
            editor.PutString("port", port.ToString());
            editor.PutString("pwd", pwd ?? "");
        }
        else
        {
            editor.PutBoolean("remember", false);
        }
        editor.Apply();
    }

    /// <summary>
    /// 搜索局域网里的服务端（UDP 广播）。只找到一个就直接填上，多个则弹列表让用户选。
    /// </summary>
    private async Task SearchServersAsync()
    {
        if (_searching) return;
        _searching = true;

        try
        {
            _searchButton.Enabled = false;
            _searchButton.Text = "搜索中…";
            _loginStatus.SetTextColor(Color.ParseColor("#98A2B3"));
            _loginStatus.Text = "正在局域网内搜索服务端…";

            int port = int.TryParse((_portBox.Text ?? "8899").Trim(), out var p) && p > 0 ? p : 8899;
            var servers = await UdpDiscovery.SearchAsync(this, port, 3500);

            if (servers.Count == 0)
            {
                _loginStatus.SetTextColor(Color.ParseColor("#FCA5A5"));
                _loginStatus.Text = "没搜到服务端。请确认：①服务端已启动 ②同一个 WiFi " +
                                    "③服务端已放行防火墙（托盘右键 → 网络自检 → 一键放行）";
                return;
            }

            if (servers.Count == 1)
            {
                var s = servers[0];
                _hostBox.Text = s.Host;
                _portBox.Text = s.Port.ToString();
                _loginStatus.SetTextColor(Color.ParseColor("#86EFAC"));
                _loginStatus.Text = $"找到 {s.MachineName}（{s.Host}:{s.Port}），正在连接…";
                Connect();
                return;
            }

            _loginStatus.SetTextColor(Color.ParseColor("#86EFAC"));
            _loginStatus.Text = $"找到 {servers.Count} 个服务端，请选择：";

            var labels = servers.Select(s => s.Describe()).ToArray();
            new AlertDialog.Builder(this)
                .SetTitle("选择要连接的服务端")
                .SetItems(labels, (_, args) =>
                {
                    var s = servers[args.Which];
                    _hostBox.Text = s.Host;
                    _portBox.Text = s.Port.ToString();
                    _loginStatus.Text = $"已选择 {s.Host}:{s.Port}";
                    Connect();
                })
                .SetNegativeButton("取消", (_, _) => { })
                .Show();
        }
        catch (Exception ex)
        {
            _loginStatus.SetTextColor(Color.ParseColor("#FCA5A5"));
            _loginStatus.Text = "搜索失败：" + ex.Message;
            Log.Warn(Tag, "搜索失败: " + ex);
        }
        finally
        {
            _searching = false;
            _searchButton.Enabled = true;
            _searchButton.Text = "搜索局域网服务端";
        }
    }

    private void Connect()
    {
        var raw = (_hostBox.Text ?? "").Trim();
        var portText = (_portBox.Text ?? "8899").Trim();
        var pwd = _pwdBox.Text ?? "";

        if (raw.Length == 0)
        {
            _loginStatus.Text = "请输入服务端 IP 地址";
            return;
        }

        raw = raw.Replace("http://", "").Replace("https://", "").Replace("ws://", "").TrimEnd('/');
        if (raw.Contains('/')) raw = raw.Substring(0, raw.IndexOf('/'));

        string host = raw;
        if (raw.Contains(':'))
        {
            var parts = raw.Split(':');
            host = parts[0];
            if (parts.Length > 1 && parts[1].Length > 0) portText = parts[1];
        }

        if (!int.TryParse(portText, out int port) || port <= 0 || port > 65535)
        {
            _loginStatus.Text = "端口不正确";
            return;
        }

        _loginStatus.Text = "";
        SavePrefs(host, port, pwd);

        _loginPanel.Visibility = ViewStates.Gone;
        _viewerPanel.Visibility = ViewStates.Visible;
        _image.FitToScreen = true;
        SetHud(true);
        _wasWatching = true;

        StartClient(host, port, pwd);
    }

    private void Disconnect(bool keepUi = false)
    {
        if (_client != null)
        {
            _client.StateChanged -= OnStateChanged;
            _client.FrameReceived -= OnFrame;
            _client.StatsUpdated -= OnStats;
            _client.MessageReceived -= OnMessage;
            _client.Dispose();
            _client = null;
        }

        ExitImmersive();

        if (!keepUi)
        {
            _wasWatching = false;
            _viewerPanel.Visibility = ViewStates.Gone;
            _loginPanel.Visibility = ViewStates.Visible;
            var old = _image.Drawable;
            _image.SetImageDrawable(null);
            (old as BitmapDrawable)?.Bitmap?.Recycle();
        }
    }

    private void OnStateChanged(object sender, ScreenClientState state)
    {
        RunOnUiThread(() =>
        {
            switch (state)
            {
                case ScreenClientState.Connecting:
                    _statusText.Text = "连接中…";
                    break;
                case ScreenClientState.Connected:
                    _statusText.Text = "已连接";
                    break;
                case ScreenClientState.Reconnecting:
                    _statusText.Text = "已断开，正在重连…";
                    break;
                case ScreenClientState.AuthFailed:
                    _statusText.Text = "访问密码错误";
                    _loginStatus.Text = _client?.LastError ?? "访问密码不正确";
                    Disconnect();
                    _loginPanel.Visibility = ViewStates.Visible;
                    _viewerPanel.Visibility = ViewStates.Gone;
                    break;
                case ScreenClientState.Closed:
                    break;
            }

            if (state == ScreenClientState.Reconnecting && _client != null && !string.IsNullOrEmpty(_client.LastError))
                _detailText.Text = _client.LastError;
        });
    }

    private void OnMessage(object sender, ServerMessage msg)
    {
        RunOnUiThread(() =>
        {
            if (msg.Type == "welcome")
            {
                var who = string.IsNullOrEmpty(msg.MachineName) ? _client?.Host : msg.MachineName;
                _detailText.Text = $"服务端 {who} · 屏幕 {msg.ScreenWidth}×{msg.ScreenHeight}" +
                                   (msg.RemoteControl ? " · 允许远程控制" : "");
            }
            else if (msg.Type == "error" && !string.IsNullOrEmpty(msg.Message))
            {
                _detailText.Text = msg.Message;
            }
        });
    }

    private void OnStats(object sender, StatsEventArgs stats)
    {
        _lastFps = stats.Fps;
        _lastKbps = stats.Kbps;
        _frameWidth = stats.Width;
        _frameHeight = stats.Height;
        _lastStatsTick = System.Environment.TickCount64;

        RunOnUiThread(() =>
        {
            var text = $"{stats.Fps:F0} fps · {stats.Kbps:F0} KB/s";
            if (stats.Width > 0) text += $" · {stats.Width}×{stats.Height}";
            _statusText.Text = text;
        });
    }

    private void OnFrame(object sender, FrameEventArgs e)
    {
        if (_decoding) return;
        _decoding = true;

        try
        {
            var bitmap = BitmapFactory.DecodeByteArray(e.Jpeg, 0, e.Jpeg.Length);
            if (bitmap == null) return;

            RunOnUiThread(() =>
            {
                var old = _image.Drawable;
                _image.SetSourceSize(bitmap.Width, bitmap.Height);
                _image.SetImageBitmap(bitmap);
                (old as BitmapDrawable)?.Bitmap?.Recycle();
            });
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "解码失败: " + ex.Message);
        }
        finally
        {
            _decoding = false;
        }
    }

    // ---------------- 画质 / 全屏 ----------------

    private void CycleQuality()
    {
        _qualityLevel = (_qualityLevel + 1) % QualityLevels.Length;
        _qualityButton.Text = "画质:" + QualityNames[_qualityLevel];
        ApplyQuality();
    }

    private void ApplyQuality()
    {
        _client?.SendQuality(QualityLevels[_qualityLevel], FpsLevels[_qualityLevel], WidthLevels[_qualityLevel]);
    }

    private void SetHud(bool visible)
    {
        _hudVisible = visible;
        _hud.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ToggleImmersive()
    {
        if (_fullscreen) ExitImmersive();
        else EnterImmersive();
    }

    private void EnterImmersive()
    {
        _fullscreen = true;
        _fullscreenButton.Text = "退出全屏";
#pragma warning disable CS0618
        Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
            SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation |
            SystemUiFlags.LayoutFullscreen | SystemUiFlags.HideNavigation |
            SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
#pragma warning restore CS0618

        SetHud(true);
        _image.FitToScreen = true;
    }

    private void ExitImmersive()
    {
        _fullscreen = false;
        if (_fullscreenButton != null) _fullscreenButton.Text = "全屏";
#pragma warning disable CS0618
        Window.DecorView.SystemUiVisibility = StatusBarVisibility.Visible;
#pragma warning restore CS0618
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent e)
    {
        if (keyCode == Keycode.Back && _viewerPanel.Visibility == ViewStates.Visible)
        {
            if (_fullscreen) { ExitImmersive(); return true; }
            Disconnect();
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    protected override void OnPause()
    {
        base.OnPause();
        // 退到后台就断开，省电；回到前台在 OnResume 里自动重连
        if (_client != null)
        {
            _client.StateChanged -= OnStateChanged;
            _client.FrameReceived -= OnFrame;
            _client.StatsUpdated -= OnStats;
            _client.MessageReceived -= OnMessage;
            _client.Dispose();
            _client = null;
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_wasWatching && _client == null && !string.IsNullOrEmpty(_lastHost))
        {
            StartClient(_lastHost, _lastPort, _lastPassword);
        }
    }

    protected override void OnDestroy()
    {
        Disconnect();
        base.OnDestroy();
    }
}
