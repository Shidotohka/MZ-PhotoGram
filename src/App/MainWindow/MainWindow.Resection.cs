using System;
using System.Windows;
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

    /// <summary>刷「后方交会」面板：只留一行「能不能解」，缺什么就说什么。</summary>
    private void UpdateResectionPanel()
    {
        if (ResectionHint is null) return;

        var image = CurrentImage;
        if (image is null)
        {
            ResectionHint.Text = "未载入影像。";
            return;
        }

        if (image.Interior is not { IsValid: true })
        {
            ResectionHint.Text = "缺内方位：先做内定向。";
            return;
        }

        var prep = ResectionTask.Prepare(image, _project.Gcps);
        ResectionHint.Text = prep.Count >= NativeAlgorithms.MinControlPoints
            ? $"可解：{prep.Count} 个控制点"
            : $"控制点 {prep.Count} 个，至少 {NativeAlgorithms.MinControlPoints} 个";
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

        if (run.Failure != ResectionFailure.None && run.Failure != ResectionFailure.NoImage &&
            run.Failure != ResectionFailure.NoInterior && run.Failure != ResectionFailure.NotEnoughPoints)
            LogSolve("后方交会", CurrentImage?.Id ?? "—", false, run.Message);

        switch (run.Failure)
        {
            case ResectionFailure.NoImage:
                DiagSummary.Text = run.Message;
                DiagSummary.Foreground = WarnBadge;
                return null;

            case ResectionFailure.NoInterior:
            case ResectionFailure.NotEnoughPoints:
                GateBlocked("后方交会", run.Message);
                return null;

            case ResectionFailure.GroundOrderUnknown:
                DiagSummary.Text = "✗ " + run.Message;
                DiagSummary.Foreground = WarnBadge;
                return null;

            case ResectionFailure.Solver:
                var rejected = run.Result!;

                // 数值照给，但每一行都标成诊断值：没见过失败结果的人最容易把它当解
                ShowResult(ResectionDiagnostic(CurrentImage, rejected));
                DiagSummary.Text = "✗ " + run.Message;
                DiagSummary.Foreground = ErrBadge;
                return rejected;
        }

        var result = run.Result!;
        var image = CurrentImage!;

        // 外方位与逐点残差进结果窗口；坐标系的统一（判手性、换轴）在 Core 里做完就算完，
        // 界面上只给一套数学系坐标，不再解释是怎么换过来的。
        // 写进影像放到「接受」那一刻（ADR-26）：这里只说明"写什么"。
        var resected = ResectionResult(image, result);
        resected.OnAccept = () => ResectionTask.Commit(image, run);
        ShowResult(resected);

        LogSolve("后方交会", image.Id, true,
            $"迭代 {result.Iterations} 次　σ₀ {result.Sigma0Mm:0.0000} mm　" +
            $"条件数 {result.Condition:0.#}　多余观测 {result.Redundancy}");

        DiagSummary.Text = "✓ 后方交会完成";
        DiagSummary.Foreground = OkBadge;

        RefreshPointTable();
        UpdateStatusCounts();
        RefreshTree();
        return result;
    }
}
