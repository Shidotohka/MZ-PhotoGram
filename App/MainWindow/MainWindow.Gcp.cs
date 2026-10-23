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

/// <summary>
/// M3 控制点与刺点：控制点文件载入、点表、刺点绑定、放大影像对照。
/// </summary>
public partial class MainWindow
{
    // ---------- M3 控制点与刺点 ----------

    /// <summary>
    /// 点表里控制点的一行。只读展示，数值按固定小数格式化好再交给 DataGrid。
    ///
    /// 格式串住在这里而不是刷新方法里：行对象是这张表唯一的"显示格式"所有者，
    /// 以后加一列、改小数位，只改这一个文件。
    /// </summary>
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

        /// <summary>
        /// 造一行。
        /// </summary>
        /// <param name="gcp">控制点本身。</param>
        /// <param name="here">本影像上的同名像点，没刺点时为 null。</param>
        /// <param name="copies">有几张影像刺了这个点，用于提示"共 N 片有观测"。</param>
        /// <param name="hasImage">当前是否载入了影像，用来区分「未载入影像」和「本片未刺点」。</param>
        public static GcpRow From(Gcp gcp, ImagePoint? here, int copies, bool hasImage) => new()
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
                ? (hasImage ? "本片未刺点" : "未载入影像")
                : (copies > 1 ? $"共 {copies} 片有观测" : ""),
        };
    }

    /// <summary>量测表里选中的控制点号。非 null 时左键点影像就是在给它刺点。</summary>
    private string? _activeGcpId;

    /// <summary>当前工作影像：鼠标所在视窗优先，否则取第一个载入影像的可见视窗。</summary>
    private ImageItem? CurrentImage => Workspace.ActiveView?.Image;

    // ---- 离屏渲染工具（src/Snap）用的入口，全部是 internal，全表见 MainWindow.Tooling.cs ----

    /// <summary>点表里选中某个控制点（触发参考影像与刺点模式）。返回是否找到。</summary>
    internal bool SelectGcp(string gcpId)
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
    /// 离屏渲染工具（src/Snap）跳过文件对话框直接调这条，全表见 MainWindow.Tooling.cs。
    /// </summary>
    internal bool LoadControlPointsFrom(string path)
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

    /// <summary>
    /// 重填一张点表：换数据源，再按条件恢复选中行。
    ///
    /// 两张点表（框标 / 控制点）的行类型不同，但这四步完全一样：判空、换源、写摘要、恢复选中。
    /// 摘要文案各表不同，留在调用方；这里只管最机械的两步。
    /// </summary>
    /// <param name="isKept">
    /// 判断某行是不是要恢复选中的那一行。调用方传进来的必须是<b>局部快照</b>：
    /// 换 <paramref name="grid"/>.ItemsSource 会触发 SelectionChanged，现读字段会踩到新的选中状态。
    /// </param>
    private static void RefillTable<TRow>(DataGrid grid, IReadOnlyList<TRow> rows, Func<TRow, bool> isKept)
        where TRow : class
    {
        grid.ItemsSource = rows;

        if (rows.FirstOrDefault(isKept) is { } again) grid.SelectedItem = again;
    }

    /// <summary>重建后方交会的控制点表。</summary>
    private void RefreshGcpTable()
    {
        if (GcpGrid is null) return;

        var image = CurrentImage;
        var keep = _activeGcpId;

        var rows = _project.Gcps
            .Select(gcp => GcpRow.From(
                gcp,
                image?.FindGcpPoint(gcp.Id),
                _project.Images.Count(i => i.FindGcpPoint(gcp.Id) is not null),
                image is not null))
            .ToList();

        RefillTable(GcpGrid, rows, r => r.Id == keep);

        var bound = image?.BoundGcpCount ?? 0;
        MeasureSummary.Text = _project.Gcps.Count == 0
            ? "点表 · 控制点：尚未载入控制点文件（在右侧「载入控制点文件…」）"
            : $"点表 · 控制点 {_project.Gcps.Count} 个"
              + (_project.Gcps.Count(g => g.Type == GcpType.CheckPoint) is > 0
                  ? $"（含检查点 {_project.Gcps.Count(g => g.Type == GcpType.CheckPoint)} 个）"
                  : "")
              + $"　本片已刺 {bound} 个"
              + (image?.Interior is null ? "　当前影像还没有内定向结果" : "");
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
    internal bool BindGcpAt(string gcpId, int px, int py, ImageItem? image = null)
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

}
