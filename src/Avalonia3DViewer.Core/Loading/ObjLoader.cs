using System.Globalization;
using System.Numerics;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// OBJ 加载器：支持 v / vt / vn / f（三角化多边形，负索引相对引用），
/// 可选逐顶点颜色扩展（v x y z r g b）。按 (位置, UV, 法线) 去重顶点，
/// 使无法线的模型获得平滑着色。MTL 材质忽略（统一使用基色）。
/// </summary>
public sealed class ObjLoader : IModelLoader
{
    private static readonly Vector3 DefaultColor = new(0.62f, 0.66f, 0.72f);

    public string FormatName => "OBJ";

    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".obj" };

    public bool CanLoad(string path) =>
        path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase);

    public ModelDocument Load(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new ModelLoadException($"文件不存在: {path}");

        var positions = new List<Vector3>(1 << 20);
        var normals = new List<Vector3>(1 << 20);
        var vertexColors = new List<float>(); // 与 positions 对齐（延迟启用）
        bool hasColors = false;
        int uvCount = 0;

        var outPositions = new List<float>(1 << 20);
        var outNormals = new List<float>(1 << 20);
        var outColors = new List<float>(1 << 20);
        var indices = new List<uint>(1 << 20);

        // (position, uv, normal) → 输出顶点索引
        var vertexCache = new Dictionary<(int, int, int), uint>(1 << 20);

        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        // 复用栈缓冲，避免逐行 stackalloc（CA2014）
        Span<float> vvals = stackalloc float[6];
        Span<float> nvals = stackalloc float[3];
        Span<int> verts = stackalloc int[64];
        Span<int> uvs = stackalloc int[64];
        Span<int> norms = stackalloc int[64];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#') continue;

            if (span.StartsWith("v "))
            {
                var rest = span.Slice(2).Trim();
                int n = 0;
                foreach (var tok in Tokenizer.Tokens(rest, ' '))
                {
                    if (n >= 6) break;
                    if (!float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out vvals[n])) break;
                    n++;
                }
                if (n >= 3)
                {
                    positions.Add(new Vector3(vvals[0], vvals[1], vvals[2]));
                    if (n >= 6)
                    {
                        if (!hasColors)
                        {
                            hasColors = true;
                            vertexColors.Clear();
                            for (int i = 0; i < positions.Count - 1; i++)
                            {
                                vertexColors.Add(DefaultColor.X); vertexColors.Add(DefaultColor.Y); vertexColors.Add(DefaultColor.Z);
                            }
                        }
                        vertexColors.Add(vvals[3]); vertexColors.Add(vvals[4]); vertexColors.Add(vvals[5]);
                    }
                    else if (hasColors)
                    {
                        vertexColors.Add(DefaultColor.X); vertexColors.Add(DefaultColor.Y); vertexColors.Add(DefaultColor.Z);
                    }
                }
            }
            else if (span.StartsWith("vt "))
            {
                uvCount++;
            }
            else if (span.StartsWith("vn "))
            {
                var rest = span.Slice(3).Trim();
                int n = 0;
                foreach (var tok in Tokenizer.Tokens(rest, ' '))
                {
                    if (n >= 3) break;
                    if (!float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out nvals[n])) break;
                    n++;
                }
                if (n == 3)
                {
                    normals.Add(new Vector3(nvals[0], nvals[1], nvals[2]));
                }
            }
            else if (span.StartsWith("f "))
            {
                var rest = span.Slice(2).Trim();
                int n = 0;
                foreach (var tok in Tokenizer.Tokens(rest, ' '))
                {
                    if (n >= 64) break;
                    // 形如 v、v/vt、v//vn、v/vt/vn（索引支持负值）
                    var slash1 = tok.IndexOf('/');
                    ReadOnlySpan<char> viSpan = slash1 < 0 ? tok : tok[..slash1];
                    if (!int.TryParse(viSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vi)) continue;
                    int resolved = vi > 0 ? vi - 1 : positions.Count + vi;
                    if (resolved < 0 || resolved >= positions.Count) { n = -1; break; }
                    verts[n] = resolved;
                    uvs[n] = -1;
                    norms[n] = -1;

                    if (slash1 >= 0)
                    {
                        var afterSlash1 = tok.Slice(slash1 + 1);
                        var slash2rel = afterSlash1.IndexOf('/');
                        var vtSpan = slash2rel < 0 ? afterSlash1 : afterSlash1[..slash2rel];
                        if (!vtSpan.IsEmpty && int.TryParse(vtSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vti))
                        {
                            // 负索引按当前已知 vt 数解析（标准文件 vt 均先于 f 出现）
                            uvs[n] = vti > 0 ? vti - 1 : uvCount + vti;
                        }
                        var vnSpan = slash2rel < 0 ? default : afterSlash1.Slice(slash2rel + 1);
                        if (!vnSpan.IsEmpty && int.TryParse(vnSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ni))
                        {
                            int rn = ni > 0 ? ni - 1 : normals.Count + ni;
                            if (rn >= 0 && rn < normals.Count) norms[n] = rn;
                        }
                    }
                    n++;
                }
                if (n < 3) continue;

                // 扇形三角化（顶点经缓存去重）
                for (int i = 1; i + 1 < n; i++)
                {
                    indices.Add(ResolveVertex(verts[0], uvs[0], norms[0]));
                    indices.Add(ResolveVertex(verts[i], uvs[i], norms[i]));
                    indices.Add(ResolveVertex(verts[i + 1], uvs[i + 1], norms[i + 1]));
                }
            }
        }

        if (outPositions.Count == 0)
            throw new ModelLoadException("OBJ 文件中未找到任何顶点/面数据。");

        sw.Stop();

        bool anyNormals = false;
        for (int i = 0; i < outNormals.Count; i++)
        {
            if (outNormals[i] != 0f) { anyNormals = true; break; }
        }

        var mesh = new Geometry.MeshData
        {
            Positions = outPositions.ToArray(),
            Normals = anyNormals ? outNormals.ToArray() : Array.Empty<float>(),
            Colors = hasColors ? outColors.ToArray() : Array.Empty<float>(),
            Indices = indices.ToArray(),
            Name = fileInfo.Name,
            BaseColor = DefaultColor,
        };
        mesh.EnsureNormals();
        mesh.EnsureColors();
        mesh.RecomputeBounds();

        return new ModelDocument
        {
            Mesh = mesh,
            FileName = fileInfo.Name,
            Format = "OBJ",
            LoadMilliseconds = sw.Elapsed.TotalMilliseconds,
        };

        uint ResolveVertex(int vIdx, int uvIdx, int nIdx)
        {
            var key = (vIdx, uvIdx, nIdx);
            if (vertexCache.TryGetValue(key, out uint cached)) return cached;

            uint idx = (uint)(outPositions.Count / 3);
            var p = positions[vIdx];
            outPositions.Add(p.X); outPositions.Add(p.Y); outPositions.Add(p.Z);

            if (nIdx >= 0)
            {
                var nv = normals[nIdx];
                if (nv.LengthSquared() < 1e-12f) nv = Vector3.UnitY;
                else nv = Vector3.Normalize(nv);
                outNormals.Add(nv.X); outNormals.Add(nv.Y); outNormals.Add(nv.Z);
            }
            else
            {
                outNormals.Add(0); outNormals.Add(0); outNormals.Add(0);
            }

            int ci = vIdx * 3;
            if (hasColors && ci + 2 < vertexColors.Count)
            {
                outColors.Add(vertexColors[ci]); outColors.Add(vertexColors[ci + 1]); outColors.Add(vertexColors[ci + 2]);
            }
            else
            {
                outColors.Add(DefaultColor.X); outColors.Add(DefaultColor.Y); outColors.Add(DefaultColor.Z);
            }

            vertexCache[key] = idx;
            return idx;
        }
    }
}
