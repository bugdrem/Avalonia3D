using System.Numerics;

namespace Avalonia3DViewer.Core.Picking;

/// <summary>拾取结果。</summary>
public readonly struct PickResult
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly float Distance;
    public readonly int TriangleIndex;

    public PickResult(Vector3 point, Vector3 normal, float distance, int triangleIndex)
    {
        Point = point;
        Normal = normal;
        Distance = distance;
        TriangleIndex = triangleIndex;
    }
}

/// <summary>
/// 网格射线拾取：均匀网格加速 + Möller–Trumbore 三角形求交。
/// 构建后线程安全（只读查询）。网格过小时退化为暴力遍历。
/// </summary>
public sealed class MeshRaycaster
{
    private readonly Geometry.MeshData _mesh;
    private readonly float[] _positions;
    private readonly uint[] _indices;
    private readonly int _triangleCount;

    // 均匀网格
    private readonly Vector3 _gridMin;
    private readonly Vector3 _cellSize;
    private readonly int _gridX, _gridY, _gridZ;
    private int[][]? _cells;
    private readonly bool _useGrid;

    public MeshRaycaster(Geometry.MeshData mesh)
    {
        _mesh = mesh;
        _positions = mesh.Positions;
        _indices = mesh.Indices;
        _triangleCount = _indices.Length / 3;

        var bounds = mesh.Bounds;
        _gridMin = bounds.Min;

        // 目标约 4 个三角形/格
        int cellsPerAxis = (int)MathF.Cbrt(MathF.Max(_triangleCount / 4f, 1f));
        cellsPerAxis = Math.Clamp(cellsPerAxis, 1, 64);
        _gridX = _gridY = _gridZ = cellsPerAxis;
        var size = bounds.Size;
        _cellSize = new Vector3(
            MathF.Max(size.X / _gridX, 1e-4f),
            MathF.Max(size.Y / _gridY, 1e-4f),
            MathF.Max(size.Z / _gridZ, 1e-4f));

        _useGrid = _triangleCount >= 256;
        if (_useGrid) BuildGrid();
    }

    private void BuildGrid()
    {
        int total = _gridX * _gridY * _gridZ;
        var buckets = new List<int>[total];
        for (int t = 0; t < _triangleCount; t++)
        {
            uint ia = _indices[t * 3], ib = _indices[t * 3 + 1], ic = _indices[t * 3 + 2];
            var a = Vertex(ia);
            var b = Vertex(ib);
            var c = Vertex(ic);
            var min = Vector3.Min(Vector3.Min(a, b), c);
            var max = Vector3.Max(Vector3.Max(a, b), c);

            int ix0 = ClampCell(Coord(min.X, _gridMin.X, _cellSize.X), _gridX);
            int iy0 = ClampCell(Coord(min.Y, _gridMin.Y, _cellSize.Y), _gridY);
            int iz0 = ClampCell(Coord(min.Z, _gridMin.Z, _cellSize.Z), _gridZ);
            int ix1 = ClampCell(Coord(max.X, _gridMin.X, _cellSize.X), _gridX);
            int iy1 = ClampCell(Coord(max.Y, _gridMin.Y, _cellSize.Y), _gridY);
            int iz1 = ClampCell(Coord(max.Z, _gridMin.Z, _cellSize.Z), _gridZ);

            for (int z = iz0; z <= iz1; z++)
                for (int y = iy0; y <= iy1; y++)
                    for (int x = ix0; x <= ix1; x++)
                    {
                        int idx = (z * _gridY + y) * _gridX + x;
                        (buckets[idx] ??= new List<int>()).Add(t);
                    }
        }

        _cells = new int[total][];
        for (int i = 0; i < total; i++)
            _cells[i] = buckets[i]?.ToArray() ?? Array.Empty<int>();
    }

    private static int Coord(float v, float min, float cell) => (int)((v - min) / cell);

    private static int ClampCell(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

    private Vector3 Vertex(uint i) => new(_positions[i * 3], _positions[i * 3 + 1], _positions[i * 3 + 2]);

    /// <summary>射线拾取，返回最近命中（含背面命中）。</summary>
    public PickResult? Pick(in Geometry.Ray ray, float maxDistance = float.MaxValue)
    {
        // 先用包围盒粗筛
        if (_mesh.Bounds.IntersectRay(ray, maxDistance) is not float _)
            return null;

        if (!_useGrid || _cells is null)
            return PickBruteForce(ray, maxDistance);

        // 网格遍历（Amanatides & Woo）
        var origin = ray.Origin;
        var dir = ray.Direction;
        int ix = ClampCell(Coord(origin.X, _gridMin.X, _cellSize.X), _gridX);
        int iy = ClampCell(Coord(origin.Y, _gridMin.Y, _cellSize.Y), _gridY);
        int iz = ClampCell(Coord(origin.Z, _gridMin.Z, _cellSize.Z), _gridZ);

        int stepX = dir.X > 0 ? 1 : dir.X < 0 ? -1 : 0;
        int stepY = dir.Y > 0 ? 1 : dir.Y < 0 ? -1 : 0;
        int stepZ = dir.Z > 0 ? 1 : dir.Z < 0 ? -1 : 0;

        float tMaxX = stepX == 0 ? float.PositiveInfinity : BoundaryDistance(origin.X, dir.X, _gridMin.X, _cellSize.X, ix);
        float tMaxY = stepY == 0 ? float.PositiveInfinity : BoundaryDistance(origin.Y, dir.Y, _gridMin.Y, _cellSize.Y, iy);
        float tMaxZ = stepZ == 0 ? float.PositiveInfinity : BoundaryDistance(origin.Z, dir.Z, _gridMin.Z, _cellSize.Z, iz);
        float tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(_cellSize.X / dir.X);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(_cellSize.Y / dir.Y);
        float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : MathF.Abs(_cellSize.Z / dir.Z);

        float best = maxDistance;
        PickResult? bestResult = null;

        int guard = 0;
        int maxSteps = (_gridX + _gridY + _gridZ) * 4 + 64;
        while (guard++ < maxSteps)
        {
            int idx = (iz * _gridY + iy) * _gridX + ix;
            foreach (int t in _cells[idx])
            {
                if (TestTriangle(ray, t, best, out var result) && result.Distance < best)
                {
                    best = result.Distance;
                    bestResult = result;
                }
            }
            if (bestResult is not null && best < MathF.Min(tMaxX, MathF.Min(tMaxY, tMaxZ)))
                break; // 已找到且之后不可能更近
            if (tMaxX < tMaxY)
            {
                if (tMaxX < tMaxZ)
                {
                    if (tMaxX > best) break;
                    ix += stepX; if (ix < 0 || ix >= _gridX) break; tMaxX += tDeltaX;
                }
                else
                {
                    if (tMaxZ > best) break;
                    iz += stepZ; if (iz < 0 || iz >= _gridZ) break; tMaxZ += tDeltaZ;
                }
            }
            else
            {
                if (tMaxY < tMaxZ)
                {
                    if (tMaxY > best) break;
                    iy += stepY; if (iy < 0 || iy >= _gridY) break; tMaxY += tDeltaY;
                }
                else
                {
                    if (tMaxZ > best) break;
                    iz += stepZ; if (iz < 0 || iz >= _gridZ) break; tMaxZ += tDeltaZ;
                }
            }
            if (MathF.Min(tMaxX, MathF.Min(tMaxY, tMaxZ)) > best) break;
        }

        return bestResult;
    }

    private static float BoundaryDistance(float origin, float dir, float gridMin, float cell, int cellIndex)
    {
        float boundary = gridMin + (cellIndex + (dir > 0 ? 1 : 0)) * cell;
        return (boundary - origin) / dir;
    }

    private PickResult? PickBruteForce(in Geometry.Ray ray, float maxDistance)
    {
        float best = maxDistance;
        PickResult? bestResult = null;
        for (int t = 0; t < _triangleCount; t++)
        {
            if (TestTriangle(ray, t, best, out var result) && result.Distance < best)
            {
                best = result.Distance;
                bestResult = result;
            }
        }
        return bestResult;
    }

    /// <summary>Möller–Trumbore 三角形求交（双面命中）。</summary>
    private bool TestTriangle(in Geometry.Ray ray, int t, float maxDistance, out PickResult result)
    {
        result = default;
        uint ia = _indices[t * 3], ib = _indices[t * 3 + 1], ic = _indices[t * 3 + 2];
        var a = Vertex(ia);
        var b = Vertex(ib);
        var c = Vertex(ic);

        var edge1 = b - a;
        var edge2 = c - a;
        var pvec = Vector3.Cross(ray.Direction, edge2);
        float det = Vector3.Dot(edge1, pvec);
        if (MathF.Abs(det) < 1e-12f) return false;
        float invDet = 1f / det;

        var tvec = ray.Origin - a;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f) return false;

        var qvec = Vector3.Cross(tvec, edge1);
        float v = Vector3.Dot(ray.Direction, qvec) * invDet;
        if (v < 0f || u + v > 1f) return false;

        float dist = Vector3.Dot(edge2, qvec) * invDet;
        if (dist <= 1e-4f || dist >= maxDistance) return false;

        var point = ray.PointAt(dist);
        // 重心插值法线
        var n = InterpolateNormal(ia, ib, ic, u, v);
        result = new PickResult(point, n, dist, t);
        return true;
    }

    private Vector3 InterpolateNormal(uint ia, uint ib, uint ic, float u, float v)
    {
        float w = 1f - u - v;
        if (_mesh.Normals.Length >= _positions.Length)
        {
            var na = NormalAt(ia);
            var nb = NormalAt(ib);
            var nc = NormalAt(ic);
            var n = na * w + nb * u + nc * v;
            if (n.LengthSquared() > 1e-12f) return Vector3.Normalize(n);
        }
        return Vector3.UnitY;
    }

    private Vector3 NormalAt(uint vertexIndex) => new(
        _mesh.Normals[(int)vertexIndex * 3],
        _mesh.Normals[(int)vertexIndex * 3 + 1],
        _mesh.Normals[(int)vertexIndex * 3 + 2]);
}
