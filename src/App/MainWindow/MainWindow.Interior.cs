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

    /// <summary>载入相机检校文件。返回是否成功；也只给离屏渲染工具用，见 MainWindow.Tooling.cs。</summary>
    internal bool LoadCameraFrom(string path)
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
            !TryRead(ParamY0, out var y0) || !TryRead(ParamPixel, out var pixelUm))
        {
            DiagSummary.Text = "! 内参必须都是数字（小数点用点，不要用逗号）";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        if (f <= 0 || pixelUm <= 0)
        {
            DiagSummary.Text = "! 焦距与像元尺寸必须大于 0";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        // 面板上按手册填微米，模型里一律存 mm/px——共线方程的 f 是毫米，两者必须同量纲
        var pixelMm = pixelUm / 1000.0;

        var io = new InteriorOrientation
        {
            FocalLength = f,
            X0 = x0,
            Y0 = y0,
            PixelSizeMm = pixelMm,
        };

        OnImageLoaded(null, image);
        RefreshTree();
        UpdateFiducialUi();

        // 参数式也是内定向的一次结论：同样留一份结果，换片/复核/写报告时看得到。
        // 它没有残差与 σ₀（没有多余观测），页脚写明这一点。
        // 四个数写进影像放到「接受」那一刻（ADR-26）；参数式没有六系数，顺手把旧的清掉
        var parametric = ParametricInteriorResult(image, io);
        parametric.OnAccept = () =>
        {
            image.Interior = io;
            image.Affine = null;
            image.InteriorTimeUtc = DateTimeOffset.Now;
            image.Status = ImageStatus.InteriorDone;
        };
        ShowResult(parametric);
        LogSolve("内定向", image.Id, true,
            $"参数式　f {f:0.###} mm　δ {pixelMm:0.000000} mm/px　x0 {x0:+0.0000;-0.0000}　y0 {y0:+0.0000;-0.0000}");

        DiagSummary.Text = $"✓ 已应用参数式内参：f = {f:0.###} mm，δ = {pixelUm:0.###} µm（{pixelMm:0.000000} mm/px）";
        DiagSummary.Foreground = OkBadge;
    }

    /// <summary>微米输入框边上实时给一句毫米换算，省得用户在两种单位之间心算。</summary>
    private void PixelUm_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PixelHint is null) return;

        PixelHint.Text = TryRead((TextBox)sender, out var um) && um > 0
            ? $"= {um / 1000.0:0.000000} mm/px"
            : string.Empty;
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
                ? "多半是框标编号与点位对不上：对照点表里的方位（如「右下角」），逐个核对后重新点一遍。"
                : string.Empty;

            ShowResult(FailTable(ResultKind.Interior, image.Id, $"内定向 · {image.Id}",
                $"（{result.ErrorCode}）{result.Message}" + (hint.Length > 0 ? "\n" + hint : "")));
            LogSolve("内定向", image.Id, false, $"rc = {result.ErrorCode}　{result.Message}");
            DiagSummary.Text = $"✗ 内定向失败：{result.Message}";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        // 内方位与六系数拿在手里，写进影像放到「接受」那一刻（ADR-26）。
        // 结果表也从这几个值排，不读工程里还没写进去的状态。
        var interior = new InteriorOrientation
        {
            FocalLength = _camera.FocalLengthMm,
            X0 = result.X0Mm,
            Y0 = result.Y0Mm,
            PixelSizeMm = result.PixelSizeMm,
        };
        // 六系数一起入账：反算的 f/x0/y0 推不回旋转与倾斜，重开复核要靠它们
        var affine = new AffineInterior
        {
            A0 = result.A0,
            A1 = result.A1,
            A2 = result.A2,
            B0 = result.B0,
            B1 = result.B1,
            B2 = result.B2,
        };

        var shown = InteriorResult(image, result, interior.FocalLength);
        shown.OnAccept = () =>
        {
            image.Interior = interior;
            image.Affine = affine;
            image.InteriorTimeUtc = DateTimeOffset.Now;
            image.Status = ImageStatus.InteriorDone;
        };
        ShowResult(shown);

        LogSolve("内定向", image.Id, true,
            $"δ {result.PixelSizeMm:0.000000} mm/px　σ₀ {result.Sigma0Mm:0.000000} mm　" +
            $"框标 {result.UsedCount} 个　多余观测 {result.Redundancy}");

        var warn = result.Sigma0Mm > 0.02;
        DiagSummary.Text = warn
            ? $"! 内定向完成，但 σ₀ 偏大（{result.Sigma0Mm:0.0000} mm），检查残差最大的框标"
            : "✓ 内定向完成";
        DiagSummary.Foreground = warn ? WarnBadge : OkBadge;

        OnImageLoaded(null, image);
        OnSelectionChanged(null, EventArgs.Empty);
        RefreshTree();
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
    /// 点表里框标的一行。格式串住在这里，刷新方法里不出现小数位。
    ///
    /// 原来这个类放在 MainWindow.Gcp.cs 里（框标表的行定义住在控制点文件里），第 4 步归位。
    /// </summary>
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

        /// <summary>
        /// 造一行。
        /// </summary>
        /// <param name="id">框标号，同时是这张表的主键。</param>
        /// <param name="xmm">框标在像平面上的坐标，mm（来自相机检校）。</param>
        /// <param name="ymm">同上，y。</param>
        /// <param name="mark">本影像上这个框标的量测，没测时为 null。</param>
        /// <param name="showWhere">框标大致关于像主点对称时才写方位，否则方位描述会误导。</param>
        public static FiducialRow From(int id, double xmm, double ymm, FiducialMark? mark, bool showWhere) => new()
        {
            FiducialId = id,
            Id = id.ToString(CultureInfo.InvariantCulture),
            X = Format.Signed(xmm, "0.0000"),
            Y = Format.Signed(ymm, "0.0000"),
            Direction = showWhere ? FiducialHelper.Describe(xmm, ymm) : "",
            PixelI = mark is null ? "—" : mark.I.ToString("0.0", CultureInfo.InvariantCulture),
            PixelJ = mark is null ? "—" : mark.J.ToString("0.0", CultureInfo.InvariantCulture),
            Note = mark is null ? "未测" : "已测"
        };
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

        var rows = _camera is null
            ? new List<FiducialRow>()
            : _camera.Fiducials
                .Select(f => FiducialRow.From(
                    f.Id, f.Xmm, f.Ymm,
                    image?.FiducialMarks.FirstOrDefault(m => m.FiducialId == f.Id),
                    showWhere))
                .ToList();

        RefillTable(FiducialGrid, rows, r => r.FiducialId == keep);

        MeasureSummary.Text = _camera is null
            ? "点表 · 框标：尚未载入相机检校文件"
            : $"点表 · 框标 {_camera.Fiducials.Count} 个　本片已测 {image?.FiducialMarks.Count ?? 0} 个"
              + (image?.Interior is null ? "" : $"　内定向已完成：δ = {image.Interior.PixelSizeMm:0.00000} mm/px");
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

    /// <summary>
    /// 输入检查：做内定向还缺什么。只出现在「内定向」模块里——别处不念内方位（各模块只显示各自的事）。
    ///
    /// <para>相机检校文件只是<b>两条路里的一条</b>（框标法）。内方位一旦齐了——不管参数式填的
    /// 还是框标求的——它就是废话，所以「未载入相机检校文件」只在真的缺内方位时出现。</para>
    /// </summary>
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

        var hasInterior = image.Interior is { IsValid: true };
        var lines = new List<string>();
        var allOk = true;

        if (_camera is null)
        {
            if (!hasInterior)
            {
                lines.Add("✗ 未载入相机检校文件（走框标法才需要；参数式直接填 f / x0 / y0 / δ 也行）");
                allOk = false;
            }
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
            lines.Add($"✓ δ = {io.PixelSizeMm * 1000.0:0.###} µm（{io.PixelSizeMm:0.000000} mm/px），" +
                      $"x0 = {Format.Signed(io.X0, "0.0000")} mm");
        else
            allOk = false;

        CheckText.Text = string.Join("\n", lines);
        CheckText.Foreground = allOk ? OkBadge : WarnBadge;
    }

}
