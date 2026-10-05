using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace Watching.Mobile;

/// <summary>
/// 画面显示控件：等比缩放、双指捏合缩放、单指拖动、双击回调。
/// </summary>
public sealed class ScreenImageView : ImageView
{
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly GestureDetector _gestureDetector;

    private readonly Matrix _matrix = new();
    private float _scale = 1f;
    private float _baseScale = 1f;
    private float _translateX, _translateY;
    private float _lastTouchX, _lastTouchY;
    private int _activePointers;
    private bool _fitToScreen = true;

    public event Action DoubleTapped;

    private int _sourceWidth, _sourceHeight;

    public ScreenImageView(Context context) : base(context)
    {
        SetScaleType(ScaleType.Matrix);
        SetBackgroundColor(Color.Black);
        Focusable = true;

        _scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this));
        _gestureDetector = new GestureDetector(context, new GestureListener(this));
    }

    public bool FitToScreen
    {
        get => _fitToScreen;
        set
        {
            _fitToScreen = value;
            if (value) ResetView();
        }
    }

    public void ResetView()
    {
        _fitToScreen = true;
        _scale = 1f;
        _translateX = 0f;
        _translateY = 0f;
        UpdateMatrix();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        UpdateMatrix();
    }

    public void SetSourceSize(int width, int height)
    {
        if (width == _sourceWidth && height == _sourceHeight) return;
        _sourceWidth = width;
        _sourceHeight = height;
        UpdateMatrix();
    }

    private void UpdateMatrix()
    {
        if (_sourceWidth <= 0 || _sourceHeight <= 0 || Width <= 0 || Height <= 0) return;

        float fit = Math.Min((float)Width / _sourceWidth, (float)Height / _sourceHeight);
        _baseScale = fit;

        float total = fit * _scale;

        _matrix.Reset();
        _matrix.PostScale(total, total);
        _matrix.PostTranslate(
            (Width - _sourceWidth * total) / 2f + _translateX,
            (Height - _sourceHeight * total) / 2f + _translateY);

        ImageMatrix = _matrix;
    }

    /// <summary>
    /// 把控件内的坐标（像素）换算成画面内的归一化坐标（0~1），用于远程控制。
    /// 自动考虑当前缩放与平移；落在画面外时返回 false。
    /// </summary>
    public bool TryMapToImage(float viewX, float viewY, out float nx, out float ny)
    {
        nx = ny = 0f;
        if (_sourceWidth <= 0 || _sourceHeight <= 0) return false;

        using var inverse = new Matrix();
        if (!_matrix.Invert(inverse)) return false;

        var pts = new float[] { viewX, viewY };
        inverse.MapPoints(pts);

        float x = pts[0], y = pts[1];
        if (x < -2 || x > _sourceWidth + 2 || y < -2 || y > _sourceHeight + 2) return false;

        nx = Math.Clamp(x / _sourceWidth, 0f, 1f);
        ny = Math.Clamp(y / _sourceHeight, 0f, 1f);
        return true;
    }

    /// <summary>
    /// 同上，但落在画面外时**夹到边缘**而不是失败。
    /// 手指从画面里拖到黑边（竖屏看 16:9 桌面时上下黑边很宽）时，
    /// 之前的实现会直接返回 false，导致「松手了却没发 up」——
    /// 对方电脑的鼠标左键就一直按着不放，长按计时器也会在手指抬起后误触发右键。
    /// </summary>
    public bool TryMapToImageClamped(float viewX, float viewY, out float nx, out float ny)
    {
        nx = ny = 0.5f;
        if (_sourceWidth <= 0 || _sourceHeight <= 0) return false;

        using var inverse = new Matrix();
        if (!_matrix.Invert(inverse)) return false;

        var pts = new float[] { viewX, viewY };
        inverse.MapPoints(pts);

        nx = Math.Clamp(pts[0] / _sourceWidth, 0f, 1f);
        ny = Math.Clamp(pts[1] / _sourceHeight, 0f, 1f);
        return true;
    }

    /// <summary>控制模式：单指触摸当作鼠标，交给 MainActivity 转发；返回 true 表示已消费。</summary>
    public Func<MotionEvent, bool> RemoteTouchHandler { get; set; }

    /// <summary>控制模式：双指触摸。返回 true = 已当作滚动消费；返回 false = 判定为缩放，交回本地处理。</summary>
    public Func<MotionEvent, bool> RemoteMultiTouchHandler { get; set; }

    /// <summary>第二根手指落下时调用：释放可能还按着的左键、取消长按计时。</summary>
    public Action RemoteGestureCancel { get; set; }

    private bool _remoteSingleActive;

    public override bool OnTouchEvent(MotionEvent e)
    {
        _activePointers = e.PointerCount;

        if (RemoteTouchHandler != null)
        {
            if (e.PointerCount >= 2)
            {
                // 第二根手指落下 → 结束单指会话，避免残留的左键按下
                if (e.ActionMasked == MotionEventActions.PointerDown && _remoteSingleActive)
                {
                    _remoteSingleActive = false;
                    RemoteGestureCancel?.Invoke();
                }

                if (RemoteMultiTouchHandler != null && RemoteMultiTouchHandler(e)) return true;
                // 未消费（判定为缩放）→ 落到下面交给缩放检测器
            }
            else if (_remoteSingleActive)
            {
                bool consumed = RemoteTouchHandler(e);
                if (e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.Cancel)
                    _remoteSingleActive = false;
                if (consumed) return true;
            }
            else if (e.ActionMasked == MotionEventActions.Down)
            {
                // 只有按在画面内才开始远程会话
                _remoteSingleActive = RemoteTouchHandler(e);
                if (_remoteSingleActive) return true;
            }
        }
        else
        {
            _remoteSingleActive = false;
        }

        _gestureDetector.OnTouchEvent(e);
        _scaleDetector.OnTouchEvent(e);

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastTouchX = e.GetX();
                _lastTouchY = e.GetY();
                break;

            case MotionEventActions.Move:
                if (_activePointers == 1 && !_scaleDetector.IsInProgress && _scale > 1.02f)
                {
                    float dx = e.GetX() - _lastTouchX;
                    float dy = e.GetY() - _lastTouchY;
                    _translateX += dx;
                    _translateY += dy;
                    UpdateMatrix();
                }
                _lastTouchX = e.GetX();
                _lastTouchY = e.GetY();
                break;
        }

        return true;
    }

    private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly ScreenImageView _view;
        public ScaleListener(ScreenImageView view) => _view = view;

        public override bool OnScale(ScaleGestureDetector detector)
        {
            _view._fitToScreen = false;
            _view._scale = Math.Clamp(_view._scale * detector.ScaleFactor, 0.5f, 8f);
            _view.UpdateMatrix();
            return true;
        }

        public override bool OnScaleBegin(ScaleGestureDetector detector) => true;
    }

    private sealed class GestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly ScreenImageView _view;
        public GestureListener(ScreenImageView view) => _view = view;

        public override bool OnDoubleTap(MotionEvent e)
        {
            _view.DoubleTapped?.Invoke();
            return true;
        }

        public override bool OnDown(MotionEvent e) => true;
    }
}
