using System.Numerics;
using Avalonia3DViewer.Core.Geometry;

namespace Avalonia3DViewer.Core.Camera;

/// <summary>标准视图方位。</summary>
public enum StandardView
{
    Isometric,
    Top,      // 俯视
    Front,    // 正视
    Right,    // 侧视（右）
}

/// <summary>视图布局：2D 画布（正交俯视）或 3D 视图（透视）。</summary>
public enum ViewLayout
{
    ThreeD,
    TwoD,
}

/// <summary>
/// 轨道相机（轨道/平移/缩放 + 指数阻尼），全部状态与变换均为纯托管计算，便于单元测试。
/// 坐标系：右手系，Y 向上，相机注视 -Z（与 OpenGL 一致）。
/// </summary>
public sealed class OrbitCamera
{
    // ---- 目标状态（输入直接修改） ----
    public Vector3 Target;
    public float Yaw;     // 弧度，绕 Y 轴
    public float Pitch;   // 弧度，绕 X 轴（向上为正）
    public float Distance;

    // ---- 阻尼后的当前状态 ----
    private Vector3 _curTarget;
    private float _curYaw;
    private float _curPitch;
    private float _curDistance;
    private bool _initialized;

    // ---- 配置 ----
    public float FieldOfView { get; set; } = MathF.PI / 4f; // 45°
    public float MinDistanceFactor { get; set; } = 0.02f;   // 相对模型半径
    public float MaxDistanceFactor { get; set; } = 40f;
    /// <summary>旋转灵敏度：弧度/像素（以 800px 视口高度为参考）。0.008 ≈ 整圈 785px。</summary>
    public float RotateSensitivity { get; set; } = 0.008f;
    /// <summary>平移灵敏度：相对 1:1 光标跟踪的倍率。</summary>
    public float PanSensitivity { get; set; } = 1.0f;
    /// <summary>缩放灵敏度：每滚轮格的距离指数因子（0.12 ≈ 每格缩放 11%）。</summary>
    public float ZoomSensitivity { get; set; } = 0.12f;
    public float Damping { get; set; } = 14f;               // 阻尼系数（越大越快稳定）

    private float _modelRadius = 100f;

    public OrbitCamera()
    {
        Reset(new Geometry.BoundingBox(new Vector3(-50, -50, -50), new Vector3(50, 50, 50)));
    }

    /// <summary>重置相机以适配指定包围盒。</summary>
    public void Reset(Geometry.BoundingBox bounds)
    {
        if (bounds.IsEmpty)
            bounds = new Geometry.BoundingBox(new Vector3(-50, -50, -50), new Vector3(50, 50, 50));

        _modelRadius = MathF.Max(bounds.Radius, 1e-3f);
        Target = bounds.Center;
        Yaw = MathF.PI * 0.25f;   // 东南方向
        Pitch = MathF.PI * 0.15f; // 略高
        Distance = FitDistance(_modelRadius);

        if (!_initialized)
        {
            _curTarget = Target;
            _curYaw = Yaw;
            _curPitch = Pitch;
            _curDistance = Distance;
            _initialized = true;
        }
    }

    /// <summary>把视图切换到标准方位（保持目标与距离）。</summary>
    public void SetStandardView(StandardView view)
    {
        switch (view)
        {
            case StandardView.Top:
                Yaw = 0; Pitch = MathF.PI / 2f - 0.001f; break;
            case StandardView.Front:
                Yaw = 0; Pitch = 0; break;
            case StandardView.Right:
                Yaw = -MathF.PI / 2f; Pitch = 0; break;
            default:
                Yaw = MathF.PI * 0.25f; Pitch = MathF.PI * 0.15f; break;
        }
        ClampPitch();
    }

    /// <summary>适配视图（无动画瞬移，用于初始加载）。</summary>
    public void SnapToTarget()
    {
        _curTarget = Target;
        _curYaw = Yaw;
        _curPitch = Pitch;
        _curDistance = Distance;
    }

    private float FitDistance(float radius)
    {
        // 使外接球刚好垂直放入视锥
        float vFov = FieldOfView;
        return radius / MathF.Sin(vFov * 0.5f);
    }

    public void FitToBounds(Geometry.BoundingBox bounds)
    {
        if (bounds.IsEmpty) return;
        _modelRadius = MathF.Max(bounds.Radius, 1e-3f);
        Target = bounds.Center;
        Distance = FitDistance(_modelRadius);
        ClampDistance();
    }

    private void ClampPitch()
    {
        // 允许接近 90°（2D 俯视需要），保留极小余量避免右向量退化
        float limit = MathF.PI / 2f - 0.002f;
        Pitch = Math.Clamp(Pitch, -limit, limit);
    }

    private void ClampDistance()
    {
        Distance = Math.Clamp(Distance, _modelRadius * MinDistanceFactor, _modelRadius * MaxDistanceFactor);
    }

    /// <summary>轨道旋转。dx/dy 为像素增量，viewportHeight 用于归一化灵敏度。</summary>
    public void Rotate(float dx, float dy, float viewportHeight)
    {
        // 以 800px 视口高度为参考的归一化灵敏度
        float scale = RotateSensitivity * (800f / MathF.Max(viewportHeight, 200f));
        Yaw -= dx * scale;
        Pitch += dy * scale;
        ClampPitch();
    }

    /// <summary>屏幕空间平移。</summary>
    public void Pan(float dx, float dy, float viewportHeight)
    {
        float scale = PanSensitivity * Distance * 2f * MathF.Tan(FieldOfView * 0.5f) / MathF.Max(viewportHeight, 200f);
        var right = RightDirection;
        var up = UpDirection;
        Target -= right * (dx * scale);
        Target += up * (dy * scale);
    }

    /// <summary>滚轮缩放（指数缩放，光标无关，保持简单稳定）。</summary>
    public void Zoom(float wheelDelta)
    {
        // 部分平台每格上报 ±120，归一化为 ±1
        float d = MathF.Abs(wheelDelta) > 10f ? wheelDelta / 120f : wheelDelta;
        Distance *= MathF.Exp(-d * ZoomSensitivity);
        ClampDistance();
    }

    /// <summary>按时间步推进阻尼。</summary>
    public void Update(float deltaSeconds)
    {
        if (!_initialized) { SnapToTarget(); return; }
        float k = 1f - MathF.Exp(-Damping * MathF.Min(deltaSeconds, 0.1f));
        _curTarget += (Target - _curTarget) * k;
        _curYaw += (Yaw - _curYaw) * k;
        _curPitch += (Pitch - _curPitch) * k;
        _curDistance += (Distance - _curDistance) * k;
    }

    /// <summary>阻尼是否仍在进行（用于决定是否继续请求渲染帧）。</summary>
    public bool IsSettling =>
        (_curTarget - Target).LengthSquared() > 1e-6f ||
        MathF.Abs(_curYaw - Yaw) > 1e-5f ||
        MathF.Abs(_curPitch - Pitch) > 1e-5f ||
        MathF.Abs(_curDistance - Distance) > _modelRadius * 1e-5f;

    // ---- 派生量 ----

    public Vector3 EyePosition => _curTarget + OffsetFromAngles(_curYaw, _curPitch, _curDistance);

    public Vector3 ForwardDirection => Vector3.Normalize(_curTarget - EyePosition);

    /// <summary>相机右向量。俯视（视线接近 ±Y）时退化为 +X，避免叉积为零导致 NaN。</summary>
    public Vector3 RightDirection
    {
        get
        {
            var r = Vector3.Cross(ForwardDirection, Vector3.UnitY);
            return r.LengthSquared() < 1e-10f ? Vector3.UnitX : Vector3.Normalize(r);
        }
    }

    public Vector3 UpDirection
    {
        get
        {
            var u = Vector3.Cross(RightDirection, ForwardDirection);
            return u.LengthSquared() < 1e-10f ? Vector3.UnitZ : Vector3.Normalize(u);
        }
    }

    private static Vector3 OffsetFromAngles(float yaw, float pitch, float distance)
    {
        float cp = MathF.Cos(pitch);
        return new Vector3(
            distance * cp * MathF.Sin(yaw),
            distance * MathF.Sin(pitch),
            distance * cp * MathF.Cos(yaw));
    }

    public Matrix4x4 ViewMatrix => CreateLookAtGl(EyePosition, _curTarget, Vector3.UnitY);

    /// <summary>
    /// 投影类型混合：0 = 正交（2D 画布），1 = 透视（3D 视图）。
    /// 中间值对两个投影矩阵做线性插值，得到视觉连续的"广义投影"过渡（w 从 1 渐变到视深）。
    /// </summary>
    public float ProjectionBlend { get; set; } = 1f;

    public bool IsOrthographic => ProjectionBlend <= 0.001f;

    /// <summary>
    /// 正交投影半高：取当前距离下透视视锥的可见高度，保证正交↔透视切换时取景范围一致（无缩放跳变）。
    /// </summary>
    public float OrthoHalfHeight => _curDistance * MathF.Tan(FieldOfView * 0.5f);

    public float OrthoHalfWidth => OrthoHalfHeight * MathF.Max(AspectRatio, 1e-3f);

    /// <summary>投影矩阵（OpenGL 约定，z∈[-1,1]；System.Numerics 的 CreatePerspective 为 DX 约定不可直接用）。</summary>
    public Matrix4x4 ProjectionMatrix
    {
        get
        {
            var persp = CreatePerspectiveGl(FieldOfView, AspectRatio, NearPlane, FarPlane);
            if (ProjectionBlend >= 0.999f) return persp;

            var ortho = CreateOrthographicGl(OrthoHalfWidth, OrthoHalfHeight, NearPlane, FarPlane);
            if (ProjectionBlend <= 0.001f) return ortho;

            return LerpMatrix(ortho, persp, Math.Clamp(ProjectionBlend, 0f, 1f));
        }
    }

    public Matrix4x4 ViewProjection => ViewMatrix * ProjectionMatrix;

    public float AspectRatio { get; set; } = 1.777f;

    public float NearPlane => MathF.Max(_curDistance * 0.0005f, _modelRadius * 1e-4f);

    public float FarPlane => _curDistance + _modelRadius * 20f + 10f;

    /// <summary>
    /// 屏幕像素坐标（左上原点，y 向下）→ 世界射线。
    /// 用视图投影矩阵的逆矩阵反投影近/远两点，正交、透视与混合投影统一适用。
    /// </summary>
    public Ray ScreenPointToRay(Vector2 screenPoint, Vector2 viewportSize)
    {
        float ndcX = screenPoint.X / MathF.Max(viewportSize.X, 1f) * 2f - 1f;
        float ndcY = 1f - screenPoint.Y / MathF.Max(viewportSize.Y, 1f) * 2f; // GL NDC y 向上

        if (Matrix4x4.Invert(ViewProjection, out var inv))
        {
            var near = Unproject(inv, ndcX, ndcY, -1f);
            var far = Unproject(inv, ndcX, ndcY, 1f);
            var dir = far - near;
            if (dir.LengthSquared() > 1e-12f)
                return new Geometry.Ray(near, dir);
        }

        // 退化回退：假设透视，用相机基向量构造方向
        float tanHalf = MathF.Tan(FieldOfView * 0.5f);
        var fallback = RightDirection * (ndcX * tanHalf * AspectRatio)
                       + UpDirection * (ndcY * tanHalf)
                       + ForwardDirection;
        return new Geometry.Ray(EyePosition, Vector3.Normalize(fallback));
    }

    private static Vector3 Unproject(in Matrix4x4 inverseViewProjection, float x, float y, float z)
    {
        var v = Vector4.Transform(new Vector4(x, y, z, 1f), inverseViewProjection);
        if (MathF.Abs(v.W) < 1e-9f) return new Vector3(v.X, v.Y, v.Z);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }

    /// <summary>世界坐标 → 屏幕像素坐标（用于标注文字覆盖层）。相机后方返回 null。</summary>
    public Vector2? ProjectToScreen(Vector3 worldPoint, Vector2 viewportSize)
    {
        var clip = Vector4.Transform(new Vector4(worldPoint, 1f), ViewProjection);
        if (clip.W <= 1e-6f) return null;
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        if (ndcX < -1.05f || ndcX > 1.05f || ndcY < -1.05f || ndcY > 1.05f) return null;
        return new Vector2(
            (ndcX * 0.5f + 0.5f) * viewportSize.X,
            (1f - (ndcY * 0.5f + 0.5f)) * viewportSize.Y);
    }

    // ---- 矩阵工厂（System.Numerics 为行向量约定，GLSL 按 uProj*uView*uModel 顺序使用） ----

    public static Matrix4x4 CreateLookAtGl(Vector3 eye, Vector3 target, Vector3 up)
    {
        var zAxis = Vector3.Normalize(eye - target);   // 相机 +Z（指向相机后方）
        var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
        var yAxis = Vector3.Cross(zAxis, xAxis);

        // System.Numerics 为行向量约定（v*M，平移位于第 4 行），基向量位于矩阵列中。
        // 上传 GL 时 transpose=false 即得 GLSL 所需的标准列向量视图矩阵。
        return new Matrix4x4(
            xAxis.X, yAxis.X, zAxis.X, 0f,
            xAxis.Y, yAxis.Y, zAxis.Y, 0f,
            xAxis.Z, yAxis.Z, zAxis.Z, 0f,
            -Vector3.Dot(xAxis, eye), -Vector3.Dot(yAxis, eye), -Vector3.Dot(zAxis, eye), 1f);
    }

    public static Matrix4x4 CreatePerspectiveGl(float fovY, float aspect, float near, float far)
    {
        float tanHalf = MathF.Tan(fovY * 0.5f);
        var m = new Matrix4x4();
        m.M11 = 1f / (tanHalf * aspect);
        m.M22 = 1f / tanHalf;
        m.M33 = (far + near) / (near - far);
        m.M34 = -1f;
        m.M43 = 2f * near * far / (near - far);
        return m;
    }

    /// <summary>
    /// 正交投影（OpenGL 约定，z∈[-1,1]，对称视锥）。
    /// 与透视矩阵同结构下线性插值即可得到连续的 2D↔3D 过渡。
    /// </summary>
    public static Matrix4x4 CreateOrthographicGl(float halfWidth, float halfHeight, float near, float far)
    {
        float hw = MathF.Max(halfWidth, 1e-4f);
        float hh = MathF.Max(halfHeight, 1e-4f);
        var m = new Matrix4x4();
        m.M11 = 1f / hw;
        m.M22 = 1f / hh;
        m.M33 = -2f / (far - near);
        m.M43 = -(far + near) / (far - near);
        m.M44 = 1f;
        return m;
    }

    /// <summary>逐元素插值两个投影矩阵（行向量约定，两个矩阵结构一致时结果仍为合法投影）。</summary>
    public static Matrix4x4 LerpMatrix(in Matrix4x4 a, in Matrix4x4 b, float t)
    {
        return new Matrix4x4(
            Lerp(a.M11, b.M11, t), Lerp(a.M12, b.M12, t), Lerp(a.M13, b.M13, t), Lerp(a.M14, b.M14, t),
            Lerp(a.M21, b.M21, t), Lerp(a.M22, b.M22, t), Lerp(a.M23, b.M23, t), Lerp(a.M24, b.M24, t),
            Lerp(a.M31, b.M31, t), Lerp(a.M32, b.M32, t), Lerp(a.M33, b.M33, t), Lerp(a.M34, b.M34, t),
            Lerp(a.M41, b.M41, t), Lerp(a.M42, b.M42, t), Lerp(a.M43, b.M43, t), Lerp(a.M44, b.M44, t));

        static float Lerp(float x, float y, float w) => x + (y - x) * w;
    }

    /// <summary>把指定半径的外接球刚好放入视锥所需的相机距离（2D/3D 预设共用）。</summary>
    public float ComputeFitDistance(float radius) =>
        MathF.Max(radius, 1e-3f) / MathF.Sin(FieldOfView * 0.5f);
}
