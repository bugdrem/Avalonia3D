using System.Numerics;
using Avalonia3DViewer.Core.Geometry;
using Avalonia3DViewer.Core.Loading;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>
/// 真实样本模型测试：加载仓库 samples/ 目录下的开源模型文件并校验数据完整性。
/// 样本来源：Khronos glTF Sample Models、Google Draco testdata、3MF Consortium、McNopper/OpenGL（Utah 茶壶）。
/// samples 目录不存在时自动跳过（克隆仓库但未下载样本的场景）。
/// </summary>
public class SampleModelTests
{
    private static readonly string SamplesDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));

    private static bool SamplesExist => Directory.Exists(SamplesDir);

    [SkippedOnMissingSamples]
    public void AllSampleModels_LoadSuccessfully()
    {
        Assert.True(SamplesExist, $"样本目录不存在: {SamplesDir}");
        var registry = new ModelLoaderRegistry();
        var files = Directory.GetFiles(SamplesDir)
            .Where(f => registry.FindLoader(f) != null)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            ModelDocument doc;
            try
            {
                doc = registry.Load(file);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"样本加载失败 [{name}]: {ex}");
            }

            var mesh = doc.Mesh;
            Assert.True(mesh.TriangleCount > 0, $"[{name}] 三角面数量为 0");
            Assert.True(mesh.VertexCount > 0, $"[{name}] 顶点数量为 0");

            // 索引全部在范围内
            foreach (var idx in mesh.Indices)
                Assert.True(idx < (uint)mesh.VertexCount, $"[{name}] 索引 {idx} 越界（顶点数 {mesh.VertexCount}）");

            // 位置均为有限值
            foreach (var p in mesh.Positions)
                Assert.True(float.IsFinite(p), $"[{name}] 存在非有限坐标");

            // 法线已计算且为单位长度
            Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var n = new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
                Assert.True(n.Length() is > 0.5f and < 1.5f, $"[{name}] 顶点 {v} 法线非单位长度: {n.Length()}");
            }

            // 颜色与位置数量匹配
            Assert.Equal(mesh.Positions.Length, mesh.Colors.Length);

            // 包围盒有效
            var bounds = mesh.Bounds;
            Assert.False(bounds.IsEmpty, $"[{name}] 包围盒为空");
            Assert.True(bounds.Size.X > 0 && bounds.Size.Y > 0 && bounds.Size.Z > 0,
                $"[{name}] 包围盒尺寸为零: {bounds.Size}");
        }
    }

    [SkippedOnMissingSamples]
    public void SampleModels_CanBeRaycast()
    {
        var registry = new ModelLoaderRegistry();
        var files = Directory.GetFiles(SamplesDir)
            .Where(f => registry.FindLoader(f) != null)
            .ToList();

        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            var doc = registry.Load(file);
            var raycaster = new Picking.MeshRaycaster(doc.Mesh);
            var b = doc.Mesh.Bounds;
            var center = b.Center;
            float radius = MathF.Max(b.Radius, 1e-3f);

            // 从包围球外多个方向射向质心：多零件装配体（如 cube_gears）单方向可能从间隙穿过，
            // 要求至少一个方向命中，且命中点必须位于包围盒内、距离为正。
            bool anyHit = false;
            var directions = new[]
            {
                new Vector3(1, 1, 1), new Vector3(-1, 1, 1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
                new Vector3(1, -1, 1), new Vector3(-1, -1, 1), new Vector3(1, -1, -1), new Vector3(-1, -1, -1),
                new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0),
                new Vector3(0, 0, 1), new Vector3(0, 0, -1),
            };

            foreach (var dir in directions)
            {
                var d = Vector3.Normalize(dir);
                var origin = center - d * (radius * 2f);
                var ray = new Geometry.Ray(origin, d);
                var hit = raycaster.Pick(ray);
                if (!hit.HasValue) continue;

                anyHit = true;
                Assert.True(hit!.Value.Distance > 0f, $"[{name}] 命中距离非正");
                // 边界命中存在浮点误差，放宽 1e-3 容差
                var expanded = new Geometry.BoundingBox(
                    b.Min - new Vector3(1e-3f), b.Max + new Vector3(1e-3f));
                Assert.True(expanded.Contains(hit.Value.Point),
                    $"[{name}] 命中点超出包围盒: {hit.Value.Point}");
            }

            Assert.True(anyHit, $"[{name}] 14 个方向的射线均未命中模型");
        }
    }

    private sealed class SkippedOnMissingSamplesAttribute : FactAttribute
    {
        public SkippedOnMissingSamplesAttribute()
        {
            if (!SamplesExist)
                Skip = $"样本目录不存在: {SamplesDir}（可执行 dotnet run --project tools/DownloadSamples 下载）";
        }
    }
}
