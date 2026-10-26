namespace Avalonia3DViewer.Core.Loading;

/// <summary>
/// 模型加载器注册表：按扩展名分发；对无扩展名/错误扩展名的文件做格式嗅探（当前仅 STL）。
/// </summary>
public sealed class ModelLoaderRegistry
{
    private readonly List<IModelLoader> _loaders = new();

    public ModelLoaderRegistry()
    {
        _loaders.Add(new StlLoader());
        _loaders.Add(new ObjLoader());
        _loaders.Add(new PlyLoader());
        _loaders.Add(new ThreeMfLoader());
        _loaders.Add(new GlbLoader());
    }

    public IReadOnlyList<IModelLoader> Loaders => _loaders;

    /// <summary>支持的过滤串，用于文件对话框（如 "*.stl;*.obj;*.ply;*.3mf;*.glb"）。</summary>
    public string FileDialogFilter
    {
        get
        {
            var all = string.Join(";", _loaders.SelectMany(l => l.SupportedExtensions));
            var perFormat = string.Join("|", _loaders.Select(l => $"{l.FormatName} 文件|{string.Join(";", l.SupportedExtensions)}"));
            return $"所有支持的模型|{all}|{perFormat}|所有文件|*.*";
        }
    }

    public IModelLoader? FindLoader(string path)
    {
        foreach (var loader in _loaders)
        {
            if (loader.CanLoad(path)) return loader;
        }
        return null;
    }

    /// <summary>加载指定文件。</summary>
    public ModelDocument Load(string path)
    {
        var loader = FindLoader(path);
        if (loader is null)
            throw new ModelLoadException(
                $"不支持的文件格式: {Path.GetExtension(path)}。支持: STL / OBJ / PLY / 3MF / GLB。");
        return loader.Load(path);
    }
}
