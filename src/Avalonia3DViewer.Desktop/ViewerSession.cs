using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia3DViewer.Desktop.Rendering;
using Avalonia3DViewer.Core;
using Avalonia3DViewer.Core.Annotations;
using Avalonia3DViewer.Core.Camera;
using Avalonia3DViewer.Core.Geometry;
using Avalonia3DViewer.Core.Loading;
using Avalonia3DViewer.Core.Picking;
using Avalonia3DViewer.Core.Scene;

namespace Avalonia3DViewer.Desktop;

/// <summary>相机与渲染状态的不可变快照（UI 线程构建，渲染线程读取）。</summary>
public readonly record struct CameraSnapshot(
    Matrix4x4 View,
    Matrix4x4 Proj,
    Vector3 Eye,
    Matrix4x4 Model,
    ViewMode Mode,
    bool SsaaEnabled,
    Vector3 BackgroundTop,
    Vector3 BackgroundBottom);

/// <summary>
/// 查看器会话：持有模型、相机、标注、拾取器等状态。
/// UI 线程负责全部可变状态；渲染线程只读取不可变快照与不可变数组。
/// </summary>
public sealed class ViewerSession : INotifyPropertyChanged
{
    // ---- 主题背景（浅色） ----
    public static readonly Vector3 BgTop = new(0.945f, 0.957f, 0.973f);
    public static readonly Vector3 BgBottom = new(0.863f, 0.885f, 0.918f);

    private static readonly Vector3 MarkerColor = new(0.95f, 0.45f, 0.12f);
    private static readonly Vector3 MarkerSelectedColor = new(0.20f, 0.55f, 0.95f);
    private static readonly Vector3 MeasureColor = new(0.13f, 0.62f, 0.28f);

    public OrbitCamera Camera { get; } = new();

    public AnnotationCollection Annotations { get; } = new();

    public ModelLoaderRegistry Loaders { get; } = new();

    public ModelDocument? Model { get; private set; }

    public MeshRaycaster? Raycaster { get; private set; }

    public ViewMode Mode { get; set; } = ViewMode.Shaded;

    public bool SsaaEnabled { get; set; } = true;

    public bool AnnotationMode { get; set; }

    /// <summary>移动模式：开启后左键拖拽拖动模型改变其世界坐标（与轨道旋转区分）。</summary>
    public bool MoveMode { get; set; }

    /// <summary>模型世界坐标变换（拖动改坐标的核心状态）。</summary>
    public ModelTransform Transform { get; } = new();

    public Annotation? SelectedAnnotation { get; set; }

    // ---- 渲染线程安全数据（不可变快照经引用交换发布） ----
    private sealed class SnapshotBox
    {
        public readonly CameraSnapshot Value;
        public SnapshotBox(CameraSnapshot value) => Value = value;
    }

    private volatile SnapshotBox _snapshotBox = new(new(
        Matrix4x4.Identity, Matrix4x4.Identity, new Vector3(0, 0, 200), Matrix4x4.Identity,
        ViewMode.Shaded, true, BgTop, BgBottom));

    private volatile MarkerVertex[] _markers = Array.Empty<MarkerVertex>();
    private volatile float[] _measureLines = Array.Empty<float>();
    private int _frameCounter;
    private bool _anyFrameRendered;

    public CameraSnapshot Snapshot => _snapshotBox.Value;

    /// <summary>是否已渲染过至少一帧（用于 UI 线程兜底请求首帧）。</summary>
    public bool HasRenderedFrame => _anyFrameRendered;

    public MarkerVertex[] Markers => _markers;

    public float[] MeasureLines => _measureLines;

    // ---- UI 绑定属性 ----

    private string _modelInfo = "尚未加载模型";
    public string ModelInfo
    {
        get => _modelInfo;
        private set => SetField(ref _modelInfo, value);
    }

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private string _fpsText = "-- FPS";
    public string FpsText
    {
        get => _fpsText;
        internal set => SetField(ref _fpsText, value);
    }

    private string _measurementText = "添加至少两个标注点后显示测距";
    public string MeasurementText
    {
        get => _measurementText;
        private set => SetField(ref _measurementText, value);
    }

    private string _glInfo = "";
    public string GlInfo
    {
        get => _glInfo;
        set => SetField(ref _glInfo, value);
    }

    private string _viewportError = string.Empty;

    /// <summary>视口错误信息（空 = 正常）。初始化失败/超时/上下文丢失时显示在视口浮层。</summary>
    public string ViewportError
    {
        get => _viewportError;
        set
        {
            if (_viewportError == value) return;
            _viewportError = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewportError)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasViewportError)));
        }
    }

    /// <summary>是否存在视口错误（供浮层显隐绑定）。</summary>
    public bool HasViewportError => !string.IsNullOrEmpty(_viewportError);

    private string _modelPositionText = "X 0.00   Y 0.00   Z 0.00 mm";
    /// <summary>模型当前世界坐标（拖动移动时实时更新）。</summary>
    public string ModelPositionText
    {
        get => _modelPositionText;
        private set => SetField(ref _modelPositionText, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        internal set => SetField(ref _isLoading, value);
    }

    // ---------------------------------------------------------------- 模型

    /// <summary>设置模型（UI 线程调用）。</summary>
    public void SetModel(ModelDocument document, bool fitView = true)
    {
        Model = document;
        Raycaster = new MeshRaycaster(document.Mesh);

        // 新模型回到原始坐标（不清空会继承上一个模型的位置，造成困惑）
        Transform.Reset();
        ModelPositionText = Transform.ToText();

        if (fitView)
        {
            Camera.FitToBounds(document.Mesh.Bounds);
            Camera.SnapToTarget();
        }

        Annotations.Clear();
        SelectedAnnotation = null;
        RefreshAnnotations();
        RebuildSnapshot();

        var size = document.Mesh.Bounds.Size;
        ModelInfo =
            $"文件: {document.FileName}\n" +
            $"格式: {document.Format}\n" +
            $"单位: {document.Unit}\n" +
            $"三角面: {document.Mesh.TriangleCount:N0}\n" +
            $"顶点: {document.Mesh.VertexCount:N0}\n" +
            $"尺寸: {size.X:F1} × {size.Y:F1} × {size.Z:F1} mm\n" +
            $"加载耗时: {document.LoadMilliseconds:F0} ms";
    }

    public void ClearModel()
    {
        Model = null;
        Raycaster = null;
        Annotations.Clear();
        SelectedAnnotation = null;
        RefreshAnnotations();
        Transform.Reset();
        ModelPositionText = Transform.ToText();
        ModelInfo = "尚未加载模型";
    }

    // ---------------------------------------------------------------- 模型坐标（拖动移动）

    /// <summary>
    /// 设置模型世界坐标（拖动结束或程序化设置）。标注随模型表面一起移动。
    /// </summary>
    public void SetModelOffset(Vector3 offset)
    {
        var delta = Transform.Set(offset);
        if (delta != Vector3.Zero)
        {
            Annotations.TranslateAll(delta);
            RefreshAnnotations();
        }
        ModelPositionText = Transform.ToText();
        RebuildSnapshot();
    }

    /// <summary>按增量移动模型（拖动过程中逐帧调用）。</summary>
    public void MoveModelBy(Vector3 delta)
    {
        if (delta == Vector3.Zero) return;
        Transform.Translate(delta);
        Annotations.TranslateAll(delta);
        RefreshAnnotations();
        ModelPositionText = Transform.ToText();
        RebuildSnapshot();
    }

    /// <summary>复位模型到原始坐标。</summary>
    public void ResetModelOffset()
    {
        var delta = Transform.Reset();
        if (delta != Vector3.Zero)
        {
            Annotations.TranslateAll(delta);
            RefreshAnnotations();
        }
        ModelPositionText = Transform.ToText();
        RebuildSnapshot();
    }

    /// <summary>
    /// 世界坐标拾取：内部把射线转换到模型局部空间，命中点转回世界坐标返回。
    /// 模型被移动后，点击/标注/聚焦全部基于移动后的实际位置。
    /// </summary>
    public PickResult? PickWorld(in Core.Geometry.Ray worldRay)
    {
        if (Raycaster is null) return null;
        var localRay = Transform.ToLocalRay(worldRay);
        var hit = Raycaster.Pick(localRay);
        if (hit is null) return null;
        var h = hit.Value;
        return new PickResult(Transform.ToWorld(h.Point), h.Normal, h.Distance, h.TriangleIndex);
    }

    // ---------------------------------------------------------------- 标注

    public Annotation? AddAnnotation(Vector3 position, Vector3 normal)
    {
        if (Model is null) return null;
        var ann = Annotations.Add(position, normal);
        SelectedAnnotation = ann;
        RefreshAnnotations();
        return ann;
    }

    public void DeleteAnnotation(Annotation ann)
    {
        if (SelectedAnnotation == ann) SelectedAnnotation = null;
        Annotations.Remove(ann);
        RefreshAnnotations();
    }

    public void ClearAnnotations()
    {
        Annotations.Clear();
        SelectedAnnotation = null;
        RefreshAnnotations();
    }

    /// <summary>重建标注标记与测距线的不可变数组（UI 线程调用）。</summary>
    public void RefreshAnnotations()
    {
        var items = Annotations.Items;
        var markers = new MarkerVertex[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var color = items[i] == SelectedAnnotation ? MarkerSelectedColor : MarkerColor;
            markers[i] = new MarkerVertex(items[i].Position, color);
        }
        _markers = markers;

        var measurements = Annotations.Measurements;
        var lines = new float[measurements.Count * 6 * 2];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < measurements.Count; i++)
        {
            var (from, to, dist) = measurements[i];
            int o = i * 12;
            lines[o + 0] = from.Position.X; lines[o + 1] = from.Position.Y; lines[o + 2] = from.Position.Z;
            lines[o + 3] = MeasureColor.X; lines[o + 4] = MeasureColor.Y; lines[o + 5] = MeasureColor.Z;
            lines[o + 6] = to.Position.X; lines[o + 7] = to.Position.Y; lines[o + 8] = to.Position.Z;
            lines[o + 9] = MeasureColor.X; lines[o + 10] = MeasureColor.Y; lines[o + 11] = MeasureColor.Z;
            if (i > 0) sb.Append('\n');
            sb.Append($"{from.Label} → {to.Label}: {dist:F2} mm");
        }

        _measureLines = lines;
        MeasurementText = measurements.Count == 0
            ? "添加至少两个标注点后显示测距"
            : sb.ToString();
    }

    /// <summary>在屏幕空间中查找距指定像素最近的标注（用于点选已有标注）。</summary>
    public Annotation? FindAnnotationNear(Vector2 screenPoint, Vector2 viewportSize, float radiusPixels)
    {
        Annotation? best = null;
        float bestDist = radiusPixels;
        foreach (var ann in Annotations.Items)
        {
            var p = Camera.ProjectToScreen(ann.Position, viewportSize);
            if (p is null) continue;
            float d = Vector2.Distance(p.Value, screenPoint);
            if (d < bestDist)
            {
                bestDist = d;
                best = ann;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- 帧循环

    /// <summary>相机或渲染状态变化后调用（UI 线程）。</summary>
    public void RebuildSnapshot()
    {
        _snapshotBox = new SnapshotBox(new CameraSnapshot(
            Camera.ViewMatrix,
            Camera.ProjectionMatrix,
            Camera.EyePosition,
            Transform.Matrix,
            Mode,
            SsaaEnabled,
            BgTop,
            BgBottom));
    }

    /// <summary>推进相机阻尼；返回是否仍在运动中。</summary>
    public bool Update(float deltaSeconds)
    {
        Camera.Update(deltaSeconds);
        RebuildSnapshot();
        return Camera.IsSettling;
    }

    public void OnFrameRendered()
    {
        Interlocked.Increment(ref _frameCounter);
        _anyFrameRendered = true;
    }

    /// <summary>读取并重置帧计数，返回该周期内的 FPS。</summary>
    public double ConsumeFps(double intervalSeconds)
    {
        int frames = Interlocked.Exchange(ref _frameCounter, 0);
        return intervalSeconds > 0 ? frames / intervalSeconds : 0;
    }

    // ---------------------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
