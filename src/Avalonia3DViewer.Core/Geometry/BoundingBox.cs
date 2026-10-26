using System.Numerics;

namespace Avalonia3DViewer.Core.Geometry;

/// <summary>
/// 轴对齐包围盒（单位：毫米，与工业模型惯例一致）。
/// </summary>
public sealed class BoundingBox
{
    public Vector3 Min;
    public Vector3 Max;

    public BoundingBox()
    {
        Min = new Vector3(float.MaxValue);
        Max = new Vector3(float.MinValue);
    }

    public BoundingBox(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public static readonly BoundingBox Empty = new();

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;

    /// <summary>外接球半径（用于相机适配）。</summary>
    public float Radius => 0.5f * (Max - Min).Length();

    public void Include(Vector3 point)
    {
        Min = Vector3.Min(Min, point);
        Max = Vector3.Max(Max, point);
    }

    public void Include(BoundingBox other)
    {
        if (other.IsEmpty) return;
        Include(other.Min);
        Include(other.Max);
    }

    public void Reset()
    {
        Min = new Vector3(float.MaxValue);
        Max = new Vector3(float.MinValue);
    }

    /// <summary>点是否在包围盒内（含边界）。</summary>
    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    /// <summary>射线与包围盒求交（Slab 方法），返回入射距离或 null。</summary>
    public float? IntersectRay(in Ray ray, float maxDistance)
    {
        float tMin = 0f;
        float tMax = maxDistance;
        var origin = ray.Origin;
        var dir = ray.Direction;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis switch { 0 => origin.X, 1 => origin.Y, _ => origin.Z };
            float d = axis switch { 0 => dir.X, 1 => dir.Y, _ => dir.Z };
            float lo = axis switch { 0 => Min.X, 1 => Min.Y, _ => Min.Z };
            float hi = axis switch { 0 => Max.X, 1 => Max.Y, _ => Max.Z };

            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) return null;
                continue;
            }

            float inv = 1f / d;
            float t1 = (lo - o) * inv;
            float t2 = (hi - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return null;
        }

        return tMin;
    }
}

/// <summary>
/// 射线（起点已归一化方向）。
/// </summary>
public readonly struct Ray
{
    public readonly Vector3 Origin;
    public readonly Vector3 Direction;

    public Ray(Vector3 origin, Vector3 direction)
    {
        Origin = origin;
        Direction = Vector3.Normalize(direction);
    }

    public Vector3 PointAt(float t) => Origin + Direction * t;
}
