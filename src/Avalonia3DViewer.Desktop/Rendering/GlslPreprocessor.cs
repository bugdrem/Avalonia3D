namespace Avalonia3DViewer.Desktop.Rendering;

/// <summary>目标 GLSL 版本档位。</summary>
public enum GlslProfile
{
    /// <summary>桌面 GL 3.3+（Windows/Linux）。</summary>
    Glsl330,
    /// <summary>桌面 GL 3.2（macOS 最高 4.1 core，使用 150 语法）。</summary>
    Glsl150,
    /// <summary>OpenGL ES 3.0+（ANGLE / 移动端）。</summary>
    GlslEs300,
    /// <summary>OpenGL ES 2.0 兼容降级（关键字替换）。</summary>
    GlslEs100,
}

/// <summary>
/// GLSL 源码预处理器：统一使用现代语法（in/out/texture()）编写，
/// 按运行时的 GL 版本生成对应 #version 头部；ES2 场景做关键字降级替换。
/// </summary>
public static class GlslPreprocessor
{
    public static GlslProfile SelectProfile(Avalonia.OpenGL.GlVersion version)
    {
        if (version.Type == Avalonia.OpenGL.GlProfileType.OpenGLES)
        {
            return version.Major >= 3 ? GlslProfile.GlslEs300 : GlslProfile.GlslEs100;
        }

        // 桌面 GL：3.3+ 用 330 core，否则（macOS 4.1 / 老驱动）退到 150
        return version.Major > 3 || (version.Major == 3 && version.Minor >= 3)
            ? GlslProfile.Glsl330
            : GlslProfile.Glsl150;
    }

    public static string Prepare(string modernSource, GlslProfile profile)
    {
        string header = profile switch
        {
            GlslProfile.Glsl330 => "#version 330 core\n",
            GlslProfile.Glsl150 => "#version 150\n",
            GlslProfile.GlslEs300 => "#version 300 es\nprecision highp float;\nprecision highp int;\n",
            _ => "#version 100\nprecision mediump float;\n",
        };

        var body = modernSource;
        if (profile == GlslProfile.GlslEs100)
        {
            body = body
                .Replace("out vec4 fragColor;", string.Empty)
                .Replace("fragColor", "gl_FragColor")
                .Replace("in vec2", "attribute vec2")
                .Replace("in vec3", "attribute vec3")
                .Replace("in float", "attribute float")
                .Replace("out vec2", "varying vec2")
                .Replace("out vec3", "varying vec3")
                .Replace("out float", "varying float")
                .Replace("texture(", "texture2D(");
        }

        return header + body;
    }
}
