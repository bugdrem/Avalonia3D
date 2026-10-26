using System.Numerics;
using Avalonia3DViewer.Core.Geometry;

namespace Avalonia3DViewer.Core.Scene;

/// <summary>
/// 模型的世界坐标变换（当前支持平移偏移，工业打标场景的「图形坐标」）。
/// 网格数据以模型局部坐标上传 GPU；渲染与拾取通过本类型在局部/世界坐标之间转换：
/// - 渲染：<see cref="Matrix"/> 作为 uModel 上传；
/// - 拾取：世界射线经 <see cref="ToLocalRay"/> 转到局部空间；
/// - 标注：命中点经 <see cref="ToWorld"/> 转回世界坐标保存。
/// </summary>
public sealed class ModelTransform
{
    private Vector3 _offset;

    /// <summary>当前世界坐标偏移（毫米）。</summary>
    public Vector3 Offset
    {
        get => _offset;
        private set
        {
            _offset = value;
            Matrix = Matrix4x4.CreateTranslation(value);
        }
    }

    /// <summary>模型矩阵（平移）。偏移变化时同步更新。</summary>
    public Matrix4x4 Matrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>是否位于原始坐标（零偏移）。</summary>
    public bool IsIdentity => _offset == Vector3.Zero;

    /// <summary>设置绝对偏移，返回相对上一值的位移量。</summary>
    public Vector3 Set(Vector3 offset)
    {
        var delta = offset - _offset;
        Offset = offset;
        return delta;
    }

    /// <summary>按增量平移，返回位移量。</summary>
    public Vector3 Translate(Vector3 delta)
    {
        Offset = _offset + delta;
        return delta;
    }

    /// <summary>复位到原始坐标，返回复位前的位移量。</summary>
    public Vector3 Reset()
    {
        var delta = -_offset;
        Offset = Vector3.Zero;
        return delta;
    }

    /// <summary>世界射线 → 模型局部空间射线（拾取用）。</summary>
    public Ray ToLocalRay(in Ray worldRay) =>
        new(worldRay.Origin - _offset, worldRay.Direction);

    /// <summary>模型局部点 → 世界坐标。</summary>
    public Vector3 ToWorld(Vector3 localPoint) => localPoint + _offset;

    /// <summary>世界点 → 模型局部坐标。</summary>
    public Vector3 ToLocal(Vector3 worldPoint) => worldPoint - _offset;

    /// <summary>坐标文本（UI 显示）。</summary>
    public string ToText() =>
        $"X {_offset.X:F2}   Y {_offset.Y:F2}   Z {_offset.Z:F2} mm";
}
