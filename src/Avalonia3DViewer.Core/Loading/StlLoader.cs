using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// STL 加载器（ASCII 与二进制，自动识别）。
/// 识别策略：前 5 字节为 "solid" 时进一步校验——文件长度恰为 84+50*面数且头部 1KB 内无
/// "facet"/"endsolid" 关键字则判定为二进制，否则按 ASCII 解析。
/// </summary>
public sealed class StlLoader : IModelLoader
{
    public string FormatName => "STL";

    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".stl" };

    public bool CanLoad(string path) =>
        path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase);

    public ModelDocument Load(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new ModelLoadException($"文件不存在: {path}");

        long length = fileInfo.Length;
        Geometry.MeshData mesh;
        string format;

        if (length < 84)
        {
            (mesh, format) = ParseAscii(path, fileInfo.Name);
        }
        else
        {
            byte[] header = new byte[84];
            using (var fs = File.OpenRead(path))
            {
                fs.ReadExactly(header, 0, 84);
            }

            bool startsWithSolid = header.AsSpan(0, 5).SequenceEqual("solid"u8);
            uint triCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
            bool sizeMatchesBinary = length == 84L + triCount * 50L;
            bool looksAscii = startsWithSolid && (ContainsAsciiKeyword(header) || !sizeMatchesBinary);

            if (looksAscii)
                (mesh, format) = ParseAscii(path, fileInfo.Name);
            else
                (mesh, format) = ParseBinary(path, triCount, fileInfo.Name);
        }

        sw.Stop();
        mesh.EnsureNormals();
        mesh.EnsureColors();
        mesh.RecomputeBounds();

        return new ModelDocument
        {
            Mesh = mesh,
            FileName = fileInfo.Name,
            Format = format,
            LoadMilliseconds = sw.Elapsed.TotalMilliseconds,
        };
    }

    private static bool ContainsAsciiKeyword(ReadOnlySpan<byte> header) =>
        header.IndexOf("facet"u8) >= 0 || header.IndexOf("endsolid"u8) >= 0;

    private static (Geometry.MeshData, string) ParseBinary(string path, uint triCount, string name)
    {
        if (triCount == 0 || triCount > 100_000_000)
            throw new ModelLoadException("STL 文件头中的三角面数量无效，文件可能已损坏。");

        var positions = new float[triCount * 9];
        var normals = new float[triCount * 9];
        var indices = new uint[triCount * 3];

        using var fs = File.OpenRead(path);
        fs.Seek(84, SeekOrigin.Begin);
        var vertexSpan = positions.AsSpan();
        var normalSpan = normals.AsSpan();
        bool swap = !BitConverter.IsLittleEndian;
        Span<byte> tri = stackalloc byte[50];
        Span<float> fv = stackalloc float[12];

        for (uint t = 0; t < triCount; t++)
        {
            fs.ReadExactly(tri);
            var floats = MemoryMarshal.Cast<byte, float>(tri[..48]);
            for (int i = 0; i < 12; i++)
            {
                fv[i] = swap ? BinaryPrimitives.ReadSingleBigEndian(tri.Slice(i * 4, 4)) : floats[i];
            }

            for (int v = 0; v < 3; v++)
            {
                int vi = (int)(t * 3 + v) * 3;
                normalSpan[vi] = fv[0]; normalSpan[vi + 1] = fv[1]; normalSpan[vi + 2] = fv[2];
                vertexSpan[vi] = fv[3 + v * 3];
                vertexSpan[vi + 1] = fv[4 + v * 3];
                vertexSpan[vi + 2] = fv[5 + v * 3];
                indices[t * 3 + v] = t * 3 + (uint)v;
            }
        }

        return (new Geometry.MeshData
        {
            Positions = positions,
            Normals = normals,
            Indices = indices,
            Name = name,
            BaseColor = new System.Numerics.Vector3(0.62f, 0.66f, 0.72f),
        }, "STL (二进制)");
    }

    private static (Geometry.MeshData, string) ParseAscii(string path, string name)
    {
        var positions = new List<float>(1 << 20);
        var indices = new List<uint>(1 << 19);
        float[] triVerts = new float[9];
        int inTri = 0;

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty) continue;

            if (span.StartsWith("vertex"))
            {
                var rest = span.Slice("vertex".Length).Trim();
                int written = 0;
                foreach (var tok in Tokenizer.Tokens(rest, ' '))
                {
                    if (written >= 3) break;
                    if (!float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out float val)) break;
                    triVerts[inTri * 3 + written] = val;
                    written++;
                }
                if (written == 3)
                {
                    inTri++;
                    if (inTri == 3)
                    {
                        uint baseIdx = (uint)(positions.Count / 3);
                        positions.AddRange(triVerts);
                        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
                        inTri = 0;
                    }
                }
            }
            else if (span.StartsWith("facet"))
            {
                inTri = 0;
            }
        }

        if (positions.Count == 0)
            throw new ModelLoadException("STL 文件中未找到任何三角面数据。");

        return (new Geometry.MeshData
        {
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            Name = name,
            BaseColor = new System.Numerics.Vector3(0.62f, 0.66f, 0.72f),
        }, "STL (ASCII)");
    }
}
