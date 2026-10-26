using Avalonia;

namespace Avalonia3DViewer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Avalonia 初始化。注意：不要在此处做反射重的自定义配置，
    /// 以保持 NativeAOT / 全裁剪兼容（详见 README「NativeAOT」章节）。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 8096L * 1024 * 1024 });
}
