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
    /// <summary>
    /// 提取特征边（轮廓/折边）：相邻两三角面法线夹角超过阈值的边，或边界边。
    /// 用于 2D 平面视图的 CAD 式轮廓绘制——比"所有三角边"干净得多，且只需在加载时计算一次。
    /// 两个工程细节：
    /// 1) STL/部分 OBJ 的顶点不共享，先按**位置量化**焊接，否则所有边都会被当成边界边；
    /// 2) 导出工具常见绕序不一致（相邻共面面法线反向），用**边的遍历方向**判定并翻转后再比较角度。
    /// </summary>
    /// <param name="minAngleDegrees">折边阈值（度）。30° 适合机加工零件，能保留齿形/台阶又不画平滑面细分。</param>
    public float[] BuildFeatureEdgeSegments(float minAngleDegrees = 30f)
    {
        int triCount = TriangleCount;
        if (triCount == 0) return Array.Empty<float>();

        float cosLimit = MathF.Cos(Math.Clamp(minAngleDegrees, 1f, 179f) * MathF.PI / 180f);
        float eps = MathF.Max(Bounds.Size.Length() * 1e-5f, 1e-5f);

        var canonical = new Dictionary<(int, int, int), int>(triCount * 3);
        var canonicalPos = new List<Vector3>(triCount * 3);

        // 1) 每个三角面的法线（原始坐标，避免量化误差影响角度判定）
        var faceNormals = new Vector3[triCount];
        for (int t = 0; t < triCount; t++)
        {
            int i = t * 3;
            var a = VertexAt(Indices[i]);
            var b = VertexAt(Indices[i + 1]);
            var d = VertexAt(Indices[i + 2]);
            var n = Vector3.Cross(b - a, d - a);
            float len = n.Length();
            faceNormals[t] = len > 1e-12f ? n / len : Vector3.Zero;
        }

        // 2) 位置焊接 + 边 → 相邻面及其遍历方向
        var edgeFaces = new Dictionary<ulong, (int f0, int f1, bool asc0, bool asc1)>(triCount * 3);
        for (int t = 0; t < triCount; t++)
        {
            int i = t * 3;
            int c0 = Weld(Indices[i]);
            int c1 = Weld(Indices[i + 1]);
            int c2 = Weld(Indices[i + 2]);
            Register(c0, c1, t);
            Register(c1, c2, t);
            Register(c2, c0, t);
        }

        // 3) 过滤并输出线段端点坐标
        var result = new List<float>(edgeFaces.Count / 2 * 6);
        foreach (var kv in edgeFaces)
        {
            var (f0, f1, asc0, asc1) = kv.Value;
            bool keep = f1 < 0; // 边界边（单邻面）

            if (!keep)
            {
                var n0 = faceNormals[f0];
                var n1 = faceNormals[f1];
                if (n0 == Vector3.Zero || n1 == Vector3.Zero)
                {
                    keep = true;
                }
                else
                {
                    // 两面对该边的遍历方向相同 ⇒ 绕序相反 ⇒ 法线翻转后再比较
                    if (asc0 == asc1) n1 = -n1;
                    keep = Vector3.Dot(n0, n1) < cosLimit;
                }
            }

            if (!keep) continue;

            int lo = (int)(kv.Key >> 32);
            int hi = (int)(kv.Key & 0xFFFFFFFF);
            var pa = canonicalPos[lo];
            var pb = canonicalPos[hi];
            result.Add(pa.X); result.Add(pa.Y); result.Add(pa.Z);
            result.Add(pb.X); result.Add(pb.Y); result.Add(pb.Z);
        }

        return result.ToArray();

        int Weld(uint index)
        {
            var p = VertexAt(index);
            var key = (
                (int)MathF.Round(p.X / eps),
                (int)MathF.Round(p.Y / eps),
                (int)MathF.Round(p.Z / eps));
            if (!canonical.TryGetValue(key, out int id))
            {
                id = canonicalPos.Count;
                canonical[key] = id;
                canonicalPos.Add(p);
            }
            return id;
        }

        void Register(int a, int b, int face)
        {
            if (a == b) return; // 退化边
            int lo = Math.Min(a, b);
            int hi = Math.Max(a, b);
            bool ascending = a < b;
            ulong key = ((ulong)(uint)lo << 32) | (uint)hi;

            if (edgeFaces.TryGetValue(key, out var existing))
            {
                if (existing.f1 < 0 && existing.f0 != face)
                    edgeFaces[key] = (existing.f0, face, existing.asc0, ascending);
            }
            else
            {
                edgeFaces[key] = (face, -1, ascending, false);
            }
        }
    }

    private Vector3 VertexAt(uint index) => new(
        Positions[index * 3], Positions[index * 3 + 1], Positions[index * 3 + 2]);

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
