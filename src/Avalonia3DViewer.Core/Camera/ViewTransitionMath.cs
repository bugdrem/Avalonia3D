using System.Numerics;
using Avalonia3DViewer.Core.Geometry;

namespace Avalonia3DViewer.Core.Camera;

/// <summary>相机预设（2D 画布 / 3D 视图的端点状态）。</summary>
public readonly record struct ViewPreset(
    float Yaw,
    float Pitch,
    float Distance,
    Vector3 Target,
    float ProjectionBlend);

/// <summary>
/// 2D/3D 视图切换的纯数学部分：预设计算、缓动、角度/向量插值。
/// 独立于 UI 与渲染，便于单元测试（AOT 安全，无反射）。
/// </summary>
public static class ViewTransitionMath
{
    /// <summary>2D 画布默认俯仰：接近正俯视，保留极小余量避免相机基向量退化。</summary>
    public const float TwoDPitch = MathF.PI / 2f - 0.002f;

    /// <summary>2D 画布默认偏航：使屏幕右向 = 世界 +X（标准平面视图）。</summary>
    public const float TwoDYaw = 0f;

    /// <summary>3D 视图默认偏航（等轴测方位）。</summary>
    public const float ThreeDYaw = MathF.PI * 0.25f;

    /// <summary>3D 视图默认俯仰。</summary>
    public const float ThreeDPitch = MathF.PI * 0.15f;

    /// <summary>过渡时长（秒）。</summary>
    public const float DefaultDuration = 0.42f;

    /// <summary>
    /// 计算目标布局的相机预设。包围盒为空时使用默认场景尺度。
    /// </summary>
    public static ViewPreset CreatePreset(ViewLayout layout, in BoundingBox bounds, float fitDistance)
    {
        var center = bounds.IsEmpty ? Vector3.Zero : bounds.Center;
        float distance = MathF.Max(fitDistance, 1e-3f);

        return layout == ViewLayout.TwoD
            ? new ViewPreset(TwoDYaw, TwoDPitch, distance, center, 0f)
            : new ViewPreset(ThreeDYaw, ThreeDPitch, distance, center, 1f);
    }

    /// <summary>三次缓入缓出（0→1）。</summary>
    public static float EaseInOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f;
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    /// <summary>角度插值（取最短弧，避免绕远路转圈）。</summary>
    public static float LerpAngle(float a, float b, float t)
    {
        float delta = b - a;
        while (delta > MathF.PI) delta -= 2f * MathF.PI;
        while (delta < -MathF.PI) delta += 2f * MathF.PI;
        return a + delta * t;
    }

    /// <summary>按缓动进度把相机推向目标预设。</summary>
    public static void Apply(OrbitCamera camera, in ViewPreset from, in ViewPreset to, float eased)
    {
        camera.Yaw = LerpAngle(from.Yaw, to.Yaw, eased);
        camera.Pitch = Lerp(from.Pitch, to.Pitch, eased);
        camera.Distance = Lerp(from.Distance, to.Distance, eased);
        camera.Target = Lerp(from.Target, to.Target, eased);
        camera.ProjectionBlend = Lerp(from.ProjectionBlend, to.ProjectionBlend, eased);
        // 过渡由缓动曲线驱动，绕过相机自身的阻尼（否则会出现双重平滑、拖尾）
        camera.SnapToTarget();
    }

    /// <summary>从当前相机状态取出预设（作为过渡起点）。</summary>
    public static ViewPreset Capture(OrbitCamera camera) => new(
        camera.Yaw,
        camera.Pitch,
        camera.Distance,
        camera.Target,
        camera.ProjectionBlend);
}
