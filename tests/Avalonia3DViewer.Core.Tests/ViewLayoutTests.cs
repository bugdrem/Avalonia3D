using System.Numerics;
using Avalonia3DViewer.Core.Camera;
using Avalonia3DViewer.Core.Geometry;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>2D 画布（正交俯视）与 3D 视图（透视）切换的数学测试。</summary>
public class ViewLayoutTests
{
    private static readonly BoundingBox Box =
        new(new Vector3(-30, 0, -20), new Vector3(30, 40, 20));

    private static OrbitCamera CreateCamera(ViewLayout layout)
    {
        var cam = new OrbitCamera { AspectRatio = 1.6f };
        cam.FitToBounds(Box);
        cam.SnapToTarget();

        var preset = ViewTransitionMath.CreatePreset(layout, Box, cam.ComputeFitDistance(Box.Radius));
        ViewTransitionMath.Apply(cam, preset, preset, 1f);
        return cam;
    }

    [Fact]
    public void OrthographicMatrix_MapsFrustumCornersToNdc()
    {
        const float hw = 10f, hh = 5f, near = 1f, far = 100f;
        var m = OrbitCamera.CreateOrthographicGl(hw, hh, near, far);

        // System.Numerics 行向量约定：Vector4.Transform(v, m)
        var right = Vector4.Transform(new Vector4(hw, 0, 0, 1), m);
        var top = Vector4.Transform(new Vector4(0, hh, 0, 1), m);
        var nearPoint = Vector4.Transform(new Vector4(0, 0, -near, 1), m);
        var farPoint = Vector4.Transform(new Vector4(0, 0, -far, 1), m);

        Assert.Equal(1f, right.X, 4);
        Assert.Equal(1f, top.Y, 4);
        Assert.Equal(-1f, nearPoint.Z, 4);   // OpenGL 深度区间 [-1,1]
        Assert.Equal(1f, farPoint.Z, 4);
        Assert.Equal(1f, nearPoint.W, 4);    // 正交：w 恒为 1
    }

    [Fact]
    public void ProjectionBlend_EndpointsMatchPureProjections()
    {
        var cam = CreateCamera(ViewLayout.ThreeD);
        var persp = OrbitCamera.CreatePerspectiveGl(cam.FieldOfView, cam.AspectRatio, cam.NearPlane, cam.FarPlane);
        var ortho = OrbitCamera.CreateOrthographicGl(cam.OrthoHalfWidth, cam.OrthoHalfHeight, cam.NearPlane, cam.FarPlane);

        cam.ProjectionBlend = 1f;
        var actualPersp = cam.ProjectionMatrix;
        cam.ProjectionBlend = 0f;
        var actualOrtho = cam.ProjectionMatrix;

        Assert.Equal(persp.M34, actualPersp.M34, 5);     // 透视：-1
        Assert.Equal(0f, actualOrtho.M34, 5);            // 正交：0
        Assert.Equal(ortho.M11, actualOrtho.M11, 5);

        // 中间值应是两者的线性插值
        cam.ProjectionBlend = 0.5f;
        var mid = cam.ProjectionMatrix;
        Assert.Equal((ortho.M34 + persp.M34) * 0.5f, mid.M34, 5);
    }

    [Fact]
    public void TwoDPreset_LooksStraightDownWithPlanOrientation()
    {
        var cam = CreateCamera(ViewLayout.TwoD);

        // 视线朝下
        Assert.True(cam.ForwardDirection.Y < -0.999f, $"forward={cam.ForwardDirection}");

        // 屏幕右向 = +X，屏幕上向 = -Z（标准平面视图）
        Assert.True(Vector3.Dot(cam.RightDirection, Vector3.UnitX) > 0.999f,
            $"right={cam.RightDirection}");
        Assert.True(Vector3.Dot(cam.UpDirection, -Vector3.UnitZ) > 0.999f,
            $"up={cam.UpDirection}");

        // 基向量与矩阵不得出现 NaN
        AssertFinite(cam.RightDirection);
        AssertFinite(cam.UpDirection);
        AssertFinite(cam.ViewMatrix);
        AssertFinite(cam.ProjectionMatrix);
    }

    [Fact]
    public void ThreeDPreset_IsTiltedAndPerspective()
    {
        var cam = CreateCamera(ViewLayout.ThreeD);
        Assert.InRange(cam.Pitch, 0.05f, MathF.PI / 2f - 0.01f);
        Assert.Equal(1f, cam.ProjectionBlend, 4);
        Assert.True(cam.EyePosition.Y > cam.Target.Y, "3D 视图相机应高于目标点");
    }

    [Fact]
    public void EaseInOutCubic_IsSmoothAndMonotonic()
    {
        Assert.Equal(0f, ViewTransitionMath.EaseInOutCubic(0f), 5);
        Assert.Equal(1f, ViewTransitionMath.EaseInOutCubic(1f), 5);
        Assert.Equal(0.5f, ViewTransitionMath.EaseInOutCubic(0.5f), 5);

        float prev = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float v = ViewTransitionMath.EaseInOutCubic(i / 20f);
            Assert.True(v >= prev, "缓动应单调递增");
            Assert.InRange(v, 0f, 1f);
            prev = v;
        }

        // 缓入缓出：端点附近斜率小于中段
        float startSlope = ViewTransitionMath.EaseInOutCubic(0.1f) - ViewTransitionMath.EaseInOutCubic(0f);
        float midSlope = ViewTransitionMath.EaseInOutCubic(0.55f) - ViewTransitionMath.EaseInOutCubic(0.45f);
        Assert.True(midSlope > startSlope, "中段应比起点更快");
    }

    [Fact]
    public void LerpAngle_TakesShortestArc()
    {
        // 3.0 → -3.0 应跨过 π（约 0.28 弧度），而不是绕 2π-0.28
        float mid = ViewTransitionMath.LerpAngle(3.0f, -3.0f, 0.5f);
        float deltaToPi = MathF.Abs(MathF.Abs(mid) - MathF.PI);
        Assert.True(deltaToPi < 1e-3f, $"中点应接近 ±π，实际 {mid}");
    }

    [Fact]
    public void TwoDPreset_CentersModelOnScreen()
    {
        var cam = CreateCamera(ViewLayout.TwoD);
        var size = new Vector2(1600, 900);

        // 包围盒中心必须投影到视口正中（2D 画布以模型中心为基准）
        var center = cam.ProjectToScreen(Box.Center, size);
        Assert.NotNull(center);
        Assert.Equal(size.X * 0.5f, center!.Value.X, 1);
        Assert.Equal(size.Y * 0.5f, center.Value.Y, 1);

        // 模型四个角也应落在视口范围内
        var corners = new[]
        {
            new Vector3(Box.Min.X, Box.Center.Y, Box.Min.Z),
            new Vector3(Box.Max.X, Box.Center.Y, Box.Min.Z),
            new Vector3(Box.Min.X, Box.Center.Y, Box.Max.Z),
            new Vector3(Box.Max.X, Box.Center.Y, Box.Max.Z),
        };
        foreach (var corner in corners)
        {
            var p = cam.ProjectToScreen(corner, size);
            Assert.NotNull(p);
            Assert.InRange(p!.Value.X, 0f, size.X);
            Assert.InRange(p.Value.Y, 0f, size.Y);
        }
    }

    [Fact]
    public void OrthoRay_IsParallelAcrossScreen()
    {
        var cam = CreateCamera(ViewLayout.TwoD);
        var size = new Vector2(1600, 900);

        var r1 = cam.ScreenPointToRay(new Vector2(200, 200), size);
        var r2 = cam.ScreenPointToRay(new Vector2(1400, 700), size);

        // 正交投影：不同屏幕位置的射线方向相同
        Assert.True(Vector3.Dot(r1.Direction, r2.Direction) > 0.9999f,
            $"{r1.Direction} vs {r2.Direction}");
    }

    [Fact]
    public void OrthoRay_PassesThroughProjectedPoint()
    {
        var cam = CreateCamera(ViewLayout.TwoD);
        var size = new Vector2(1600, 900);
        var world = new Vector3(11f, 20f, -7f);

        var screen = cam.ProjectToScreen(world, size);
        Assert.NotNull(screen);

        var ray = cam.ScreenPointToRay(screen!.Value, size);
        // 点到射线的距离
        var toPoint = world - ray.Origin;
        var along = Vector3.Dot(toPoint, ray.Direction);
        var perpendicular = (toPoint - ray.Direction * along).Length();

        Assert.True(perpendicular < 0.05f, $"垂距 {perpendicular} 过大");
        Assert.True(along > 0f, "命中点应在射线正方向");
    }

    [Fact]
    public void PerspectiveRay_StillPassesThroughProjectedPoint()
    {
        var cam = CreateCamera(ViewLayout.ThreeD);
        var size = new Vector2(1280, 720);
        var world = new Vector3(5f, 30f, 8f);

        var screen = cam.ProjectToScreen(world, size);
        Assert.NotNull(screen);

        var ray = cam.ScreenPointToRay(screen!.Value, size);
        var toPoint = world - ray.Origin;
        var along = Vector3.Dot(toPoint, ray.Direction);
        var perpendicular = (toPoint - ray.Direction * along).Length();

        Assert.True(perpendicular < 0.05f, $"垂距 {perpendicular} 过大");
    }

    [Fact]
    public void BlendedProjection_ProducesFiniteTransforms()
    {
        var cam = CreateCamera(ViewLayout.ThreeD);
        foreach (float blend in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            cam.ProjectionBlend = blend;
            AssertFinite(cam.ProjectionMatrix);
            var clip = Vector4.Transform(new Vector4(cam.Target, 1f), cam.ViewProjection);
            AssertFinite(new Vector3(clip.X, clip.Y, clip.Z));
            Assert.True(MathF.Abs(clip.W) > 1e-4f, $"blend={blend} 时 w 退化");
        }
    }

    private static void AssertFinite(Vector3 v)
    {
        Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z), $"向量含 NaN/Inf: {v}");
    }

    private static void AssertFinite(Matrix4x4 m)
    {
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                float v = m[r, c];
                Assert.True(float.IsFinite(v), $"矩阵元素 ([{r},{c}]) 非有限值: {v}");
            }
        }
    }
}
