using System.Numerics;
using Avalonia3DViewer.Core.Camera;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>轨道相机与矩阵数学测试。</summary>
public class OrbitCameraTests
{
    [Fact]
    public void PerspectiveMatrix_MapsNearFarToNdcRange()
    {
        var proj = OrbitCamera.CreatePerspectiveGl(MathF.PI / 4f, 1.5f, 1f, 100f);

        // 近平面上的点 (0,0,-1) → NDC z = -1
        var nearClip = Vector4.Transform(new Vector4(0, 0, -1, 1), proj);
        Assert.Equal(-1f, nearClip.Z / nearClip.W, 3);

        // 远平面上的点 (0,0,-100) → NDC z = +1
        var farClip = Vector4.Transform(new Vector4(0, 0, -100, 1), proj);
        Assert.Equal(1f, farClip.Z / farClip.W, 3);

        // 视线中心点应落在 NDC 原点（x/y），z 按双曲线映射（中点约 0.98）
        var centerClip = Vector4.Transform(new Vector4(0, 0, -50, 1), proj);
        Assert.Equal(0f, centerClip.X / centerClip.W, 3);
        Assert.Equal(0f, centerClip.Y / centerClip.W, 3);
        Assert.Equal(0.9798f, centerClip.Z / centerClip.W, 2);
    }

    [Fact]
    public void LookAt_ProducesViewMatrixLookingDownMinusZ()
    {
        var view = OrbitCamera.CreateLookAtGl(new Vector3(0, 0, 100), Vector3.Zero, Vector3.UnitY);

        // 目标点应变换到相机前方 -Z 方向
        var target = Vector4.Transform(new Vector4(0, 0, 0, 1), view);
        Assert.Equal(0f, target.X, 4);
        Assert.Equal(0f, target.Y, 4);
        Assert.True(target.Z < 0, "目标应在相机前方（-Z）");
    }

    [Fact]
    public void ScreenPointToRay_PassesThroughCameraEye()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-50, -50, -50), new Vector3(50, 50, 50)));
        camera.SnapToTarget();
        camera.AspectRatio = 1.6f;

        // 屏幕中心 → 射线应沿相机注视方向
        var centerRay = camera.ScreenPointToRay(new Vector2(320, 240), new Vector2(640, 480));
        Assert.True(Vector3.Dot(camera.ForwardDirection, centerRay.Direction) > 0.999f,
            "屏幕中心射线应沿相机注视方向");
        Assert.Equal(camera.EyePosition, centerRay.Origin);

        // 左上角 → 方向偏左偏上
        var cornerRay = camera.ScreenPointToRay(new Vector2(0, 0), new Vector2(640, 480));
        Assert.True(Vector3.Dot(camera.RightDirection, cornerRay.Direction) < 0f, "左上角应偏向左侧");
        Assert.True(Vector3.Dot(camera.UpDirection, cornerRay.Direction) > 0f, "左上角应偏向上侧");
    }

    [Fact]
    public void ProjectToScreen_RoundTripsWithRay()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-30, -10, -30), new Vector3(30, 50, 30)));
        camera.SnapToTarget();
        camera.AspectRatio = 16f / 9f;

        var world = new Vector3(12f, 5f, -20f);
        var screen = camera.ProjectToScreen(world, new Vector2(1600, 900));
        Assert.NotNull(screen);

        var ray = camera.ScreenPointToRay(screen.Value, new Vector2(1600, 900));

        // 射线方向与世界点到相机的连线应平行
        var toPoint = world - camera.EyePosition;
        var dot = Vector3.Dot(Vector3.Normalize(toPoint), ray.Direction);
        Assert.True(dot > 0.999f, $"投影/射线往返不一致: dot={dot}");
    }

    [Fact]
    public void ProjectToScreen_ReturnsNullBehindCamera()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-10, -10, -10), new Vector3(10, 10, 10)));
        camera.SnapToTarget();

        var behind = camera.EyePosition + new Vector3(0, 0, 500); // 相机后方
        Assert.Null(camera.ProjectToScreen(behind, new Vector2(800, 600)));
    }

    [Fact]
    public void Rotate_PitchIsClamped()
    {
        var camera = new OrbitCamera();
        camera.Rotate(0, 100000f, 800f); // 极端下移
        Assert.True(camera.Pitch < MathF.PI / 2f);
        camera.Rotate(0, -100000f, 800f);
        Assert.True(camera.Pitch > -MathF.PI / 2f);
    }

    [Fact]
    public void Zoom_IsClampedToModelRadius()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-10, -10, -10), new Vector3(10, 10, 10)));
        camera.Zoom(100000f); // 疯狂放大
        Assert.True(camera.Distance > 0.1f);

        camera.Zoom(-100000f); // 疯狂缩小
        Assert.True(camera.Distance < 10000f);
    }

    [Fact]
    public void Update_DampingConvergesAndSettles()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-20, -20, -20), new Vector3(20, 20, 20)));
        camera.SnapToTarget();

        camera.Yaw += 1.0f;
        Assert.True(camera.IsSettling);

        for (int i = 0; i < 600; i++)
        {
            camera.Update(1f / 60f);
        }
        Assert.False(camera.IsSettling);
    }

    [Fact]
    public void Rotate_SensitivityIsReasonable()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-50, -50, -50), new Vector3(50, 50, 50)));
        camera.SnapToTarget();

        float yaw0 = camera.Yaw;
        camera.Rotate(100f, 0f, 800f); // 水平拖拽 100px
        float delta = MathF.Abs(camera.Yaw - yaw0);
        Assert.True(delta is > 0.3f and < 1.5f,
            $"100px 水平拖拽应旋转 0.3~1.5 弧度，实际 {delta:F3}");
    }

    [Fact]
    public void Zoom_PerNotchIsReasonable()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-50, -50, -50), new Vector3(50, 50, 50)));
        camera.SnapToTarget();

        float d0 = camera.Distance;
        camera.Zoom(1f); // 一个滚轮格
        float ratio = camera.Distance / d0;
        Assert.True(ratio is > 0.80f and < 0.95f,
            $"每滚轮格应缩放 5%~20%，实际 {ratio:P1}");

        // ±120 的原始增量应归一化为同等效果
        float d1 = camera.Distance;
        camera.Zoom(120f);
        float ratio2 = camera.Distance / d1;
        Assert.True(ratio2 is > 0.80f and < 0.95f,
            $"±120 增量应归一化，实际 {ratio2:P1}");
    }

    [Fact]
    public void Pan_MovesTargetInCameraPlane()
    {
        var camera = new OrbitCamera();
        camera.FitToBounds(new Geometry.BoundingBox(new Vector3(-50, -50, -50), new Vector3(50, 50, 50)));
        camera.SnapToTarget();

        var target0 = camera.Target;
        float dist0 = camera.Distance;
        camera.Pan(50f, 0f, 800f); // 水平平移 50px

        Assert.NotEqual(target0, camera.Target);
        Assert.Equal(dist0, camera.Distance); // 平移不改变距离
        Assert.Equal(0f, (camera.Target - target0).Y, 3); // 水平平移不改变高度
    }
}
