using System.Numerics;
using Avalonia3DViewer.Core.Annotations;
using Xunit;

namespace Avalonia3DViewer.Core.Tests;

/// <summary>标注集合与测距测试。</summary>
public class AnnotationTests
{
    [Fact]
    public void Add_AutoNumbersLabels()
    {
        var c = new AnnotationCollection();
        c.Add(new Vector3(0, 0, 0), Vector3.UnitY);
        c.Add(new Vector3(1, 0, 0), Vector3.UnitY);
        c.Add(new Vector3(2, 0, 0), Vector3.UnitY);

        Assert.Equal(3, c.Count);
        Assert.Equal("P1", c.Items[0].Label);
        Assert.Equal("P2", c.Items[1].Label);
        Assert.Equal("P3", c.Items[2].Label);
    }

    [Fact]
    public void Remove_RenumbersConsecutively()
    {
        var c = new AnnotationCollection();
        var a1 = c.Add(new Vector3(0, 0, 0), Vector3.UnitY);
        c.Add(new Vector3(1, 0, 0), Vector3.UnitY);
        c.Add(new Vector3(2, 0, 0), Vector3.UnitY);

        c.Remove(a1);

        Assert.Equal(2, c.Count);
        Assert.Equal("P1", c.Items[0].Label);
        Assert.Equal("P2", c.Items[1].Label);

        // 新增标注从 P3 继续
        var a4 = c.Add(new Vector3(3, 0, 0), Vector3.UnitY);
        Assert.Equal("P3", a4.Label);
    }

    [Fact]
    public void Measurements_ConsecutiveDistances()
    {
        var c = new AnnotationCollection();
        c.Add(new Vector3(0, 0, 0), Vector3.UnitY);
        c.Add(new Vector3(10, 0, 0), Vector3.UnitY);
        c.Add(new Vector3(10, 5, 0), Vector3.UnitY);

        var m = c.Measurements;
        Assert.Equal(2, m.Count);
        Assert.Equal(10f, m[0].Distance, 3);
        Assert.Equal(5f, m[1].Distance, 3);
    }

    [Fact]
    public void ExportJson_ContainsAnnotationsAndMeasurements()
    {
        var c = new AnnotationCollection();
        c.Add(new Vector3(1.5f, 2.5f, 3.5f), Vector3.UnitY);
        c.Add(new Vector3(4.5f, 2.5f, 3.5f), Vector3.UnitY);

        string json = c.ExportJson("test-model.stl");

        Assert.Contains("\"model\": \"test-model.stl\"", json);
        Assert.Contains("\"unit\": \"mm\"", json);
        Assert.Contains("\"label\": \"P1\"", json);
        Assert.Contains("1.5000", json);   // 保留 4 位小数
        Assert.Contains("\"distance\": 3.0000", json);
        Assert.DoesNotContain("NaN", json);
    }

    [Fact]
    public void Clear_ResetsState()
    {
        var c = new AnnotationCollection();
        c.Add(new Vector3(0, 0, 0), Vector3.UnitY);
        c.Clear();

        Assert.Empty(c.Items);
        Assert.Empty(c.Measurements);

        var a = c.Add(new Vector3(1, 1, 1), Vector3.UnitY);
        Assert.Equal("P1", a.Label);
    }

    [Fact]
    public void NormalIsNormalizedOnAdd()
    {
        var c = new AnnotationCollection();
        var a = c.Add(Vector3.Zero, new Vector3(0, 3, 0));
        Assert.Equal(1f, a.Normal.Length(), 4);
    }
}
