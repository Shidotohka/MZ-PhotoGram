using System;
using System.Linq;
using System.Windows;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 绝对定向（M7）：把模型坐标拉到地面坐标。
///
/// <para><b>界面只放三样东西</b>：一句能不能解、一个像对、一个按钮。七参数与不符值全部
/// 进结果窗口——右栏不放结果，这条纪律见 <see cref="MainWindow.Results.cs"/>。</para>
///
/// <para><b>参与解算的不是匹配点云</b>：每个已刺点的控制点，用自己的两片像点交会出模型坐标，
/// 再和它的地面坐标按点号配对。理由见 Docs/路线图.md §3 M7。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 要变换的像对：优先两窗里看得见的那一对；否则退到工程里第一个解过相对定向的像对。
    ///
    /// <para>为什么要退：绝对定向的工作区是「模型」形态，只有一个视窗，
    /// <see cref="RelativeImages"/> 拿不到左右片。此时用第一个已定向的像对是唯一可解释的选择，
    /// 面板上会把用的是哪一对写出来。</para>
    /// </summary>
    private StereoPair? AbsolutePair()
    {
        var (left, right) = RelativeImages();
        if (left is not null && right is not null)
        {
            var visible = _project.FindPair(left.Id, right.Id);
            if (visible is not null) return visible;
        }

        return _project.Pairs.FirstOrDefault(p => p.Relative is not null)
               ?? _project.Pairs.FirstOrDefault();
    }

    /// <summary>刷新绝对定向面板：用的是哪个像对、能不能解。（internal 供离屏工具读面板文字）</summary>
    internal void UpdateAbsolutePanel()
    {
        if (AbsolutePanel is null) return;

        var pair = AbsolutePair();
        var prep = AbsoluteOrientationTask.Prepare(_project, pair);

        AbsolutePairText.Text = pair is null
            ? "像对 —"
            : $"{pair.LeftImageId} × {pair.RightImageId}" +
              (pair.Relative is null
                  ? "　未解相对定向"
                  : $"　基准 {pair.Relative.ReferenceImageId ?? "未记录"}");

        AbsoluteSolveButton.IsEnabled = prep.Ready;
        AbsoluteHint.Text = prep.Ready
            ? $"两片都刺过点的控制点 {prep.Count} 个（含检查点），可以解算。"
            : prep.Message;
    }

    private void SolveAbsolute_Click(object sender, RoutedEventArgs e) => SolveAbsoluteNow();

    /// <summary>跑一次绝对定向，把七参数与不符值摆到结果窗口。</summary>
    internal AbsoluteOrientationRun SolveAbsoluteNow()
    {
        var pair = AbsolutePair();
        var label = pair is null ? "—" : $"{pair.LeftImageId} × {pair.RightImageId}";
        var run = AbsoluteOrientationTask.Run(_project, pair);

        if (!run.Success)
        {
            ShowResult(FailTable(ResultKind.Absolute, label, $"绝对定向 · {label}", run.Message));
            GateBlocked("绝对定向", run.Message);
            UpdateAbsolutePanel();
            return run;
        }

        var r = run.Result!;

        LogSolve("绝对定向", label, true,
            $"λ {r.Scale:0.000000}　φ {r.PhiDeg:+0.0000;-0.0000}°　" +
            $"ω {r.OmegaDeg:+0.0000;-0.0000}°　κ {r.KappaDeg:+0.0000;-0.0000}°　" +
            $"rms {r.Rms:0.0000} m　σ₀ {r.Sigma0:0.0000} m　" +
            $"控点 {run.Used.Count}　检查点 {run.Checks.Count}");

        // 提醒的全文进日志，结果窗口只留结论——和相对定向同一个口径
        foreach (var warning in run.Warnings) Log("!", warning);

        DiagSummary.Text = run.Warnings.Count > 0
            ? $"! 绝对定向完成，{run.Warnings.Count} 条提醒（见日志）"
            : $"✓ 绝对定向完成　rms {r.Rms:0.000} m";
        DiagSummary.Foreground = run.Warnings.Count > 0 ? WarnBadge : OkBadge;

        var absoluteTable = AbsoluteResultTable(run);
        absoluteTable.OnAccept = () => AbsoluteOrientationTask.Commit(pair!, run);
        ShowResult(absoluteTable);
        UpdateAbsolutePanel();
        RefreshTree();
        return run;
    }
}
