using System.Numerics;
using Avalonia3DViewer.Core.Annotations;
using Avalonia3DViewer.Core.Geometry;
using Avalonia3DViewer.Core.Scene;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>模型坐标变换（拖动改坐标的核心数学）测试。</summary>
public class ModelTransformTests
{
    [Fact]
    public void Initial_IsIdentityAtOrigin()
    {
        var t = new ModelTransform();
        Assert.Equal(Vector3.Zero, t.Offset);
        Assert.True(t.IsIdentity);
        Assert.Equal(Matrix4x4.Identity, t.Matrix);
    }

    [Fact]
    public void Translate_AccumulatesOffset()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(10, 0, 0));
        t.Translate(new Vector3(0, 5, -3));
        Assert.Equal(new Vector3(10, 5, -3), t.Offset);
        Assert.False(t.IsIdentity);
    }

    [Fact]
    public void Set_ReturnsDeltaFromPrevious()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(4, 0, 0));
        var delta = t.Set(new Vector3(1, 2, 3));
        Assert.Equal(new Vector3(-3, 2, 3), delta);
        Assert.Equal(new Vector3(1, 2, 3), t.Offset);
    }

    [Fact]
    public void Reset_ReturnsNegatedOffset_AndRestoresIdentity()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(-7, 8, 9));
        var delta = t.Reset();
        Assert.Equal(new Vector3(7, -8, -9), delta);
        Assert.True(t.IsIdentity);
    }

    [Fact]
    public void Matrix_IsPureTranslation()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(3, -4, 5));
        var m = t.Matrix;
        // System.Numerics 行向量约定：平移量位于第 4 行（M41/M42/M43）。
        // 上传 GL 时（transpose=false）平移量落在 GL 矩阵第 4 列，GLSL 的 uModel*vec4(p,1) 正确平移。
        Assert.Equal(new Vector3(3, -4, 5), new Vector3(m.M41, m.M42, m.M43));
        // 3x3 部分为单位矩阵（无旋转/缩放）
        Assert.Equal(1f, m.M11); Assert.Equal(0f, m.M12); Assert.Equal(0f, m.M13);
        Assert.Equal(0f, m.M21); Assert.Equal(1f, m.M22); Assert.Equal(0f, m.M23);
        Assert.Equal(0f, m.M31); Assert.Equal(0f, m.M32); Assert.Equal(1f, m.M33);
    }

    [Fact]
    public void ToWorld_ToLocal_RoundTrip()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(12.5f, -3.25f, 7f));
        var local = new Vector3(1, 2, 3);
        var world = t.ToWorld(local);
        Assert.Equal(local + t.Offset, world);
        Assert.Equal(local, t.ToLocal(world));
    }

    [Fact]
    public void ToLocalRay_ShiftsOriginOnly()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(100, 0, 0));
        var worldRay = new Ray(new Vector3(200, 0, 0), new Vector3(-1, 0, 0));
        var local = t.ToLocalRay(worldRay);
        Assert.Equal(new Vector3(100, 0, 0), local.Origin);
        Assert.Equal(new Vector3(-1, 0, 0), local.Direction);
    }

    [Fact]
    public void PickingConsistency_RayAtWorldPosition_MatchesLocalRayAtOrigin()
    {
        // 模型被移动后，指向世界位置的射线应与未移动时指向原点的射线命中同一表面
        var t = new ModelTransform();
        var shift = new Vector3(50, 0, 0);
        t.Translate(shift);

        var worldRay = new Ray(new Vector3(50, 0, 30), new Vector3(0, 0, -1));
        var local = t.ToLocalRay(worldRay);

        var expectedLocal = new Ray(new Vector3(0, 0, 30), new Vector3(0, 0, -1));
        Assert.Equal(expectedLocal.Origin, local.Origin);
        Assert.Equal(expectedLocal.Direction, local.Direction);
    }

    [Fact]
    public void ToText_ContainsComponents()
    {
        var t = new ModelTransform();
        t.Translate(new Vector3(1.234f, -5.678f, 9.01f));
        var text = t.ToText();
        Assert.Contains("X 1.23", text);
        Assert.Contains("Y -5.68", text);
        Assert.Contains("Z 9.01", text);
    }
}

/// <summary>标注随模型整体移动的测试。</summary>
public class AnnotationTranslationTests
{
    [Fact]
    public void TranslateAll_MovesEveryAnnotation()
    {
        var col = new AnnotationCollection();
        col.Add(new Vector3(0, 0, 0), Vector3.UnitY);
        col.Add(new Vector3(10, 0, 0), Vector3.UnitY);

        col.TranslateAll(new Vector3(5, 0, 0));

        Assert.Equal(new Vector3(5, 0, 0), col.Items[0].Position);
        Assert.Equal(new Vector3(15, 0, 0), col.Items[1].Position);
    }

    [Fact]
    public void TranslateAll_PreservesDistances()
    {
        var col = new AnnotationCollection();
        col.Add(new Vector3(0, 0, 0), Vector3.UnitY);
        col.Add(new Vector3(3, 4, 0), Vector3.UnitY);

        float before = col.Measurements[0].Distance;
        col.TranslateAll(new Vector3(-20, 100, 7));
        float after = col.Measurements[0].Distance;

        Assert.Equal(5f, before, 3);
        Assert.Equal(before, after, 4);
    }

    [Fact]
    public void TranslateAll_Zero_IsNoOp()
    {
        var col = new AnnotationCollection();
        col.Add(new Vector3(1, 2, 3), Vector3.UnitY);
        col.TranslateAll(Vector3.Zero);
        Assert.Equal(new Vector3(1, 2, 3), col.Items[0].Position);
    }
}
