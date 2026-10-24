using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 后方交会的<b>界面部分</b>：面板提示、按钮、把解算结果摆到界面上。
/// <para>
/// 收集观测、判手性、换轴、解算、写回模型都不在这里，在
/// <see cref="Photogrammetry.Core.Tasks.ResectionTask"/>。本文件只回答两件事：
/// 现在能不能解（面板提示），解完了怎么显示（结果文案、残差表、状态条）。
/// </para>
/// </summary>
public partial class MainWindow
{
    // ---------- 后方交会 ----------

    /// <summary>刷「后方交会」面板的提示：能解就报能解，不能解就说清缺什么。</summary>
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

        var prep = ResectionTask.Prepare(image, _project.Gcps);
        var text = $"影像 {image.Id}：可用于解算的控制点 {prep.Count} 个";

        if (image.Interior is { } io && io.IsValid)
            text += $"\nf = {io.FocalLength:0.###} mm　x0 = {io.X0:0.####}　y0 = {io.Y0:0.####}　δ = {io.PixelSizeMm:0.######} mm/px";
        else
            text += "\n内方位元素缺失：先在「内定向」里解算。";

        if (prep.Count < NativeAlgorithms.MinControlPoints)
            text += $"\n至少需要 {NativeAlgorithms.MinControlPoints} 个才能解算。";
        if (prep.Skipped.Count > 0)
            text += "\n未参与：" + string.Join("，", prep.Skipped.Take(6)) +
                    (prep.Skipped.Count > 6 ? $" 等 {prep.Skipped.Count} 项" : "");

        ResectionHint.Text = text;
    }

    private void SolveResection_Click(object sender, RoutedEventArgs e) => SolveResectionNow();

    /// <summary>
    /// 跑一次后方交会并把结果摆到界面上。返回解算结果，失败时为 null。
    /// 离屏渲染工具（src/Snap）的 M3 走这条——它要的是「解算后点表与残差页也刷新了」，
    /// 所以不能绕开界面直接调 Core。全表见 MainWindow.Tooling.cs。
    /// </summary>
    internal ResectionResult? SolveResectionNow()
    {
        SetBusy(true, "正在解算后方交会…");
        ResectionRun run;
        try
        {
            run = ResectionTask.Run(CurrentImage, _project.Gcps);
        }
        finally
        {
            SetBusy(false, null);
        }

        switch (run.Failure)
        {
            case ResectionFailure.NoImage:
                DiagSummary.Text = run.Message;
                DiagSummary.Foreground = WarnBadge;
                return null;

            case ResectionFailure.NoInterior:
            case ResectionFailure.NotEnoughPoints:
                DiagSummary.Text = "✗ " + run.Message;
                DiagSummary.Foreground = ErrBadge;
                return null;

            case ResectionFailure.GroundOrderUnknown:
                DiagSummary.Text = "✗ " + run.Message;
                DiagSummary.Foreground = WarnBadge;
                return null;

            case ResectionFailure.Solver:
                var rejected = run.Result!;
                ResectionResultText.Foreground = ErrBadge;
                ResectionResultText.Text = $"未采用（rc = {rejected.ErrorCode}）\n{rejected.Message}\n\n" +
                                           "诊断值（不要当作结果）：\n" +
                                           $"Xs {rejected.Xs:0.0}　Ys {rejected.Ys:0.0}　Zs {rejected.Zs:0.0} m\n" +
                                           $"φ {rejected.PhiDeg:+0.0;-0.0}°　ω {rejected.OmegaDeg:+0.0;-0.0}°　" +
                                           $"κ {rejected.KappaDeg:+0.0;-0.0}°\nσ₀ {rejected.Sigma0Mm:0.0000} mm";
                DiagSummary.Text = "✗ " + run.Message;
                DiagSummary.Foreground = ErrBadge;
                return rejected;
        }

        var result = run.Result!;
        var image = CurrentImage!;

        // 上一次失败会把文字染红，成功后要还原成继承来的颜色，否则红色会一直留着
        ResectionResultText.ClearValue(TextBlock.ForegroundProperty);
        ResectionResultText.Text = FormatResection(result) +
                                   $"\n{run.Evidence!.Describe()}\n" +
                                   "外方位 X/Y 在数学系里（X 东、Y 北）。";
        ResPanelText.Text = FormatResidualTable(image.Id, result);

        DiagSummary.Text = $"✓ 后方交会完成：迭代 {result.Iterations} 次，σ₀ = " +
                           $"{result.Sigma0Mm:0.0000} mm，摄影距离约 {result.MeanRangeM:0} m，" +
                           $"{GroundAxes.Describe(run.Evidence.Order)}";
        DiagSummary.Foreground = OkBadge;

        ResTab.IsChecked = true;
        RefreshPointTable();
        UpdateStatusCounts();
        RefreshTree();
        return result;
    }

    private static string FormatResection(ResectionResult r) =>
        $"Xs = {r.Xs:0.000} m\n" +
        $"Ys = {r.Ys:0.000} m\n" +
        $"Zs = {r.Zs:0.000} m\n" +
        $"φ = {r.PhiDeg:+0.000000;-0.000000}°\n" +
        $"ω = {r.OmegaDeg:+0.000000;-0.000000}°\n" +
        $"κ = {r.KappaDeg:+0.000000;-0.000000}°\n" +
        $"σ₀ = {r.Sigma0Mm:0.0000} mm　迭代 {r.Iterations} 次\n" +
        $"m_Xs = {r.MXs:0.000} m　m_Ys = {r.MYs:0.000} m　m_Zs = {r.MZs:0.000} m";

    /// <summary>残差表，进「结果残差」页。</summary>
    private static string FormatResidualTable(string imageId, ResectionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"影像 {imageId}　后方交会　多余观测 {result.Redundancy}　" +
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

        return sb.ToString();
    }
}
