using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia3DViewer.Core;
using Avalonia3DViewer.Core.Annotations;
using Avalonia3DViewer.Core.Scene;
using Avalonia3DViewer.Core.Camera;
using Avalonia3DViewer.Core.Loading;
using Avalonia3DViewer.Core.Procedural;

namespace Avalonia3DViewer.Desktop;

public partial class MainWindow : Window
{
    private readonly ViewerSession _session = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<Annotation, TextBlock> _labels = new();
    private double _lastTickSeconds;
    private double _lastFpsSeconds;
    private bool _ready; // XAML 初始化期间的事件先于 Attach 到达，需守卫

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _session;
        Viewport.Attach(_session);

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _tickTimer.Tick += OnTick;

        Loaded += OnLoaded;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, (s, e) =>
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });

        _ready = true;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 扫描 samples 目录并默认加载 teapot.obj（GPU 上传延迟到首帧，无需等待 GL 上下文）
        LoadSampleList();

        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        _lastFpsSeconds = _lastTickSeconds;
        _tickTimer.Start();

        if (_samples.Count > 0)
            await LoadFileAsync(_samples[0].Path);
    }

    // ---------------------------------------------------------------- 示例模型

    private sealed record SampleModel(string Display, string Path);

    private readonly List<SampleModel> _samples = new();
    private bool _suppressSampleSelection;

    /// <summary>从程序目录向上查找 samples 目录（发布目录与开发目录都能命中）。</summary>
    private static string? FindSamplesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void LoadSampleList()
    {
        var dir = FindSamplesDirectory();
        if (dir is null)
        {
            _session.StatusText = "未找到 samples 目录，请用「打开模型…」加载文件";
            return;
        }

        var files = Directory.GetFiles(dir)
            .Where(f => _session.Loaders.FindLoader(f) is not null)
            .OrderBy(f => Path.GetFileName(f).Equals("teapot.obj", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
            _samples.Add(new SampleModel(Path.GetFileName(file), file));

        _suppressSampleSelection = true;
        SampleBox.ItemsSource = _samples.Select(s => s.Display).ToList();
        if (_samples.Count > 0)
            SampleBox.SelectedIndex = 0;
        _suppressSampleSelection = false;

        if (_samples.Count == 0)
            _session.StatusText = "samples 目录中没有可识别的模型";
    }

    private void OnSampleModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !IsLoaded || _suppressSampleSelection) return;
        int index = SampleBox.SelectedIndex;
        if (index >= 0 && index < _samples.Count)
            _ = LoadFileAsync(_samples[index].Path);
    }

    // ---------------------------------------------------------------- 帧循环（UI 线程）

    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastTickSeconds, 0.0001, 0.1);
        _lastTickSeconds = now;

        var size = new Vector2((float)Viewport.Bounds.Width, (float)Viewport.Bounds.Height);
        if (size.Y > 1f)
            _session.Camera.AspectRatio = size.X / size.Y;

        bool moving = _session.Update(dt);
        UpdateOverlayLabels();

        // 兜底：若 GL 已初始化但尚未渲染过任何一帧，持续请求首帧
        // （RequestFrame 在初始化前是空操作，不会触发 Avalonia 的过早请求缺陷）
        if (!_session.HasRenderedFrame)
            Viewport.RequestFrame();

        if (moving)
            Viewport.RequestFrame();

        if (now - _lastFpsSeconds >= 0.5)
        {
            double fps = _session.ConsumeFps(now - _lastFpsSeconds);
            _session.FpsText = fps > 0 ? $"{fps:F0} FPS" : "-- FPS";
            _lastFpsSeconds = now;
        }
    }

    private void UpdateOverlayLabels()
    {
        var items = _session.Annotations.Items;
        if (_labels.Count != items.Count)
        {
            LabelLayer.Children.Clear();
            _labels.Clear();
            foreach (var ann in items)
            {
                var tb = new TextBlock
                {
                    Text = ann.Label,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(178, 82, 16)),
                    IsVisible = false,
                };
                _labels[ann] = tb;
                LabelLayer.Children.Add(tb);
            }
        }

        var size = new Vector2((float)Viewport.Bounds.Width, (float)Viewport.Bounds.Height);
        if (size.X < 1 || size.Y < 1) return;

        foreach (var (ann, tb) in _labels)
        {
            var p = _session.Camera.ProjectToScreen(ann.Position, size);
            if (p.HasValue)
            {
                Canvas.SetLeft(tb, p.Value.X + 14);
                Canvas.SetTop(tb, p.Value.Y - 9);
                tb.IsVisible = true;
            }
            else
            {
                tb.IsVisible = false;
            }
        }
    }

    // ---------------------------------------------------------------- 模型加载

    private async void OnOpenModelClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 3D 模型文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("所有支持的模型")
                {
                    Patterns = new[] { "*.stl", "*.obj", "*.ply", "*.3mf", "*.glb" },
                },
                FilePickerFileTypes.All,
            },
        });

        if (files.Count == 0) return;
        await LoadFileAsync(files[0].Path.LocalPath);
    }

    private async Task LoadFileAsync(string path)
    {
        _session.IsLoading = true;
        _session.StatusText = $"正在加载 {Path.GetFileName(path)} …";
        try
        {
            var doc = await Task.Run(() => _session.Loaders.Load(path));
            _session.SetModel(doc);
            Viewport.PushSceneToRenderer();
            _session.StatusText = $"已加载 {doc.FileName}（{doc.Mesh.TriangleCount:N0} 三角面，{doc.LoadMilliseconds:F0} ms）";
        }
        catch (ModelLoadException ex)
        {
            _session.StatusText = $"加载失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            _session.StatusText = $"加载失败: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _session.IsLoading = false;
        }
    }

    // ---------------------------------------------------------------- 拖放支持

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var files = e.DataTransfer.TryGetFiles();
            var path = files?.FirstOrDefault()?.Path?.LocalPath;
            if (string.IsNullOrEmpty(path))
            {
                _session.StatusText = "拖入的文件无法识别";
                return;
            }
            _ = LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            _session.StatusText = $"拖放加载失败: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 工具栏

    private void OnViewModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _session.Mode = ViewModeBox.SelectedIndex switch
        {
            1 => Avalonia3DViewer.Desktop.Rendering.ViewMode.Wireframe,
            2 => Avalonia3DViewer.Desktop.Rendering.ViewMode.Points,
            _ => Avalonia3DViewer.Desktop.Rendering.ViewMode.Shaded,
        };
        _session.RebuildSnapshot();
        Viewport.RequestFrame();
    }

    private void On2DToggleClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyLayout(View2DToggle.IsChecked == true ? ViewLayout.TwoD : ViewLayout.ThreeD);
    }

    /// <summary>切换 2D/3D 布局（带过渡动画）。</summary>
    private void ApplyLayout(ViewLayout layout)
    {
        _session.SetLayout(layout);
        View2DToggle.IsChecked = layout == ViewLayout.TwoD;
        Viewport.RequestFrame();
    }

    private void OnStandardViewClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is Button { Tag: string tag })
        {
            // 标准方位属于 3D 视图：若处于 2D 或过渡中，先立即回到 3D
            if (_session.IsTwoD || _session.IsViewTransitioning)
            {
                _session.ApplyLayoutImmediate(ViewLayout.ThreeD);
                View2DToggle.IsChecked = false;
            }
            _session.Camera.SetStandardView(Enum.Parse<StandardView>(tag));
            _session.RebuildSnapshot();
            Viewport.RequestFrame();
        }
    }

    private void OnFitViewClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_session.Model != null)
        {
            _session.Camera.FitToBounds(_session.Model.Mesh.Bounds);
            if (_session.IsTwoD)
                _session.ApplyLayoutImmediate(ViewLayout.TwoD);
            _session.RebuildSnapshot();
            Viewport.RequestFrame();
        }
    }

    private void OnResetViewClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_session.Model != null)
        {
            _session.Camera.Reset(_session.Model.Mesh.Bounds);
            _session.RebuildSnapshot();
            Viewport.RequestFrame();
        }
    }

    private void OnSsaaToggleClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _session.SsaaEnabled = SsaaToggle.IsChecked == true;
        _session.RebuildSnapshot();
        Viewport.RequestFrame();
    }

    private void OnAnnotationToggleClick(object? sender, RoutedEventArgs e)
    {
        SetAnnotationMode(AnnotationToggle.IsChecked == true);
    }

    private void OnMoveToggleClick(object? sender, RoutedEventArgs e)
    {
        SetMoveMode(MoveToggle.IsChecked == true);
    }

    /// <summary>复位模型坐标到原始位置（标注随模型一起复位）。</summary>
    private void OnResetOffsetClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_session.Transform.IsIdentity)
        {
            _session.StatusText = "模型已在原始坐标";
            return;
        }
        _session.ResetModelOffset();
        Viewport.PushSceneToRenderer();
        _session.StatusText = "模型坐标已复位";
    }

    /// <summary>切换移动模式（与标注模式互斥，避免单击语义冲突）。</summary>
    // ---------------------------------------------------------------- 2D 草图

    private void OnSketchToolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !IsLoaded) return;
        SetSketchTool(SketchToolBox.SelectedIndex switch
        {
            1 => SketchTool.Rectangle,
            2 => SketchTool.Circle,
            _ => SketchTool.None,
        });
    }

    /// <summary>切换 2D 绘图工具（自动切到 2D 画布，并与移动/标注模式互斥）。</summary>
    private void SetSketchTool(SketchTool tool)
    {
        if (!_ready) return;

        if (tool != SketchTool.None)
        {
            // 绘图只发生在 2D 画布上：自动切换（带过渡动画）
            if (!_session.IsTwoD && !_session.IsViewTransitioning)
                ApplyLayout(ViewLayout.TwoD);

            SetMoveMode(false);
            SetAnnotationMode(false);
        }

        _session.SketchTool = tool;
        int index = (int)tool;
        if (SketchToolBox.SelectedIndex != index) SketchToolBox.SelectedIndex = index;
        Viewport.UpdateInteractionCursor();

        _session.StatusText = tool switch
        {
            SketchTool.Rectangle => "矩形工具：在 2D 画布上按住左键拖拽绘制（Esc 退出）",
            SketchTool.Circle => "圆工具：从圆心向外拖拽，拖拽距离为半径（Esc 退出）",
            _ => _session.IsTwoD ? "2D 画布：左键拖拽平移" : "3D 视图：左键拖拽旋转",
        };
    }

    private void OnClearSketchClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_session.Sketch.Count == 0)
        {
            _session.StatusText = "草图已经是空的";
            return;
        }

        _session.Sketch.Clear();
        _session.SketchPreview = null;
        _session.RefreshSketchInfo();
        Viewport.PushSketchToRenderer();
        _session.StatusText = "已清空 2D 草图";
    }

    private void OnFlatShadingToggleClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _session.FlatShading = FlatShadingToggle.IsChecked == true;
        _session.RebuildSnapshot();
        Viewport.RequestFrame();
        _session.StatusText = _session.FlatShading
            ? "平面着色开启：每个三角面单色（工程网格观感，与 XD2D/OCCT 显示一致）"
            : "平面着色关闭：平滑法线插值（曲面连续观感）";
    }

    private void SetMoveMode(bool enabled)
    {
        if (!_ready) return;
        _session.MoveMode = enabled;
        if (enabled && _session.AnnotationMode)
        {
            _session.AnnotationMode = false;
            AnnotationToggle.IsChecked = false;
        }
        if (enabled && _session.SketchTool != SketchTool.None)
        {
            _session.SketchTool = SketchTool.None;
            SketchToolBox.SelectedIndex = 0;
        }
        MoveToggle.IsChecked = enabled;
        Viewport.UpdateInteractionCursor();
        _session.StatusText = enabled
            ? "移动模式已开启：按住左键拖动模型改变坐标（Shift 锁定轴向）"
            : "移动模式已关闭";
    }

    /// <summary>切换标注模式（与移动模式互斥）。</summary>
    private void SetAnnotationMode(bool enabled)
    {
        if (!_ready) return;
        _session.AnnotationMode = enabled;
        if (enabled && _session.MoveMode)
        {
            _session.MoveMode = false;
            MoveToggle.IsChecked = false;
        }
        if (enabled && _session.SketchTool != SketchTool.None)
        {
            _session.SketchTool = SketchTool.None;
            SketchToolBox.SelectedIndex = 0;
        }
        AnnotationToggle.IsChecked = enabled;
        Viewport.UpdateInteractionCursor();
        _session.StatusText = enabled
            ? "标注模式已开启：点击模型表面放置标注点"
            : "标注模式已关闭";
    }

    // ---------------------------------------------------------------- 标注操作

    private void OnDeleteAnnotationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Annotation ann })
        {
            _session.DeleteAnnotation(ann);
            Viewport.PushSceneToRenderer();
        }
    }

    private void OnClearAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        _session.ClearAnnotations();
        Viewport.PushSceneToRenderer();
        _session.StatusText = "已清除全部标注";
    }

    private async void OnExportAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        if (_session.Annotations.Count == 0)
        {
            _session.StatusText = "没有可导出的标注";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出标注 JSON",
            SuggestedFileName = "annotations.json",
            DefaultExtension = "json",
        });
        if (file is null) return;

        try
        {
            var json = _session.Annotations.ExportJson(_session.Model?.FileName);
            await File.WriteAllTextAsync(file.Path.LocalPath, json);
            _session.StatusText = $"标注已导出: {file.Path.LocalPath}";
        }
        catch (Exception ex)
        {
            _session.StatusText = $"导出失败: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- 键盘

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                if (_session.SelectedAnnotation != null)
                {
                    _session.DeleteAnnotation(_session.SelectedAnnotation);
                    Viewport.PushSceneToRenderer();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (_session.AnnotationMode || _session.MoveMode || _session.SketchTool != SketchTool.None)
                {
                    SetAnnotationMode(false);
                    SetMoveMode(false);
                    SetSketchTool(SketchTool.None);
                    _session.SelectedAnnotation = null;
                    _session.RefreshAnnotations();
                    Viewport.PushSceneToRenderer();
                    e.Handled = true;
                }
                break;
            case Key.F:
                OnFitViewClick(this, e);
                break;
            case Key.R:
                OnResetViewClick(this, e);
                break;
            case Key.G:
                // G：切换移动模型模式（拖动改坐标）
                SetMoveMode(!_session.MoveMode);
                e.Handled = true;
                break;
            case Key.T:
                // T：2D 画布 / 3D 视图 切换（带过渡动画）
                ApplyLayout(_session.IsTwoD ? ViewLayout.ThreeD : ViewLayout.TwoD);
                e.Handled = true;
                break;
            case Key.K:
                // K：平面着色开关（每面单色，工程网格观感）
                FlatShadingToggle.IsChecked = !(FlatShadingToggle.IsChecked == true);
                OnFlatShadingToggleClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1:
                // 1：矩形草图工具
                SetSketchTool(_session.SketchTool == SketchTool.Rectangle ? SketchTool.None : SketchTool.Rectangle);
                e.Handled = true;
                break;
            case Key.D2 or Key.NumPad2:
                // 2：圆草图工具
                SetSketchTool(_session.SketchTool == SketchTool.Circle ? SketchTool.None : SketchTool.Circle);
                e.Handled = true;
                break;
        }
    }
}
