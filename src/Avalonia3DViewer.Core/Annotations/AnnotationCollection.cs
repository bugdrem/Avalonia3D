using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Avalonia3DViewer.Core.Annotations;

/// <summary>
/// 3D 标注点（打标）。位置为模型坐标系（毫米），支持命名与删除。
/// </summary>
public sealed class Annotation : INotifyPropertyChanged
{
    private string _label;

    public Annotation(int index, Vector3 position, Vector3 normal)
    {
        Index = index;
        _position = position;
        _normal = normal;
        _label = $"P{index}";
    }

    public int Index { get; internal set; }

    private Vector3 _position;
    public Vector3 Position
    {
        get => _position;
        set => SetField(ref _position, value);
    }

    private Vector3 _normal;
    public Vector3 Normal
    {
        get => _normal;
        set => SetField(ref _normal, value);
    }

    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string PositionText =>
        $"X {_position.X:F2}  Y {_position.Y:F2}  Z {_position.Z:F2} mm";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Position)) OnPropertyChanged(nameof(PositionText));
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 标注集合：维护编号、相邻点测距（连续两点间距离），支持 JSON 导出。
/// </summary>
public sealed class AnnotationCollection
{
    public ObservableCollection<Annotation> Items { get; } = new();

    private int _nextIndex = 1;

    public int Count => Items.Count;

    public Annotation Add(Vector3 position, Vector3 normal)
    {
        var ann = new Annotation(_nextIndex++, position, Vector3.Normalize(normal));
        Items.Add(ann);
        return ann;
    }

    public void Remove(Annotation ann)
    {
        Items.Remove(ann);
        // 重新连续编号，保持 P1..Pn
        _nextIndex = 1;
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            item.Index = _nextIndex++;
            item.Label = $"P{item.Index}";
        }
    }

    public void Clear()
    {
        Items.Clear();
        _nextIndex = 1;
    }

    /// <summary>
    /// 整体平移所有标注（模型被拖动改变坐标时，标注附着在模型表面随之移动）。
    /// </summary>
    public void TranslateAll(Vector3 delta)
    {
        if (delta == Vector3.Zero) return;
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Position += delta;
        }
    }

    /// <summary>相邻标注之间的距离测量列表（P1→P2, P2→P3, ...）。</summary>
    public List<(Annotation From, Annotation To, float Distance)> Measurements
    {
        get
        {
            var list = new List<(Annotation, Annotation, float)>();
            for (int i = 0; i + 1 < Items.Count; i++)
            {
                var a = Items[i];
                var b = Items[i + 1];
                list.Add((a, b, Vector3.Distance(a.Position, b.Position)));
            }
            return list;
        }
    }

    /// <summary>导出为 JSON 文本（AOT 安全：全部为 POCO + 手写序列化）。</summary>
    public string ExportJson(string? modelName = null)
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\n  \"model\": ");
        WriteString(sb, modelName ?? "");
        sb.Append(",\n  \"unit\": \"mm\",\n  \"annotationCount\": ").Append(Items.Count);
        sb.Append(",\n  \"annotations\": [\n");
        for (int i = 0; i < Items.Count; i++)
        {
            var a = Items[i];
            sb.Append("    { \"label\": ");
            WriteString(sb, a.Label);
            sb.Append(", \"position\": [")
              .Append(F(a.Position.X)).Append(", ").Append(F(a.Position.Y)).Append(", ").Append(F(a.Position.Z))
              .Append("], \"normal\": [")
              .Append(F(a.Normal.X)).Append(", ").Append(F(a.Normal.Y)).Append(", ").Append(F(a.Normal.Z))
              .Append("] }");
            if (i + 1 < Items.Count) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ],\n  \"measurements\": [\n");
        var measurements = Measurements;
        for (int i = 0; i < measurements.Count; i++)
        {
            var (from, to, dist) = measurements[i];
            sb.Append("    { \"from\": ");
            WriteString(sb, from.Label);
            sb.Append(", \"to\": ");
            WriteString(sb, to.Label);
            sb.Append(", \"distance\": ").Append(F(dist)).Append(" }");
            if (i + 1 < measurements.Count) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}");
        return sb.ToString();

        static string F(float v) => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
