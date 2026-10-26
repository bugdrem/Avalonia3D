using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Avalonia3DViewer.Core.Geometry;
using Avalonia3DViewer.Core.Loading;
using Avalonia3DViewer.Core.Procedural;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>
/// 各格式加载器测试：临时生成文件 → 加载 → 校验数据。
/// </summary>
public class LoaderTests : IDisposable
{
    private readonly string _tempDir;

    public LoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "avalonia3d_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* 忽略清理失败 */ }
    }

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    // ---------------------------------------------------------------- STL

    [Fact]
    public void Stl_Binary_LoadsTriangle()
    {
        var path = TempPath("cube_binary.stl");
        WriteBinaryStl(path, CubeTriangles());

        var doc = new StlLoader().Load(path);
        Assert.Equal("STL (二进制)", doc.Format);
        Assert.Equal(12, doc.Mesh.TriangleCount);
        Assert.Equal(36, doc.Mesh.VertexCount);
        Assert.False(doc.Mesh.Bounds.IsEmpty);
        Assert.All(doc.Mesh.Normals, n => Assert.True(float.IsFinite(n)));
    }

    [Fact]
    public void Stl_Ascii_LoadsTriangle()
    {
        var path = TempPath("cube_ascii.stl");
        WriteAsciiStl(path, CubeTriangles());

        var doc = new StlLoader().Load(path);
        Assert.Equal("STL (ASCII)", doc.Format);
        Assert.Equal(12, doc.Mesh.TriangleCount);
    }

    [Fact]
    public void Stl_BinaryWithSolidHeader_StillDetectedAsBinary()
    {
        // 经典陷阱：二进制文件头部恰好以 "solid" 开头
        var path = TempPath("tricky.stl");
        WriteBinaryStl(path, CubeTriangles(), header: "solid tricky binary header");

        var doc = new StlLoader().Load(path);
        Assert.Equal("STL (二进制)", doc.Format);
        Assert.Equal(12, doc.Mesh.TriangleCount);
    }

    [Fact]
    public void Stl_InvalidFile_ThrowsModelLoadException()
    {
        var path = TempPath("empty.stl");
        File.WriteAllText(path, "solid empty\nendsolid empty\n");
        Assert.Throws<ModelLoadException>(() => new StlLoader().Load(path));
    }

    // ---------------------------------------------------------------- OBJ

    [Fact]
    public void Obj_QuadIsTriangulated()
    {
        var path = TempPath("quad.obj");
        File.WriteAllText(path,
            "# test\n" +
            "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\n" +
            "f 1 2 3 4\n");

        var doc = new ObjLoader().Load(path);
        Assert.Equal(4, doc.Mesh.VertexCount);
        Assert.Equal(2, doc.Mesh.TriangleCount); // 四边形 → 2 三角形
    }

    [Fact]
    public void Obj_NegativeIndicesAndNormals()
    {
        var path = TempPath("neg.obj");
        File.WriteAllText(path,
            "v 0 0 0\nv 2 0 0\nv 0 2 0\n" +
            "vn 0 0 1\n" +
            "f -3//-1 -2//-1 -1//-1\n");

        var doc = new ObjLoader().Load(path);
        Assert.Equal(3, doc.Mesh.VertexCount);
        Assert.Equal(1, doc.Mesh.TriangleCount);
        // 法线被显式提供，应保留 (0,0,1)
        Assert.Equal(0f, doc.Mesh.Normals[0], 4);
        Assert.Equal(0f, doc.Mesh.Normals[1], 4);
        Assert.Equal(1f, doc.Mesh.Normals[2], 4);
    }

    [Fact]
    public void Obj_VertexColors()
    {
        var path = TempPath("color.obj");
        File.WriteAllText(path,
            "v 0 0 0 1.0 0.0 0.0\n" +
            "v 1 0 0 0.0 1.0 0.0\n" +
            "v 0 1 0 0.0 0.0 1.0\n" +
            "f 1 2 3\n");

        var doc = new ObjLoader().Load(path);
        Assert.Equal(1f, doc.Mesh.Colors[0], 4);   // 红
        Assert.Equal(0f, doc.Mesh.Colors[1], 4);
        Assert.Equal(1f, doc.Mesh.Colors[4], 4);   // 绿
        Assert.Equal(1f, doc.Mesh.Colors[8], 4);   // 蓝
    }

    // ---------------------------------------------------------------- PLY

    [Fact]
    public void Ply_Ascii_Loads()
    {
        var path = TempPath("tri_ascii.ply");
        File.WriteAllText(path,
            "ply\nformat ascii 1.0\n" +
            "element vertex 3\n" +
            "property float x\nproperty float y\nproperty float z\n" +
            "element face 1\n" +
            "property list uchar int vertex_indices\n" +
            "end_header\n" +
            "0 0 0\n1 0 0\n0 1 0\n" +
            "3 0 1 2\n");

        var doc = new PlyLoader().Load(path);
        Assert.Equal(3, doc.Mesh.VertexCount);
        Assert.Equal(1, doc.Mesh.TriangleCount);
    }

    [Fact]
    public void Ply_BinaryLittleEndian_Loads()
    {
        var path = TempPath("tri_bin.ply");
        WriteBinaryPly(path, littleEndian: true);

        var doc = new PlyLoader().Load(path);
        Assert.Equal("PLY (binary_little_endian)", doc.Format);
        Assert.Equal(3, doc.Mesh.VertexCount);
        Assert.Equal(1, doc.Mesh.TriangleCount);
        Assert.Equal(1f, doc.Mesh.Positions[3], 3); // 第二个顶点 x=1
    }

    // ---------------------------------------------------------------- 3MF

    [Fact]
    public void ThreeMf_LoadsWithUnitScale()
    {
        var path = TempPath("box.3mf");
        WriteThreeMf(path, unitScale: 10f); // 1cm 单位 → 10mm

        var doc = new ThreeMfLoader().Load(path);
        Assert.Equal("3MF", doc.Format);
        Assert.Equal(3, doc.Mesh.VertexCount);
        Assert.Equal(1, doc.Mesh.TriangleCount);
        // 顶点 (0.1, 0, 0) 厘米 → 1mm（首个顶点为原点保持 0）
        Assert.Equal(0f, doc.Mesh.Positions[0], 3);
        Assert.Equal(1f, doc.Mesh.Positions[3], 3);
    }

    // ---------------------------------------------------------------- GLB

    [Fact]
    public void Glb_LoadsMinimalMesh()
    {
        var path = TempPath("tri.glb");
        WriteGlb(path);

        var doc = new GlbLoader().Load(path);
        Assert.Equal(3, doc.Mesh.VertexCount);
        Assert.Equal(1, doc.Mesh.TriangleCount);
        Assert.Equal(2f, doc.Mesh.Positions[3], 3);
    }

    // ---------------------------------------------------------------- 注册表与错误处理

    [Fact]
    public void Registry_UnknownExtension_Throws()
    {
        var path = TempPath("model.xyz");
        File.WriteAllText(path, "whatever");
        Assert.Throws<ModelLoadException>(() => new ModelLoaderRegistry().Load(path));
    }

    [Fact]
    public void Registry_MissingFile_Throws()
    {
        Assert.Throws<ModelLoadException>(() =>
            new ModelLoaderRegistry().Load(TempPath("nope.stl")));
    }

    [Fact]
    public void Registry_DispatchesByExtension()
    {
        var registry = new ModelLoaderRegistry();
        Assert.IsType<StlLoader>(registry.FindLoader("a.stl"));
        Assert.IsType<ObjLoader>(registry.FindLoader("a.OBJ"));
        Assert.IsType<PlyLoader>(registry.FindLoader("a.ply"));
        Assert.IsType<ThreeMfLoader>(registry.FindLoader("a.3mf"));
        Assert.IsType<GlbLoader>(registry.FindLoader("a.glb"));
        Assert.Null(registry.FindLoader("a.txt"));
    }

    // ---------------------------------------------------------------- 程序化模型

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DemoModels_AreValid(int model)
    {
        MeshData mesh = model switch
        {
            0 => DemoModels.CreateGear(),
            1 => DemoModels.CreateSteppedShaft(),
            2 => DemoModels.CreateHexNut(),
            _ => DemoModels.CreateTorusKnot(),
        };

        Assert.True(mesh.TriangleCount > 10);
        Assert.False(mesh.Bounds.IsEmpty);

        // 所有索引在范围内
        foreach (var idx in mesh.Indices)
            Assert.True(idx < (uint)mesh.VertexCount, $"索引 {idx} 越界");

        // 法线均已计算且为单位长度
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var n = new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
            Assert.True(n.Length() > 0.9f && n.Length() < 1.1f, "法线应为单位向量");
        }

        // 颜色与位置数量匹配
        Assert.Equal(mesh.Positions.Length, mesh.Colors.Length);
    }

    // ---------------------------------------------------------------- 测试数据构造

    /// <summary>单位立方体的 12 个三角形（每面 2 个）。</summary>
    private static (Vector3 a, Vector3 b, Vector3 c)[] CubeTriangles() => new[]
    {
        // 前面 z=+1
        (new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1)),
        (new Vector3(0, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1)),
        // 后面 z=0
        (new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0)),
        (new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0)),
        // 右面 x=1
        (new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1)),
        (new Vector3(1, 0, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1)),
        // 左面 x=0
        (new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1)),
        (new Vector3(0, 0, 0), new Vector3(0, 1, 1), new Vector3(0, 1, 0)),
        // 顶面 y=1
        (new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1)),
        (new Vector3(0, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 1, 0)),
        // 底面 y=0
        (new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1)),
        (new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1)),
    };

    private static void WriteBinaryStl(string path, (Vector3 a, Vector3 b, Vector3 c)[] tris, string header = "unit cube")
    {
        using var fs = File.Create(path);
        var headerBytes = new byte[80];
        Encoding.ASCII.GetBytes(header).CopyTo(headerBytes, 0);
        fs.Write(headerBytes);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)tris.Length);
        fs.Write(count);

        Span<byte> tri = stackalloc byte[50];
        foreach (var (a, b, c) in tris)
        {
            tri.Clear();
            BinaryPrimitives.WriteSingleLittleEndian(tri[..4], 0f);   // 法线占位
            BinaryPrimitives.WriteSingleLittleEndian(tri.Slice(4, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(tri.Slice(8, 4), 1f);
            WriteVec3(tri, 12, a);
            WriteVec3(tri, 24, b);
            WriteVec3(tri, 36, c);
            fs.Write(tri);
        }
    }

    private static void WriteVec3(Span<byte> buf, int offset, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buf.Slice(offset, 4), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(buf.Slice(offset + 4, 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.Slice(offset + 8, 4), v.Z);
    }

    private static void WriteAsciiStl(string path, (Vector3 a, Vector3 b, Vector3 c)[] tris)
    {
        var sb = new StringBuilder();
        sb.Append("solid ascii_cube\n");
        foreach (var (a, b, c) in tris)
        {
            sb.Append("facet normal 0 0 1\n outer loop\n");
            sb.Append($"  vertex {a.X} {a.Y} {a.Z}\n");
            sb.Append($"  vertex {b.X} {b.Y} {b.Z}\n");
            sb.Append($"  vertex {c.X} {c.Y} {c.Z}\n");
            sb.Append(" endloop\nendfacet\n");
        }
        sb.Append("endsolid ascii_cube\n");
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteBinaryPly(string path, bool littleEndian)
    {
        using var fs = File.Create(path);
        var header = Encoding.ASCII.GetBytes(
            "ply\nformat binary_little_endian 1.0\n" +
            "element vertex 3\n" +
            "property float x\nproperty float y\nproperty float z\n" +
            "element face 1\n" +
            "property list uchar int vertex_indices\n" +
            "end_header\n");
        fs.Write(header);

        Vector3[] verts = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        Span<byte> b = stackalloc byte[12];
        foreach (var v in verts)
        {
            b.Clear();
            if (littleEndian)
            {
                BinaryPrimitives.WriteSingleLittleEndian(b[..4], v.X);
                BinaryPrimitives.WriteSingleLittleEndian(b.Slice(4, 4), v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(b.Slice(8, 4), v.Z);
            }
            else
            {
                BinaryPrimitives.WriteSingleBigEndian(b[..4], v.X);
                BinaryPrimitives.WriteSingleBigEndian(b.Slice(4, 4), v.Y);
                BinaryPrimitives.WriteSingleBigEndian(b.Slice(8, 4), v.Z);
            }
            fs.Write(b);
        }

        Span<byte> face = stackalloc byte[13];
        face[0] = 3;
        if (littleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(face.Slice(1, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(face.Slice(5, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(face.Slice(9, 4), 2);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(face.Slice(1, 4), 0);
            BinaryPrimitives.WriteInt32BigEndian(face.Slice(5, 4), 1);
            BinaryPrimitives.WriteInt32BigEndian(face.Slice(9, 4), 2);
        }
        fs.Write(face);
    }

    private static void WriteThreeMf(string path, float unitScale)
    {
        // 单位由 XML 属性声明；此处用毫米单位直接写毫米坐标更直观，
        // 但为了测试单位换算，写入 (0.1, 0, 0) 并声明 unit="centimeter"
        string unit = unitScale == 10f ? "centimeter" : "millimeter";
        float v = unitScale == 10f ? 0.1f : 1f;

        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<model unit=\"" + unit + "\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">\n" +
            "  <resources>\n" +
            "    <object id=\"1\" type=\"model\" pid=\"0\" pindex=\"0\">\n" +
            "      <mesh>\n" +
            "        <vertices>\n" +
            $"          <vertex x=\"0\" y=\"0\" z=\"0\"/>\n" +
            $"          <vertex x=\"{v}\" y=\"0\" z=\"0\"/>\n" +
            $"          <vertex x=\"0\" y=\"{v}\" z=\"0\"/>\n" +
            "        </vertices>\n" +
            "        <triangles>\n" +
            "          <triangle v1=\"0\" v2=\"1\" v3=\"2\"/>\n" +
            "        </triangles>\n" +
            "      </mesh>\n" +
            "    </object>\n" +
            "  </resources>\n" +
            "  <build>\n" +
            "    <item objectid=\"1\"/>\n" +
            "  </build>\n" +
            "</model>\n";

        using var fs = File.Create(path);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("3D/3dmodel.model");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(xml);
    }

    private static void WriteGlb(string path)
    {
        // 一个三角形：位置 (0,0,0) (2,0,0) (0,2,0)，含索引
        float[] positions = { 0, 0, 0, 2, 0, 0, 0, 2, 0 };
        ushort[] indices = { 0, 1, 2 };

        var bin = new byte[36 + 6];
        Buffer.BlockCopy(positions, 0, bin, 0, 36);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()).CopyTo(bin.AsSpan(36));

        var json =
            "{\n" +
            "  \"asset\": { \"version\": \"2.0\" },\n" +
            "  \"scene\": 0,\n" +
            "  \"scenes\": [ { \"nodes\": [ 0 ] } ],\n" +
            "  \"nodes\": [ { \"mesh\": 0 } ],\n" +
            "  \"meshes\": [ { \"primitives\": [ { \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 } ] } ],\n" +
            "  \"buffers\": [ { \"byteLength\": " + bin.Length + " } ],\n" +
            "  \"bufferViews\": [\n" +
            "    { \"buffer\": 0, \"byteOffset\": 0, \"byteLength\": 36 },\n" +
            "    { \"buffer\": 0, \"byteOffset\": 36, \"byteLength\": 6 }\n" +
            "  ],\n" +
            "  \"accessors\": [\n" +
            "    { \"bufferView\": 0, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC3\", \"max\": [2,2,0], \"min\": [0,0,0] },\n" +
            "    { \"bufferView\": 1, \"componentType\": 5123, \"count\": 3, \"type\": \"SCALAR\" }\n" +
            "  ]\n" +
            "}";
        var jsonBytes = Encoding.ASCII.GetBytes(json);
        var jsonPad = (4 - jsonBytes.Length % 4) % 4;
        var binPad = (4 - bin.Length % 4) % 4;

        using var fs = File.Create(path);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), (uint)(12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length + binPad));
        fs.Write(header);

        Span<byte> jsonChunkHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(jsonChunkHeader, (uint)(jsonBytes.Length + jsonPad));
        BinaryPrimitives.WriteUInt32LittleEndian(jsonChunkHeader.Slice(4, 4), 0x4E4F534A);
        fs.Write(jsonChunkHeader);
        fs.Write(jsonBytes);
        fs.Write(new byte[jsonPad]);

        Span<byte> binChunkHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(binChunkHeader, (uint)(bin.Length + binPad));
        BinaryPrimitives.WriteUInt32LittleEndian(binChunkHeader.Slice(4, 4), 0x004E4942);
        fs.Write(binChunkHeader);
        fs.Write(bin);
        fs.Write(new byte[binPad]);
    }
}
