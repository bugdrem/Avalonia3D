using System.Numerics;
using Avalonia3DViewer.Core.Geometry;
using Avalonia3DViewer.Core.Scene;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

public class SketchLayerTests
{
    private static SketchShape Rect(float cx, float cz, float hw, float hd) => new()
    {
        Kind = SketchShapeKind.Rectangle,
        Center = new Vector2(cx, cz),
        Size = new Vector2(hw, hd),
    };

    [Fact]
    public void Rectangle_ProducesTwoTrianglesAndFourEdges()
    {
        var layer = new SketchLayer();
        layer.Add(Rect(10, -5, 4, 3));

        var geo = layer.Build(0f);

        // 2 三角形 × 3 顶点 × 6 float；轮廓 4 段 × 2 顶点 × 6 float
        Assert.Equal(2 * 3 * 6, geo.Fill.Length);
        Assert.Equal(4 * 2 * 6, geo.Outline.Length);
        Assert.Equal(1, geo.ShapeCount);
    }

    [Fact]
    public void Rectangle_VerticesLieOnFloorPlane()
    {
        var layer = new SketchLayer();
        layer.Add(Rect(0, 0, 2, 2));

        var geo = layer.Build(floorY: 7.5f);

        for (int i = 0; i < geo.Fill.Length; i += 6)
            Assert.Equal(7.5f, geo.Fill[i + 1], 4);
        for (int i = 0; i < geo.Outline.Length; i += 6)
            Assert.Equal(7.5f, geo.Outline[i + 1], 4);
    }

    [Fact]
    public void FilledTriangles_FaceUpward()
    {
        var layer = new SketchLayer();
        layer.Add(Rect(3, 4, 5, 2));

        var geo = layer.Build(0f);

        // 每个三角形的叉积 Y 分量应为正（法线朝 +Y，从上方可见）
        for (int i = 0; i < geo.Fill.Length; i += 18)
        {
            var a = new Vector3(geo.Fill[i], geo.Fill[i + 1], geo.Fill[i + 2]);
            var b = new Vector3(geo.Fill[i + 6], geo.Fill[i + 7], geo.Fill[i + 8]);
            var c = new Vector3(geo.Fill[i + 12], geo.Fill[i + 13], geo.Fill[i + 14]);
            float ny = Vector3.Cross(b - a, c - a).Y;
            Assert.True(ny > 0f, $"三角形 {i / 18} 法线未朝上 (Y={ny})");
        }
    }

    [Fact]
    public void Circle_UsesConfiguredSegments()
    {
        var layer = new SketchLayer();
        layer.Add(new SketchShape
        {
            Kind = SketchShapeKind.Circle,
            Center = new Vector2(1, 2),
            Size = new Vector2(6, 0),
        });

        var geo = layer.Build(0f);

        int segments = SketchLayer.CircleSegments;
        Assert.Equal(segments * 3 * 6, geo.Fill.Length);
        Assert.Equal(segments * 2 * 6, geo.Outline.Length);

        // 所有轮廓点到圆心的距离应等于半径
        for (int i = 0; i < geo.Outline.Length; i += 6)
        {
            float dx = geo.Outline[i] - 1f;
            float dz = geo.Outline[i + 2] - 2f;
            Assert.Equal(6f, MathF.Sqrt(dx * dx + dz * dz), 3);
        }
    }

    [Fact]
    public void Preview_IsDrawnButNotCounted()
    {
        var layer = new SketchLayer();
        layer.Add(Rect(0, 0, 1, 1));

        var withoutPreview = layer.Build(0f);
        var withPreview = layer.Build(0f, Rect(20, 20, 3, 3));

        Assert.Equal(1, layer.Count); // 预览不进入图层
        Assert.True(withPreview.Fill.Length > withoutPreview.Fill.Length);
        Assert.Equal(2, withPreview.ShapeCount);
    }

    [Fact]
    public void Clear_ResetsShapesAndBumpsVersion()
    {
        var layer = new SketchLayer();
        layer.Add(Rect(0, 0, 1, 1));
        layer.Add(Rect(2, 2, 1, 1));
        int version = layer.Version;

        layer.Clear();

        Assert.Equal(0, layer.Count);
        Assert.True(layer.Version > version);
        Assert.True(layer.Build(0f).IsEmpty);
    }

    [Fact]
    public void Bounds_CoverAllShapes()
    {
        var layer = new SketchLayer();
        layer.Add(Rect(0, 0, 5, 5));      // X/Z ∈ [-5, 5]
        layer.Add(Rect(20, 0, 2, 2));     // X ∈ [18, 22]

        var box = layer.ComputeBounds(floorY: 1f);

        Assert.Equal(-5f, box.Min.X, 3);
        Assert.Equal(22f, box.Max.X, 3);
        Assert.Equal(1f, box.Min.Y, 3);
        Assert.Equal(1f, box.Max.Y, 3);
    }

    [Fact]
    public void FloorIntersection_HitsPlaneFromAbove()
    {
        var ray = new Ray(new Vector3(3, 50, -4), new Vector3(0, -1, 0));

        Assert.True(SketchLayer.TryIntersectFloor(ray, 0f, out var point));
        Assert.Equal(3f, point.X, 3);
        Assert.Equal(0f, point.Y, 3);
        Assert.Equal(-4f, point.Z, 3);
    }

    [Fact]
    public void FloorIntersection_RejectsParallelAndBackwardRays()
    {
        // 与地板平行
        Assert.False(SketchLayer.TryIntersectFloor(
            new Ray(new Vector3(0, 10, 0), new Vector3(1, 0, 0)), 0f, out _));

        // 地板在射线后方
        Assert.False(SketchLayer.TryIntersectFloor(
            new Ray(new Vector3(0, 10, 0), new Vector3(0, 1, 0)), 0f, out _));
    }
}
