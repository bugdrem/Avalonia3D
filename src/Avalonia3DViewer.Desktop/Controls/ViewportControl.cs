using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia3DViewer.Desktop.Rendering;
using Avalonia3DViewer.Core.Camera;
using static Avalonia.OpenGL.GlConsts;

namespace Avalonia3DViewer.Desktop.Controls;

/// <summary>拖拽按键（自维护，避免依赖可能调整的输入 API）。</summary>
internal enum DragButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// 3D 视口控件：基于 Avalonia 内置 <see cref="OpenGlControlBase"/> 的 OpenGL 嵌入。
/// 交互：左键拖拽=轨道旋转，右键/中键拖拽=平移视图，滚轮=缩放，双击=聚焦，标注模式下单击=放置标注。
/// 移动模式（MoveMode）下左键拖拽=拖动模型改变其世界坐标（与轨道旋转明确区分）。
/// </summary>
/// <remarks>
/// 实现 <see cref="ICustomHitTest"/> 是必需的：OpenGlControlBase 通过 Composition SurfaceVisual
/// 渲染，不出现在场景图命中测试数据中，默认收不到任何指针事件（Avalonia 已知问题，
/// 见 AvaloniaUI/Avalonia Discussion #10879）。此处声明自身矩形区域可被命中。
/// </remarks>
public sealed class ViewportControl : OpenGlControlBase, ICustomHitTest
{
    private readonly GlRenderer _renderer = new();
    private ViewerSession _session = null!;
    private bool _initFailed;

    /// <summary>GL 初始化是否已完成（OnOpenGlInit 已触发）。</summary>
    private bool _glReady;

    // 初始化看门狗：GL 回调始终未触发时给出可操作的错误提示（而非空白视口）
    private DispatcherTimer? _watchdog;
    private int _watchdogTicks;
    private const int WatchdogTimeoutTicks = 3; // 3 秒

    // 交互状态（仅 UI 线程访问）
    private bool _captured;
    private DragButton _dragButton;
    private Vector2 _lastPosition;
    private long _pressTimestampMs;
    private bool _moved;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // 移动模型拖拽状态（仅 UI 线程访问）
    private bool _movingModel;
    private Vector3 _moveStartOffset;
    private Vector2 _moveStartScreen;   // 按下时的屏幕位置（位移基准）
    private Vector3 _grabPlanePoint;    // 按下时与相机平行的抓取平面（过抓取点）
    private Vector3 _grabPlaneNormal;   // 平面法线 = 相机视线
    private int _axisLock;              // 0=自由, 1=屏幕水平, 2=屏幕垂直（Shift 锁定）

    /// <summary>XAML 实例化使用；随后必须调用 <see cref="Attach"/>。</summary>
    public ViewportControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>绑定查看器会话（由主窗口在 InitializeComponent 之后调用）。</summary>
    public void Attach(ViewerSession session)
    {
        _session = session;

        // 看门狗：若 3 秒内 OnOpenGlInit 始终未触发（Linux/虚拟机常见：无 GL 互操作），
        // 在视口浮层显示原因，避免用户只看到一片空白
        _watchdogTicks = 0;
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += OnWatchdogTick;
        _watchdog.Start();
    }

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (_glReady)
        {
            _watchdog?.Stop();
            _session.ViewportError = string.Empty;
            return;
        }

        if (++_watchdogTicks < WatchdogTimeoutTicks) return;

        _watchdog?.Stop();
        _session.ViewportError =
            "OpenGL 初始化超时（3 秒无响应）。常见原因：\n" +
            "• 虚拟机未开启 3D 加速（VirtualBox：设置 → 显示 → 屏幕 → 启用 3D 加速）\n" +
            "• 缺少 Mesa 软件渲染：sudo apt install libgl1-mesa-dri libegl-mesa0\n" +
            "• 远程/无显示器会话：可用 xvfb-run 启动\n" +
            "• 在终端运行 ./Avalonia3DViewer 可查看详细日志";
        _session.GlInfo = "OpenGL 未初始化";
    }

    /// <summary>
    /// 命中测试：point 为视觉根（窗口）坐标，转换到控件本地坐标后按自身矩形判定。
    /// </summary>
    bool ICustomHitTest.HitTest(Point point)
    {
        var local = VisualRoot?.TranslatePoint(point, this);
        return local.HasValue && new Rect(Bounds.Size).Contains(local.Value);
    }

    public ViewerSession Session => _session;

    /// <summary>视口尺寸（逻辑像素）。</summary>
    private Vector2 ViewportSize => new((float)Bounds.Width, (float)Bounds.Height);

    // ---------------------------------------------------------------- OpenGL 生命周期

    protected override void OnOpenGlInit(Avalonia.OpenGL.GlInterface gl)
    {
        // 先置位：PushSceneToRenderer 内的帧请求依赖此标志（见 RequestFrame 注释）
        _glReady = true;
        _watchdog?.Stop();
        _session.ViewportError = string.Empty;

        try
        {
            _renderer.Init(gl, GlVersion);
            PushSceneToRenderer();
            _session.GlInfo = _renderer.GlVersionInfo;
            _initFailed = false;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _session.GlInfo = "OpenGL 初始化失败";
            _session.StatusText = $"OpenGL 初始化失败: {ex.Message}";
            _session.ViewportError =
                $"OpenGL 初始化失败: {ex.Message}\n\n" +
                "如与 GL 版本/驱动相关，可尝试：更新显卡驱动，或在虚拟机中启用 3D 加速。";
        }
    }

    protected override void OnOpenGlDeinit(Avalonia.OpenGL.GlInterface gl)
    {
        _glReady = false;
        _renderer.Dispose();
    }

    protected override void OnOpenGlLost()
    {
        // 上下文丢失：资源随 Deinit 释放，重新初始化时自动重建
        _glReady = false;
        _watchdogTicks = 0;
        _watchdog?.Start();
        _session.ViewportError = "OpenGL 上下文丢失，正在尝试恢复…";
        _renderer.Dispose();
    }

    protected override void OnOpenGlRender(Avalonia.OpenGL.GlInterface gl, int fb)
    {
        if (_initFailed)
        {
            // 初始化失败时清屏为浅灰，避免花屏
            gl.ClearColor(0.85f, 0.87f, 0.90f, 1f);
            gl.Clear(GL_COLOR_BUFFER_BIT);
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        double scale = topLevel?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scale));

        CameraSnapshot snap = default;
        try
        {
            snap = _session.Snapshot;
            _renderer.RenderFrame(new FrameState(
                snap.View, snap.Proj, snap.Eye, snap.Model,
                width, height, fb,
                snap.Mode, snap.SsaaEnabled,
                snap.BackgroundTop, snap.BackgroundBottom));
        }
        catch (Exception ex)
        {
            // 渲染异常不应冒泡到 Avalonia 渲染循环导致进程崩溃：降级为关闭 SSAA 并报告
            if (_session.SsaaEnabled)
            {
                _session.SsaaEnabled = false;
                _session.RebuildSnapshot();
            }
            _session.StatusText = $"渲染错误（已禁用抗锯齿）: {ex.Message}";
            return;
        }

        _session.OnFrameRendered();

        if (_session.Camera.IsSettling)
            RequestFrame();
    }

    /// <summary>把当前场景数据推送到渲染器（GL 上传发生在下一帧）。</summary>
    public void PushSceneToRenderer()
    {
        var mesh = _session.Model?.Mesh;
        if (mesh != null)
            _renderer.SetMesh(mesh);
        else
            _renderer.SetMesh(Core.Geometry.MeshData.Empty());

        if (mesh != null)
        {
            var bounds = mesh.Bounds;
            float extent = MathF.Max(bounds.Size.X, bounds.Size.Z) * 3f;
            _renderer.SetGrid(bounds.Center, extent, bounds.Min.Y);
        }

        _renderer.SetMarkers(_session.Markers);
        _renderer.SetMeasureLines(_session.MeasureLines);

        // 场景数据已变更：请求一帧以完成上传与绘制（相机静止时不会自动请求）
        RequestFrame();
    }

    /// <summary>
    /// 请求下一帧渲染——仅在 GL 初始化完成后发出。
    /// ⚠️ 不要在初始化前调用 <see cref="OpenGlControlBase.RequestNextFrameRendering"/>：
    /// Avalonia 已知缺陷（#13879）——过早调用会把内部 _updateQueued 置位但合成器尚未就绪，
    /// 导致首帧请求永久丢失、OnOpenGlInit/OnOpenGlRender 不再触发（症状：视口空白、FPS 为 0）。
    /// 控件自身在附着与初始化完成后会自动请求首帧，此处只需处理"初始化后"的增量请求。
    /// </summary>
    public void RequestFrame()
    {
        if (_glReady)
            RequestNextFrameRendering();
    }

    /// <summary>按当前交互模式刷新光标（移动模式=四向移动，标注模式=十字，默认=箭头）。</summary>
    public void UpdateInteractionCursor()
    {
        Cursor = _session.MoveMode
            ? new Cursor(StandardCursorType.SizeAll)
            : _session.AnnotationMode
                ? new Cursor(StandardCursorType.Cross)
                : Cursor.Default;
    }

    // ---------------------------------------------------------------- 指针交互

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        _captured = true;
        _moved = false;
        _pressTimestampMs = _clock.ElapsedMilliseconds;
        _lastPosition = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _dragButton = point.Properties.IsRightButtonPressed ? DragButton.Right
            : point.Properties.IsMiddleButtonPressed ? DragButton.Middle
            : DragButton.Left;

        // 移动模式 + 左键：开始拖动模型（改变世界坐标），而非旋转相机
        _movingModel = false;
        if (_session.MoveMode && _dragButton == DragButton.Left && _session.Model != null)
        {
            _movingModel = true;
            _moveStartOffset = _session.Transform.Offset;
            _moveStartScreen = _lastPosition;
            _axisLock = 0;

            var cam = _session.Camera;
            _grabPlaneNormal = cam.ForwardDirection;
            var ray = cam.ScreenPointToRay(_lastPosition, ViewportSize);
            var meshCenter = _session.Model.Mesh.Bounds.Center + _moveStartOffset;
            // 抓取平面：过模型中心、面向相机；抓取点取射线与该平面的交点（取不到则用模型中心）
            _grabPlanePoint = RayPlaneIntersect(ray, meshCenter, _grabPlaneNormal, out var grab)
                ? grab
                : meshCenter;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_captured) return;

        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        var current = new Vector2((float)pos.X, (float)pos.Y);
        float dx = current.X - _lastPosition.X;
        float dy = current.Y - _lastPosition.Y;
        if (MathF.Abs(dx) < 0.01f && MathF.Abs(dy) < 0.01f) return;

        if (MathF.Abs(dx) + MathF.Abs(dy) > 3f) _moved = true;

        var viewport = ViewportSize;

        if (_movingModel)
        {
            DragModel(dx, dy, viewport, e.KeyModifiers);
        }
        else if (_dragButton == DragButton.Left)
        {
            _session.Camera.Rotate(dx, dy, viewport.Y);
        }
        else
        {
            _session.Camera.Pan(dx, dy, viewport.Y);
        }

        _lastPosition = current;
        _session.RebuildSnapshot();
        RequestFrame();
        e.Handled = true;
    }

    /// <summary>
    /// 拖动模型：屏幕位移经「面向相机的抓取平面」转换为世界位移。
    /// Shift 按下时锁定为屏幕水平/垂直单向拖动（按首次移动的主方向）。
    /// </summary>
    private void DragModel(float dx, float dy, Vector2 viewport, KeyModifiers modifiers)
    {
        var cam = _session.Camera;

        // 当前帧与按下基准位置都投影到同一抓取平面，差值即本次拖拽的总位移
        var ray = cam.ScreenPointToRay(new Vector2(_lastPosition.X + dx, _lastPosition.Y + dy), viewport);
        var startRay = cam.ScreenPointToRay(_moveStartScreen, viewport);
        if (!RayPlaneIntersect(ray, _grabPlanePoint, _grabPlaneNormal, out var now) ||
            !RayPlaneIntersect(startRay, _grabPlanePoint, _grabPlaneNormal, out var start))
            return;

        var delta = now - start;

        // Shift 轴锁定：屏幕水平（沿相机右向）或屏幕垂直（沿相机上向）
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        if (shift)
        {
            if (_axisLock == 0)
                _axisLock = MathF.Abs(dx) >= MathF.Abs(dy) ? 1 : 2;
            var axis = _axisLock == 1 ? cam.RightDirection : cam.UpDirection;
            delta = axis * Vector3.Dot(delta, axis);
        }

        _session.SetModelOffset(_moveStartOffset + delta);
        _session.StatusText =
            $"移动模型 {_session.Transform.ToText()}（Shift 锁定轴向，Esc/G 退出移动模式）";
    }

    /// <summary>射线与平面求交（平面用点 + 法线表示）。</summary>
    private static bool RayPlaneIntersect(in Core.Geometry.Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        float denom = Vector3.Dot(ray.Direction, planeNormal);
        if (MathF.Abs(denom) < 1e-6f)
        {
            hit = default;
            return false;
        }
        float t = Vector3.Dot(planePoint - ray.Origin, planeNormal) / denom;
        if (t < 0f)
        {
            hit = default;
            return false;
        }
        hit = ray.PointAt(t);
        return true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_captured) return;
        _captured = false;
        _movingModel = false;
        e.Pointer.Capture(null);

        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        bool quickTap = !_moved && _clock.ElapsedMilliseconds - _pressTimestampMs < 600;

        if (quickTap && _dragButton == DragButton.Left)
        {
            HandleTap(new Vector2((float)pos.X, (float)pos.Y));
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (MathF.Abs((float)e.Delta.Y) < 0.001f) return;
        _session.Camera.Zoom((float)e.Delta.Y);
        _session.RebuildSnapshot();
        RequestFrame();
        e.Handled = true;
    }


    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        var pos = e.GetPosition(this);
        var ray = _session.Camera.ScreenPointToRay(
            new Vector2((float)pos.X, (float)pos.Y), ViewportSize);
        var hit = _session.PickWorld(ray);
        if (hit.HasValue)
        {
            _session.Camera.Target = hit.Value.Point;
            _session.RebuildSnapshot();
            RequestFrame();
        }
    }

    /// <summary>单击：标注模式下放置/选中标注，否则提示。</summary>
    private void HandleTap(Vector2 screenPoint)
    {
        if (_session.Model is null) return;
        var viewport = ViewportSize;

        if (_session.AnnotationMode)
        {
            // 优先选中已有标注
            var existing = _session.FindAnnotationNear(screenPoint, viewport, 22f);
            if (existing != null)
            {
                _session.SelectedAnnotation = existing;
                _session.RefreshAnnotations();
                _session.StatusText = $"已选中标注 {existing.Label}";
                PushSceneToRenderer();
                return;
            }

            var ray = _session.Camera.ScreenPointToRay(screenPoint, viewport);
            var hit = _session.PickWorld(ray);
            if (hit.HasValue)
            {
                var ann = _session.AddAnnotation(hit.Value.Point, hit.Value.Normal);
                if (ann != null)
                {
                    _session.StatusText = $"已添加标注 {ann.Label} @ {ann.PositionText}";
                }
                PushSceneToRenderer();
            }
            else
            {
                _session.StatusText = "未命中模型表面，请点击模型上的位置";
            }
        }
        else if (_session.MoveMode)
        {
            // 移动模式下的单击不执行任何操作（拖拽才移动模型），仅提示当前坐标
            _session.StatusText = $"移动模式：按住左键拖动模型改变坐标，当前 {_session.Transform.ToText()}";
        }
        else
        {
            // 非标注模式：单击显示命中点坐标（便于调试/演示）
            var ray = _session.Camera.ScreenPointToRay(screenPoint, viewport);
            var hit = _session.PickWorld(ray);
            _session.StatusText = hit.HasValue
                ? $"命中点 X {hit.Value.Point.X:F2} Y {hit.Value.Point.Y:F2} Z {hit.Value.Point.Z:F2} mm"
                : "未命中模型";
        }
    }
}
