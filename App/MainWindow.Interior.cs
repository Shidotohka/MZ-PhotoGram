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
/// 内定向：参数式与框标仿射两种方式，含取点、解算、残差表与校验面板。
/// </summary>
public partial class MainWindow
{
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

}
