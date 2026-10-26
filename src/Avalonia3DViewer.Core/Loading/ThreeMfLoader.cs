using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// 3MF 加载器（3D Manufacturing Format）：ZIP 容器 + XML 模型。
/// 读取 3D/3dmodel.model（兼容根目录 3dmodel.model），合并所有 mesh 资源，
/// 按模型声明的单位换算为毫米。忽略构建变换（按工业件常见用法处理）。
/// </summary>
public sealed class ThreeMfLoader : IModelLoader
{
    public string FormatName => "3MF";

    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".3mf" };

    public bool CanLoad(string path) =>
        path.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase);

    // 3MF 单位 → 毫米
    private static float UnitToMm(string unit) => unit.ToLowerInvariant() switch
    {
        "micron" => 0.001f,
        "millimeter" => 1f,
        "centimeter" => 10f,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000f,
        _ => 1f,
    };

    public ModelDocument Load(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new ModelLoadException($"文件不存在: {path}");

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("3D/3dmodel.model") ?? archive.GetEntry("3dmodel.model");
        if (entry is null)
            throw new ModelLoadException("3MF 包内未找到 3D/3dmodel.model。");

        float unitScale = 1f;
        var positions = new List<float>(1 << 20);
        var normals = new List<float>(1 << 20);
        var indices = new List<uint>(1 << 20);
        int meshCount = 0;

        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore }))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "mesh")
                    {
                        ReadMesh(reader, unitScale, positions, normals, indices);
                        meshCount++;
                    }
                    else if (reader.LocalName == "model" && reader.HasAttributes)
                    {
                        // <model unit="millimeter" ...>
                        for (int i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToNextAttribute();
                            if (reader.LocalName == "unit")
                                unitScale = UnitToMm(reader.Value);
                        }
                        reader.MoveToElement();
                    }
                }
            }
        }

        if (positions.Count == 0 || meshCount == 0)
            throw new ModelLoadException("3MF 中未找到任何网格数据。");

        sw.Stop();

        var mesh = new Geometry.MeshData
        {
            Positions = positions.ToArray(),
            Normals = Array.Empty<float>(),
            Indices = indices.ToArray(),
            Name = fileInfo.Name,
            BaseColor = new Vector3(0.62f, 0.66f, 0.72f),
        };
        mesh.EnsureNormals();
        mesh.EnsureColors();
        mesh.RecomputeBounds();

        return new ModelDocument
        {
            Mesh = mesh,
            FileName = fileInfo.Name,
            Format = "3MF",
            LoadMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    private static void ReadMesh(XmlReader reader, float scale, List<float> positions, List<float> normals, List<uint> indices)
    {
        int vertexBase = positions.Count / 3;
        var localNormals = new List<Vector3>();

        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "mesh")
                break;

            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.LocalName == "vertex" && reader.HasAttributes)
            {
                float x = 0, y = 0, z = 0;
                for (int i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToNextAttribute();
                    switch (reader.LocalName)
                    {
                        case "x": float.TryParse(reader.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x); break;
                        case "y": float.TryParse(reader.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y); break;
                        case "z": float.TryParse(reader.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z); break;
                    }
                }
                reader.MoveToElement();
                positions.Add(x * scale); positions.Add(y * scale); positions.Add(z * scale);
                localNormals.Add(default);
            }
            else if (reader.LocalName == "triangle" && reader.HasAttributes)
            {
                int v1 = 0, v2 = 0, v3 = 0;
                for (int i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToNextAttribute();
                    switch (reader.LocalName)
                    {
                        case "v1": int.TryParse(reader.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v1); break;
                        case "v2": int.TryParse(reader.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v2); break;
                        case "v3": int.TryParse(reader.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v3); break;
                    }
                }
                reader.MoveToElement();
                indices.Add((uint)(vertexBase + v1));
                indices.Add((uint)(vertexBase + v2));
                indices.Add((uint)(vertexBase + v3));

                // 面积加权法线累加（v1..v3 为网格局部索引）
                if (v1 < localNormals.Count && v2 < localNormals.Count && v3 < localNormals.Count)
                {
                    var a = new Vector3(positions[(vertexBase + v1) * 3], positions[(vertexBase + v1) * 3 + 1], positions[(vertexBase + v1) * 3 + 2]);
                    var b = new Vector3(positions[(vertexBase + v2) * 3], positions[(vertexBase + v2) * 3 + 1], positions[(vertexBase + v2) * 3 + 2]);
                    var c = new Vector3(positions[(vertexBase + v3) * 3], positions[(vertexBase + v3) * 3 + 1], positions[(vertexBase + v3) * 3 + 2]);
                    var n = Vector3.Cross(b - a, c - a);
                    localNormals[v1] += n; localNormals[v2] += n; localNormals[v3] += n;
                }
            }
        }

        for (int i = 0; i < localNormals.Count; i++)
        {
            var n = localNormals[i];
            n = n.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(n);
            normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
        }
    }
}
