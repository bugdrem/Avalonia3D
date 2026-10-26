using System.Numerics;

namespace Avalonia3DViewer.Core.Procedural;

/// <summary>
/// 程序化示例模型（无需外部文件即可体验渲染/拾取/标注）。
/// 全部为参数化网格：直齿轮、阶梯轴、六角螺母、圆环结。
/// </summary>
public static class DemoModels
{
    /// <summary>直齿轮（圆柱齿轮）：齿圈 + 轮毂 + 中心孔。</summary>
    public static Geometry.MeshData CreateGear()
    {
        const int teeth = 24;
        const float module = 4f;              // 模数 mm
        const float thickness = 30f;          // 齿宽 mm
        float pitchR = module * teeth / 2f;   // 节圆半径
        float outerR = pitchR + module;       // 齿顶圆
        float rootR = pitchR - 1.25f * module; // 齿根圆
        float hubR = pitchR * 0.45f;
        float boreR = 10f;

        var b = new MeshBuilder();
        var steel = new Vector3(0.62f, 0.66f, 0.72f);

        // 齿圈轮廓（半径, 角度）→ 挤压
        var profile = new List<(float r, float a)>();
        float toothAngle = MathF.PI * 2f / teeth;
        for (int t = 0; t < teeth; t++)
        {
            float a0 = t * toothAngle;
            AddArc(profile, rootR, a0 + toothAngle * 0.18f, a0 + toothAngle * 0.34f);
            AddArc(profile, outerR, a0 + toothAngle * 0.34f, a0 + toothAngle * 0.52f);
            AddArc(profile, outerR, a0 + toothAngle * 0.52f, a0 + toothAngle * 0.68f);
            AddArc(profile, rootR, a0 + toothAngle * 0.68f, a0 + toothAngle * 0.84f);
            AddArc(profile, rootR, a0 + toothAngle * 0.84f, a0 + toothAngle);
        }
        b.ExtrudeProfile(profile, thickness, steel);

        // 中心通孔内壁
        b.AddTube(boreR, thickness, steel, flipNormals: true);

        // 轮毂（加高 + 顶面环）
        b.AddCylinder(hubR, thickness + 12f, 0f, steel);
        b.AddAnnulusTop(hubR, boreR, thickness + 12f, steel);

        return b.ToMesh("直齿轮 (24齿)", steel);
    }

    /// <summary>阶梯轴：5 段不同直径的圆柱。</summary>
    public static Geometry.MeshData CreateSteppedShaft()
    {
        float[] radii = { 15f, 22f, 30f, 22f, 15f };
        float[] heights = { 25f, 40f, 70f, 40f, 25f };
        var b = new MeshBuilder();
        var steel = new Vector3(0.55f, 0.60f, 0.67f);
        float y = 0f;
        for (int i = 0; i < radii.Length; i++)
        {
            b.AddCylinder(radii[i], heights[i], y, steel);
            y += heights[i];
        }
        return b.ToMesh("阶梯轴", steel);
    }

    /// <summary>六角螺母：六棱柱 + 通孔。</summary>
    public static Geometry.MeshData CreateHexNut()
    {
        const int sides = 6;
        const float acrossFlats = 24f; // 对边距 mm
        const float height = 14f;
        float circumR = acrossFlats / MathF.Sqrt(3f);
        float boreR = acrossFlats * 0.42f;

        var b = new MeshBuilder();
        var brass = new Vector3(0.72f, 0.66f, 0.58f);

        var profile = new List<(float r, float a)>();
        float side = MathF.PI * 2f / sides;
        for (int i = 0; i < sides; i++)
        {
            AddArc(profile, circumR, i * side, (i + 1) * side);
        }
        b.ExtrudeProfile(profile, height, brass);
        b.AddTube(boreR, height, brass, flipNormals: true);

        return b.ToMesh("六角螺母 M24", brass);
    }

    /// <summary>圆环结（Torus Knot (2,3)）：光滑参数曲面。</summary>
    public static Geometry.MeshData CreateTorusKnot()
    {
        const int pathSteps = 240;
        const int ringSteps = 24;
        const float tubeR = 6f;
        const float scale = 1.4f;

        var b = new MeshBuilder();
        var color = new Vector3(0.35f, 0.55f, 0.75f);

        for (int i = 0; i < pathSteps; i++)
        {
            float u = i / (float)pathSteps * MathF.PI * 2f;
            var center = TorusKnotPoint(u) * scale;
            var tangent = TorusKnotTangent(u);
            var normal = Vector3.Normalize(Vector3.Cross(tangent, Vector3.UnitY));
            if (normal.LengthSquared() < 0.5f) normal = Vector3.UnitZ;
            var binormal = Vector3.Normalize(Vector3.Cross(tangent, normal));

            uint ringStart = (uint)b.Positions.Count / 3;
            for (int j = 0; j < ringSteps; j++)
            {
                float v = j / (float)ringSteps * MathF.PI * 2f;
                var p = center + (normal * MathF.Cos(v) + binormal * MathF.Sin(v)) * tubeR;
                b.AddVertex(p, color);
            }

            if (i > 0)
            {
                uint prevStart = ringStart - (uint)ringSteps;
                for (int j = 0; j < ringSteps; j++)
                {
                    int j2 = (j + 1) % ringSteps;
                    b.AddTriangle(prevStart + (uint)j, ringStart + (uint)j, ringStart + (uint)j2);
                    b.AddTriangle(prevStart + (uint)j, ringStart + (uint)j2, prevStart + (uint)j2);
                }
            }
        }

        return b.ToMesh("圆环结 (2,3)", color);
    }

    // ---- 内部 ----

    private static void AddArc(List<(float r, float a)> profile, float radius, float a0, float a1, int steps = 2)
    {
        for (int i = 0; i < steps; i++)
        {
            float a = a0 + (a1 - a0) * i / steps;
            profile.Add((radius, a));
        }
    }

    private static Vector3 TorusKnotPoint(float u) => new(
        (2f + MathF.Cos(3f * u)) * MathF.Cos(2f * u) * 20f,
        (2f + MathF.Cos(3f * u)) * MathF.Sin(2f * u) * 20f,
        MathF.Sin(3f * u) * 20f);

    private static Vector3 TorusKnotTangent(float u)
    {
        const float h = 1e-3f;
        return Vector3.Normalize(TorusKnotPoint(u + h) - TorusKnotPoint(u - h));
    }
}

/// <summary>程序化网格构建器。</summary>
internal sealed class MeshBuilder
{
    public List<float> Positions { get; } = new();
    public List<uint> Indices { get; } = new();

    public uint AddVertex(Vector3 p, Vector3 color)
    {
        uint idx = (uint)(Positions.Count / 3);
        Positions.Add(p.X); Positions.Add(p.Y); Positions.Add(p.Z);
        return idx;
    }

    public uint AddVertex(float x, float y, float z)
    {
        uint idx = (uint)(Positions.Count / 3);
        Positions.Add(x); Positions.Add(y); Positions.Add(z);
        return idx;
    }

    public void AddTriangle(uint a, uint b, uint c)
    {
        Indices.Add(a); Indices.Add(b); Indices.Add(c);
    }

    /// <summary>将 (半径,角度) 轮廓在 Y∈[0,thickness] 挤压为实体。</summary>
    public void ExtrudeProfile(List<(float r, float a)> profile, float thickness, Vector3 color)
    {
        int n = profile.Count;
        uint bottomStart = (uint)(Positions.Count / 3);
        uint topStart = bottomStart + (uint)n;
        for (int i = 0; i < n; i++)
        {
            float x = profile[i].r * MathF.Cos(profile[i].a);
            float z = profile[i].r * MathF.Sin(profile[i].a);
            AddVertex(x, 0f, z);
            AddVertex(x, thickness, z);
        }

        for (int i = 0; i < n; i++)
        {
            int i2 = (i + 1) % n;
            AddTriangle(bottomStart + (uint)i, topStart + (uint)i, topStart + (uint)i2);
            AddTriangle(bottomStart + (uint)i, topStart + (uint)i2, bottomStart + (uint)i2);
        }

        // 端面（扇形，以第 0 个顶点为中心）
        for (int i = 1; i + 1 < n; i++)
        {
            AddTriangle(topStart, topStart + (uint)i, topStart + (uint)(i + 1));
            AddTriangle(bottomStart, bottomStart + (uint)(i + 1), bottomStart + (uint)i);
        }
    }

    /// <summary>圆柱（含上下端面扇形），从 y0 起。</summary>
    public void AddCylinder(float radius, float height, float y0, Vector3 color, int segments = 64)
    {
        uint bottomStart = (uint)(Positions.Count / 3);
        uint topStart = bottomStart + (uint)segments;
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * MathF.PI * 2f;
            float x = radius * MathF.Cos(a);
            float z = radius * MathF.Sin(a);
            AddVertex(x, y0, z);
            AddVertex(x, y0 + height, z);
        }
        for (int i = 0; i < segments; i++)
        {
            int i2 = (i + 1) % segments;
            AddTriangle(bottomStart + (uint)i, topStart + (uint)i, topStart + (uint)i2);
            AddTriangle(bottomStart + (uint)i, topStart + (uint)i2, bottomStart + (uint)i2);
        }
        for (int i = 1; i + 1 < segments; i++)
        {
            AddTriangle(topStart, topStart + (uint)i, topStart + (uint)(i + 1));
            AddTriangle(bottomStart, bottomStart + (uint)(i + 1), bottomStart + (uint)i);
        }
    }

    /// <summary>圆管（孔壁）：从 y=0 到 y=height。</summary>
    public void AddTube(float radius, float height, Vector3 color, bool flipNormals = false, int segments = 48)
    {
        uint bottomStart = (uint)(Positions.Count / 3);
        uint topStart = bottomStart + (uint)segments;
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * MathF.PI * 2f;
            float x = radius * MathF.Cos(a);
            float z = radius * MathF.Sin(a);
            AddVertex(x, 0f, z);
            AddVertex(x, height, z);
        }
        for (int i = 0; i < segments; i++)
        {
            int i2 = (i + 1) % segments;
            if (!flipNormals)
            {
                AddTriangle(bottomStart + (uint)i, topStart + (uint)i, topStart + (uint)i2);
                AddTriangle(bottomStart + (uint)i, topStart + (uint)i2, bottomStart + (uint)i2);
            }
            else
            {
                AddTriangle(bottomStart + (uint)i, topStart + (uint)i2, topStart + (uint)i);
                AddTriangle(bottomStart + (uint)i, bottomStart + (uint)i2, topStart + (uint)i2);
            }
        }
    }

    /// <summary>y=height 处的水平环面（内径→外径）。</summary>
    public void AddAnnulusTop(float outerR, float innerR, float height, Vector3 color, int segments = 48)
    {
        uint start = (uint)(Positions.Count / 3);
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * MathF.PI * 2f;
            float xo = outerR * MathF.Cos(a);
            float zo = outerR * MathF.Sin(a);
            float xi = innerR * MathF.Cos(a);
            float zi = innerR * MathF.Sin(a);
            AddVertex(xo, height, zo);
            AddVertex(xi, height, zi);
        }
        for (int i = 0; i < segments; i++)
        {
            int i2 = (i + 1) % segments;
            uint o0 = start + (uint)(i * 2), i0 = o0 + 1;
            uint o1 = start + (uint)(i2 * 2), i1 = o1 + 1;
            AddTriangle(o0, i0, i1);
            AddTriangle(o0, i1, o1);
        }
    }

    public Geometry.MeshData ToMesh(string name, Vector3 baseColor)
    {
        var mesh = new Geometry.MeshData
        {
            Positions = Positions.ToArray(),
            Indices = Indices.ToArray(),
            Name = name,
            BaseColor = baseColor,
        };
        mesh.EnsureNormals();
        mesh.EnsureColors();
        mesh.RecomputeBounds();
        return mesh;
    }
}
