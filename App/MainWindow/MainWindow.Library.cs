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

/// <summary>
/// 影像库：添加影像、去重 Id、位图缓存与解码、载入工作区。
/// 
/// 这里混了两件事：解码缓存（服务）与「载入到哪个视窗」（界面编排）。第 5 步把前者搬到
/// App/Services/ImageLibrary.cs，见 Docs/重构计划.md。
/// </summary>
public partial class MainWindow
{
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
        if (_images.TryGet(item.Id, out var cached)) return cached;

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
            var source = await _images.LoadAsync(item.Id, item.FullPath);

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
        if (_moduleName == "前方交会") UpdateIntersectionPanel();
    }

}
