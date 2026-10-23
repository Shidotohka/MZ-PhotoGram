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
using Photogrammetry.Core;

namespace Photogrammetry.App;

public partial class MainWindow : Window
{
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

    private readonly Dictionary<string, BitmapSource> _bitmapCache = new();

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
        _bitmapCache.Clear();
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

    /// <summary>打开指定工程文件。也供离屏渲染工具调用。</summary>
    public async Task OpenProjectAsync(string path)
    {
        try
        {
            var project = ProjectFile.Load(path);
            _project = project;
            _projectPath = path;

            _bitmapCache.Clear();
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

    // ---------- 影像库 ----------

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加影像到工程",
            Filter = "影像文件|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            // 不预置内方位元素：δ 与主点要靠内定向解出来，填一组假的只会让读数看着像真的
            _project.Images.Add(new ImageItem
            {
                Id = UniqueImageId(Path.GetFileNameWithoutExtension(file)),
                FullPath = file,
                CameraId = _camera?.Id,
            });
        }

        RefreshTree();
        UpdateStatusCounts();
        DiagSummary.Text = $"✓ 已加入 {dialog.FileNames.Length} 张影像，双击或拖入工作区即可量测";
        DiagSummary.Foreground = OkBadge;
    }

    private string UniqueImageId(string baseId)
    {
        var id = baseId;
        var n = 2;
        while (_project.Images.Any(i => i.Id == id)) id = $"{baseId}({n++})";
        return id;
    }

    /// <summary>影像在工程里但还没解码过时，按需解码。</summary>
    private async Task<BitmapSource?> EnsureBitmapAsync(ImageItem item)
    {
        if (_bitmapCache.TryGetValue(item.Id, out var cached)) return cached;

        if (item.FullPath is null || !File.Exists(item.FullPath))
        {
            item.FileState = ImageFileState.Missing;
            MessageBox.Show(this, $"影像文件不存在：\n{item.FullPath}", "影像缺失",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        try
        {
            SetBusy(true, $"正在读取 {item.Id} …");
            var source = await Task.Run(() => DecodeImage(item.FullPath));

            _bitmapCache[item.Id] = source;
            item.Width = source.PixelWidth;
            item.Height = source.PixelHeight;
            item.FileState = ImageFileState.Ok;
            return source;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法读取影像",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>把影像载入工作区；target 为空则放进下一个可用视窗。</summary>
    private async Task LoadItemAsync(ImageItem item, ImageView? target = null)
    {
        var source = await EnsureBitmapAsync(item);
        if (source is null) return;

        if (target is null) Workspace.LoadImage(source, item);
        else Workspace.LoadInto(target, source, item);

        RefreshTree();
    }

    private static BitmapSource DecodeImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnImageLoaded(object? sender, ImageItem item)
    {
        var interior = item.Interior is { } io
            ? $"内方位：f {io.FocalLength:0.###} mm · δ {io.PixelSizeMm:0.000000} mm/px\n" +
              $"x0 {io.X0:+0.0000;-0.0000} · y0 {io.Y0:+0.0000;-0.0000} mm"
            : "内方位元素：未设置";

        ImageInfoText.Text = $"{item.Id}\n{item.Width} × {item.Height} px\n{interior}";

        Workspace.RefreshFiducials();
        if (_fiducialMode) EnsureFiducialPicking();
        UpdateFiducialUi();

        // 参数式面板回填当前影像的内参，方便改
        if (item.Interior is { } current)
        {
            ParamF.Text = current.FocalLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            ParamX0.Text = current.X0.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            ParamY0.Text = current.Y0.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            ParamPixel.Text = current.PixelSizeMm.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        }

        UpdateZoomLabel();
        OnSelectionChanged(null, EventArgs.Empty);

        // 切到另一张影像，「本片」列与可解算点数都要跟着变
        RefreshPointTable();
        UpdateResectionPanel();
    }

    // ---------- 数据树 ----------

    private void RefreshTree()
    {
        var roots = new List<TreeNode>
        {
            BuildImageGroup(),
            BuildPlaceholderGroup("控制点", "◆", _project.Gcps.Count, "尚未导入控制点"),
            BuildPlaceholderGroup("像对", "⊟", _project.Pairs.Count, "尚未建立像对"),
            BuildPlaceholderGroup("模型", "◈", _project.Models.Count, "尚未建立模型"),
            BuildPlaceholderGroup("计算结果", "≡", 0, "尚无计算结果")
        };

        DataTree.ItemsSource = roots;
    }

    private TreeNode BuildImageGroup()
    {
        var group = new TreeNode
        {
            Icon = "▣",
            Title = $"影像库 · {_project.Images.Count}"
        };

        if (_project.Images.Count == 0)
        {
            group.Children.Add(Placeholder("尚未添加影像"));
            return group;
        }

        foreach (var image in _project.Images)
        {
            var slot = Workspace.SlotOf(image);
            var missing = image.FileState == ImageFileState.Missing;

            var badge = slot ?? (missing ? "缺失" : image.Points.Count > 0 ? $"{image.Points.Count} 点" : "");
            var badgeBrush = slot is not null ? AccentBadge : missing ? ErrBadge : DimBadge;

            var node = new TreeNode
            {
                Icon = missing ? "⊘" : slot is not null ? "▶" : "▣",
                Title = missing ? $"{image.Id}（文件缺失）" : image.Id,
                Badge = badge,
                BadgeBrush = badgeBrush,
                Tag = image
            };

            foreach (var point in image.Points)
            {
                node.Children.Add(new TreeNode
                {
                    Icon = "·",
                    Title = point.Id,
                    Badge = $"{point.PixelX}, {point.PixelY}",
                    BadgeBrush = DimBadge,
                    Tag = point,
                    IsExpanded = false
                });
            }

            group.Children.Add(node);
        }

        return group;
    }

    private TreeNode BuildPlaceholderGroup(string title, string icon, int count, string emptyHint)
    {
        var group = new TreeNode { Icon = icon, Title = $"{title} · {count}" };
        if (count == 0) group.Children.Add(Placeholder(emptyHint));
        return group;
    }

    private static TreeNode Placeholder(string text) => new()
    {
        Icon = "",
        Title = text,
        IsPlaceholder = true,
        BadgeBrush = DimBadge
    };

    private void DataTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeNode { Tag: ImagePoint point }) return;

        var owner = _project.Images.FirstOrDefault(i => i.Points.Contains(point));
        if (owner is not null) _ = SelectPointAsync(owner, point);
    }

    private async Task SelectPointAsync(ImageItem owner, ImagePoint point)
    {
        await LoadItemAsync(owner);
        Workspace.SelectPoint(point);
    }

    // ---------- 树上的拖拽 / 双击 / 右键 ----------

    private void DataTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void DataTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var delta = _dragStart - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (SelectedNodeImage() is not { } image) return;

        DragDrop.DoDragDrop(DataTree, new DataObject(typeof(ImageItem), image), DragDropEffects.Copy);
    }

    /// <summary>
    /// 右键不会改变 TreeView 的选中项，导致右键菜单不知道作用于哪个节点。
    /// 这里先把光标下的节点选中，菜单才能拿到正确的目标。
    /// </summary>
    private void DataTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        item.IsSelected = true;
        item.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void DataTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedNodeImage() is { } image) _ = LoadItemAsync(image);
    }

    private ImageItem? SelectedNodeImage() =>
        (DataTree.SelectedItem as TreeNode)?.Tag as ImageItem;

    private void CtxLoad_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNodeImage() is { } image) _ = LoadItemAsync(image);
    }

    private void CtxUnload_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNodeImage() is not { } image) return;
        Workspace.Unload(image);
        RefreshTree();
    }

    /// <summary>从工程里移除影像。只去掉索引记录，磁盘文件不动。</summary>
    private void CtxRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNodeImage() is not { } image) return;

        var answer = MessageBox.Show(this,
            $"从工程中移除影像「{image.Id}」？\n\n只从工程里去掉，磁盘上的文件不受影响。",
            "移除影像", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Workspace.Unload(image);
        _project.Images.Remove(image);
        _bitmapCache.Remove(image.Id);

        RefreshTree();
        UpdateStatusCounts();
        UpdateFiducialUi();
        ImageInfoText.Text = "未载入";

        DiagSummary.Text = $"✓ 已从工程移除 {image.Id}（文件未删除）";
        DiagSummary.Foreground = OkBadge;
    }

    // ---------- 模块 ----------

    /// <summary>
    /// 切换作业方法：模块栏只留该方法的那一串步骤，按从左到右的顺序走。
    ///
    /// 当前模块若属于被换掉的方法，就落到新方法的第一步——否则会出现"选中的按钮
    /// 已被藏起来、模块栏看起来一个都没选中"的状态。
    /// </summary>
    private void Method_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MethodBox.SelectedIndex < 0 || MethodBox.SelectedIndex >= Workflows.Length) return;
        var steps = Workflows[MethodBox.SelectedIndex].Steps;

        foreach (var rb in ModuleBar.Children.OfType<RadioButton>())
        {
            var name = rb.Content?.ToString() ?? string.Empty;
            rb.Visibility = steps.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
        }

        var current = ModuleBar.Children.OfType<RadioButton>()
            .FirstOrDefault(r => r.IsChecked == true);
        if (current is null || current.Visibility != Visibility.Visible)
        {
            var first = ModuleBar.Children.OfType<RadioButton>()
                .FirstOrDefault(r => r.Content?.ToString() == steps[0]);
            if (first is not null) first.IsChecked = true;
        }
    }

    /// <summary>
    /// 按模块名选中，并自动切到包含该模块的作业方法。
    ///
    /// 只给离屏渲染工具（Snap）用——界面上没有入口，正常切换走模块栏。之所以放在
    /// 这里而不是工具里：哪个模块属于哪条方法，只有 <see cref="Workflows"/> 知道，
    /// 工具自己去猜就会踩「选中了一个被藏起来的按钮」这个坑。
    /// </summary>
    public bool SelectModuleForTooling(string moduleName)
    {
        var index = Array.FindIndex(Workflows, w => w.Steps.Contains(moduleName));
        if (index < 0) return false;

        if (MethodBox.SelectedIndex != index) MethodBox.SelectedIndex = index;

        var rb = ModuleBar.Children.OfType<RadioButton>()
            .FirstOrDefault(r => r.Content?.ToString() == moduleName);
        if (rb is null) return false;

        rb.IsChecked = true;
        return true;
    }

    private void Module_Checked(object sender, RoutedEventArgs e)
    {
        if (Workspace is null || sender is not RadioButton rb) return;
        ApplyModule(rb);
    }

    /// <summary>当前模块名，点表与标记层都按它分发。</summary>
    private string _moduleName = "内定向";

    private void ApplyModule(RadioButton rb)
    {
        var name = rb.Content?.ToString() ?? string.Empty;
        _moduleName = name;
        InspectorTitle.Text = name;
        StatusTask.Text = name;

        if (Modules.TryGetValue(name, out var info))
        {
            ModuleHint.Text = info.Hint;
            Workspace.SetMode(info.Mode);
            PointTools.Visibility = info.Mode == WorkspaceMode.Report
                ? Visibility.Collapsed
                : Visibility.Visible;
            RefreshTree();
        }

        var isInterior = name == "内定向";
        var isResection = name == "后方交会";
        InteriorPanel.Visibility = isInterior ? Visibility.Visible : Visibility.Collapsed;
        ResectionPanel.Visibility = isResection ? Visibility.Visible : Visibility.Collapsed;
        GenericTaskPanel.Visibility = isInterior || isResection ? Visibility.Collapsed : Visibility.Visible;

        if (isInterior)
        {
            ApplyInteriorMode(ModeFiducial.IsChecked == true);
        }
        else
        {
            // 离开内定向必须把框标方式也关掉。只清"待测框标编号"是不够的：
            // 框标方式还开着时，左键会被框标分支吞掉，后方交会里怎么点都没反应。
            Workspace.SetFiducialMode(false);
            CancelFiducialPicking();
        }

        // 每个流程只显示自己那层的点：内定向看不到刺点，后方交会看不到框标
        Workspace.SetLayer(isInterior ? ImageLayer.Fiducials
            : isResection ? ImageLayer.ControlPoints
            : ImageLayer.All);

        // 刺点只在后方交会里生效；离开这个模块就退出刺点模式，免得在内定向里误点
        if (!isResection) SetActiveGcp(null);

        RefreshPointTable();
        if (isResection) UpdateResectionPanel();
    }

    // ---------- 内定向 ----------

    private void LoadCamera_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入相机检校文件",
            Filter = "相机检校|*.cmr;*.csv;*.txt|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        LoadCameraFrom(dialog.FileName);
    }

    /// <summary>载入相机检校文件。返回是否成功；也供离屏渲染工具直接调用。</summary>
    public bool LoadCameraFrom(string path)
    {
        Camera camera;
        try
        {
            camera = CameraFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DiagSummary.Text = $"✗ 相机文件读取失败：{ex.Message}";
            DiagSummary.Foreground = ErrBadge;
            return false;
        }

        _camera = camera;
        if (_project.Cameras.All(c => c.Id != camera.Id)) _project.Cameras.Add(camera);

        foreach (var image in _project.Images) image.CameraId ??= camera.Id;

        DiagSummary.Text = camera.HasEnoughFiducials
            ? $"✓ 已载入相机 {camera.Id}：f = {camera.FocalLengthMm:0.###} mm，框标 {camera.Fiducials.Count} 个"
            : $"! 已载入相机 {camera.Id}，但只有 {camera.Fiducials.Count} 个框标，做不了仿射内定向";
        DiagSummary.Foreground = camera.HasEnoughFiducials ? OkBadge : WarnBadge;

        // 相机文件里有焦距和主点时，顺手填进参数式面板
        if (camera.FocalLengthMm > 0 && string.IsNullOrWhiteSpace(ParamF.Text))
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            ParamF.Text = camera.FocalLengthMm.ToString("0.###", ci);
            ParamX0.Text = camera.X0Mm.ToString("0.####", ci);
            ParamY0.Text = camera.Y0Mm.ToString("0.####", ci);
        }

        UpdateFiducialUi();
        RefreshTree();
        return true;
    }

    private void InteriorMode_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 初始化阶段也会触发，此时后面的控件还没建出来
        if (ParametricPanel is null || FiducialPanel is null) return;
        ApplyInteriorMode(ModeFiducial.IsChecked == true);
    }

    private void ApplyInteriorMode(bool fiducial)
    {
        _fiducialMode = fiducial;

        ParametricPanel.Visibility = fiducial ? Visibility.Collapsed : Visibility.Visible;
        FiducialPanel.Visibility = fiducial ? Visibility.Visible : Visibility.Collapsed;

        InteriorModeHint.Text = fiducial
            ? "在影像上量 8 个框标，解 6 个仿射系数。能吸收扫描比例、旋转与主点偏差。"
            : "直接代入检定给出的内参，不量框标。精度完全取决于检定值本身准不准。";

        Workspace.SetFiducialMode(fiducial);

        if (fiducial) EnsureFiducialPicking();
        else CancelFiducialPicking();

        UpdateFiducialUi();
    }

    /// <summary>框标方式下始终保持"下一个待测框标"可选，不需要再点一个开关。</summary>
    private void EnsureFiducialPicking()
    {
        var image = Workspace.ActiveView?.Image;
        if (_camera is null || image is null)
        {
            Workspace.SetActiveFiducial(null);
            return;
        }

        var id = _activeFiducialId ?? NextMissingFiducial(image);
        _activeFiducialId = id;
        Workspace.SetActiveFiducial(id);
    }

    private void ApplyParametric_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.ActiveView?.Image is not { } image)
        {
            DiagSummary.Text = "! 先把影像载入工作区";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        if (!TryRead(ParamF, out var f) || !TryRead(ParamX0, out var x0) ||
            !TryRead(ParamY0, out var y0) || !TryRead(ParamPixel, out var pixel))
        {
            DiagSummary.Text = "! 内参必须都是数字（小数点用点，不要用逗号）";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        if (f <= 0 || pixel <= 0)
        {
            DiagSummary.Text = "! 焦距与像元尺寸必须大于 0";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        image.Interior = new InteriorOrientation
        {
            FocalLength = f,
            X0 = x0,
            Y0 = y0,
            PixelSizeMm = pixel,
        };
        image.Status = ImageStatus.InteriorDone;

        OnImageLoaded(null, image);
        RefreshTree();
        UpdateFiducialUi();

        DiagSummary.Text = $"✓ 已应用参数式内参：f = {f:0.###} mm，δ = {pixel:0.000000} mm/px";
        DiagSummary.Foreground = OkBadge;
    }

    private static bool TryRead(TextBox box, out double value) =>
        double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private void SolveFiducial_Click(object sender, RoutedEventArgs e)
    {
        if (_camera is null)
        {
            DiagSummary.Text = "! 先载入相机检校文件";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        if (Workspace.ActiveView?.Image is not { } image)
        {
            DiagSummary.Text = "! 先把影像载入工作区";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        if (image.FiducialMarks.Count < NativeAlgorithms.MinFiducials)
        {
            DiagSummary.Text =
                $"! 本影像只量了 {image.FiducialMarks.Count} 个框标，至少要 "
                + $"{NativeAlgorithms.MinFiducials} 个才能解算";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        var points = _camera.Fiducials
            .Select(f => new FiducialPoint(f.Id, f.Xmm, f.Ymm)).ToList();
        var observations = image.FiducialMarks
            .Select(m => new FiducialObservation(m.FiducialId, m.I, m.J)).ToList();

        SetBusy(true, "正在解算内定向…");
        FiducialOrientationResult result;
        try
        {
            result = NativeAlgorithms.SolveFiducialOrientation(
                points, observations, image.Width, image.Height);
        }
        finally
        {
            SetBusy(false, null);
        }

        if (!result.Success)
        {
            var hint = result.ErrorCode == NativeAlgorithms.ErrorAxisSign
                ? "\n\n多半是框标编号与点位对不上。\n对照上面列表里的方位（如「右下角」），逐个核对后重新点一遍。"
                : string.Empty;

            FiducialResultText.Text = $"解算失败（{result.ErrorCode}）\n{result.Message}{hint}";
            FiducialResultText.Foreground = ErrBadge;
            DiagSummary.Text = $"✗ 内定向失败：{result.Message}";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        // 回填内方位元素
        image.Interior = new InteriorOrientation
        {
            FocalLength = _camera.FocalLengthMm,
            X0 = result.X0Mm,
            Y0 = result.Y0Mm,
            PixelSizeMm = result.PixelSizeMm,
        };
        image.Status = ImageStatus.InteriorDone;

        FiducialResultText.Foreground = MutBadge;
        FiducialResultText.Text =
            $"δ    = {result.PixelSizeMm:0.000000} mm/px\n" +
            $"s_x  = {result.SxMm:0.000000}   s_y = {result.SyMm:0.000000}\n" +
            $"x0   = {result.X0Mm:+0.000000;-0.000000} mm\n" +
            $"y0   = {result.Y0Mm:+0.000000;-0.000000} mm\n" +
            $"主点 = {result.PrincipalI:0.00}, {result.PrincipalJ:0.00} px\n" +
            $"σ0   = {result.Sigma0Mm:0.000000} mm   冗余 {result.Redundancy}";

        ResPanelText.Text = BuildFiducialResidualTable(result);
        ResTab.IsChecked = true;

        var warn = result.Sigma0Mm > 0.02;
        DiagSummary.Text = warn
            ? $"! 内定向完成，但 σ0 = {result.Sigma0Mm:0.0000} mm 偏大，检查残差最大的框标"
            : $"✓ 内定向完成：δ = {result.PixelSizeMm:0.000000} mm/px，σ0 = {result.Sigma0Mm:0.0000} mm";
        DiagSummary.Foreground = warn ? WarnBadge : OkBadge;

        OnImageLoaded(null, image);
        OnSelectionChanged(null, EventArgs.Empty);
        RefreshTree();
    }

    private static string BuildFiducialResidualTable(FiducialOrientationResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"内定向 · 框标 {result.UsedCount} 个 · 多余观测 {result.Redundancy} · " +
                      $"σ0 = {result.Sigma0Mm:0.000000} mm · 最大残差 {result.MaxAbsResidualMm:0.000000} mm");
        sb.AppendLine();
        sb.AppendLine("框标        I(px)         J(px)        vx(mm)        vy(mm)");
        sb.AppendLine(new string('-', 62));

        foreach (var r in result.Residuals)
        {
            var id = r.Id.ToString().PadLeft(3);
            var mark = Math.Max(Math.Abs(r.VxMm), Math.Abs(r.VyMm));
            sb.AppendLine($"{id}   {r.VxMm,12:+0.000000;-0.000000} {r.VyMm,12:+0.000000;-0.000000}"
                          + (mark >= 5 * result.Sigma0Mm && result.Sigma0Mm > 0 ? "   ← 偏大" : ""));
        }

        return sb.ToString();
    }

    private int? NextMissingFiducial(ImageItem image)
    {
        if (_camera is null) return null;
        foreach (var fiducial in _camera.Fiducials)
            if (image.FiducialMarks.All(m => m.FiducialId != fiducial.Id))
                return fiducial.Id;
        return null;
    }

    private void OnFiducialPicked(object? sender, FiducialPickEventArgs e)
    {
        if (sender is not ImageView view || view.Image is not { } image) return;

        var mark = image.FiducialMarks.FirstOrDefault(m => m.FiducialId == e.FiducialId);
        if (mark is null)
        {
            mark = new FiducialMark { FiducialId = e.FiducialId };
            image.FiducialMarks.Add(mark);
        }

        mark.I = e.I;
        mark.J = e.J;

        var next = NextMissingFiducial(image);
        _activeFiducialId = next;
        Workspace.SetActiveFiducial(next);
        Workspace.RefreshFiducials();
        UpdateFiducialUi();

        if (next is null)
        {
            DiagSummary.Text = "✓ 框标取点完成，可以解算了";
            DiagSummary.Foreground = OkBadge;
        }
        else
        {
            var nextFiducial = _camera?.Fiducials.FirstOrDefault(f => f.Id == next);
            var where = nextFiducial is null || _camera is null ||
                        !FiducialHelper.IsRoughlySymmetric(_camera.Fiducials)
                ? ""
                : $"（{FiducialHelper.Describe(nextFiducial.Xmm, nextFiducial.Ymm)}）";
            DiagSummary.Text = $"框标 {e.FiducialId} 已记录，请点击框标 {next}{where} 的十字交点";
            DiagSummary.Foreground = AccentBadge;
        }
    }

    /// <summary>框标取点模式下右键：撤掉刚取的那个框标，并回到它重新等点。</summary>
    private void OnFiducialUndo(object? sender, EventArgs e)
    {
        if (sender is not ImageView view || view.Image is not { } image) return;
        if (image.FiducialMarks.Count == 0) return;

        var last = image.FiducialMarks.MaxBy(m => m.FiducialId)!;
        image.FiducialMarks.Remove(last);

        _activeFiducialId = last.FiducialId;
        Workspace.SetActiveFiducial(last.FiducialId);
        Workspace.RefreshFiducials();
        UpdateFiducialUi();

        DiagSummary.Text = $"已撤销框标 {last.FiducialId}，请重新点击它";
        DiagSummary.Foreground = AccentBadge;
    }

    private void ClearFiducials_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.ActiveView?.Image is not { } image) return;
        if (image.FiducialMarks.Count == 0) return;

        var count = image.FiducialMarks.Count;
        image.FiducialMarks.Clear();

        CancelFiducialPicking();
        Workspace.RefreshFiducials();
        UpdateFiducialUi();

        DiagSummary.Text = $"已清空 {image.Id} 上的 {count} 个框标量测";
        DiagSummary.Foreground = DimBadge;
    }

    private void CancelFiducialPicking()
    {
        if (_activeFiducialId is null) return;
        _activeFiducialId = null;
        Workspace.SetActiveFiducial(null);
    }

    private void UpdateFiducialUi()
    {
        if (CameraInfoText is null) return;

        var image = Workspace.ActiveView?.Image;

        CameraInfoText.Text = _camera is null
            ? "未载入相机检校文件"
            : $"{_camera.Id} · f = {_camera.FocalLengthMm:0.###} mm · " +
              $"已测 {image?.FiducialMarks.Count ?? 0} / {_camera.Fiducials.Count}";

        RefreshPointTable();
        UpdateCheckPanel();
    }

    /// <summary>
    /// 内定向的点表：框标一行一个，标出像平面坐标与方位。
    /// 影像上的 8 个十字长得一样，光说「请点框标 1」没人知道是哪一个，
    /// 所以每行都给出方位（右下角 / 上边中点 …）；点某一行即把它设为待测框标。
    /// </summary>
    private void RefreshFiducialTable()
    {
        if (FiducialGrid is null) return;

        var image = CurrentImage;
        var keep = _activeFiducialId;

        // 框标不关于像主点对称时，方位描述会误导，只显示坐标
        var showWhere = _camera is not null && FiducialHelper.IsRoughlySymmetric(_camera.Fiducials);

        var rows = new List<FiducialRow>();
        if (_camera is not null)
        {
            foreach (var fiducial in _camera.Fiducials)
            {
                var mark = image?.FiducialMarks.FirstOrDefault(m => m.FiducialId == fiducial.Id);
                rows.Add(new FiducialRow
                {
                    FiducialId = fiducial.Id,
                    Id = fiducial.Id.ToString(CultureInfo.InvariantCulture),
                    X = fiducial.Xmm.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture),
                    Y = fiducial.Ymm.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture),
                    Direction = showWhere ? FiducialHelper.Describe(fiducial.Xmm, fiducial.Ymm) : "",
                    PixelI = mark is null ? "—" : mark.I.ToString("0.0", CultureInfo.InvariantCulture),
                    PixelJ = mark is null ? "—" : mark.J.ToString("0.0", CultureInfo.InvariantCulture),
                    Note = mark is null ? "未测" : "已测"
                });
            }
        }

        FiducialGrid.ItemsSource = rows;

        MeasureSummary.Text = _camera is null
            ? "点表 · 框标：尚未载入相机检校文件"
            : $"点表 · 框标 {_camera.Fiducials.Count} 个　本片已测 {image?.FiducialMarks.Count ?? 0} 个"
              + (image?.Interior is null ? "" : $"　内定向已完成：δ = {image.Interior.PixelSizeMm:0.00000} mm/px");

        if (keep is { } id && rows.FirstOrDefault(r => r.FiducialId == id) is { } again)
            FiducialGrid.SelectedItem = again;

    }

    private void FiducialGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FiducialGrid.SelectedItem is not FiducialRow row) return;

        _activeFiducialId = row.FiducialId;
        Workspace.SetActiveFiducial(row.FiducialId);

        var where = _camera is null || !FiducialHelper.IsRoughlySymmetric(_camera.Fiducials)
            ? ""
            : $"（{FiducialHelper.Describe(
                _camera.Fiducials.FirstOrDefault(f => f.Id == row.FiducialId)?.Xmm ?? 0,
                _camera.Fiducials.FirstOrDefault(f => f.Id == row.FiducialId)?.Ymm ?? 0)}）";

        DiagSummary.Text = $"已选定框标 {row.FiducialId}{where}，在影像上点它的十字交点（相机会覆盖上一次）";
        DiagSummary.Foreground = AccentBadge;
    }

    /// <summary>手工录入或改正框标坐标。文件格式对不上、内容不全时走这条。</summary>
    private void EditFiducials_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FiducialEditorWindow(_camera) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } camera) return;

        _camera = camera;

        var index = _project.Cameras.FindIndex(c => c.Id == camera.Id);
        if (index >= 0) _project.Cameras[index] = camera;
        else _project.Cameras.Add(camera);

        // 编号变了以后，旧量测可能指向不存在的框标，先清掉免得张冠李戴
        var valid = camera.Fiducials.Select(f => f.Id).ToHashSet();
        foreach (var image in _project.Images)
        {
            image.CameraId ??= camera.Id;
            image.FiducialMarks.RemoveAll(m => !valid.Contains(m.FiducialId));
        }

        _activeFiducialId = null;
        Workspace.RefreshFiducials();
        EnsureFiducialPicking();
        UpdateFiducialUi();
        RefreshTree();

        var enough = camera.HasEnoughFiducials;
        DiagSummary.Text = enough
            ? $"✓ 框标已更新：{camera.Fiducials.Count} 个，f = {camera.FocalLengthMm:0.###} mm"
            : $"! 只填了 {camera.Fiducials.Count} 个框标，至少需要 {NativeAlgorithms.MinFiducials} 个";
        DiagSummary.Foreground = enough ? OkBadge : WarnBadge;
    }

    /// <summary>输入检查：把当前影像做内定向还缺什么，逐条列出来。</summary>
    private void UpdateCheckPanel()
    {
        if (CheckText is null) return;

        var image = Workspace.ActiveView?.Image;
        if (image is null)
        {
            CheckText.Text = "未载入影像";
            CheckText.Foreground = DimBadge;
            return;
        }

        var lines = new List<string>();
        var allOk = true;

        if (_camera is null)
        {
            lines.Add("✗ 未载入相机检校文件");
            allOk = false;
        }
        else if (!_camera.HasEnoughFiducials)
        {
            lines.Add($"✗ 框标只有 {_camera.Fiducials.Count} 个，至少要 {NativeAlgorithms.MinFiducials} 个");
            allOk = false;
        }
        else
        {
            lines.Add($"✓ 相机框标 {_camera.Fiducials.Count} 个");

            var done = image.FiducialMarks.Count;
            if (done < NativeAlgorithms.MinFiducials)
            {
                lines.Add($"✗ 本影像已测 {done} 个，至少要 {NativeAlgorithms.MinFiducials} 个");
                allOk = false;
            }
            else
            {
                lines.Add($"✓ 本影像已测 {done} / {_camera.Fiducials.Count}");
            }
        }

        if (image.Interior is { IsValid: true } io)
            lines.Add($"✓ δ = {io.PixelSizeMm:0.000000} mm/px，x0 = {io.X0:+0.0000;-0.0000} mm");
        else
            allOk = false;

        CheckText.Text = string.Join("\n", lines);
        CheckText.Foreground = allOk ? OkBadge : WarnBadge;
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

    // ---------- M3 控制点与刺点 ----------

    /// <summary>点表里框标的一行。</summary>
    private sealed class FiducialRow
    {
        public int FiducialId { get; init; }
        public string Id { get; init; } = "";
        public string X { get; init; } = "";
        public string Y { get; init; } = "";
        public string Direction { get; init; } = "";
        public string PixelI { get; init; } = "";
        public string PixelJ { get; init; } = "";
        public string Note { get; init; } = "";
    }

    /// <summary>点表里控制点的一行。只读展示，数值按固定小数格式化好再交给 DataGrid。</summary>
    private sealed class GcpRow
    {
        public Gcp? Source { get; init; }
        public string Id { get; init; } = "";
        public string X { get; init; } = "";
        public string Y { get; init; } = "";
        public string Z { get; init; } = "";
        public string Type { get; init; } = "";
        public string Xmm { get; init; } = "";
        public string Ymm { get; init; } = "";
        public string Pixel { get; init; } = "";
        public string Residual { get; init; } = "";
        public string Note { get; init; } = "";
    }

    /// <summary>量测表里选中的控制点号。非 null 时左键点影像就是在给它刺点。</summary>
    private string? _activeGcpId;

    /// <summary>当前工作影像：鼠标所在视窗优先，否则取第一个载入影像的可见视窗。</summary>
    private ImageItem? CurrentImage => Workspace.ActiveView?.Image;

    // ---- 供离屏渲染工具（src/Snap）读取的状态，正常交互不会用到 ----

    public int GcpCount => _project.Gcps.Count;

    public IReadOnlyList<Gcp> ControlPoints => _project.Gcps;

    public Project CurrentProject => _project;

    /// <summary>点表里选中某个控制点（触发参考影像与刺点模式）。返回是否找到。</summary>
    public bool SelectGcp(string gcpId)
    {
        if (GcpGrid.ItemsSource is not IEnumerable<GcpRow> rows) return false;
        var row = rows.FirstOrDefault(r => r.Id == gcpId);
        if (row is null) return false;
        GcpGrid.SelectedItem = row;
        GcpGrid.ScrollIntoView(row);
        BottomTabsMeasure.IsChecked = true;
        return true;
    }

    private void LoadControlPoints_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入控制点文件",
            Filter = "控制点文件|*.rtf;*.csv;*.txt;*.dat|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        LoadControlPointsFrom(dialog.FileName);
    }

    /// <summary>
    /// 载入控制点文件并刷新量测表。返回是否成功。
    /// 也供离屏渲染工具（src/Snap）跳过文件对话框直接调用。
    /// </summary>
    public bool LoadControlPointsFrom(string path)
    {
        SetBusy(true, "正在读取控制点…");
        ControlPointImportResult result;
        try
        {
            result = ControlPointFile.Load(path);
        }
        finally
        {
            SetBusy(false, null);
        }

        if (!result.Success)
        {
            DiagSummary.Text = "✗ 控制点读取失败：" + string.Join("；", result.Warnings);
            DiagSummary.Foreground = ErrBadge;
            return false;
        }

        // 重新载入就是整体替换：旧控制点上的像点绑定已经没有意义
        var stale = _project.Gcps.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var image in _project.Images)
            image.Points.RemoveAll(p => p.BoundGcpId is not null && stale.Contains(p.BoundGcpId));

        _project.Gcps.Clear();
        foreach (var gcp in result.Points) _project.Gcps.Add(gcp);
        _project.ControlPointSourceFullPath = result.SourcePath;
        _project.ControlPointZoomDirectoryFullPath = result.ZoomDirectory;

        // 控制点文件旁边如果有放大影像，顺手挂上；没有就等用户按「载入控制点图像…」
        _zoomImages.Clear();
        if (result.ZoomDirectory is { } zoomDir)
        {
            foreach (var gcp in _project.Gcps)
            {
                if (ControlPointFile.FindZoomImage(zoomDir, gcp.Id) is { } found)
                    _zoomImages[gcp.Id] = found;
            }
        }
        RebuildZoomImageList();

        _activeGcpId = null;
        Workspace.SetActiveGcp(null);
        Workspace.ClearReference();

        RefreshPointTable();
        RefreshTree();
        UpdateStatusCounts();

        DiagSummary.Foreground = result.Warnings.Count > 0 ? WarnBadge : OkBadge;
        DiagSummary.Text = (result.Warnings.Count > 0 ? "! " : "✓ ") + result.Summary +
                           (result.Warnings.Count > 0 ? "\n  " + string.Join("\n  ", result.Warnings) : "");

        BottomTabsMeasure.IsChecked = true;
        UpdateResectionPanel();
        return true;
    }

    /// <summary>点表按当前模块分发：内定向列框标，后方交会列控制点。</summary>
    private void RefreshPointTable()
    {
        if (FiducialGrid is null) return;

        var isFiducial = _moduleName == "内定向";
        FiducialGrid.Visibility = isFiducial ? Visibility.Visible : Visibility.Collapsed;
        GcpGrid.Visibility = isFiducial ? Visibility.Collapsed : Visibility.Visible;

        if (isFiducial) RefreshFiducialTable();
        else RefreshGcpTable();
    }

    /// <summary>重建后方交会的控制点表。</summary>
    private void RefreshGcpTable()
    {
        if (GcpGrid is null) return;

        var image = CurrentImage;
        var keep = _activeGcpId;

        var rows = new List<GcpRow>(_project.Gcps.Count);
        foreach (var gcp in _project.Gcps)
        {
            var here = image?.FindGcpPoint(gcp.Id);
            var everywhere = _project.Images.Count(i => i.FindGcpPoint(gcp.Id) is not null);

            rows.Add(new GcpRow
            {
                Source = gcp,
                Id = gcp.Id,
                X = gcp.X.ToString("F3", CultureInfo.InvariantCulture),
                Y = gcp.Y.ToString("F3", CultureInfo.InvariantCulture),
                Z = gcp.Z.ToString("F3", CultureInfo.InvariantCulture),
                Type = gcp.Type switch
                {
                    GcpType.CheckPoint => "检查点",
                    GcpType.Elevation => "高程点",
                    _ => "控制点"
                },
                Xmm = here is null ? "—" : here.PositionMm.X.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture),
                Ymm = here is null ? "—" : here.PositionMm.Y.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture),
                Pixel = here is null ? "—" : $"{here.PixelX}, {here.PixelY}",
                Residual = here?.ResidualMm is { } r
                    ? r.ToString("0.0000", CultureInfo.InvariantCulture)
                    : "",
                Note = here is null
                    ? (image is null ? "未载入影像" : "本片未刺点")
                    : (everywhere > 1 ? $"共 {everywhere} 片有观测" : ""),
            });
        }

        GcpGrid.ItemsSource = rows;

        var bound = image?.BoundGcpCount ?? 0;
        MeasureSummary.Text = _project.Gcps.Count == 0
            ? "点表 · 控制点：尚未载入控制点文件（在右侧「载入控制点文件…」）"
            : $"点表 · 控制点 {_project.Gcps.Count} 个"
              + (_project.Gcps.Count(g => g.Type == GcpType.CheckPoint) is > 0
                  ? $"（含检查点 {_project.Gcps.Count(g => g.Type == GcpType.CheckPoint)} 个）"
                  : "")
              + $"　本片已刺 {bound} 个"
              + (image?.Interior is null ? "　当前影像还没有内定向结果" : "");

        if (keep is not null && rows.FirstOrDefault(r => r.Id == keep) is { } again)
            GcpGrid.SelectedItem = again;

    }

    private void GcpGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GcpGrid.SelectedItem is not GcpRow { Source: { } gcp }) return;

        _activeGcpId = gcp.Id;
        Workspace.SetActiveGcp(gcp.Id);
        ShowReferenceFor(gcp);
        UpdateResectionPanel();
    }

    private void SetActiveGcp(string? gcpId)
    {
        _activeGcpId = gcpId;
        Workspace.SetActiveGcp(gcpId);
    }

    /// <summary>后方交会面板里载入的控制点放大影像：点号 → 文件路径。</summary>
    private readonly Dictionary<string, string> _zoomImages = new(StringComparer.Ordinal);

    /// <summary>把某个控制点的放大影像送到左窗。没有就显示提示，不影响刺点。</summary>
    private void ShowReferenceFor(Gcp? gcp)
    {
        if (gcp is null)
        {
            Workspace.ClearReference();
            return;
        }

        var path = _zoomImages.TryGetValue(gcp.Id, out var found) ? found : null;
        Workspace.ShowReference(path, path is null
            ? $"{gcp.Id} · 无放大影像"
            : $"{gcp.Id} · {Path.GetFileName(path)}");
    }

    /// <summary>载入控制点放大影像。文件名（去扩展名）要能对上点号。</summary>
    private void LoadZoomImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入控制点放大影像（文件名用点号）",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var accepted = 0;
        var unknown = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (_project.FindGcp(id) is null)
            {
                unknown.Add(id);
                continue;
            }
            _zoomImages[id] = file;
            accepted++;
        }

        RebuildZoomImageList();

        if (_activeGcpId is { } active && _project.FindGcp(active) is { } gcp) ShowReferenceFor(gcp);

        DiagSummary.Foreground = unknown.Count > 0 ? WarnBadge : OkBadge;
        DiagSummary.Text = $"✓ 已载入 {accepted} 张放大影像"
                           + (unknown.Count == 0
                               ? ""
                               : $"\n  点号对不上的 {unknown.Count} 个：{string.Join("、", unknown.Take(8))}");
    }

    /// <summary>重建「载入控制点图像」按钮下面的列表。</summary>
    private void RebuildZoomImageList()
    {
        if (ZoomImageListPanel is null) return;
        ZoomImageListPanel.Children.Clear();

        if (_zoomImages.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "尚未载入控制点放大影像。\n加载后每个点一行，点一行就在左窗看它。",
                FontSize = 10.5,
                Foreground = DimBadge,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 3, 4, 3),
            };
            ZoomImageListPanel.Children.Add(hint);
            return;
        }

        foreach (var gcp in _project.Gcps)
        {
            if (!_zoomImages.TryGetValue(gcp.Id, out var path)) continue;

            var isActive = _activeGcpId == gcp.Id;
            var row = new Button
            {
                Content = $"{gcp.Id}    {Path.GetFileName(path)}",
                Tag = gcp.Id,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 0, 1),
                Background = isActive ? AccentBadge : Brushes.Transparent,
                Foreground = isActive ? Brushes.White : MutBadge,
                BorderBrush = isActive ? AccentBadge : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            row.Click += ZoomImageRow_Click;
            ZoomImageListPanel.Children.Add(row);
        }

        var missing = _project.Gcps.Count(g => !_zoomImages.ContainsKey(g.Id));
        if (missing > 0)
        {
            ZoomImageListPanel.Children.Add(new TextBlock
            {
                Text = $"还有 {missing} 个点没有放大影像（不影响刺点）",
                FontSize = 10.5,
                Foreground = DimBadge,
                Margin = new Thickness(6, 3, 4, 3),
            });
        }
    }

    private void ZoomImageRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        // 点图像行 = 顺带选中这个控制点，免得再回点表里找一次
        if (_project.FindGcp(id) is { } gcp)
        {
            _activeGcpId = id;
            Workspace.SetActiveGcp(id);
            ShowReferenceFor(gcp);
            if (GcpGrid.ItemsSource is IEnumerable<GcpRow> rows &&
                rows.FirstOrDefault(r => r.Id == id) is { } row)
            {
                GcpGrid.SelectedItem = row;
            }
            RebuildZoomImageList();
            UpdateResectionPanel();

            DiagSummary.Text = $"已选定 {id}，左键在影像上点它的位置";
            DiagSummary.Foreground = AccentBadge;
        }
    }

    /// <summary>在影像上刺了一个点：绑定给控制点，一个控制点在一张影像上最多一个像点。</summary>
    private void OnGcpPicked(object? sender, GcpPickEventArgs e) =>
        BindGcpAt(e.GcpId, (int)Math.Round(e.I), (int)Math.Round(e.J), (sender as ImageView)?.Image);

    /// <summary>把某个控制点刺到指定像素上。返回是否成功（控制点不存在、没有影像都算失败）。</summary>
    public bool BindGcpAt(string gcpId, int px, int py, ImageItem? image = null)
    {
        image ??= CurrentImage;
        if (image is null) return false;

        var gcp = _project.FindGcp(gcpId);
        if (gcp is null) return false;

        var pixel = new ImagePixel(px, py);

        var point = image.FindGcpPoint(gcpId);
        if (point is null)
        {
            point = new ImagePoint
            {
                Id = NextObservationId(image),
                ImageId = image.Id,
                BoundGcpId = gcpId,
            };
            image.Points.Add(point);
        }

        point.PixelX = pixel.I;
        point.PixelY = pixel.J;
        point.ResidualMm = null;
        point.PositionMm = image.Interior is { } io
            ? CoordinateConverter.PixelToImagePlane(pixel, io, image.Width, image.Height)
            : default;

        foreach (var view in Workspace.VisibleViews().ToList()) view.RefreshPoints();
        RefreshPointTable();
        UpdateStatusCounts();

        DiagSummary.Foreground = image.Interior is null ? WarnBadge : OkBadge;
        DiagSummary.Text = image.Interior is null
            ? $"已把 {gcpId} 刺在 px {px}, {py}，但这张影像还没有内定向，算不出像平面坐标。"
            : $"✓ {gcpId} → px {px}, {py}　像平面 " +
              $"{point.PositionMm.X:0.000}, {point.PositionMm.Y:0.000} mm";
        return true;
    }

    private string NextObservationId(ImageItem image)
    {
        for (var n = image.Points.Count + 1; ; n++)
        {
            var id = $"{image.Id}-o{n}";
            if (image.Points.All(p => p.Id != id)) return id;
        }
    }

    /// <summary>删掉点表里选中的那一个点。两个模块共用，按模块决定删框标还是删刺点。</summary>
    private void DeleteSelectedPoint_Click(object sender, RoutedEventArgs e)
    {
        var image = CurrentImage;
        if (image is null) return;

        if (FiducialGrid.Visibility == Visibility.Visible)
        {
            if (FiducialGrid.SelectedItem is not FiducialRow row) return;
            var mark = image.FiducialMarks.FirstOrDefault(m => m.FiducialId == row.FiducialId);
            if (mark is null) return;

            image.FiducialMarks.Remove(mark);
            _activeFiducialId = row.FiducialId;
            Workspace.SetActiveFiducial(row.FiducialId);
            Workspace.RefreshFiducials();
            UpdateFiducialUi();

            DiagSummary.Text = $"已删掉框标 {row.FiducialId} 在本影像上的量测";
            DiagSummary.Foreground = DimBadge;
            return;
        }

        if (GcpGrid.SelectedItem is not GcpRow { Source: { } gcp }) return;
        var point = image.FindGcpPoint(gcp.Id);
        if (point is null) return;

        image.Points.Remove(point);
        foreach (var view in Workspace.VisibleViews().ToList()) view.RefreshPoints();
        RefreshGcpTable();
        UpdateStatusCounts();

        DiagSummary.Text = $"已删掉 {gcp.Id} 在本影像上的刺点";
        DiagSummary.Foreground = DimBadge;
    }

    /// <summary>清空本影像的刺点。只动当前这张影像，别的片不受影响。</summary>
    private void ClearGcpPoints_Click(object sender, RoutedEventArgs e)
    {
        var image = CurrentImage;
        if (image is null) return;

        var count = image.BoundGcpCount;
        if (count == 0)
        {
            DiagSummary.Text = "本影像还没有刺点。";
            DiagSummary.Foreground = DimBadge;
            return;
        }

        image.Points.RemoveAll(p => p.BoundGcpId is not null);
        foreach (var view in Workspace.VisibleViews().ToList()) view.RefreshPoints();
        RefreshGcpTable();
        UpdateStatusCounts();

        DiagSummary.Text = $"已清空 {image.Id} 上的 {count} 个刺点（其它影像不受影响）";
        DiagSummary.Foreground = DimBadge;
    }

    private void GcpSetControl_Click(object sender, RoutedEventArgs e) => SetSelectedGcpType(GcpType.Planimetric);

    private void GcpSetCheck_Click(object sender, RoutedEventArgs e) => SetSelectedGcpType(GcpType.CheckPoint);

    private void SetSelectedGcpType(GcpType type)
    {
        if (GcpGrid.SelectedItem is not GcpRow { Source: { } gcp }) return;

        gcp.Type = type;
        RefreshPointTable();
        UpdateResectionPanel();

        DiagSummary.Text = type == GcpType.CheckPoint
            ? $"{gcp.Id} 已设为检查点，不参与后方交会"
            : $"{gcp.Id} 已设为控制点";
        DiagSummary.Foreground = DimBadge;
    }

    // ---------- 后方交会 ----------

    /// <summary>可参与解算的控制点观测：本影像上已刺点、有内定向、且不是检查点。</summary>
    private List<ResectionObservation> CollectObservations(ImageItem image, out List<string> skipped)
    {
        skipped = new List<string>();
        var list = new List<ResectionObservation>();

        if (image.Interior is not { } io || !io.IsValid)
        {
            skipped.Add("当前影像还没有有效内定向");
            return list;
        }

        foreach (var gcp in _project.Gcps)
        {
            if (gcp.Type == GcpType.CheckPoint) continue;

            var point = image.FindGcpPoint(gcp.Id);
            if (point is null)
            {
                skipped.Add($"{gcp.Id} 未刺点");
                continue;
            }

            var mm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(point.PixelX, point.PixelY), io, image.Width, image.Height);
            point.PositionMm = mm;
            list.Add(new ResectionObservation(gcp.Id, mm.X, mm.Y, gcp.X, gcp.Y, gcp.Z));
        }

        return list;
    }

    private void UpdateResectionPanel()
    {
        var image = CurrentImage;
        ResectionEquationText.Text =
            "x = -f·U/W,  y = -f·V/W\n" +
            "[U V W]ᵀ = R·[X-Xs, Y-Ys, Z-Zs]ᵀ\n" +
            "R = Rκ·Rω·Rφ（教材形式）";

        if (image is null)
        {
            ResectionHint.Text = "尚未载入影像。";
            return;
        }

        var observations = CollectObservations(image, out var skipped);
        var text = $"影像 {image.Id}：可用于解算的控制点 {observations.Count} 个";

        if (image.Interior is { } io && io.IsValid)
            text += $"\nf = {io.FocalLength:0.###} mm　x0 = {io.X0:0.####}　y0 = {io.Y0:0.####}　δ = {io.PixelSizeMm:0.######} mm/px";
        else
            text += "\n内方位元素缺失：先在「内定向」里解算。";

        if (observations.Count < NativeAlgorithms.MinControlPoints)
            text += $"\n至少需要 {NativeAlgorithms.MinControlPoints} 个才能解算。";
        if (skipped.Count > 0)
            text += "\n未参与：" + string.Join("，", skipped.Take(6)) +
                    (skipped.Count > 6 ? $" 等 {skipped.Count} 项" : "");

        ResectionHint.Text = text;
    }

    private void SolveResection_Click(object sender, RoutedEventArgs e) => SolveResectionNow();

    /// <summary>跑一次后方交会并刷新界面。返回解算结果，失败时为 null。也供离屏渲染工具调用。</summary>
    public ResectionResult? SolveResectionNow()
    {
        var image = CurrentImage;
        if (image is null)
        {
            DiagSummary.Text = "尚未载入影像。";
            DiagSummary.Foreground = WarnBadge;
            return null;
        }

        if (image.Interior is not { } io || !io.IsValid)
        {
            DiagSummary.Text = "✗ 当前影像还没有内定向结果。没有像平面坐标系就做不了后方交会。";
            DiagSummary.Foreground = ErrBadge;
            return null;
        }

        var observations = CollectObservations(image, out _);
        if (observations.Count < NativeAlgorithms.MinControlPoints)
        {
            DiagSummary.Text = $"✗ 只有 {observations.Count} 个可用的控制点观测，后方交会至少要 " +
                               $"{NativeAlgorithms.MinControlPoints} 个。";
            DiagSummary.Foreground = ErrBadge;
            return null;
        }

        // 先判手性（三点叉乘，不解算），判出是左手系就换轴，然后只解一次
        var evidence = NativeAlgorithms.DetectGroundOrder(observations);
        if (!evidence.Decided)
        {
            DiagSummary.Text = "✗ 判不出地面坐标轴序：" + evidence.Message;
            DiagSummary.Foreground = WarnBadge;
            return null;
        }

        SetBusy(true, "正在解算后方交会…");
        ResectionResult result;
        try
        {
            // 已换到数学系（X 东、Y 北），再让自动初值与已存外方位各解一次，取残差小的，最后查残差与几何
            result = NativeAlgorithms.SolveResectionRobust(
                NativeAlgorithms.ToMathFrame(observations, evidence.Order),
                io.FocalLength, io.PixelSizeMm, image.Exterior);
        }
        finally
        {
            SetBusy(false, null);
        }
        if (!result.Success)
        {
            ResectionResultText.Foreground = ErrBadge;
            ResectionResultText.Text = $"未采用（rc = {result.ErrorCode}）\n{result.Message}\n\n" +
                                       "诊断值（不要当作结果）：\n" +
                                       $"Xs {result.Xs:0.0}　Ys {result.Ys:0.0}　Zs {result.Zs:0.0} m\n" +
                                       $"φ {result.PhiDeg:+0.0;-0.0}°　ω {result.OmegaDeg:+0.0;-0.0}°　" +
                                       $"κ {result.KappaDeg:+0.0;-0.0}°\nσ₀ {result.Sigma0Mm:0.0000} mm";
            DiagSummary.Text = "✗ " + result.Message;
            DiagSummary.Foreground = ErrBadge;
            return result;
        }

        _lastResection = result;
        WriteResectionResult(image, result, observations);

        ResectionResultText.Text = FormatResection(result) +
                                   $"\n{evidence.Describe()}\n" +
                                   "外方位 X/Y 在数学系里（X 东、Y 北）。";
        DiagSummary.Text = $"✓ 后方交会完成：迭代 {result.Iterations} 次，σ₀ = " +
                           $"{result.Sigma0Mm:0.0000} mm，摄影距离约 {result.MeanRangeM:0} m，" +
                           $"{GroundAxes.Describe(evidence.Order)}";
        DiagSummary.Foreground = OkBadge;

        ResTab.IsChecked = true;
        RefreshPointTable();
        UpdateStatusCounts();
        RefreshTree();
        return result;
    }

    /// <summary>最近一次解算结果，用于「应用外方位到影像」。</summary>
    private ResectionResult? _lastResection;

    /// <summary>把解算结果写回模型：外方位、逐点残差、影像状态。</summary>
    private void WriteResectionResult(ImageItem image, ResectionResult result,
        IReadOnlyList<ResectionObservation> observations)
    {
        image.Exterior = result.ToExterior();
        image.Status = ImageStatus.Oriented;

        foreach (var residual in result.Residuals)
        {
            var point = image.FindGcpPoint(residual.GcpId);
            if (point is null) continue;
            point.ResidualMm = Math.Sqrt(residual.VxMm * residual.VxMm + residual.VyMm * residual.VyMm);
            point.PositionMm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(point.PixelX, point.PixelY), image.Interior!, image.Width, image.Height);
        }

        // 残差表进「结果残差」页
        var sb = new StringBuilder();
        sb.AppendLine($"影像 {image.Id}　后方交会　多余观测 {result.Redundancy}　" +
                      $"σ₀ = {result.Sigma0Mm:0.0000} mm　条件数 {result.Condition:0.#}");
        sb.AppendLine($"外方位　Xs = {result.Xs:0.000} m　Ys = {result.Ys:0.000} m　Zs = {result.Zs:0.000} m");
        sb.AppendLine($"　　　　φ = {result.PhiDeg:+0.000000;-0.000000}°　ω = {result.OmegaDeg:+0.000000;-0.000000}°　" +
                      $"κ = {result.KappaDeg:+0.000000;-0.000000}°");
        sb.AppendLine();
        sb.AppendLine("点号        Vx(mm)     Vy(mm)     |V|(mm)   |V|/σ₀");
        foreach (var r in result.Residuals)
        {
            var norm = Math.Sqrt(r.VxMm * r.VxMm + r.VyMm * r.VyMm);
            var ratio = result.Sigma0Mm > 0 ? norm / result.Sigma0Mm : 0;

            // σ₀ 本身会被粗差抬高，所以 3 倍是个偏松的门槛；但它能让人一眼看到"哪个点不对劲"
            sb.AppendLine($"{r.GcpId,-8} {r.VxMm,10:+0.0000;-0.0000} {r.VyMm,10:+0.0000;-0.0000} " +
                          $"{norm,10:0.0000} {ratio,8:0.00}{(ratio > 3 ? "   ← 偏大" : "")}");
        }
        ResPanelText.Text = sb.ToString();
    }

    private string FormatResection(ResectionResult r) =>
        $"Xs = {r.Xs:0.000} m\n" +
        $"Ys = {r.Ys:0.000} m\n" +
        $"Zs = {r.Zs:0.000} m\n" +
        $"φ = {r.PhiDeg:+0.000000;-0.000000}°\n" +
        $"ω = {r.OmegaDeg:+0.000000;-0.000000}°\n" +
        $"κ = {r.KappaDeg:+0.000000;-0.000000}°\n" +
        $"σ₀ = {r.Sigma0Mm:0.0000} mm　迭代 {r.Iterations} 次\n" +
        $"m_Xs = {r.MXs:0.000} m　m_Ys = {r.MYs:0.000} m　m_Zs = {r.MZs:0.000} m";

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

    private void SetBusy(bool busy, string? text)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) BusyText.Text = text;
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
}


