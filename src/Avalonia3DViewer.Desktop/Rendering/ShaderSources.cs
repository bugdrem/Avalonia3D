namespace Avalonia3DViewer.Desktop.Rendering;

/// <summary>
/// GLSL 着色器源码（统一现代语法，由 <see cref="GlslPreprocessor"/> 按版本转换）。
/// 约束：不使用 GlInterface 未暴露的能力（无混合、无 glLineWidth、无 glPolygonMode），
/// 线框/网格/测距线均以 GL_LINES 几何绘制。
/// </summary>
public static class ShaderSources
{
    // ---- mesh (shaded) ----
    public const string MeshVertex = @"
in vec3 aPos;
in vec3 aNormal;
in vec3 aColor;

uniform mat4 uModel;      // model world transform (dragged position)
uniform mat4 uView;
uniform mat4 uProj;
uniform mat4 uCameraPos;   // row 4 holds camera position

out vec3 vNormal;
out vec3 vColor;
out vec3 vWorld;

void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vNormal = aNormal;
    vColor = aColor;
    gl_Position = uProj * uView * world;
}
";

    public const string MeshFragment = @"
in vec3 vNormal;
in vec3 vColor;
in vec3 vWorld;

uniform mat4 uCameraPos;
uniform float uHighlight;

out vec4 fragColor;

void main()
{
    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) n = -n;

    vec3 eye = uCameraPos[3].xyz;
    vec3 viewDir = normalize(eye - vWorld);

    // hemispheric ambient (cool sky / warm ground)
    vec3 sky = vec3(0.62, 0.68, 0.76);
    vec3 groundColor = vec3(0.42, 0.40, 0.37);
    vec3 ambient = mix(groundColor, sky, n.y * 0.5 + 0.5) * 0.55;

    // key light (warm white, upper right front)
    vec3 keyDir = normalize(vec3(0.55, 0.78, 0.30));
    float diff = max(dot(n, keyDir), 0.0);
    vec3 key = vec3(1.0, 0.97, 0.90) * diff * 0.72;

    // fill light (cool, left back)
    vec3 fillDir = normalize(vec3(-0.60, 0.22, -0.55));
    float fdiff = max(dot(n, fillDir), 0.0);
    vec3 fill = vec3(0.72, 0.80, 0.94) * fdiff * 0.22;

    // rim light
    float rim = pow(1.0 - max(dot(n, viewDir), 0.0), 3.0) * 0.14;

    // Blinn-Phong specular
    vec3 halfDir = normalize(keyDir + viewDir);
    float spec = pow(max(dot(n, halfDir), 0.0), 48.0) * 0.30;

    vec3 color = vColor * (ambient + key + fill) + vec3(rim) + vec3(spec);

    // selection highlight (orange shift)
    color = mix(color, vec3(1.0, 0.52, 0.14), uHighlight);

    fragColor = vec4(color, 1.0);
}
";

    // ---- lines (grid / wireframe / measure) ----
    public const string LineVertex = @"
in vec3 aPos;
in vec3 aColor;

uniform mat4 uModel;       // model world transform (identity for grid/measure/triad)
uniform mat4 uView;
uniform mat4 uProj;
uniform float uScreenSpace;   // 1 = screen space (skip view-projection)
uniform mat4 uCameraPos;      // row 4 holds camera position
uniform float uUseFade;
uniform float uDepthBias;
uniform float uFadeNear;
uniform float uFadeFar;

out vec3 vColor;

void main()
{
    vec4 clip = mix(uProj * uView * uModel * vec4(aPos, 1.0), vec4(aPos, 1.0), uScreenSpace);
    clip.z -= uDepthBias * clip.w;   // depth bias to avoid line/surface z-fighting
    gl_Position = clip;
    float dist = length(aPos - uCameraPos[3].xyz);
    float fade = clamp((dist - uFadeNear) / max(uFadeFar - uFadeNear, 0.001), 0.0, 1.0) * uUseFade;
    vColor = mix(aColor, aColor * 0.25, fade);
}
";

    public const string LineFragment = @"
in vec3 vColor;

out vec4 fragColor;

void main()
{
    fragColor = vec4(vColor, 1.0);
}
";

    // ---- points (annotation markers / point cloud) ----
    public const string PointVertex = @"
in vec3 aPos;
in vec3 aColor;

uniform mat4 uModel;       // model world transform (identity for markers)
uniform mat4 uView;
uniform mat4 uProj;
uniform float uPointSize;

out vec3 vColor;

void main()
{
    gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
    gl_PointSize = uPointSize;
    vColor = aColor;
}
";

    public const string PointFragment = @"
in vec3 vColor;
uniform float uRing;   // 1 = round marker (white border), 0 = square point (point cloud)

out vec4 fragColor;

void main()
{
    if (uRing > 0.5)
    {
        vec2 pc = gl_PointCoord - vec2(0.5);
        float r = length(pc);
        if (r > 0.5) discard;
        float border = smoothstep(0.36, 0.44, r);
        vec3 c = mix(vColor, vec3(1.0), border);
        fragColor = vec4(c, 1.0);
    }
    else
    {
        fragColor = vec4(vColor, 1.0);
    }
}
";

    // ---- background gradient ----
    public const string BackgroundVertex = @"
in vec2 aPos;
out float vY;

void main()
{
    vY = aPos.y;
    gl_Position = vec4(aPos, 0.0, 1.0);
}
";

    public const string BackgroundFragment = @"
in float vY;
uniform mat4 uTopColor;
uniform mat4 uBottomColor;

out vec4 fragColor;

void main()
{
    vec3 top = uTopColor[3].xyz;
    vec3 bottom = uBottomColor[3].xyz;
    fragColor = vec4(mix(bottom, top, vY * 0.5 + 0.5), 1.0);
}
";

    // ---- SSAA resolve (fullscreen triangle) ----
    public const string BlitVertex = @"
in vec2 aPos;
out vec2 vUv;

void main()
{
    vUv = aPos * 0.5 + 0.5;
    gl_Position = vec4(aPos, 0.0, 1.0);
}
";

    public const string BlitFragment = @"
in vec2 vUv;
uniform sampler2D uTex;

out vec4 fragColor;

void main()
{
    fragColor = texture(uTex, vUv);
}
";
}
