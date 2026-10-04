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

    // 远程控制
    private Button _controlButton, _keyboardButton;
    private TextView _controlHint;
    private EditText _keyInput;
    private bool _controlMode;
    private bool _remoteKeyDown;

    // 单指手势状态
    private float _remoteStartX, _remoteStartY;      // 控件坐标（判定位移用）
    private float _remoteCurNx, _remoteCurNy;        // 归一化当前位置
    private float _remoteMoved;                      // 最大位移（控件像素）
    private bool _remoteLongPressFired;

    // 长按 = 右键
    private Handler _longPressHandler;
    private Java.Lang.Runnable _longPressRunnable;

    // 双指滚动
    private bool _twoFingerActive;
    private float _twoFingerStartDist;
    private float _twoFingerLastY;
    private float _twoFingerAcc;

    private ScreenClient _client;
    private bool _fullscreen;
    private bool _hudVisible = true;
    private int _qualityLevel = 1;
    private long _lastStatsTick;
    private double _lastFps, _lastKbps;
    private int _frameWidth, _frameHeight;
    private volatile bool _decoding;
    private bool _searching;

    // 分块增量合成用的基准位图（整帧替换，增量帧往上面贴变化块）
    private Bitmap _composite;
    private double _lastDeltaRatio;
    private string _detailBase;

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
        // 控制模式下：单指当鼠标，双指当滚轮/缩放
        _image.RemoteTouchHandler = e => OnRemoteTouch(e, false);
        _image.RemoteMultiTouchHandler = e => OnRemoteTouch(e, true);
        _image.RemoteGestureCancel = OnRemoteGestureCancel;
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

        // 第二行：远程控制开关 + 键盘
        var ctrlRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        ctrlRow.SetGravity(GravityFlags.CenterVertical);

        _controlButton = FlatButton("控制:关");
        _controlButton.Click += (_, _) => ToggleControl();
        ctrlRow.AddView(_controlButton, Lp(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 0, 0));

        _keyboardButton = FlatButton("键盘");
        _keyboardButton.Visibility = ViewStates.Gone;
        _keyboardButton.Click += (_, _) => ShowSoftKeyboard();
        ctrlRow.AddView(_keyboardButton);

        _controlHint = new TextView(this) { Text = "" };
        _controlHint.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
        _controlHint.SetTextColor(Color.ParseColor("#98A2B3"));
        ctrlRow.AddView(_controlHint, Lp(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 1f));

        bar.AddView(ctrlRow);

        // 隐藏的输入框：控制模式下点「键盘」聚焦它，用输入法打字
        _keyInput = new EditText(this);
        _keyInput.SetSingleLine(true);
        _keyInput.SetTextColor(Color.Transparent);
        _keyInput.SetBackgroundColor(Color.Transparent);
        _keyInput.SetCursorVisible(false);
        _keyInput.ImeOptions = Android.Views.InputMethods.ImeAction.Done;
        _keyInput.AddTextChangedListener(new KeyWatcher(this));
        var hiddenLp = new LinearLayout.LayoutParams(2, 2);
        bar.AddView(_keyInput, hiddenLp);

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

            _image.SetImageDrawable(null);
            try { _composite?.Recycle(); } catch { }
            _composite = null;
            _lastDeltaRatio = 0;
            _detailBase = null;
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
                _detailBase = $"服务端 {who} · 屏幕 {msg.ScreenWidth}×{msg.ScreenHeight}" +
                              (msg.RemoteControl ? " · 可远程控制" : " · 仅观看");
                UpdateDetailWithDelta();
                UpdateControlUi();
            }
            else if (msg.Type == "state")
            {
                // 服务端开关远程控制时，客户端界面跟着更新
                UpdateControlUi();
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
            var parsed = e.Frame;
            if (parsed == null) return;

            if (!parsed.IsDelta)
            {
                // 整帧：作为新的合成基准
                var bmp = BitmapFactory.DecodeByteArray(parsed.FullJpeg, 0, parsed.FullJpeg.Length);
                if (bmp == null) return;

                var mutable = bmp.GetConfig() == Bitmap.Config.Argb8888 && bmp.IsMutable
                    ? bmp
                    : bmp.Copy(Bitmap.Config.Argb8888, true);
                if (!ReferenceEquals(mutable, bmp)) bmp.Recycle();

                RunOnUiThread(() =>
                {
                    _composite = mutable;
                    _image.SetSourceSize(mutable.Width, mutable.Height);
                    _image.SetImageBitmap(mutable);
                    UpdateDetailWithDelta();
                });
                return;
            }

            // 增量帧：把变化的分块画到基准上
            var baseBmp = _composite;
            if (baseBmp == null) return;   // 还没有基准，等关键帧

            var decoded = new System.Collections.Generic.List<(int x, int y, Bitmap bmp)>();
            foreach (var tile in parsed.Tiles)
            {
                var tb = BitmapFactory.DecodeByteArray(tile.Jpeg, 0, tile.Jpeg.Length);
                if (tb != null) decoded.Add((tile.X, tile.Y, tb));
            }
            if (decoded.Count == 0) return;

            long totalFrames = e.DeltaFrames + e.FullFrames;
            _lastDeltaRatio = totalFrames > 0 ? e.DeltaFrames / (double)totalFrames : 0;

            RunOnUiThread(() =>
            {
                try
                {
                    using var canvas = new Canvas(baseBmp);
                    using var paint = new Paint { FilterBitmap = false };
                    foreach (var (x, y, bmp) in decoded)
                    {
                        canvas.DrawBitmap(bmp, x, y, paint);
                        bmp.Recycle();
                    }
                    // 复用同一个 Bitmap 时需要通知控件重绘
                    _image.Invalidate();
                    UpdateDetailWithDelta();
                }
                catch (Exception ex)
                {
                    Log.Warn(Tag, "分块合成失败: " + ex.Message);
                }
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

    private void UpdateDetailWithDelta()
    {
        string baseText = _detailBase;
        if (string.IsNullOrEmpty(baseText) && _client != null && !string.IsNullOrEmpty(_client.ServerName))
            baseText = $"服务端 {_client.ServerName} · 屏幕 {_client.ScreenWidth}×{_client.ScreenHeight}";

        _detailText.Text = (baseText ?? "") + (_lastDeltaRatio > 0.01 ? $" · 增量 {_lastDeltaRatio:P0}" : "");
    }

    // ---------------- 远程控制 ----------------

    /// <summary>切换「控制」开关。服务端没开远程控制时给出提示。</summary>
    private void ToggleControl()
    {
        if (_client == null) return;

        if (!_client.RemoteControlEnabled)
        {
            _controlHint.Text = "服务端未开启远程控制";
            new AlertDialog.Builder(this)
                .SetTitle("无法控制")
                .SetMessage("服务端没有开启「允许远程控制」。\n\n请在服务端那台电脑上：托盘图标右键 → 设置 → " +
                            "勾选「允许客户端远程控制本机鼠标和键盘」（需要先设置密码并验证）。")
                .SetPositiveButton("知道了", (_, _) => { })
                .Show();
            UpdateControlUi();
            return;
        }

        _controlMode = !_controlMode;
        UpdateControlUi();

        if (_controlMode)
        {
            _controlHint.Text = ControlHintText;
            _image.FitToScreen = true;   // 控制时用适应窗口，坐标最直观
        }
        else
        {
            _controlHint.Text = "";
            _remoteKeyDown = false;
            CancelLongPressWatch();
            _twoFingerActive = false;
            _twoFingerAcc = 0;
            HideSoftKeyboard();
        }
    }

    private void UpdateControlUi()
    {
        if (_controlButton == null) return;

        bool allowed = _client?.RemoteControlEnabled == true;
        _controlButton.Text = !allowed ? "控制:不可用" : (_controlMode ? "控制:开" : "控制:关");
        _keyboardButton.Visibility = (_controlMode && allowed) ? ViewStates.Visible : ViewStates.Gone;

        if (!allowed && _controlMode)
        {
            _controlMode = false;
        }
    }

    /// <summary>
    /// 触摸 → 鼠标事件。支持：
    ///   单指点按 = 左键单击；单指拖动 = 按住左键拖动；长按 = 右键单击；双指上下滑 = 滚轮。
    /// </summary>
    private bool OnRemoteTouch(MotionEvent e, bool allowMultiTouch)
    {
        if (!_controlMode || _client == null) return false;

        // ---------- 双指上下滑 = 滚动（距离稳定时按滚动处理，明显变化时留给缩放手势） ----------
        if (allowMultiTouch && e.PointerCount >= 2)
        {
            // 只有两根手指都落在画面上才算滚动，否则还给本地缩放
            if (!_image.TryMapToImage(e.GetX(0), e.GetY(0), out _, out _)) return false;
            if (!_image.TryMapToImage(e.GetX(1), e.GetY(1), out _, out _)) return false;

            float dist = (float)Math.Sqrt((e.GetX(1) - e.GetX(0)) * (e.GetX(1) - e.GetX(0)) +
                                          (e.GetY(1) - e.GetY(0)) * (e.GetY(1) - e.GetY(0)));
            float midY = (e.GetY(0) + e.GetY(1)) / 2f;

            switch (e.ActionMasked)
            {
                case MotionEventActions.PointerDown:
                    _twoFingerActive = true;
                    _twoFingerStartDist = dist;
                    _twoFingerLastY = midY;
                    _twoFingerAcc = 0;
                    break;

                case MotionEventActions.Move:
                    if (!_twoFingerActive)
                    {
                        _twoFingerActive = true;
                        _twoFingerStartDist = dist;
                        _twoFingerLastY = midY;
                        _twoFingerAcc = 0;
                        break;
                    }

                    float ratio = _twoFingerStartDist > 1 ? dist / _twoFingerStartDist : 1f;

                    // 距离基本没变 → 当作滚动；明显变化 → 交给缩放手势
                    if (ratio > 0.85f && ratio < 1.15f)
                    {
                        // 手指上滑 = 内容跟着往上走 = 滚轮向下（和触屏直觉一致）
                        _twoFingerAcc += _twoFingerLastY - midY;
                        _twoFingerLastY = midY;

                        const float Notch = 60f;                    // 每 60px 算一格滚轮
                        if (Math.Abs(_twoFingerAcc) >= Notch)
                        {
                            int notches = (int)(_twoFingerAcc / Notch);
                            _twoFingerAcc -= notches * Notch;
                            int delta = notches > 0 ? -120 : 120;
                            for (int i = 0; i < Math.Min(Math.Abs(notches), 5); i++)
                            {
                                _client.Send(new ClientMessage
                                {
                                    Type = "input",
                                    Kind = "wheel",
                                    Delta = delta
                                });
                            }
                        }
                    }
                    else
                    {
                        // 明显缩放：让 ScreenImageView 自己处理缩放，本轮不当作滚动
                        return false;
                    }
                    break;

                case MotionEventActions.PointerUp:
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    _twoFingerActive = false;
                    _twoFingerAcc = 0;
                    break;
            }

            return true;   // 消费掉，避免本地缩放
        }

        // ---------- 单指 ----------
        float nx, ny;
        if (!_image.TryMapToImage(e.GetX(), e.GetY(), out nx, out ny)) return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _remoteCurNx = nx;
                _remoteCurNy = ny;
                _remoteMoved = 0;
                _remoteKeyDown = false;
                _remoteLongPressFired = false;
                _remoteStartX = e.GetX();
                _remoteStartY = e.GetY();

                // 按下的位置先报给服务端；左键 down 推迟到确认是拖动时再发
                _client.Send(new ClientMessage { Type = "input", Kind = "move", X = nx, Y = ny });
                StartLongPressWatch();
                return true;

            case MotionEventActions.Move:
                _remoteCurNx = nx;
                _remoteCurNy = ny;
                _remoteMoved = Math.Max(_remoteMoved,
                    (float)Math.Sqrt((e.GetX() - _remoteStartX) * (e.GetX() - _remoteStartX) +
                                     (e.GetY() - _remoteStartY) * (e.GetY() - _remoteStartY)));

                if (_remoteLongPressFired) return true;      // 右键已发，忽略后续移动

                // 动了就取消长按
                if (_remoteMoved >= MoveSlop) CancelLongPressWatch();

                // 位移超过阈值 → 升级成「按住左键拖动」
                if (!_remoteKeyDown && _remoteMoved >= MoveSlop)
                {
                    _remoteKeyDown = true;
                    _client.Send(new ClientMessage { Type = "input", Kind = "down", Button = "left", X = nx, Y = ny });
                }

                _client.Send(new ClientMessage { Type = "input", Kind = "move", X = nx, Y = ny });
                return true;

            case MotionEventActions.Up:
                CancelLongPressWatch();
                if (_remoteLongPressFired) { _remoteLongPressFired = false; return true; }

                if (_remoteKeyDown)
                {
                    _client.Send(new ClientMessage { Type = "input", Kind = "up", Button = "left", X = nx, Y = ny });
                    _remoteKeyDown = false;
                }
                else
                {
                    // 位移很小 = 单击：这时才补上左键 down+up
                    _client.Send(new ClientMessage { Type = "input", Kind = "down", Button = "left", X = nx, Y = ny });
                    _client.Send(new ClientMessage { Type = "input", Kind = "up", Button = "left", X = nx, Y = ny });
                }
                return true;

            case MotionEventActions.Cancel:
                CancelLongPressWatch();
                if (_remoteKeyDown)
                {
                    _client.Send(new ClientMessage { Type = "input", Kind = "up", Button = "left", X = nx, Y = ny });
                    _remoteKeyDown = false;
                }
                return true;
        }

        return false;
    }

    // ---- 长按 = 右键 ----

    /// <summary>长按阈值：480ms。比双击间隔(300ms)长、比单击响应(120ms)长，
    /// 又不会让人觉得"按了半天没反应"。</summary>
    private const int LongPressMs = 480;
    private const float MoveSlop = 8f;
    private const string ControlHintText = "点按=单击 · 拖动=按住左键 · 长按=右键 · 双指上下滑=滚动";

    private void StartLongPressWatch()
    {
        CancelLongPressWatch();
        var handler = new Handler(Looper.MainLooper);
        _longPressRunnable = new Java.Lang.Runnable(() =>
        {
            if (!_controlMode || _client == null) return;
            if (_remoteMoved >= MoveSlop || _remoteKeyDown || _remoteLongPressFired) return;

            // 按下的那一刻已经发过 move 了，这里补一次右键 down + up
            _client.Send(new ClientMessage { Type = "input", Kind = "move", X = _remoteCurNx, Y = _remoteCurNy });
            _client.Send(new ClientMessage { Type = "input", Kind = "down", Button = "right", X = _remoteCurNx, Y = _remoteCurNy });
            _client.Send(new ClientMessage { Type = "input", Kind = "up", Button = "right", X = _remoteCurNx, Y = _remoteCurNy });

            _remoteLongPressFired = true;
            VibrateShort();
            ShowHint("已发送右键单击");

            // 1.2 秒后把提示条恢复成手势说明
            handler.PostDelayed(new Java.Lang.Runnable(() =>
            {
                if (_controlHint != null && _controlMode) _controlHint.Text = ControlHintText;
            }), 1200);
        });
        handler.PostDelayed(_longPressRunnable, LongPressMs);
        _longPressHandler = handler;
    }

    private void CancelLongPressWatch()
    {
        if (_longPressHandler != null && _longPressRunnable != null)
        {
            _longPressHandler.RemoveCallbacks(_longPressRunnable);
        }
        _longPressHandler = null;
        _longPressRunnable = null;
    }

    /// <summary>第二根手指落下时调用：结束单指会话，别留下按住的左键或没取消的右键计时。</summary>
    private void OnRemoteGestureCancel()
    {
        CancelLongPressWatch();
        _remoteLongPressFired = false;

        if (_client != null && _remoteKeyDown)
        {
            _client.Send(new ClientMessage
            {
                Type = "input", Kind = "up", Button = "left",
                X = _remoteCurNx, Y = _remoteCurNy
            });
            _remoteKeyDown = false;
        }

        _twoFingerActive = true;
    }

    private void ShowHint(string text)
    {
        if (_controlHint != null) _controlHint.Text = text;
    }

    private void VibrateShort()
    {
        try
        {
            Vibrator v;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                v = (GetSystemService(VibratorManagerService) as VibratorManager)?.DefaultVibrator;
            }
            else
            {
#pragma warning disable CA1422 // API 31 之前只有 Context.VibratorService
                v = GetSystemService(VibratorService) as Vibrator;
#pragma warning restore CA1422
            }
            if (v == null) return;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                v.Vibrate(VibrationEffect.CreateOneShot(30, 80));
            else
#pragma warning disable CA1422 // API 26 之前只有 Vibrate(long)
                v.Vibrate(30);
#pragma warning restore CA1422
        }
        catch
        {
            // 没有振动权限或无马达：静默忽略
        }
    }

    private void ShowSoftKeyboard()
    {
        if (!_controlMode) return;
        _keyInput.Text = "";
        _keyInput.RequestFocus();
        var imm = (Android.Views.InputMethods.InputMethodManager)
            GetSystemService(Android.Content.Context.InputMethodService);
        imm?.ShowSoftInput(_keyInput, Android.Views.InputMethods.ShowFlags.Implicit);
    }

    private void HideSoftKeyboard()
    {
        var imm = (Android.Views.InputMethods.InputMethodManager)GetSystemService(InputMethodService);
        imm?.HideSoftInputFromWindow(_keyInput.WindowToken, 0);
        _keyInput.ClearFocus();
    }

    /// <summary>把输入法上屏的字符发到服务端（中文、表情等也能用）。</summary>
    private sealed class KeyWatcher : Java.Lang.Object, Android.Text.ITextWatcher
    {
        private readonly MainActivity _owner;
        public KeyWatcher(MainActivity owner) => _owner = owner;

        public void AfterTextChanged(Android.Text.IEditable s)
        {
            if (!_owner._controlMode || s == null || s.Length() == 0) return;

            string text = s.ToString();
            for (int i = 0; i < text.Length; i++)
            {
                string ch = text.Substring(i, 1);
                _owner._client?.Send(new ClientMessage { Type = "input", Kind = "key", Key = ch });
            }
            s.Clear();
        }

        public void BeforeTextChanged(Java.Lang.ICharSequence s, int start, int count, int after) { }
        public void OnTextChanged(Java.Lang.ICharSequence s, int start, int before, int count) { }
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
