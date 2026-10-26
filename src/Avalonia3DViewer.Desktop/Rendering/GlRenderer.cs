using System.Numerics;
using Avalonia.OpenGL;
using Avalonia3DViewer.Core.Geometry;

namespace Avalonia3DViewer.Desktop.Rendering;

/// <summary>视图模式。</summary>
public enum ViewMode
{
    /// <summary>着色渲染（默认）。</summary>
    Shaded,
    /// <summary>线框（GL_LINES 去重边）。</summary>
    Wireframe,
    /// <summary>点云。</summary>
    Points,
}

/// <summary>单帧渲染状态（不可变快照，由 UI 线程构建，渲染线程读取）。</summary>
public readonly record struct FrameState(
    Matrix4x4 View,
    Matrix4x4 Proj,
    Vector3 Eye,
    Matrix4x4 Model,
    int Width,
    int Height,
    int TargetFb,
    ViewMode Mode,
    bool SsaaEnabled,
    Vector3 BackgroundTop,
    Vector3 BackgroundBottom);

/// <summary>标注标记点（位置 + 颜色，颜色已按选中状态处理）。</summary>
public readonly record struct MarkerVertex(Vector3 Position, Vector3 Color);

/// <summary>GlInterface 未暴露的 GL 常量。</summary>
internal static class Glx
{
    public const int GL_POINTS = 0x0000;
    public const int GL_LINES = 0x0001;
    public const int GL_TRIANGLES = 0x0004;
    public const int GL_UNSIGNED_INT = 0x1405;
    public const int GL_LEQUAL = 0x0203;
    public const int GL_PROGRAM_POINT_SIZE = 0x8862;
}

/// <summary>
/// OpenGL 渲染器：负责网格/线框/点云/网格地面/标注标记/测距线/坐标三角/背景渐变/SSAA。
/// 所有 GL 调用只发生在 <see cref="RenderFrame"/> 内（由 OpenGlControlBase 在持有上下文时调用）。
/// </summary>
public sealed class GlRenderer : IDisposable
{
    private readonly record struct GridParams(Vector3 Center, float Extent, float GroundY);

    private GlInterface _gl = null!;
    private GlslProfile _profile;
    private bool _initialized;
    private bool _disposed;

    private GlProgram? _meshProgram;
    private GlProgram? _lineProgram;
    private GlProgram? _pointProgram;
    private GlProgram? _bgProgram;
    private GlProgram? _blitProgram;

    // ---- 模型 ----
    private MeshData? _pendingMesh;
    private int _meshVao, _meshVbo, _meshEbo;
    private int _meshIndexCount;
    private int _edgeVao, _edgeVbo, _edgeEbo;
    private int _edgeIndexCount;
    private bool _meshUploaded;

    // ---- 地面网格 ----
    private GridParams? _pendingGrid;
    private int _gridVao, _gridVbo;
    private int _gridVertexCount;
    private bool _gridDirty;

    // ---- 标注标记（动态） ----
    private MarkerVertex[]? _pendingMarkers;
    private int _markerVao, _markerVbo;
    private int _markerCount;
    private bool _markersDirty;

    // ---- 测距线（动态） ----
    private float[]? _pendingMeasure; // pos(3)+color(3) 交替
    private int _measureVao, _measureVbo;
    private int _measureVertexCount;
    private bool _measureDirty;

    // ---- 坐标三角（每帧重建） ----
    private int _triadVao, _triadVbo;

    // ---- 全屏四边形（背景 / SSAA 解析） ----
    private int _quadVao, _quadVbo;

    // ---- SSAA 离屏目标 ----
    private int _fbo, _colorTex, _depthRb;
    private int _rtWidth, _rtHeight;
    private const float SsaaScale = 2f;

    public string RendererInfo { get; private set; } = string.Empty;
    public string GlVersionInfo { get; private set; } = string.Empty;

    /// <summary>（重新）初始化 GL 资源。可在上下文重建后重复调用。</summary>
    public void Init(GlInterface gl, GlVersion version)
    {
        if (_initialized) Dispose();

        _disposed = false; // 允许上下文重建后重新初始化
        _gl = gl;
        _profile = GlslPreprocessor.SelectProfile(version);

        RendererInfo = gl.GetString(GlConsts.GL_RENDERER) ?? "未知";
        GlVersionInfo = $"{version.Type} {version.Major}.{version.Minor} · {gl.GetString(GlConsts.GL_VERSION) ?? ""}";

        _meshProgram = CreateProgram(ShaderSources.MeshVertex, ShaderSources.MeshFragment,
            ("aPos", 0), ("aNormal", 1), ("aColor", 2));
        _lineProgram = CreateProgram(ShaderSources.LineVertex, ShaderSources.LineFragment,
            ("aPos", 0), ("aColor", 1));
        _pointProgram = CreateProgram(ShaderSources.PointVertex, ShaderSources.PointFragment,
            ("aPos", 0), ("aColor", 2));
        _bgProgram = CreateProgram(ShaderSources.BackgroundVertex, ShaderSources.BackgroundFragment,
            ("aPos", 0));
        _blitProgram = CreateProgram(ShaderSources.BlitVertex, ShaderSources.BlitFragment,
            ("aPos", 0));

        // 全屏四边形（两个三角形，NDC 坐标）
        _quadVao = gl.GenVertexArray();
        _quadVbo = gl.GenBuffer();
        gl.BindVertexArray(_quadVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _quadVbo);
        float[] quad =
        {
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f,
        };
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, quad, GlConsts.GL_STATIC_DRAW);
        int quadPos = _bgProgram!.Attrib("aPos");
        gl.EnableVertexAttribArray(quadPos);
        gl.VertexAttribPointer(quadPos, 2, GlConsts.GL_FLOAT, 0, 0, IntPtr.Zero);

        // 坐标三角缓冲（每帧更新）
        _triadVao = gl.GenVertexArray();
        _triadVbo = gl.GenBuffer();

        // 网格 / 标记 / 测距线缓冲
        _meshVao = gl.GenVertexArray(); _meshVbo = gl.GenBuffer(); _meshEbo = gl.GenBuffer();
        _edgeVao = gl.GenVertexArray(); _edgeVbo = gl.GenBuffer(); _edgeEbo = gl.GenBuffer();
        _gridVao = gl.GenVertexArray(); _gridVbo = gl.GenBuffer();
        _markerVao = gl.GenVertexArray(); _markerVbo = gl.GenBuffer();
        _measureVao = gl.GenVertexArray(); _measureVbo = gl.GenBuffer();

        gl.Enable(GlConsts.GL_DEPTH_TEST);
        gl.DepthFunc(Glx.GL_LEQUAL);
        gl.DepthMask(1);
        gl.Enable(Glx.GL_PROGRAM_POINT_SIZE); // 桌面 GL 需要显式启用以使用 gl_PointSize
        gl.ClearDepth(1f);

        _initialized = true;
    }

    private GlProgram CreateProgram(string vs, string fs, params (string name, int index)[] attribs)
    {
        var vertex = GlslPreprocessor.Prepare(vs, _profile);
        var fragment = GlslPreprocessor.Prepare(fs, _profile);
        return new GlProgram(_gl, vertex, fragment, attribs);
    }

    // ---------------------------------------------------------------- 场景数据提交

    /// <summary>提交新模型（实际 GPU 上传发生在下一帧）。</summary>
    public void SetMesh(MeshData mesh)
    {
        _pendingMesh = mesh;
        _meshUploaded = false;
    }

    /// <summary>提交地面网格参数。</summary>
    public void SetGrid(Vector3 center, float extent, float groundY)
    {
        _pendingGrid = new GridParams(center, extent, groundY);
        _gridDirty = true;
    }

    /// <summary>提交标注标记（不可变数组，渲染线程安全读取）。</summary>
    public void SetMarkers(MarkerVertex[] markers)
    {
        _pendingMarkers = markers;
        _markersDirty = true;
    }

    /// <summary>提交测距线段（pos3+color3 交替的不可变数组）。</summary>
    public void SetMeasureLines(float[] vertices)
    {
        _pendingMeasure = vertices;
        _measureDirty = true;
    }

    // ---------------------------------------------------------------- 帧渲染

    public void RenderFrame(in FrameState state)
    {
        if (!_initialized || _disposed) return;

        var gl = _gl;
        int renderScale = state.SsaaEnabled ? (int)SsaaScale : 1;
        int rtW = Math.Max(1, state.Width * renderScale);
        int rtH = Math.Max(1, state.Height * renderScale);

        // ---- 延迟上传 ----
        if (!_meshUploaded && _pendingMesh != null)
        {
            UploadMesh(_pendingMesh);
            _meshUploaded = true;
        }
        if (_gridDirty && _pendingGrid.HasValue)
        {
            UploadGrid(_pendingGrid.Value);
            _gridDirty = false;
        }
        if (_markersDirty)
        {
            UploadMarkers(_pendingMarkers);
            _markersDirty = false;
        }
        if (_measureDirty)
        {
            UploadMeasure(_pendingMeasure);
            _measureDirty = false;
        }

        // ---- 离屏目标 ----
        int targetFb;
        if (state.SsaaEnabled)
        {
            EnsureRenderTarget(rtW, rtH);
            targetFb = _fbo;
        }
        else
        {
            targetFb = state.TargetFb;
        }

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, targetFb);
        gl.Viewport(0, 0, rtW, rtH);

        // 背景（清屏 + 渐变）
        gl.ClearColor(state.BackgroundBottom.X, state.BackgroundBottom.Y, state.BackgroundBottom.Z, 1f);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);
        DrawBackground(state);

        // 地面网格
        if (_gridVertexCount > 0)
            DrawGrid(state);

        // 模型
        switch (state.Mode)
        {
            case ViewMode.Shaded:
                DrawMesh(state);
                break;
            case ViewMode.Wireframe:
                DrawEdges(state);
                break;
            case ViewMode.Points:
                DrawMeshPoints(state, renderScale);
                break;
        }

        // 测距线与标注标记
        if (_measureVertexCount > 0) DrawMeasure(state);
        if (_markerCount > 0) DrawMarkers(state, renderScale);

        // 坐标三角（屏幕空间，关闭深度）
        gl.Disable(GlConsts.GL_DEPTH_TEST);
        DrawTriad(state);
        gl.Enable(GlConsts.GL_DEPTH_TEST);

        // ---- SSAA 解析 ----
        if (state.SsaaEnabled)
        {
            gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, state.TargetFb);
            gl.Viewport(0, 0, state.Width, state.Height);
            DrawBlit();
        }

        // 结束时保持 state.TargetFb 绑定（Avalonia 期望的渲染目标）
        gl.Flush();
    }

    // ---------------------------------------------------------------- 绘制子过程

    private void DrawBackground(in FrameState state)
    {
        var gl = _gl;
        gl.Disable(GlConsts.GL_DEPTH_TEST);
        _bgProgram!.Use();
        _bgProgram.SetVec3("uTopColor", state.BackgroundTop);
        _bgProgram.SetVec3("uBottomColor", state.BackgroundBottom);
        gl.BindVertexArray(_quadVao);
        gl.DrawArrays(Glx.GL_TRIANGLES, 0, 6);
        gl.Enable(GlConsts.GL_DEPTH_TEST);
    }

    private void DrawMesh(in FrameState state)
    {
        if (_meshIndexCount == 0) return;
        var gl = _gl;
        _meshProgram!.Use();
        _meshProgram.SetMatrix("uModel", state.Model);
        _meshProgram.SetMatrix("uView", state.View);
        _meshProgram.SetMatrix("uProj", state.Proj);
        _meshProgram.SetVec3("uCameraPos", state.Eye);
        _meshProgram.SetFloat("uHighlight", 0f);
        gl.BindVertexArray(_meshVao);
        gl.DrawElements(Glx.GL_TRIANGLES, _meshIndexCount, Glx.GL_UNSIGNED_INT, IntPtr.Zero);
    }

    private void DrawEdges(in FrameState state)
    {
        if (_edgeIndexCount == 0) return;
        var gl = _gl;
        _lineProgram!.Use();
        SetupLineUniforms(state, depthBias: 0.0012f, useFade: 0f, model: state.Model);
        gl.BindVertexArray(_edgeVao);
        gl.DrawElements(Glx.GL_LINES, _edgeIndexCount, Glx.GL_UNSIGNED_INT, IntPtr.Zero);
    }

    private void DrawMeshPoints(in FrameState state, int renderScale)
    {
        if (_meshIndexCount == 0) return;
        var gl = _gl;
        _pointProgram!.Use();
        _pointProgram.SetMatrix("uModel", state.Model);
        _pointProgram.SetMatrix("uView", state.View);
        _pointProgram.SetMatrix("uProj", state.Proj);
        _pointProgram.SetFloat("uPointSize", 2.4f * renderScale);
        _pointProgram.SetFloat("uRing", 0f);
        // 网格 VAO 的 location 2 即顶点颜色
        gl.BindVertexArray(_meshVao);
        gl.DrawElements(Glx.GL_POINTS, _meshIndexCount, Glx.GL_UNSIGNED_INT, IntPtr.Zero);
    }

    private void DrawGrid(in FrameState state)
    {
        var gl = _gl;
        _lineProgram!.Use();
        float dist = MathF.Max(state.Eye.Length(), 1f);
        SetupLineUniforms(state, depthBias: 0f, useFade: 1f, fadeNear: dist * 0.35f, fadeFar: dist * 1.5f);
        gl.BindVertexArray(_gridVao);
        gl.DrawArrays(Glx.GL_LINES, 0, _gridVertexCount);
    }

    private void DrawMeasure(in FrameState state)
    {
        var gl = _gl;
        _lineProgram!.Use();
        SetupLineUniforms(state, depthBias: 0.0016f, useFade: 0f);
        gl.BindVertexArray(_measureVao);
        gl.DrawArrays(Glx.GL_LINES, 0, _measureVertexCount);
    }

    private void DrawMarkers(in FrameState state, int renderScale)
    {
        var gl = _gl;
        _pointProgram!.Use();
        _pointProgram.SetMatrix("uModel", Matrix4x4.Identity); // 标记已是世界坐标
        _pointProgram.SetMatrix("uView", state.View);
        _pointProgram.SetMatrix("uProj", state.Proj);
        _pointProgram.SetFloat("uPointSize", 17f * renderScale);
        _pointProgram.SetFloat("uRing", 1f);
        gl.BindVertexArray(_markerVao);
        gl.DrawArrays(Glx.GL_POINTS, 0, _markerCount);
    }

    private void DrawTriad(in FrameState state)
    {
        // 从视图矩阵提取相机基（行向量约定：基向量位于矩阵列中）
        var m = state.View;
        var right = Vector3.Normalize(new Vector3(m.M11, m.M21, m.M31));
        var up = Vector3.Normalize(new Vector3(m.M12, m.M22, m.M32));
        var back = Vector3.Normalize(new Vector3(m.M13, m.M23, m.M33));

        const float cx = 0.84f, cy = 0.80f;   // 屏幕空间位置（NDC）
        const float len = 0.11f;              // 轴长度
        const float halfW = 0.0042f;          // 轴半宽

        // 每条轴一个四边形（2 三角形）
        float[] verts = new float[3 * 6 * 6]; // 3 轴 × 6 顶点 × (pos3+color3)
        int o = 0;
        AddAxis(right, new Vector3(0.90f, 0.32f, 0.32f));
        AddAxis(up, new Vector3(0.28f, 0.72f, 0.34f));
        AddAxis(back, new Vector3(0.34f, 0.55f, 0.94f));

        var gl = _gl;
        _lineProgram!.Use();
        _lineProgram.SetMatrix("uModel", Matrix4x4.Identity); // 屏幕空间，不用模型变换
        _lineProgram.SetMatrix("uView", Matrix4x4.Identity);
        _lineProgram.SetMatrix("uProj", Matrix4x4.Identity);
        _lineProgram.SetVec3("uCameraPos", Vector3.Zero);
        _lineProgram.SetFloat("uScreenSpace", 1f);
        _lineProgram.SetFloat("uUseFade", 0f);
        _lineProgram.SetFloat("uDepthBias", 0f);
        _lineProgram.SetFloat("uFadeNear", 0f);
        _lineProgram.SetFloat("uFadeFar", 1f);

        gl.BindVertexArray(_triadVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _triadVbo);
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, verts, GlConsts.GL_STATIC_DRAW);
        int posLoc = _lineProgram.Attrib("aPos");
        int colLoc = _lineProgram.Attrib("aColor");
        gl.EnableVertexAttribArray(posLoc);
        gl.VertexAttribPointer(posLoc, 3, GlConsts.GL_FLOAT, 0, 24, IntPtr.Zero);
        gl.EnableVertexAttribArray(colLoc);
        gl.VertexAttribPointer(colLoc, 3, GlConsts.GL_FLOAT, 0, 24, new IntPtr(12));
        gl.DrawArrays(Glx.GL_TRIANGLES, 0, 18);

        void AddAxis(Vector3 dir, Vector3 color)
        {
            var perp = new Vector3(-dir.Y, dir.X, 0f);
            float pl = perp.Length();
            perp = pl < 1e-4f ? new Vector3(0f, -dir.Z, dir.Y) : perp / pl;

            var c = new Vector2(cx, cy);
            var e = new Vector2(cx, cy) + new Vector2(dir.X, dir.Y) * len;
            var w = perp * halfW;

            Span<Vector2> quad = stackalloc Vector2[4]
            {
                c + new Vector2(w.X, w.Y),
                c - new Vector2(w.X, w.Y),
                e - new Vector2(w.X, w.Y),
                e + new Vector2(w.X, w.Y),
            };

            // 三角形 1: 0,1,2   三角形 2: 0,2,3
            ReadOnlySpan<int> idx = stackalloc[] { 0, 1, 2, 0, 2, 3 };
            foreach (int i in idx)
            {
                verts[o++] = quad[i].X;
                verts[o++] = quad[i].Y;
                verts[o++] = 0f;
                verts[o++] = color.X;
                verts[o++] = color.Y;
                verts[o++] = color.Z;
            }
        }
    }

    private void DrawBlit()
    {
        var gl = _gl;
        _blitProgram!.Use();
        _blitProgram.SetInt("uTex", 0);
        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _colorTex);
        gl.Disable(GlConsts.GL_DEPTH_TEST);
        gl.BindVertexArray(_quadVao);
        gl.DrawArrays(Glx.GL_TRIANGLES, 0, 6);
        gl.Enable(GlConsts.GL_DEPTH_TEST);
    }

    private void SetupLineUniforms(in FrameState state, float depthBias, float useFade,
        float fadeNear = 0f, float fadeFar = 1f, Matrix4x4? model = null)
    {
        _lineProgram!.SetMatrix("uModel", model ?? Matrix4x4.Identity);
        _lineProgram!.SetMatrix("uView", state.View);
        _lineProgram.SetMatrix("uProj", state.Proj);
        _lineProgram.SetVec3("uCameraPos", state.Eye);
        _lineProgram.SetFloat("uScreenSpace", 0f);
        _lineProgram.SetFloat("uUseFade", useFade);
        _lineProgram.SetFloat("uDepthBias", depthBias);
        _lineProgram.SetFloat("uFadeNear", fadeNear);
        _lineProgram.SetFloat("uFadeFar", fadeFar);
    }

    // ---------------------------------------------------------------- 资源上传

    private void UploadMesh(MeshData mesh)
    {
        var gl = _gl;
        mesh.EnsureNormals();
        mesh.EnsureColors();

        // 交错顶点：pos(3) + normal(3) + color(3)
        int vcount = mesh.VertexCount;
        var interleaved = new float[vcount * 9];
        for (int v = 0; v < vcount; v++)
        {
            interleaved[v * 9 + 0] = mesh.Positions[v * 3 + 0];
            interleaved[v * 9 + 1] = mesh.Positions[v * 3 + 1];
            interleaved[v * 9 + 2] = mesh.Positions[v * 3 + 2];
            interleaved[v * 9 + 3] = mesh.Normals[v * 3 + 0];
            interleaved[v * 9 + 4] = mesh.Normals[v * 3 + 1];
            interleaved[v * 9 + 5] = mesh.Normals[v * 3 + 2];
            interleaved[v * 9 + 6] = mesh.Colors[v * 3 + 0];
            interleaved[v * 9 + 7] = mesh.Colors[v * 3 + 1];
            interleaved[v * 9 + 8] = mesh.Colors[v * 3 + 2];
        }

        gl.BindVertexArray(_meshVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _meshVbo);
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, interleaved, GlConsts.GL_STATIC_DRAW);
        gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, _meshEbo);
        UploadBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, mesh.Indices, GlConsts.GL_STATIC_DRAW);
        int p = _meshProgram!.Attrib("aPos");
        int n = _meshProgram!.Attrib("aNormal");
        int c = _meshProgram!.Attrib("aColor");
        gl.EnableVertexAttribArray(p);
        gl.VertexAttribPointer(p, 3, GlConsts.GL_FLOAT, 0, 36, IntPtr.Zero);
        gl.EnableVertexAttribArray(n);
        gl.VertexAttribPointer(n, 3, GlConsts.GL_FLOAT, 0, 36, new IntPtr(12));
        gl.EnableVertexAttribArray(c);
        gl.VertexAttribPointer(c, 3, GlConsts.GL_FLOAT, 0, 36, new IntPtr(24));
        _meshIndexCount = mesh.Indices.Length;

        // 线框边（pos + 深色）
        uint[] edges = mesh.BuildEdgeIndices();
        var edgeVerts = new float[(edges.Length / 2) * 6];
        var edgeColor = new Vector3(0.16f, 0.18f, 0.22f);
        for (int i = 0, o = 0; i < edges.Length; i += 2, o += 6)
        {
            uint vi = edges[i];
            edgeVerts[o + 0] = mesh.Positions[vi * 3 + 0];
            edgeVerts[o + 1] = mesh.Positions[vi * 3 + 1];
            edgeVerts[o + 2] = mesh.Positions[vi * 3 + 2];
            edgeVerts[o + 3] = edgeColor.X;
            edgeVerts[o + 4] = edgeColor.Y;
            edgeVerts[o + 5] = edgeColor.Z;
        }

        gl.BindVertexArray(_edgeVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _edgeVbo);
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, edgeVerts, GlConsts.GL_STATIC_DRAW);
        gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, _edgeEbo);
        UploadBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, edges, GlConsts.GL_STATIC_DRAW);
        int ep = _lineProgram!.Attrib("aPos");
        int ec = _lineProgram!.Attrib("aColor");
        gl.EnableVertexAttribArray(ep);
        gl.VertexAttribPointer(ep, 3, GlConsts.GL_FLOAT, 0, 24, IntPtr.Zero);
        gl.EnableVertexAttribArray(ec);
        gl.VertexAttribPointer(ec, 3, GlConsts.GL_FLOAT, 0, 24, new IntPtr(12));
        _edgeIndexCount = edges.Length;

        gl.BindVertexArray(0);
    }

    private void UploadGrid(GridParams grid)
    {
        var gl = _gl;
        float half = grid.Extent * 0.5f;
        float step = NiceStep(grid.Extent / 12f);

        var verts = new List<float>(4096);
        var minor = new Vector3(0.62f, 0.67f, 0.75f);
        var major = new Vector3(0.42f, 0.50f, 0.62f);

        int linesPerSide = (int)(half / step) + 1;
        for (int i = -linesPerSide; i <= linesPerSide; i++)
        {
            float x = grid.Center.X + i * step;
            float z = grid.Center.Z + i * step;
            bool isMajor = i % 5 == 0;
            var col = isMajor ? major : minor;

            if (MathF.Abs(x - grid.Center.X) < step * 0.01f)
            {
                // Z 方向中心轴（蓝）
                AddLine(x, grid.Center.Z - half, x, grid.Center.Z + half, new Vector3(0.30f, 0.48f, 0.85f));
            }
            else
            {
                AddLine(x, grid.Center.Z - half, x, grid.Center.Z + half, col);
            }

            if (MathF.Abs(z - grid.Center.Z) < step * 0.01f)
            {
                // X 方向中心轴（红）
                AddLine(grid.Center.X - half, z, grid.Center.X + half, z, new Vector3(0.82f, 0.30f, 0.30f));
            }
            else
            {
                AddLine(grid.Center.X - half, z, grid.Center.X + half, z, col);
            }
        }

        float[] arr = verts.ToArray();
        gl.BindVertexArray(_gridVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _gridVbo);
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, arr, GlConsts.GL_STATIC_DRAW);
        int p = _lineProgram!.Attrib("aPos");
        int c = _lineProgram!.Attrib("aColor");
        gl.EnableVertexAttribArray(p);
        gl.VertexAttribPointer(p, 3, GlConsts.GL_FLOAT, 0, 24, IntPtr.Zero);
        gl.EnableVertexAttribArray(c);
        gl.VertexAttribPointer(c, 3, GlConsts.GL_FLOAT, 0, 24, new IntPtr(12));
        _gridVertexCount = arr.Length / 6;
        gl.BindVertexArray(0);

        void AddLine(float x0, float z0, float x1, float z1, Vector3 color)
        {
            float y = grid.GroundY;
            verts.Add(x0); verts.Add(y); verts.Add(z0);
            verts.Add(color.X); verts.Add(color.Y); verts.Add(color.Z);
            verts.Add(x1); verts.Add(y); verts.Add(z1);
            verts.Add(color.X); verts.Add(color.Y); verts.Add(color.Z);
        }
    }

    private static float NiceStep(float raw)
    {
        if (raw <= 0) return 1f;
        float exp = MathF.Floor(MathF.Log10(raw));
        float f = raw / MathF.Pow(10f, exp);
        float nice = f switch
        {
            < 1.5f => 1f,
            < 3.5f => 2f,
            < 7.5f => 5f,
            _ => 10f,
        };
        return nice * MathF.Pow(10f, exp);
    }

    private void UploadMarkers(MarkerVertex[]? markers)
    {
        var gl = _gl;
        if (markers is null || markers.Length == 0)
        {
            _markerCount = 0;
            return;
        }

        var verts = new float[markers.Length * 6];
        for (int i = 0; i < markers.Length; i++)
        {
            verts[i * 6 + 0] = markers[i].Position.X;
            verts[i * 6 + 1] = markers[i].Position.Y;
            verts[i * 6 + 2] = markers[i].Position.Z;
            verts[i * 6 + 3] = markers[i].Color.X;
            verts[i * 6 + 4] = markers[i].Color.Y;
            verts[i * 6 + 5] = markers[i].Color.Z;
        }

        gl.BindVertexArray(_markerVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _markerVbo);
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, verts, GlConsts.GL_STATIC_DRAW);
        int p = _pointProgram!.Attrib("aPos");   // location 0
        int c = _pointProgram!.Attrib("aColor"); // location 2
        gl.EnableVertexAttribArray(p);
        gl.VertexAttribPointer(p, 3, GlConsts.GL_FLOAT, 0, 24, IntPtr.Zero);
        gl.EnableVertexAttribArray(c);
        gl.VertexAttribPointer(c, 3, GlConsts.GL_FLOAT, 0, 24, new IntPtr(12));
        _markerCount = markers.Length;
        gl.BindVertexArray(0);
    }

    private void UploadMeasure(float[]? vertices)
    {
        var gl = _gl;
        if (vertices is null || vertices.Length == 0)
        {
            _measureVertexCount = 0;
            return;
        }

        gl.BindVertexArray(_measureVao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _measureVbo);
        UploadBuffer(GlConsts.GL_ARRAY_BUFFER, vertices, GlConsts.GL_STATIC_DRAW);
        int p = _lineProgram!.Attrib("aPos");
        int c = _lineProgram!.Attrib("aColor");
        gl.EnableVertexAttribArray(p);
        gl.VertexAttribPointer(p, 3, GlConsts.GL_FLOAT, 0, 24, IntPtr.Zero);
        gl.EnableVertexAttribArray(c);
        gl.VertexAttribPointer(c, 3, GlConsts.GL_FLOAT, 0, 24, new IntPtr(12));
        _measureVertexCount = vertices.Length / 6;
        gl.BindVertexArray(0);
    }

    private void EnsureRenderTarget(int width, int height)
    {
        var gl = _gl;
        if (_fbo != 0 && _rtWidth == width && _rtHeight == height) return;

        if (_fbo != 0)
        {
            gl.DeleteFramebuffer(_fbo);
            gl.DeleteTexture(_colorTex);
            gl.DeleteRenderbuffer(_depthRb);
        }

        _colorTex = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _colorTex);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlConsts.GL_RGBA8, width, height, 0,
            GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, IntPtr.Zero);

        _depthRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(GlConsts.GL_RENDERBUFFER, _depthRb);
        gl.RenderbufferStorage(GlConsts.GL_RENDERBUFFER, GlConsts.GL_DEPTH_COMPONENT16, width, height);

        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, _fbo);
        gl.FramebufferTexture2D(GlConsts.GL_FRAMEBUFFER, GlConsts.GL_COLOR_ATTACHMENT0,
            GlConsts.GL_TEXTURE_2D, _colorTex, 0);
        gl.FramebufferRenderbuffer(GlConsts.GL_FRAMEBUFFER, GlConsts.GL_DEPTH_ATTACHMENT,
            GlConsts.GL_RENDERBUFFER, _depthRb);

        int status = gl.CheckFramebufferStatus(GlConsts.GL_FRAMEBUFFER);
        if (status != GlConsts.GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"离屏渲染目标创建失败 (status=0x{status:X})。");

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, 0);
        _rtWidth = width;
        _rtHeight = height;
    }

    private unsafe void UploadBuffer(int target, ReadOnlySpan<float> data, int usage)
    {
        fixed (float* p = data)
        {
            _gl.BufferData(target, new IntPtr(data.Length * sizeof(float)), new IntPtr(p), usage);
        }
    }

    private unsafe void UploadBuffer(int target, uint[] data, int usage)
    {
        fixed (uint* p = data)
        {
            _gl.BufferData(target, new IntPtr(data.Length * sizeof(uint)), new IntPtr(p), usage);
        }
    }

    // ---------------------------------------------------------------- 释放

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_initialized) return;

        var gl = _gl;
        _meshProgram?.Dispose();
        _lineProgram?.Dispose();
        _pointProgram?.Dispose();
        _bgProgram?.Dispose();
        _blitProgram?.Dispose();

        gl.DeleteBuffer(_meshVbo); gl.DeleteBuffer(_meshEbo); gl.DeleteVertexArray(_meshVao);
        gl.DeleteBuffer(_edgeVbo); gl.DeleteBuffer(_edgeEbo); gl.DeleteVertexArray(_edgeVao);
        gl.DeleteBuffer(_gridVbo); gl.DeleteVertexArray(_gridVao);
        gl.DeleteBuffer(_markerVbo); gl.DeleteVertexArray(_markerVao);
        gl.DeleteBuffer(_measureVbo); gl.DeleteVertexArray(_measureVao);
        gl.DeleteBuffer(_triadVbo); gl.DeleteVertexArray(_triadVao);
        gl.DeleteBuffer(_quadVbo); gl.DeleteVertexArray(_quadVao);

        if (_fbo != 0)
        {
            gl.DeleteFramebuffer(_fbo);
            gl.DeleteTexture(_colorTex);
            gl.DeleteRenderbuffer(_depthRb);
            _fbo = 0;
        }

        _initialized = false;
    }
}
