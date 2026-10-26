using System.Numerics;
using Avalonia.OpenGL;

namespace Avalonia3DViewer.Desktop.Rendering;

/// <summary>
/// 着色器 Program 包装：编译、链接、uniform/attribute 位置缓存。
/// 注意 GlInterface 仅暴露 Uniform1f / Uniform1i / UniformMatrix4fv，
/// 传递 vec3 统一变量时使用 <see cref="SetVec3"/>（编码进矩阵第 4 行）。
/// </summary>
internal sealed class GlProgram : IDisposable
{
    private readonly GlInterface _gl;
    private readonly Dictionary<string, int> _uniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _attribs = new(StringComparer.Ordinal);

    public int Handle { get; }

    public GlProgram(GlInterface gl, string vertexSource, string fragmentSource, params (string name, int index)[] attribLocations)
    {
        _gl = gl;

        int vs = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        string? vsError = gl.CompileShaderAndGetError(vs, vertexSource);
        if (vsError != null)
            throw new InvalidOperationException($"顶点着色器编译失败: {vsError}");

        int fs = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        string? fsError = gl.CompileShaderAndGetError(fs, fragmentSource);
        if (fsError != null)
            throw new InvalidOperationException($"片元着色器编译失败: {fsError}");

        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vs);
        gl.AttachShader(Handle, fs);
        foreach (var (name, index) in attribLocations)
            gl.BindAttribLocationString(Handle, index, name);

        string? linkError = gl.LinkProgramAndGetError(Handle);
        if (linkError != null)
            throw new InvalidOperationException($"着色器链接失败: {linkError}");

        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    public void Use() => _gl.UseProgram(Handle);

    public int Uniform(string name)
    {
        if (!_uniforms.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocationString(Handle, name);
            _uniforms[name] = loc;
        }
        return loc;
    }

    public int Attrib(string name)
    {
        if (!_attribs.TryGetValue(name, out int loc))
        {
            loc = _gl.GetAttribLocationString(Handle, name);
            _attribs[name] = loc;
        }
        return loc;
    }

    public void SetFloat(string name, float value)
    {
        int loc = Uniform(name);
        if (loc >= 0) _gl.Uniform1f(loc, value);
    }

    public void SetInt(string name, int value)
    {
        int loc = Uniform(name);
        if (loc >= 0) _gl.Uniform1i(loc, value);
    }

    public void SetMatrix(string name, Matrix4x4 value)
    {
        int loc = Uniform(name);
        if (loc >= 0)
        {
            unsafe
            {
                _gl.UniformMatrix4fv(loc, 1, false, &value);
            }
        }
    }

    /// <summary>
    /// 通过 uniform mat4 的第 4 行（GL 视角的第 4 列）传递 vec3。
    /// </summary>
    public void SetVec3(string name, Vector3 value)
    {
        int loc = Uniform(name);
        if (loc < 0) return;
        var m = Matrix4x4.Identity;
        m.M41 = value.X;
        m.M42 = value.Y;
        m.M43 = value.Z;
        unsafe
        {
            _gl.UniformMatrix4fv(loc, 1, false, &m);
        }
    }

    public void Dispose()
    {
        _gl.DeleteProgram(Handle);
    }
}
