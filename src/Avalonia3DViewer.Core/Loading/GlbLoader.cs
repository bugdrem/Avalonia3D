using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// GLB (glTF 2.0 binary) 加载器：静态网格，支持 POSITION/NORMAL/indices 与
/// pbrMetallicRoughness.baseColorFactor 基色。使用 JsonDocument 解析（零反射，AOT 安全）。
/// 忽略动画/皮肤/纹理（工业预览场景以静态网格为主）。
/// </summary>
public sealed class GlbLoader : IModelLoader
{
    private const uint MagicGlb = 0x46546C67; // 'glTF'
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    private const int CompByte = 5120;
    private const int CompUByte = 5121;
    private const int CompShort = 5122;
    private const int CompUShort = 5123;
    private const int CompUInt = 5125;
    private const int CompFloat = 5126;

    public string FormatName => "GLB";

    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".glb" };

    public bool CanLoad(string path) =>
        path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);

    public ModelDocument Load(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new ModelLoadException($"文件不存在: {path}");

        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 12)
            throw new ModelLoadException("GLB 文件过小，可能已损坏。");

        uint magic = BitConverter.ToUInt32(data, 0);
        uint version = BitConverter.ToUInt32(data, 4);
        if (magic != MagicGlb)
            throw new ModelLoadException("不是有效的 GLB 文件（魔数不匹配）。");
        if (version != 2)
            throw new ModelLoadException($"仅支持 glTF 2.0，检测到版本 {version}。");

        // ---- 遍历 chunk ----
        byte[]? jsonChunk = null;
        byte[] binChunk = Array.Empty<byte>();
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            uint chunkLength = BitConverter.ToUInt32(data, offset);
            uint chunkType = BitConverter.ToUInt32(data, offset + 4);
            offset += 8;
            if (offset + chunkLength > data.Length)
                throw new ModelLoadException("GLB chunk 长度越界，文件可能已损坏。");
            if (chunkType == ChunkJson)
            {
                // JSON chunk 可能带 0x00 / 空格填充，解析前需裁剪
                int jsonLen = (int)chunkLength;
                while (jsonLen > 0 && (data[offset + jsonLen - 1] == 0 || data[offset + jsonLen - 1] == (byte)' '))
                    jsonLen--;
                jsonChunk = data[offset..(offset + jsonLen)];
            }
            else if (chunkType == ChunkBin)
                binChunk = data[offset..(int)(offset + chunkLength)];
            offset += (int)chunkLength;
        }

        if (jsonChunk is null)
            throw new ModelLoadException("GLB 缺少 JSON chunk。");

        using var doc = JsonDocument.Parse(jsonChunk);
        var root = doc.RootElement;

        var positions = new List<float>(1 << 20);
        var normals = new List<float>(1 << 20);
        var indices = new List<uint>(1 << 20);
        var firstColor = new Vector3(0.62f, 0.66f, 0.72f);
        bool colorCaptured = false;

        if (root.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
        {
            foreach (var meshElement in meshes.EnumerateArray())
            {
                if (!meshElement.TryGetProperty("primitives", out var prims)) continue;
                foreach (var prim in prims.EnumerateArray())
                {
                    if (prim.ValueKind != JsonValueKind.Object) continue;
                    if (!prim.TryGetProperty("attributes", out var attrs)) continue;

                    if (!attrs.TryGetProperty("POSITION", out var posAcc) || posAcc.ValueKind != JsonValueKind.Number)
                        continue;

                    var baseColor = ReadBaseColor(root, prim);
                    if (!colorCaptured)
                    {
                        firstColor = baseColor;
                        colorCaptured = true;
                    }

                    int posAccessor = posAcc.GetInt32();
                    bool hasNormal = attrs.TryGetProperty("NORMAL", out var normAcc) && normAcc.ValueKind == JsonValueKind.Number;
                    int normAccessor = hasNormal ? normAcc.GetInt32() : -1;

                    int vertexCount = GetAccessorCount(root, posAccessor);
                    if (vertexCount <= 0) continue;

                    // 读取顶点
                    int baseVertex = positions.Count / 3;
                    for (int i = 0; i < vertexCount; i++)
                    {
                        var p = ReadAccessorVec3(root, binChunk, posAccessor, i);
                        positions.Add(p.X); positions.Add(p.Y); positions.Add(p.Z);

                        if (hasNormal)
                        {
                            var n = ReadAccessorVec3(root, binChunk, normAccessor, i);
                            if (n.LengthSquared() < 1e-12f) n = Vector3.UnitY; else n = Vector3.Normalize(n);
                            normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
                        }
                        else
                        {
                            normals.Add(0); normals.Add(0); normals.Add(0);
                        }
                    }

                    // 索引
                    if (prim.TryGetProperty("indices", out var idxAcc) && idxAcc.ValueKind == JsonValueKind.Number)
                    {
                        int indexCount = GetAccessorCount(root, idxAcc.GetInt32());
                        for (int i = 0; i < indexCount; i++)
                        {
                            indices.Add((uint)(baseVertex + ReadAccessorScalarInt(root, binChunk, idxAcc.GetInt32(), i)));
                        }
                    }
                    else
                    {
                        for (uint i = 0; i < vertexCount; i++)
                        {
                            indices.Add((uint)(baseVertex + i));
                        }
                    }
                }
            }
        }

        if (positions.Count == 0)
            throw new ModelLoadException("GLB 中未找到任何网格数据。");

        sw.Stop();

        var mesh = new Geometry.MeshData
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Indices = indices.ToArray(),
            Name = fileInfo.Name,
            BaseColor = firstColor,
        };
        mesh.EnsureNormals();
        mesh.EnsureColors();
        mesh.RecomputeBounds();

        return new ModelDocument
        {
            Mesh = mesh,
            FileName = fileInfo.Name,
            Format = "GLB (glTF 2.0)",
            LoadMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    private static Vector3 ReadBaseColor(JsonElement root, JsonElement prim)
    {
        if (!prim.TryGetProperty("material", out var matIdx) || matIdx.ValueKind != JsonValueKind.Number)
            return new Vector3(0.62f, 0.66f, 0.72f);
        if (!root.TryGetProperty("materials", out var materials)) return new Vector3(0.62f, 0.66f, 0.72f);
        int idx = matIdx.GetInt32();
        int i = 0;
        foreach (var mat in materials.EnumerateArray())
        {
            if (i == idx && mat.TryGetProperty("pbrMetallicRoughness", out var pbr) &&
                pbr.TryGetProperty("baseColorFactor", out var factor) && factor.ValueKind == JsonValueKind.Array &&
                factor.GetArrayLength() >= 3)
            {
                // glTF 颜色为线性空间 → 转换到 sRGB 显示空间
                float r = MathF.Pow(factor[0].GetSingle(), 1f / 2.2f);
                float g = MathF.Pow(factor[1].GetSingle(), 1f / 2.2f);
                float b = MathF.Pow(factor[2].GetSingle(), 1f / 2.2f);
                return new Vector3(r, g, b);
            }
            i++;
        }
        return new Vector3(0.62f, 0.66f, 0.72f);
    }

    private static int GetAccessorCount(JsonElement root, int accessorIndex)
    {
        int i = 0;
        foreach (var acc in root.GetProperty("accessors").EnumerateArray())
        {
            if (i == accessorIndex && acc.TryGetProperty("count", out var c))
                return c.GetInt32();
            i++;
        }
        return 0;
    }

    private static JsonElement GetAccessor(JsonElement root, int accessorIndex)
    {
        int i = 0;
        foreach (var acc in root.GetProperty("accessors").EnumerateArray())
        {
            if (i == accessorIndex) return acc;
            i++;
        }
        throw new ModelLoadException("GLB accessor 索引越界。");
    }

    private static JsonElement GetBufferView(JsonElement root, int viewIndex)
    {
        int i = 0;
        foreach (var v in root.GetProperty("bufferViews").EnumerateArray())
        {
            if (i == viewIndex) return v;
            i++;
        }
        throw new ModelLoadException("GLB bufferView 索引越界。");
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        CompByte or CompUByte => 1,
        CompShort or CompUShort => 2,
        CompUInt or CompFloat => 4,
        _ => 4,
    };

    private static int ComponentsInType(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => 1,
    };

    private static (int viewOffset, int accessorOffset, int stride) GetAccessorLayout(JsonElement root, JsonElement accessor)
    {
        int accessorOffset = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        int stride;
        int viewOffset;
        if (accessor.TryGetProperty("bufferView", out var bv) && bv.ValueKind == JsonValueKind.Number)
        {
            var view = GetBufferView(root, bv.GetInt32());
            viewOffset = view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            if (view.TryGetProperty("byteStride", out var bs) && bs.ValueKind == JsonValueKind.Number)
                stride = bs.GetInt32();
            else
                stride = ComponentSize(accessor.GetProperty("componentType").GetInt32()) * ComponentsInType(accessor.GetProperty("type").GetString() ?? "SCALAR");
        }
        else
        {
            viewOffset = 0;
            stride = ComponentSize(accessor.GetProperty("componentType").GetInt32()) * ComponentsInType(accessor.GetProperty("type").GetString() ?? "SCALAR");
        }
        return (viewOffset, accessorOffset, stride);
    }

    private static float ReadAccessorComponent(byte[] bin, int byteOffset, int componentType, int indexInElement)
    {
        int o = byteOffset + indexInElement * ComponentSize(componentType);
        return componentType switch
        {
            CompFloat => BitConverter.ToSingle(bin, o),
            CompUByte => bin[o],
            CompByte => (sbyte)bin[o],
            CompUShort => BitConverter.ToUInt16(bin, o),
            CompShort => BitConverter.ToInt16(bin, o),
            CompUInt => BitConverter.ToUInt32(bin, o),
            _ => BitConverter.ToSingle(bin, o),
        };
    }

    private static Vector3 ReadAccessorVec3(JsonElement root, byte[] bin, int accessorIndex, int elementIndex)
    {
        var acc = GetAccessor(root, accessorIndex);
        var (viewOffset, accessorOffset, stride) = GetAccessorLayout(root, acc);
        int comp = acc.GetProperty("componentType").GetInt32();
        int baseOffset = viewOffset + accessorOffset + elementIndex * stride;
        return new Vector3(
            ReadAccessorComponent(bin, baseOffset, comp, 0),
            ReadAccessorComponent(bin, baseOffset, comp, 1),
            ReadAccessorComponent(bin, baseOffset, comp, 2));
    }

    private static int ReadAccessorScalarInt(JsonElement root, byte[] bin, int accessorIndex, int elementIndex)
    {
        var acc = GetAccessor(root, accessorIndex);
        var (viewOffset, accessorOffset, stride) = GetAccessorLayout(root, acc);
        int comp = acc.GetProperty("componentType").GetInt32();
        int baseOffset = viewOffset + accessorOffset + elementIndex * stride;
        return (int)ReadAccessorComponent(bin, baseOffset, comp, 0);
    }
}
