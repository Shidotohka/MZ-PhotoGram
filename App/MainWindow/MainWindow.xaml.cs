using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Photogrammetry.App.Services;
using Photogrammetry.Core;

namespace Photogrammetry.App;

public partial class MainWindow : Window
{
    // 本类被拆成多个 partial 文件，全部在本文件夹内，见 Docs/重构计划.md 第 4 节：
    //   MainWindow.xaml         界面骨架
    //   MainWindow.xaml.cs      外壳：菜单、工程读写、视图工具条、状态栏、底部页签
    //   MainWindow.Library.cs   影像库
    //   MainWindow.Tree.cs      数据树
    //   MainWindow.Modules.cs   作业方法与模块切换
    //   MainWindow.Interior.cs  内定向
    //   MainWindow.Gcp.cs       控制点与刺点
    //   MainWindow.Resection.cs 后方交会

    /// <summary>
    /// 模块 → (右上角提示, 工作区形态)。
    ///
    /// 模块栏只显示当前作业方法的那一串步骤，见 <see cref="Workflows"/>。
    /// 这里的键按"全部步骤"排列，与 XAML 里按钮的顺序一致，便于对照。
    /// </summary>
    private static readonly Dictionary<string, (string Hint, WorkspaceMode Mode)> Modules = new()
    {
        ["内定向"] = ("像素坐标 → 像平面坐标", WorkspaceMode.Single),
        ["后方交会"] = ("选中控制点 → 在影像上刺点 → 解算外方位", WorkspaceMode.Pick),
        ["前方交会"] = ("同名像点 → 地面坐标", WorkspaceMode.Double),
        ["相对定向"] = ("同名像点 → 相对定向元素", WorkspaceMode.Double),
        ["模型交会"] = ("同名像点 → 模型坐标", WorkspaceMode.Double),
        ["绝对定向"] = ("模型点 + 控制点 → 七参数", WorkspaceMode.Model),
        // 光束平差的工作区形态还没定（该模块未实现），暂用单图；形态定了再改这里与 §4.2
        ["光束平差"] = ("全部影像同时平差 → 外方位 + 物点坐标", WorkspaceMode.Single),
        ["报告"] = ("汇总输出", WorkspaceMode.Report)
    };

    /// <summary>
    /// 作业方法：名称 + 该方法在模块栏里显示的步骤，从左到右即执行顺序。
    ///
    /// 三条方法的后两条尚未实现，选中后对应模块只显示占位参数区。内定向与报告是
    /// 三条方法共有的首尾两步；中间的步骤各自不同——这正是它们要用下拉框区分的原因：
    /// 同一个「交会」在第一条里是求地面坐标，在第二条里是求模型坐标。
    /// </summary>
    private static readonly (string Name, string[] Steps)[] Workflows =
    {
        ("后方交会-前方交会法", new[] { "内定向", "后方交会", "前方交会", "报告" }),
        ("相对定向-绝对定向法", new[] { "内定向", "相对定向", "模型交会", "绝对定向", "报告" }),
        ("光束平差法", new[] { "内定向", "光束平差", "报告" })
    };

    private static readonly Brush OkBadge = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x5A));
    private static readonly Brush WarnBadge = new SolidColorBrush(Color.FromRgb(0xA8, 0x6A, 0x12));
    private static readonly Brush ErrBadge = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush DimBadge = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0x9F));
    private static readonly Brush MutBadge = new SolidColorBrush(Color.FromRgb(0x58, 0x63, 0x6E));
    private static readonly Brush AccentBadge = new SolidColorBrush(Color.FromRgb(0x0F, 0x6F, 0xA8));

    /// <summary>影像解码与缓存。只管位图，不弹窗、不碰工作区。</summary>
    private readonly ImageLibrary _images = new();

    private Project _project = new() { Name = "未命名工程" };
    private string? _projectPath;
    private Point _dragStart;

    /// <summary>当前解析出的相机（内定向需要它的框标坐标）。</summary>
    private Camera? _camera;

    /// <summary>框标取点模式下正在等待取点的框标编号；null 表示不在该模式。</summary>
    private int? _activeFiducialId;

    /// <summary>内定向当前是哪种方式：true = 框标仿射，false = 参数式。</summary>
    private bool _fiducialMode = true;

    public MainWindow()
    {
        InitializeComponent();

        Workspace.CursorMoved += OnCursorMoved;
        Workspace.ViewChanged += (_, _) => UpdateZoomLabel();
        Workspace.ImageLoaded += OnImageLoaded;
        Workspace.SelectionChanged += OnSelectionChanged;
        Workspace.ImageClosed += (_, _) => RefreshTree();
        Workspace.ImageDropped += (_, e) => _ = LoadItemAsync(e.Item, e.Target);
        Workspace.FiducialPicked += OnFiducialPicked;
        Workspace.FiducialUndoRequested += OnFiducialUndo;
        Workspace.GcpPicked += OnGcpPicked;

        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            // 先定作业方法：它决定模块栏显示哪一串步骤，并顺手把第一步选上
            MethodBox.ItemsSource = Workflows.Select(w => w.Name).ToArray();
            MethodBox.SelectedIndex = 0;

            foreach (var child in ModuleBar.Children)
            {
                if (child is RadioButton { IsChecked: true } rb) ApplyModule(rb);
            }

            RefreshTree();
            UpdateStatusCounts();
            UpdateZoomLabel();
        };
    }

    // ---------- 菜单 ----------

    private void FileMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private void LoadMenu_Click(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private static void OpenMenu(object sender)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        _project = new Project { Name = "未命名工程" };
        _projectPath = null;
        _images.Clear();
        Workspace.Reset();
        RefreshTree();
        UpdateStatusCounts();
        UpdateProjectTitle();
        ImageInfoText.Text = "未载入";
        PointInfoText.Text = "未选择";
        DiagSummary.Text = "诊断 · 新建工程";
        DiagSummary.Foreground = DimBadge;
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "工程另存为",
            Filter = $"摄影测量工程|*{ProjectFile.Extension}",
            FileName = _project.Name + ProjectFile.Extension
        };
        if (dialog.ShowDialog(this) != true) return;

        _projectPath = dialog.FileName;
        SaveProject_Click(sender, e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 工程 ----------

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开工程",
            Filter = $"摄影测量工程|*{ProjectFile.Extension}|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        await OpenProjectAsync(dialog.FileName);
    }

    /// <summary>打开指定工程文件。也供离屏渲染工具调用，见 MainWindow.Tooling.cs。</summary>
    internal async Task OpenProjectAsync(string path)
    {
        try
        {
            var project = ProjectFile.Load(path);
            _project = project;
            _projectPath = path;

            _images.Clear();
            Workspace.Reset();
            RefreshTree();
            UpdateStatusCounts();
            UpdateProjectTitle();

            var missing = project.Images.Count(i => i.FileState == ImageFileState.Missing);
            DiagSummary.Text = missing > 0
                ? $"⚠ 工程已打开：影像 {project.Images.Count} 张，其中 {missing} 张文件缺失"
                : $"✓ 工程已打开：影像 {project.Images.Count} 张，像点 {project.Images.Sum(i => i.Points.Count)} 个";
            DiagSummary.Foreground = missing > 0 ? WarnBadge : OkBadge;

            // 打开工程后先把第一张可用影像放进工作区，避免空白窗口
            var first = project.Images.FirstOrDefault(i => i.FileState == ImageFileState.Ok);
            if (first is not null) await LoadItemAsync(first);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开工程",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var path = _projectPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存工程",
                Filter = $"摄影测量工程|*{ProjectFile.Extension}",
                FileName = _project.Name + ProjectFile.Extension
            };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }

        try
        {
            SetBusy(true, "正在保存工程…");
            ProjectFile.Save(_project, path);
            _projectPath = path;
            UpdateProjectTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void UpdateProjectTitle()
    {
        ProjectNameText.Text = string.IsNullOrEmpty(_project.Name) ? "未命名工程" : _project.Name;
        Title = $"摄影测量工作台 — {ProjectNameText.Text}";
    }

    // ---------- 视图 ----------

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Workspace.ZoomBy(1.16);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Workspace.ZoomBy(1 / 1.16);

    private void Fit_Click(object sender, RoutedEventArgs e) => Workspace.FitToWindow();

    /// <summary>删除当前选中的像点。已绑定的点必须走量测表，避免误删绑定关系。</summary>
    private void DeletePoint_Click(object sender, RoutedEventArgs e)
    {
        var point = Workspace.SelectedPoint;
        if (point is null)
        {
            DiagSummary.Text = "! 先在工作区里点选一个像点，再删除";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        if (point.IsBound)
        {
            DiagSummary.Text = "! 已绑定的点不能直接删除，请在量测表里解除绑定";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        Workspace.DeleteSelected();
        UpdateStatusCounts();
        RefreshTree();
        UpdateCheckPanel();

        DiagSummary.Text = $"✓ 已删除像点 {point.Id}";
        DiagSummary.Foreground = OkBadge;
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{Workspace.Scale * 100:0}%";

    private void UpdateStatusCounts()
    {
        StatusImageCount.Text = _project.Images.Count.ToString();
        StatusPointCount.Text = _project.Images.Sum(i => i.Points.Count).ToString();
        StatusGcpCount.Text = _project.Gcps.Count.ToString();
    }

    private void OnCursorMoved(object? sender, CursorEventArgs e)
    {
        if (!e.Inside)
        {
            StatusCursor.Text = "—";
            return;
        }

        var pixel = $"px {e.Pixel.I}, {e.Pixel.J}";
        StatusCursor.Text = e.ImageMm is { } mm
            ? $"{pixel}  ·  mm {mm.X:0.00}, {mm.Y:+0.00;-0.00}"
            : pixel;
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var point = Workspace.SelectedPoint;
        UpdateStatusCounts();

        if (point is null)
        {
            PointInfoText.Text = "未选择";
            return;
        }

        PointInfoText.Text =
            $"{point.Id}\n像素 {point.PixelX}, {point.PixelY}\n" +
            $"像平面 {point.PositionMm.X:0.000}, {point.PositionMm.Y:0.000} mm\n" +
            $"状态 {(point.IsBound ? "已绑定" : "未绑定")}";
    }

    // ---------- 底部页签 ----------

    private void BottomTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DiagPanel is null || sender is not RadioButton rb) return;

        var key = rb.Tag as string;
        MeasurePanel.Visibility = key == "meas" ? Visibility.Visible : Visibility.Collapsed;
        DiagPanel.Visibility = key == "diag" ? Visibility.Visible : Visibility.Collapsed;
        ResPanel.Visibility = key == "res" ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Visibility = key == "log" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 全局快捷键 ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.N) { NewProject_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.O) { OpenProject_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.S) { SaveProject_Click(sender, e); e.Handled = true; return; }
            if (e.Key == Key.I) { AddImage_Click(sender, e); e.Handled = true; return; }
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            SaveProjectAs_Click(sender, e);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_activeFiducialId is not null)
                {
                    CancelFiducialPicking();
                    DiagSummary.Text = "已停止框标取点";
                    DiagSummary.Foreground = DimBadge;
                }
                else if (_activeGcpId is not null)
                {
                    SetActiveGcp(null);
                    GcpGrid.SelectedItem = null;
                    DiagSummary.Text = "已停止刺点。在量测表里重新选一个控制点即可继续。";
                    DiagSummary.Foreground = DimBadge;
                }
                else
                {
                    Workspace.CancelSelection();
                }
                e.Handled = true;
                break;
            case Key.Delete:
                Workspace.DeleteSelected();
                UpdateStatusCounts();
                RefreshTree();
                e.Handled = true;
                break;
        }
    }

    // ---------- 忙碌遮罩 ----------

    private void SetBusy(bool busy, string? text)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) BusyText.Text = text;
    }
}
