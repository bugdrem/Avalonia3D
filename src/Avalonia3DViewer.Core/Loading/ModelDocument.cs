namespace Avalonia3DViewer.Core.Loading;

/// <summary>模型文档：网格数据 + 元信息。</summary>
public sealed class ModelDocument
{
    public required Geometry.MeshData Mesh { get; init; }

    /// <summary>来源文件名（不含路径）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>格式名称，如 "STL (二进制)"。</summary>
    public string Format { get; init; } = string.Empty;

    /// <summary>模型声明的单位（统一换算为毫米后此处固定为 "mm"）。</summary>
    public string Unit { get; init; } = "mm";

    /// <summary>加载耗时（毫秒）。</summary>
    public double LoadMilliseconds { get; init; }

    public string Describe()
    {
        var name = string.IsNullOrEmpty(FileName) ? Mesh.Name : FileName;
        return $"{name} [{Format}] {Mesh.Describe()}";
    }
}

/// <summary>模型加载失败异常（携带用户可读的中文信息）。</summary>
public sealed class ModelLoadException : Exception
{
    public ModelLoadException(string message) : base(message) { }
    public ModelLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>模型加载器接口。</summary>
public interface IModelLoader
{
    /// <summary>格式名称。</summary>
    string FormatName { get; }

    /// <summary>支持的扩展名（小写，含点）。</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>根据扩展名判断是否能加载。</summary>
    bool CanLoad(string path);

    /// <summary>加载模型。失败时抛出 <see cref="ModelLoadException"/>。</summary>
    ModelDocument Load(string path);
}
