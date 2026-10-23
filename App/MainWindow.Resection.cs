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
/// 后方交会：收集观测、判手性、解算、写回外方位、生成结果文案。
/// 
/// 这一整套属于流程控制，本来就该在 Core。第 2 步整体搬到 Core/Tasks/ResectionTask.cs，
/// 本文件只剩「按钮 → 调用 → 显示」。
/// </summary>
public partial class MainWindow
{
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

}
