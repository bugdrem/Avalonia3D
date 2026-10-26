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

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 初始加载示例模型（GPU 上传延迟到首帧，无需等待 GL 上下文）
        LoadDemoModel(0);

        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        _lastFpsSeconds = _lastTickSeconds;
        _tickTimer.Start();
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

    private void OnDemoModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !IsLoaded) return;
        LoadDemoModel(DemoModelBox.SelectedIndex);
    }

    private void LoadDemoModel(int index)
    {
        try
        {
            var mesh = index switch
            {
                1 => DemoModels.CreateSteppedShaft(),
                2 => DemoModels.CreateHexNut(),
                3 => DemoModels.CreateTorusKnot(),
                _ => DemoModels.CreateGear(),
            };
            var doc = new ModelDocument
            {
                Mesh = mesh,
                FileName = "（内置示例）",
                Format = "程序化生成",
                LoadMilliseconds = 0,
            };
            _session.SetModel(doc);
            Viewport.PushSceneToRenderer();
            _session.StatusText = $"已加载示例模型: {mesh.Name}";
        }
        catch (Exception ex)
        {
            _session.StatusText = $"示例模型生成失败: {ex.Message}";
        }
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

    private void OnStandardViewClick(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is Button { Tag: string tag })
        {
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
    private void SetMoveMode(bool enabled)
    {
        if (!_ready) return;
        _session.MoveMode = enabled;
        if (enabled && _session.AnnotationMode)
        {
            _session.AnnotationMode = false;
            AnnotationToggle.IsChecked = false;
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
                if (_session.AnnotationMode || _session.MoveMode)
                {
                    SetAnnotationMode(false);
                    SetMoveMode(false);
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
        }
    }
}
