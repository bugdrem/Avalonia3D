using System.Numerics;
using Avalonia3DViewer.Core.Geometry;
using Avalonia3DViewer.Core.Picking;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>射线拾取测试（均匀网格加速 + Möller–Trumbore）。</summary>
public class MeshRaycasterTests
{
    /// <summary>构造一个 10mm 居中立方体网格（索引化，24 顶点）。</summary>
    private static MeshData CreateBox(float half = 5f)
    {
        var positions = new List<float>();
        var indices = new List<uint>();

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            uint start = (uint)(positions.Count / 3);
            foreach (var v in new[] { a, b, c, d })
            {
                positions.Add(v.X); positions.Add(v.Y); positions.Add(v.Z);
            }
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }

        Quad(new(-half, -half, half), new(half, -half, half), new(half, half, half), new(-half, half, half));
        Quad(new(-half, -half, -half), new(-half, half, -half), new(half, half, -half), new(half, -half, -half));
        Quad(new(-half, -half, -half), new(half, -half, -half), new(half, -half, half), new(-half, -half, half));
        Quad(new(-half, half, -half), new(-half, half, half), new(half, half, half), new(half, half, -half));
        Quad(new(half, -half, -half), new(half, half, -half), new(half, half, half), new(half, -half, half));
        Quad(new(-half, -half, -half), new(-half, -half, half), new(-half, half, half), new(-half, half, -half));

        var mesh = new MeshData
        {
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            Name = "box",
        };
        mesh.EnsureNormals();
        mesh.EnsureColors();
        mesh.RecomputeBounds();
        return mesh;
    }

    [Fact]
    public void Pick_HitsBoxCenter()
    {
        var mesh = CreateBox();
        var raycaster = new MeshRaycaster(mesh);

        var ray = new Ray(new Vector3(0, 0, 50), new Vector3(0, 0, -1));
        var hit = raycaster.Pick(ray);

        Assert.NotNull(hit);
        Assert.Equal(45f, hit!.Value.Distance, 2); // 从 z=50 到前表面 z=5
        Assert.Equal(0f, hit.Value.Point.X, 3);
        Assert.Equal(0f, hit.Value.Point.Y, 3);
        Assert.Equal(5f, hit.Value.Point.Z, 3);
        Assert.Equal(1f, hit.Value.Normal.Z, 3); // 前表面法线 +Z
    }

    [Fact]
    public void Pick_MissesBox()
    {
        var mesh = CreateBox();
        var raycaster = new MeshRaycaster(mesh);

        var ray = new Ray(new Vector3(50, 50, 50), new Vector3(1, 0, 0));
        Assert.Null(raycaster.Pick(ray));
    }

    [Fact]
    public void Pick_ReturnsNearestOfMultipleHits()
    {
        var mesh = CreateBox();
        var raycaster = new MeshRaycaster(mesh);

        // 穿过盒子的射线：应先命中前表面 (z=5)，距离 45
        var ray = new Ray(new Vector3(0, 0, 100), new Vector3(0, 0, -1));
        var hit = raycaster.Pick(ray);
        Assert.NotNull(hit);
        Assert.Equal(95f, hit!.Value.Distance, 2);
    }

    [Fact]
    public void Pick_RespectsMaxDistance()
    {
        var mesh = CreateBox();
        var raycaster = new MeshRaycaster(mesh);

        var ray = new Ray(new Vector3(0, 0, 50), new Vector3(0, 0, -1));
        Assert.Null(raycaster.Pick(ray, maxDistance: 10f)); // 表面在 45，超出限制
        Assert.NotNull(raycaster.Pick(ray, maxDistance: 50f));
    }

    [Fact]
    public void Pick_DiagonalRayThroughBox()
    {
        var mesh = CreateBox();
        var raycaster = new MeshRaycaster(mesh);

        var ray = new Ray(new Vector3(-50, -50, -50), Vector3.Normalize(new Vector3(1, 1, 1)));
        var hit = raycaster.Pick(ray);
        Assert.NotNull(hit);
        // 从角点 (-50,-50,-50) 到角点 (-5,-5,-5)：距离 = 45√3
        Assert.Equal(45f * MathF.Sqrt(3f), hit!.Value.Distance, 1);
    }

    [Fact]
    public void EdgeIndices_AreDeduplicated()
    {
        // 共享顶点的四边形（2 三角形）：4 条边 + 1 条对角线 = 5 条唯一边
        var positions = new List<float>
        {
            0, 0, 0,
            1, 0, 0,
            1, 1, 0,
            0, 1, 0,
        };
        var indices = new List<uint> { 0, 1, 2, 0, 2, 3 };
        var mesh = new MeshData
        {
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            Name = "quad",
        };
        mesh.RecomputeBounds();

        uint[] edges = mesh.BuildEdgeIndices();
        Assert.Equal(10, edges.Length); // 5 条唯一边 × 2 端点

        // 验证无重复边
        var seen = new HashSet<ulong>();
        for (int i = 0; i < edges.Length; i += 2)
        {
            ulong key = edges[i] < edges[i + 1]
                ? ((ulong)edges[i] << 32) | edges[i + 1]
                : ((ulong)edges[i + 1] << 32) | edges[i];
            Assert.True(seen.Add(key), "存在重复边");
        }
    }

    [Fact]
    public void BoundingBox_IntersectRay()
    {
        var box = new BoundingBox(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));

        Assert.NotNull(box.IntersectRay(new Ray(new Vector3(0, 0, 5), new Vector3(0, 0, -1)), 100f));
        Assert.NotNull(box.IntersectRay(new Ray(new Vector3(-5, 0, 0), new Vector3(1, 0, 0)), 100f));
        Assert.Null(box.IntersectRay(new Ray(new Vector3(0, 5, 5), new Vector3(0, 0, -1)), 100f));
        Assert.Null(box.IntersectRay(new Ray(new Vector3(0, 0, 5), new Vector3(0, 0, -1)), 3f));
    }
}
