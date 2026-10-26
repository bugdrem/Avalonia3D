using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Numerics;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// PLY (Stanford Polygon File Format) 加载器：ascii / binary_little_endian / binary_big_endian。
/// 支持属性：x,y,z,nx,ny,nz,red,green,blue,alpha（uchar 或 float）；面：list uchar int 索引。
/// </summary>
public sealed class PlyLoader : IModelLoader
{
    public string FormatName => "PLY";

    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".ply" };

    public bool CanLoad(string path) =>
        path.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);

    public ModelDocument Load(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new ModelLoadException($"文件不存在: {path}");

        using var fs = File.OpenRead(path);
        var lineReader = new AsciiLineReader(fs);
        using var br = new BinaryReader(fs);

        // ---- 头部 ----
        string? magic = lineReader.ReadLine();
        if (magic is null || !magic.StartsWith("ply", StringComparison.OrdinalIgnoreCase))
            throw new ModelLoadException("不是有效的 PLY 文件（缺少 ply 魔数）。");

        string format = "ascii";
        int vertexCount = -1, faceCount = -1;
        // 顶点属性顺序
        var vertexProps = new List<(string name, string type)>();
        var faceProps = new List<(string name, string type)>();
        bool inVertexElement = false, inFaceElement = false;

        while (true)
        {
            var line = lineReader.ReadLine();
            if (line is null) throw new ModelLoadException("PLY 头部不完整（缺少 end_header）。");
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t == "end_header") break;

            var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (tokens[0])
            {
                case "format":
                    if (tokens.Length < 2) throw new ModelLoadException("PLY format 行无效。");
                    format = tokens[1];
                    break;
                case "comment":
                case "obj_info":
                    break;
                case "element":
                    inVertexElement = tokens.Length > 1 && tokens[1] == "vertex";
                    inFaceElement = tokens.Length > 1 && tokens[1] == "face";
                    if (inVertexElement && tokens.Length > 2) int.TryParse(tokens[2], out vertexCount);
                    if (inFaceElement && tokens.Length > 2) int.TryParse(tokens[2], out faceCount);
                    break;
                case "property":
                    if (tokens.Length < 3) break;
                    if (inVertexElement) vertexProps.Add((tokens[2], tokens[1]));
                    else if (inFaceElement) faceProps.Add((tokens[2], tokens[1]));
                    break;
            }
        }

        if (vertexCount < 0)
            throw new ModelLoadException("PLY 头部缺少 vertex 元素。");
        if (format != "ascii" && format != "binary_little_endian" && format != "binary_big_endian")
            throw new ModelLoadException($"不支持的 PLY 格式: {format}");

        // CR-only 换行时行读取器可能窥视了二进制首字节，先归还
        lineReader.Rewind();

        var positions = new float[vertexCount * 3];
        var normals = new float[vertexCount * 3];
        var colors = new float[vertexCount * 3];
        bool hasNormals = false, hasColors = false;
        var indices = new List<uint>(Math.Max(0, faceCount) * 3);

        // 属性索引映射
        int idxX = -1, idxY = -1, idxZ = -1, idxNX = -1, idxNY = -1, idxNZ = -1, idxR = -1, idxG = -1, idxB = -1;
        int stride = 0;
        var types = new List<string>();
        for (int i = 0; i < vertexProps.Count; i++)
        {
            var (name, type) = vertexProps[i];
            stride += PlyTypeSize(type);
            types.Add(type);
            switch (name)
            {
                case "x": idxX = i; break;
                case "y": idxY = i; break;
                case "z": idxZ = i; break;
                case "nx": idxNX = i; hasNormals = true; break;
                case "ny": idxNY = i; hasNormals = true; break;
                case "nz": idxNZ = i; hasNormals = true; break;
                case "red": idxR = i; hasColors = true; break;
                case "green": idxG = i; hasColors = true; break;
                case "blue": idxB = i; hasColors = true; break;
            }
        }
        if (idxX < 0 || idxY < 0 || idxZ < 0)
            throw new ModelLoadException("PLY 顶点属性缺少 x/y/z。");

        if (format == "ascii")
        {
            for (int v = 0; v < vertexCount; v++)
            {
                var line = lineReader.ReadLine();
                if (line is null) throw new ModelLoadException("PLY 顶点数据不完整。");
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length < vertexProps.Count)
                    throw new ModelLoadException($"PLY 第 {v + 1} 行顶点属性不足。");
                var values = new float[vertexProps.Count];
                for (int i = 0; i < vertexProps.Count; i++)
                    values[i] = float.Parse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture);
                WriteVertex(v, values);
            }

            for (int f = 0; f < faceCount; f++)
            {
                var line = lineReader.ReadLine();
                if (line is null) break;
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0) continue;
                if (!int.TryParse(tokens[0], out int n) || n < 3) continue;
                for (int i = 1; i + 2 < tokens.Length && i - 1 + 2 < n; i++)
                {
                    // 扇形三角化
                    if (!int.TryParse(tokens[1], out int a) ||
                        !int.TryParse(tokens[1 + i], out int b) ||
                        !int.TryParse(tokens[2 + i], out int c)) break;
                    indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
                }
            }
        }
        else
        {
            bool littleEndian = format == "binary_little_endian";
            var row = new byte[stride];
            for (int v = 0; v < vertexCount; v++)
            {
                fs.ReadExactly(row);
                var values = new float[vertexProps.Count];
                int offset = 0;
                for (int i = 0; i < vertexProps.Count; i++)
                {
                    values[i] = ReadPlyValue(row, offset, types[i], littleEndian);
                    offset += PlyTypeSize(types[i]);
                }
                WriteVertex(v, values);
            }

            for (int f = 0; f < faceCount; f++)
            {
                int n = br.ReadByte();
                if (n < 3) { fs.Seek(n * 4, SeekOrigin.Current); continue; }
                var buf = new byte[n * 4];
                fs.ReadExactly(buf);
                for (int i = 1; i + 1 < n; i++)
                {
                    int a = ReadInt32(buf, 0, littleEndian);
                    int b = ReadInt32(buf, i * 4, littleEndian);
                    int c = ReadInt32(buf, (i + 1) * 4, littleEndian);
                    indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
                }
            }
        }

        if (indices.Count == 0)
            throw new ModelLoadException("PLY 文件中未找到任何面数据。");

        sw.Stop();

        var mesh = new Geometry.MeshData
        {
            Positions = positions,
            Normals = hasNormals ? normals : Array.Empty<float>(),
            Colors = hasColors ? colors : Array.Empty<float>(),
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
            Format = $"PLY ({format})",
            LoadMilliseconds = sw.Elapsed.TotalMilliseconds,
        };

        void WriteVertex(int v, float[] values)
        {
            positions[v * 3] = values[idxX];
            positions[v * 3 + 1] = values[idxY];
            positions[v * 3 + 2] = values[idxZ];
            if (hasNormals && idxNX >= 0 && idxNY >= 0 && idxNZ >= 0)
            {
                normals[v * 3] = values[idxNX];
                normals[v * 3 + 1] = values[idxNY];
                normals[v * 3 + 2] = values[idxNZ];
            }
            if (hasColors && idxR >= 0 && idxG >= 0 && idxB >= 0)
            {
                float r = PlyPropIsFloat(types[idxR]) ? values[idxR] : values[idxR] / 255f;
                float g = PlyPropIsFloat(types[idxG]) ? values[idxG] : values[idxG] / 255f;
                float b = PlyPropIsFloat(types[idxB]) ? values[idxB] : values[idxB] / 255f;
                colors[v * 3] = r; colors[v * 3 + 1] = g; colors[v * 3 + 2] = b;
            }
        }
    }

    private static bool PlyPropIsFloat(string type) => type is "float" or "float32" or "double" or "float64";

    private static int PlyTypeSize(string type) => type switch
    {
        "char" or "int8" => 1,
        "uchar" or "uint8" => 1,
        "short" or "int16" => 2,
        "ushort" or "uint16" => 2,
        "int" or "int32" => 4,
        "uint" or "uint32" => 4,
        "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => 4,
    };

    private static float ReadPlyValue(byte[] row, int offset, string type, bool littleEndian)
    {
        var span = row.AsSpan(offset);
        bool be = !littleEndian;
        return type switch
        {
            "char" or "int8" => (sbyte)span[0],
            "uchar" or "uint8" => span[0],
            "short" or "int16" => be ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadInt16LittleEndian(span),
            "ushort" or "uint16" => be ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span),
            "int" or "int32" => be ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span),
            "uint" or "uint32" => be ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span),
            "float" or "float32" => be ? BinaryPrimitives.ReadSingleBigEndian(span) : BinaryPrimitives.ReadSingleLittleEndian(span),
            "double" or "float64" => (float)(be ? BinaryPrimitives.ReadDoubleBigEndian(span) : BinaryPrimitives.ReadDoubleLittleEndian(span)),
            _ => 0f,
        };
    }

    private static int ReadInt32(byte[] buf, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(offset)) : BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(offset));
}
