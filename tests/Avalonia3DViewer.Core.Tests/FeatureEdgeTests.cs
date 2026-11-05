using System.Numerics;
using Avalonia3DViewer.Core.Geometry;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>2D 平面视图轮廓用的特征边提取测试（含 STL 式不共享顶点的情况）。</summary>
public class FeatureEdgeTests
{
    /// <summary>构造一个顶点完全不共享的立方体（模拟 STL：每个三角形独立顶点）。</summary>
    private static MeshData CreateUnweldedBox()
    {
        var c = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
        };
        int[] faces =
        {
            0, 1, 2, 0, 2, 3, // 后
            4, 6, 5, 4, 7, 6, // 前
            0, 4, 5, 0, 5, 1, // 下
            3, 2, 6, 3, 6, 7, // 上
            0, 3, 7, 0, 7, 4, // 左
            1, 5, 6, 1, 6, 2, // 右
        };

        var positions = new List<float>();
        var indices = new List<uint>();
        foreach (int fi in faces)
        {
            indices.Add((uint)(positions.Count / 3));
            positions.Add(c[fi].X); positions.Add(c[fi].Y); positions.Add(c[fi].Z);
        }

        var mesh = new MeshData
        {
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            Name = "unwelded-box",
        };
        mesh.EnsureNormals();
        mesh.RecomputeBounds();
        return mesh;
    }

    [Fact]
    public void Box_HasTwelveFeatureEdges_NotAllTriangleEdges()
    {
        var mesh = CreateUnweldedBox();
        Assert.Equal(36, mesh.VertexCount);        // 顶点不共享
        Assert.Equal(12, mesh.TriangleCount);

        var segments = mesh.BuildFeatureEdgeSegments(30f);
        int edgeCount = segments.Length / 6;

        // 立方体 12 条棱；每个面内的对角线与共面邻面夹角 0°，应被剔除
        Assert.Equal(12, edgeCount);
    }

    [Fact]
    public void CoplanarQuad_KeepsOnlyBoundary()
    {
        // 两个共面三角形组成一个四边形（顶点不共享）
        var positions = new[]
        {
            0f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f,
            0f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f,
        };
        var indices = new uint[] { 0, 1, 2, 3, 4, 5 };

        var mesh = new MeshData { Positions = positions, Indices = indices, Name = "quad" };
        mesh.RecomputeBounds();

        var segments = mesh.BuildFeatureEdgeSegments(30f);
        int edgeCount = segments.Length / 6;

        // 对角线（两三角形共享，夹角 0°）剔除；四条边界保留
        Assert.Equal(4, edgeCount);
    }

    [Fact]
    public void FeatureSegments_HaveSixFloatsPerEdge()
    {
        var mesh = CreateUnweldedBox();
        var segments = mesh.BuildFeatureEdgeSegments();
        Assert.Equal(0, segments.Length % 6);

        foreach (float v in segments)
        {
            Assert.True(float.IsFinite(v), "特征边坐标含 NaN/Inf");
        }
    }

    [Fact]
    public void EmptyMesh_ReturnsEmpty()
    {
        var mesh = MeshData.Empty();
        Assert.Empty(mesh.BuildFeatureEdgeSegments());
    }

    [Fact]
    public void HigherAngleThreshold_KeepsFewerEdges()
    {
        var mesh = CreateUnweldedBox();
        int at30 = mesh.BuildFeatureEdgeSegments(30f).Length / 6;
        int at100 = mesh.BuildFeatureEdgeSegments(100f).Length / 6;

        // 90° 棱在 100° 阈值下不算折边，只剩边界（此处无边界）→ 更少
        Assert.True(at100 <= at30, $"30°={at30}, 100°={at100}");
    }
}
