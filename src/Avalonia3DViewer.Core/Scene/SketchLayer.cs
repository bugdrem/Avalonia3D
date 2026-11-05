using System.Numerics;
using Avalonia3DViewer.Core.Geometry;

namespace Avalonia3DViewer.Core.Scene;

/// <summary>2D 画布当前的绘图工具。</summary>
public enum SketchTool
{
    /// <summary>不绘图（拖拽为平移）。</summary>
    None,
    Rectangle,
    Circle,
}

/// <summary>草图图元类型。</summary>
public enum SketchShapeKind
{
    Rectangle,
    Circle,
    Polygon,
}

/// <summary>
/// 2D 画布图元。坐标定义在**地板平面**（世界 XZ），高度由图层统一给定。
/// <para>
/// 这是「2D 画布 ↔ 3D 地板」共用一份数据的关键：同一个矩形既是 2D 画布上的矩形，
/// 也是 3D 场景中平铺在地板上的矩形。2D 视图用正交投影俯视它，3D 视图用透视投影看它，
/// 切换时只需插值投影矩阵，几何数据完全不需要转换。
/// </para>
/// </summary>
public sealed class SketchShape
{
    public SketchShapeKind Kind { get; init; }

    /// <summary>中心点（地板平面 XZ 坐标）。</summary>
    public Vector2 Center { get; set; }

    /// <summary>矩形：半宽 / 半深；圆：X 分量为半径。</summary>
    public Vector2 Size { get; set; }

    /// <summary>多边形顶点（XZ，不含重复首点）。</summary>
    public Vector2[] Points { get; set; } = Array.Empty<Vector2>();

    public Vector3 FillColor { get; set; } = new(0.30f, 0.58f, 0.92f);

    public Vector3 StrokeColor { get; set; } = new(0.08f, 0.20f, 0.42f);

    public bool Filled { get; set; } = true;

    public float Radius => MathF.Max(Size.X, 0f);

    /// <summary>复制一份（拖动预览时避免改动已提交的图元）。</summary>
    public SketchShape Clone() => new()
    {
        Kind = Kind,
        Center = Center,
        Size = Size,
        Points = Points,
        FillColor = FillColor,
        StrokeColor = StrokeColor,
        Filled = Filled,
    };
}

/// <summary>草图三角化结果：填充三角形（pos3+color3）与轮廓线段（pos3+color3）。</summary>
public readonly record struct SketchGeometry(float[] Fill, float[] Outline, int ShapeCount)
{
    public bool IsEmpty => Fill.Length == 0 && Outline.Length == 0;
}

/// <summary>
/// 2D 草图图层：保存画布上绘制的矩形/圆/多边形，并可三角化为 GPU 顶点数据。
/// 纯 System.Numerics 实现，无反射，NativeAOT 安全。
/// </summary>
public sealed class SketchLayer
{
    private readonly List<SketchShape> _shapes = new();

    public IReadOnlyList<SketchShape> Shapes => _shapes;

    public int Count => _shapes.Count;

    /// <summary>变更计数：渲染端据此判断是否需要重建顶点缓冲。</summary>
    public int Version { get; private set; }

    public void Add(SketchShape shape)
    {
        _shapes.Add(shape);
        Version++;
    }

    public bool Remove(SketchShape shape)
    {
        if (!_shapes.Remove(shape)) return false;
        Version++;
        return true;
    }

    public void Clear()
    {
        if (_shapes.Count == 0) return;
        _shapes.Clear();
        Version++;
    }

    /// <summary>
    /// 三角化为填充三角形与轮廓线段。preview 为拖动中的临时图元（不提交，仅参与绘制）。
    /// </summary>
    /// <param name="floorY">地板高度（世界 Y）。</param>
    public SketchGeometry Build(float floorY, SketchShape? preview = null)
    {
        int count = _shapes.Count + (preview is null ? 0 : 1);
        if (count == 0) return new SketchGeometry(Array.Empty<float>(), Array.Empty<float>(), 0);

        var fill = new List<float>(count * 18);
        var outline = new List<float>(count * 24);

        foreach (var shape in _shapes)
            AppendShape(shape, floorY, fill, outline);

        if (preview is not null)
            AppendShape(preview, floorY, fill, outline);

        return new SketchGeometry(fill.ToArray(), outline.ToArray(), count);
    }

    /// <summary>图元在 XZ 平面的包围盒（Y 固定为地板高度）。空图层返回空盒。</summary>
    public BoundingBox ComputeBounds(float floorY)
    {
        var box = new BoundingBox();
        box.Reset();
        foreach (var shape in _shapes)
        {
            foreach (var p in GetCorners(shape))
                box.Include(new Vector3(p.X, floorY, p.Y));
        }
        return box;
    }

    /// <summary>
    /// 射线与水平地板平面求交（2D 画布上把鼠标位置换算为地板坐标）。
    /// 正交俯视与透视视图都适用。
    /// </summary>
    public static bool TryIntersectFloor(in Ray ray, float floorY, out Vector3 point)
    {
        point = default;
        float dy = ray.Direction.Y;
        if (MathF.Abs(dy) < 1e-7f) return false; // 射线与地板平行
        float t = (floorY - ray.Origin.Y) / dy;
        if (t <= 0f || t > 1e7f) return false;   // 交点在射线后方
        point = ray.PointAt(t);
        return true;
    }

    // ---------------------------------------------------------------- 三角化

    private static void AppendShape(SketchShape shape, float floorY, List<float> fill, List<float> outline)
    {
        var poly = GetCorners(shape);
        if (poly.Length < 3) return;

        // 填充：矩形直接 2 个三角形，其它用质心三角扇。
        // 顶点顺序保证法线朝 +Y（俯视逆时针），3D 贴地时从上方可见。
        if (shape.Filled)
        {
            if (shape.Kind == SketchShapeKind.Rectangle && poly.Length == 4)
            {
                AddTri(poly[0], poly[3], poly[2]);
                AddTri(poly[0], poly[2], poly[1]);
            }
            else
            {
                var center = Vector2.Zero;
                foreach (var p in poly) center += p;
                center /= poly.Length;

                for (int i = 0; i < poly.Length; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Length];
                    AddTri(center, b, a);
                }
            }
        }

        // 轮廓：闭合折线
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            AddVertex(outline, a, floorY, shape.StrokeColor);
            AddVertex(outline, b, floorY, shape.StrokeColor);
        }

        void AddTri(Vector2 a, Vector2 b, Vector2 c)
        {
            AddVertex(fill, a, floorY, shape.FillColor);
            AddVertex(fill, b, floorY, shape.FillColor);
            AddVertex(fill, c, floorY, shape.FillColor);
        }
    }

    private static void AddVertex(List<float> buffer, Vector2 p, float y, Vector3 color)
    {
        buffer.Add(p.X); buffer.Add(y); buffer.Add(p.Y);
        buffer.Add(color.X); buffer.Add(color.Y); buffer.Add(color.Z);
    }

    /// <summary>图元轮廓顶点（矩形 4 个角、圆 N 段、多边形原顶点）。</summary>
    public static Vector2[] GetCorners(SketchShape shape)
    {
        switch (shape.Kind)
        {
            case SketchShapeKind.Rectangle:
            {
                float hw = MathF.Max(shape.Size.X, 0f);
                float hd = MathF.Max(shape.Size.Y, 0f);
                var c = shape.Center;
                return
                [
                    new Vector2(c.X - hw, c.Y - hd),
                    new Vector2(c.X + hw, c.Y - hd),
                    new Vector2(c.X + hw, c.Y + hd),
                    new Vector2(c.X - hw, c.Y + hd),
                ];
            }

            case SketchShapeKind.Circle:
            {
                int segments = CircleSegments;
                float r = MathF.Max(shape.Radius, 0f);
                var c = shape.Center;
                var pts = new Vector2[segments];
                for (int i = 0; i < segments; i++)
                {
                    float angle = MathF.Tau * i / segments;
                    pts[i] = new Vector2(c.X + r * MathF.Cos(angle), c.Y + r * MathF.Sin(angle));
                }
                return pts;
            }

            default:
                return shape.Points.Length >= 3 ? shape.Points : Array.Empty<Vector2>();
        }
    }

    /// <summary>圆的细分段数：兼顾平滑与顶点开销（性能优先）。</summary>
    public const int CircleSegments = 32;
}
