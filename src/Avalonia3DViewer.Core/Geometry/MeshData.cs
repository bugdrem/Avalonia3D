using System.Numerics;

namespace Avalonia3DViewer.Core.Geometry;

/// <summary>
/// 三角网格数据（SoA 布局，加载后不可变；GPU 数据在渲染层单独构建）。
/// 坐标单位约定为毫米（STL/OBJ 等无单位格式按毫米处理，3MF 按声明单位换算为毫米）。
/// </summary>
public sealed class MeshData
{
    /// <summary>顶点位置，每顶点 3 个 float (xyz)。</summary>
    public float[] Positions = Array.Empty<float>();

    /// <summary>顶点法线，每顶点 3 个 float (xyz)，与 Positions 对齐。</summary>
    public float[] Normals = Array.Empty<float>();

    /// <summary>顶点颜色，每顶点 3 个 float (rgb)，与 Positions 对齐。</summary>
    public float[] Colors = Array.Empty<float>();

    /// <summary>三角形索引，每三角形 3 个 uint。</summary>
    public uint[] Indices = Array.Empty<uint>();

    public string Name { get; init; } = string.Empty;

    /// <summary>网格默认基色（生成 Colors 时使用）。</summary>
    public Vector3 BaseColor { get; init; } = new(0.55f, 0.60f, 0.67f);

    private BoundingBox? _bounds;

    public BoundingBox Bounds => _bounds ??= ComputeBounds();

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;

    public static MeshData Empty(string name = "") => new() { Name = name };

    public void RecomputeBounds() => _bounds = ComputeBounds();

    private BoundingBox ComputeBounds()
    {
        var box = new BoundingBox();
        for (int i = 0; i < Positions.Length; i += 3)
        {
            box.Include(new Vector3(Positions[i], Positions[i + 1], Positions[i + 2]));
        }
        return box;
    }

    /// <summary>法线缺失或为零时按面积加权重新计算平滑法线。</summary>
    public void EnsureNormals()
    {
        int count = VertexCount;
        if (Normals.Length != Positions.Length)
        {
            Normals = new float[Positions.Length];
        }

        bool allZero = true;
        for (int i = 0; i < Normals.Length; i++)
        {
            if (Normals[i] != 0f) { allZero = false; break; }
        }

        bool hasZeroVertex = false;
        if (!allZero)
        {
            for (int v = 0; v < count; v++)
            {
                int i = v * 3;
                if (Normals[i] == 0f && Normals[i + 1] == 0f && Normals[i + 2] == 0f)
                {
                    hasZeroVertex = true;
                    break;
                }
            }
        }

        if (!allZero && !hasZeroVertex) return; // 已有有效法线

        Array.Clear(Normals);
        for (int t = 0; t < Indices.Length; t += 3)
        {
            uint ia = Indices[t], ib = Indices[t + 1], ic = Indices[t + 2];
            var a = new Vector3(Positions[ia * 3], Positions[ia * 3 + 1], Positions[ia * 3 + 2]);
            var b = new Vector3(Positions[ib * 3], Positions[ib * 3 + 1], Positions[ib * 3 + 2]);
            var c = new Vector3(Positions[ic * 3], Positions[ic * 3 + 1], Positions[ic * 3 + 2]);
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-16f) continue;
            Normals[ia * 3] += n.X; Normals[ia * 3 + 1] += n.Y; Normals[ia * 3 + 2] += n.Z;
            Normals[ib * 3] += n.X; Normals[ib * 3 + 1] += n.Y; Normals[ib * 3 + 2] += n.Z;
            Normals[ic * 3] += n.X; Normals[ic * 3 + 1] += n.Y; Normals[ic * 3 + 2] += n.Z;
        }

        for (int i = 0; i < Normals.Length; i += 3)
        {
            var n = new Vector3(Normals[i], Normals[i + 1], Normals[i + 2]);
            if (n.LengthSquared() < 1e-16f)
            {
                n = Vector3.UnitY;
            }
            else
            {
                n = Vector3.Normalize(n);
            }
            Normals[i] = n.X; Normals[i + 1] = n.Y; Normals[i + 2] = n.Z;
        }
    }

    /// <summary>用 BaseColor 填充顶点颜色。</summary>
    public void EnsureColors()
    {
        if (Colors.Length == Positions.Length) return;
        Colors = new float[Positions.Length];
        for (int i = 0; i < Colors.Length; i += 3)
        {
            Colors[i] = BaseColor.X;
            Colors[i + 1] = BaseColor.Y;
            Colors[i + 2] = BaseColor.Z;
        }
    }

    /// <summary>
    /// 构建线框模式的线段索引（无重复边）。大网格（>30 万三角形）跳过去重以控制内存。
    /// </summary>
    public uint[] BuildEdgeIndices()
    {
        int triCount = TriangleCount;
        if (triCount == 0) return Array.Empty<uint>();

        if (triCount > 300_000)
        {
            var raw = new uint[triCount * 6];
            for (int t = 0, o = 0; t < Indices.Length; t += 3, o += 6)
            {
                raw[o] = Indices[t]; raw[o + 1] = Indices[t + 1];
                raw[o + 2] = Indices[t + 1]; raw[o + 3] = Indices[t + 2];
                raw[o + 4] = Indices[t + 2]; raw[o + 5] = Indices[t];
            }
            return raw;
        }

        var seen = new HashSet<ulong>(triCount * 2);
        var edges = new List<uint>(triCount * 6);
        for (int t = 0; t < Indices.Length; t += 3)
        {
            uint a = Indices[t], b = Indices[t + 1], c = Indices[t + 2];
            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }

        return edges.ToArray();

        void AddEdge(uint i0, uint i1)
        {
            ulong key = i0 < i1 ? ((ulong)i0 << 32) | i1 : ((ulong)i1 << 32) | i0;
            if (seen.Add(key))
            {
                edges.Add(i0);
                edges.Add(i1);
            }
        }
    }

    /// <summary>生成统计信息文本。</summary>
    public string Describe()
    {
        var size = Bounds.Size;
        return $"顶点 {VertexCount:N0} · 三角面 {TriangleCount:N0} · 尺寸 {size.X:F1}×{size.Y:F1}×{size.Z:F1} mm";
    }
}
