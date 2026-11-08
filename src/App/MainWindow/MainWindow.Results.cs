using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 结果：一份解算的「元素表 + 残差表」怎么装，以及它进了哪个集合、给谁看。
///
/// <para><b>结果不再放底部页签，也不放右栏。</b>底部那一页只有一个槽，第二步算完第一步
/// 就没了；右栏 372 px 又会把十几行残差挤到折叠线以下。现在结果按
/// 「模块 + 对象」留在会话级的 <see cref="ResultStore"/> 里，由
/// <see cref="ResultWindow"/> 显示——一个窗口装全部，选哪份看哪份（ADR-25）。</para>
///
/// <para>每个模块只负责把「解出来的东西」翻译成人能读的几行（下面那七个构造函数），
/// 排版与留档不归它们管。</para>
/// </summary>
public partial class MainWindow
{
    private readonly ResultStore _results = new();

    /// <summary>结果窗口。单实例、非模态；关掉后置空，下次再开。</summary>
    private ResultWindow? _resultWindow;

    /// <summary>
    /// 模块名 → 结果种类。「报告」「光束平差」没有结果，返回 null。
    /// 窗口按它划作用域：这一步的结果只在这一步里看。
    /// </summary>
    private static ResultKind? KindOfModule(string module) => module switch
    {
        "内定向" => ResultKind.Interior,
        "后方交会" => ResultKind.Resection,
        "前方交会" => ResultKind.Intersection,
        "相对定向" => ResultKind.Relative,
        "模型交会" => ResultKind.Model,
        "绝对定向" => ResultKind.Absolute,
        _ => null,
    };

    /// <summary>当前模块对应的结果种类。Snap 用它验「窗口跟到刚解的那一步」。</summary>
    internal ResultKind? CurrentResultKind => KindOfModule(_moduleName);

    /// <summary>结果窗口现在管哪一步。没开就是 null。</summary>
    internal ResultKind? ResultWindowScope => _resultWindow?.Scope;

    // ===================== 显示 =====================

    /// <summary>一份结果算完了：留档、更新右栏按钮、把窗口切到这一步并选中它。</summary>
    private void ShowResult(SolveResult result)
    {
        _results.Put(result);
        UpdateResultButton();

        if (_resultWindow is { IsVisible: true } window)
        {
            // 刚解的是哪一步，窗口就显示哪一步——不把别的步骤的结果并排放在一起
            window.ShowKind(result.Kind, result);
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            return;
        }

        if (ResultWindow.AutoOpen) OpenResultWindow(result.Kind);
    }

    /// <summary>
    /// 打开（或唤起）结果窗口。右栏按钮、菜单「查看 → 结果与残差…」与 F3 都走这里。
    ///
    /// <para><paramref name="kind"/> 不给就用当前模块；没有结果的步骤（报告、光束平差）
    /// 直接不开。没有结果时也允许打开：空状态那句话正是「结果会留在这里」的说明。</para>
    /// </summary>
    private void OpenResultWindow(ResultKind? kind = null)
    {
        var scope = kind ?? KindOfModule(_moduleName);
        if (scope is not { } target)
        {
            // 「报告」「光束平差」没有结果；菜单/快捷键也得给一句，不能静默无反应
            DiagSummary.Text = "! 这一步没有结果可看（报告还没实现）";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        if (_resultWindow is { IsVisible: true } window)
        {
            // 从另一模块按「结果」时，窗口跟着切过去，而不是并排摆两套结果
            window.ShowKind(target, _results.LatestOf(target));
            window.Activate();
            return;
        }

        _resultWindow = new ResultWindow(_results, target, AcceptResult, DiscardResult) { Owner = this };
        _resultWindow.Closed += (_, _) => _resultWindow = null;
        _resultWindow.Show();
    }

    /// <summary>
    /// 打开工程后把已入账的结果读回结果集合（M7.5 第 4 步）。
    ///
    /// <para><b>只走一条路</b>：先把结果块还原成"任务结果对象"（<c>StoredResults</c>），
    /// 再交给与解算后**完全相同**的那几个排表函数。所以"屏幕上是什么"与"文件里是什么"
    /// 不会各写一份格式化——这也是重开工程后表格还能逐字对上的原因。</para>
    ///
    /// <para>读回来的结果状态是<b>已入账</b>、不挂 <c>OnAccept</c>：它已经在工程里了，
    /// 再点一次「接受」没有意义（按钮也是禁用的）。</para>
    /// </summary>
    internal void HydrateResults()
    {
        foreach (var image in _project.Images)
        {
            // 内定向"有内参"不等于"有结果"：v3 文件、或解算前就填好的内参，都没有 `interiorTimeUtc`。
            // 只有入账过的那次才留了时刻，才该在结果窗口里出现。
            if (image.Interior is { } interior && image.InteriorTimeUtc is not null)
            {
                var table = StoredResults.Interior(image) is { } fiducial
                    ? InteriorResult(image, fiducial, interior.FocalLength)
                    : ParametricInteriorResult(image, interior);
                _results.Put(Hydrated(table, image.InteriorTimeUtc));
            }

            if (image.Resection is { } resection && StoredResults.Resection(image) is { } resectionRun)
            {
                _results.Put(Hydrated(ResectionResult(image, resectionRun.Result!), resection.TimeUtc));
            }
        }

        foreach (var pair in _project.Pairs)
        {
            if (pair.Relative is { } relative && StoredResults.Relative(_project, pair) is { } relativeRun)
            {
                _results.Put(Hydrated(RelativeResultTable(relativeRun), relative.TimeUtc));
            }

            if (pair.Model is { } model && StoredResults.Model(_project, pair) is { } outcome)
            {
                _results.Put(Hydrated(ModelResultTable(outcome), model.TimeUtc));
            }

            if (pair.Absolute is { } absolute && StoredResults.Absolute(_project, pair) is { } absoluteRun)
            {
                _results.Put(Hydrated(AbsoluteResultTable(absoluteRun), absolute.TimeUtc));
            }
        }

        UpdateResultButton();
        if (_resultWindow is { IsVisible: true } window)
        {
            window.ShowKind(window.Scope, _results.LatestOf(window.Scope));
        }
    }

    /// <summary>刚读回来的结果：已入账、时刻取存档的、不再挂入账动作。</summary>
    private static SolveResult Hydrated(SolveResult result, DateTimeOffset? solvedAt)
    {
        result.State = ResultState.Accepted;
        result.OnAccept = null;

        if (solvedAt is { } at)
        {
            result.SolvedAt = at.LocalDateTime;
            result.AcceptedAt = at.LocalDateTime;
        }

        return result;
    }

    /// <summary>
    /// 接受一份结果（ADR-26）：写进工程 → 记日志 → 立即保存 → 刷新受影响的界面。
    ///
    /// <para>写工程那一步由解算方挂在 <see cref="SolveResult.OnAccept"/> 上——只有它手里
    /// 有一手的结果对象（七参数、逐点不符值）。这里是"什么时候写"的判断，那里是"写什么"。</para>
    ///
    /// <para>失败的结果、没有可入账内容的结果一律不接受：结果窗口里按钮本来就是禁用的，
    /// 这里再挡一次，免得被别的入口绕过去。</para>
    /// </summary>
    internal bool AcceptResult(SolveResult result)
    {
        if (result.State == ResultState.Accepted) return false;
        if (result.Failed || result.OnAccept is null) return false;

        result.OnAccept();
        _results.Accept(result);

        LogSolve(result.KindLabel, result.TargetId, true, "已入账");
        SaveAfterAccept();
        RefreshAfterDecision();
        return true;
    }

    /// <summary>不采纳：从集合里删掉，日志留一行。它没进过工程，所以没有要回滚的东西。</summary>
    internal bool DiscardResult(SolveResult result)
    {
        if (!_results.Discard(result)) return false;

        Log("!", $"{result.KindLabel} · {result.TargetId} 未采纳（结果已丢弃，需要就重解一次）");
        UpdateResultButton();
        return true;
    }

    /// <summary>
    /// 接受即存盘：成果的价值就是"关掉程序还在"。没有保存路径时不弹另存为——
    /// 那个对话框会把"边解边看"的循环打断；留一句日志，用户按 Ctrl+S 时自然选路径。
    /// </summary>
    private void SaveAfterAccept()
    {
        if (_projectPath is null)
        {
            Log("!", "结果已入账；工程还没有保存路径，按 Ctrl+S 才会写进文件");
            return;
        }

        try
        {
            ProjectFile.Save(_project, _projectPath);
            Log("OK", $"工程已保存　{System.IO.Path.GetFileName(_projectPath)}");
        }
        catch (Exception ex)
        {
            Log("✗", "结果已入账，但保存失败：" + ex.Message);
        }
    }

    /// <summary>入账/丢弃之后，界面上"取决于工程状态"的东西都要重读一遍。</summary>
    private void RefreshAfterDecision()
    {
        RefreshPointTable();
        RefreshTree();
        UpdateStatusCounts();
        UpdateResultButton();
        UpdateFiducialUi();      // 内方位刚出现/消失时，框标清单与「输入检查」都跟着变
        UpdateModulePanels();
        if (_resultWindow is { IsVisible: true } window) window.ShowKind(window.Scope, _results.LatestOf(window.Scope));
    }

    private void ResultButton_Click(object sender, RoutedEventArgs e) => OpenResultWindow();

    private void OpenResults_Click(object sender, RoutedEventArgs e) => OpenResultWindow();

    /// <summary>
    /// 右栏标题上那颗按钮：**当前这一步**有几份结果 + 能不能按。
    /// 解算前禁用，不让人点了个寂寞；切模块时跟着变。
    /// </summary>
    private void UpdateResultButton()
    {
        if (ResultButton is null) return;

        var kind = KindOfModule(_moduleName);
        var count = kind is { } k ? _results.CountOf(k) : 0;

        ResultButton.Content = count == 0 ? "结果" : $"结果 ({count})";
        ResultButton.IsEnabled = count > 0;
        ResultButton.ToolTip = kind is null
            ? "这一步没有结果可看"
            : count == 0
                ? $"{ResultKinds.Label(kind.Value)}还没有结果：解算之后会留在这里"
                : $"查看{ResultKinds.Label(kind.Value)}的 {count} 份结果（F3）";
    }

    /// <summary>换工程时用：上一份结果对着旧数据，留着必然被误读。</summary>
    private void ClearResult()
    {
        _results.Clear();
        UpdateResultButton();

        // 窗口不订阅集合，这里显式让它退回空状态。
        // 当前模块没有结果种类（报告/光束平差）时，留在窗口原来的作用域上清空。
        if (_resultWindow is { IsVisible: true } window)
        {
            window.ShowKind(KindOfModule(_moduleName) ?? window.Scope, null);
        }
    }

    // ===================== 供离屏渲染工具断言 =====================

    /// <summary>一份结果上有什么。给 Snap 用——它验的是"界面真的摆出了这几行"，不是"算对了"。</summary>
    internal sealed record ResultSnapshot(
        ResultKind? Kind,
        string Title,
        IReadOnlyList<ResultRow> Items,
        IReadOnlyList<string> Headers,
        IReadOnlyList<ResidualRow> Rows,
        string Note);

    /// <summary>最近一次解出的结果。没有结果时返回一份空的（标题写「尚未解算」）。</summary>
    internal ResultSnapshot SnapshotResult() =>
        _results.Latest is { } result
            ? ToSnapshot(result)
            : new ResultSnapshot(null, "尚未解算", Array.Empty<ResultRow>(),
                Array.Empty<string>(), Array.Empty<ResidualRow>(), string.Empty);

    /// <summary>会话里的全部结果，按作业顺序。Snap 用它验「解第二步没有把第一步顶掉」。</summary>
    internal IReadOnlyList<ResultSnapshot> SnapshotResults() => _results.All.Select(ToSnapshot).ToList();

    /// <summary>结果窗口现在是不是开着。Snap 用它验「解完自动打开」这条。</summary>
    internal bool ResultWindowIsOpen => _resultWindow is { IsVisible: true };

    /// <summary>结果窗口本体。Snap 的 <c>--result-shot</c> 要单独渲染它（它不在主窗的树里）。</summary>
    internal Window? ResultWindowForTooling => _resultWindow;

    private static ResultSnapshot ToSnapshot(SolveResult result) =>
        new(result.Kind, result.Title, result.Items, result.Headers, result.Rows, result.Note);

    // ===================== 各模块的结果表 =====================
    private static string Fmt(double value, string format) => Format.Fixed(value, format);

    /// <summary>带正负号的写法，角元素与残差用。</summary>
    private static string FmtSigned(double value, string format) => Format.Signed(value, format);

    internal static string GaugeName(int gauge) => gauge switch
    {
        1 => "b_y",
        2 => "b_z",
        _ => "b_x",
    };

    /// <summary>解算没通过时的那一份：一行「未解出」，原因写在页脚。</summary>
    private static SolveResult FailTable(ResultKind kind, string targetId, string title,
        string message, bool notAdopted = false)
    {
        var table = new SolveResult
        {
            Kind = kind,
            TargetId = targetId,
            // 失败必须写在标题上：结果是快照，翻回来的人先看到的就是标题这一行
            Title = title + (notAdopted ? " · 未采用" : " · 未解出"),
            Failed = true,
            Note = message,
            NoteWarn = true,
        };

        table.Add(notAdopted ? "未采用" : "未解出", "✗", "—");
        return table;
    }

    // ---------- 内定向 ----------

    private static SolveResult InteriorResult(ImageItem image, FiducialOrientationResult r,
        double focalLengthMm)
    {
        var table = new SolveResult
        {
            Kind = ResultKind.Interior,
            TargetId = image.Id,
            Title = $"内定向 · {image.Id}",
        };

        table.Num("比例尺（I 向）", "s_x", r.SxMm, "0.000000", "mm/px");
        table.Num("比例尺（J 向）", "s_y", r.SyMm, "0.000000", "mm/px");
        table.Num("像元尺寸", "δ", r.PixelSizeMm, "0.000000", "mm/px", "(s_x+s_y)/2");
        table.Num("主点 x", "x₀", r.X0Mm, "+0.000000;-0.000000", "mm", "像平面，y 向上");
        table.Num("主点 y", "y₀", r.Y0Mm, "+0.000000;-0.000000", "mm");
        table.Add("主点像素", "—",
            $"{Fmt(r.PrincipalI, "0.00")}, {Fmt(r.PrincipalJ, "0.00")}", "px");
        table.Num("坐标轴旋转（I）", "θ_I", r.ThetaIDeg, "+0.0000;-0.0000", "°");
        table.Num("坐标轴旋转（J）", "θ_J", r.ThetaJDeg, "+0.0000;-0.0000", "°");
        table.Num("单位权中误差", "σ₀", r.Sigma0Mm, "0.000000", "mm",
            r.Sigma0Mm > 0.02 ? "偏大" : "", r.Sigma0Mm > 0.02);
        table.Num("最大残差", "max|v|", r.MaxAbsResidualMm, "0.000000", "mm");
        table.Add("多余观测", "r", r.Redundancy.ToString(CultureInfo.InvariantCulture), "", "2n − 6");
        table.Num("焦距", "f", focalLengthMm, "0.00", "mm",
            "相机检定值，不由内定向求");

        table.Head("框标", "vx (mm)", "vy (mm)", "说明");
        foreach (var v in r.Residuals)
        {
            var big = r.Sigma0Mm > 0 &&
                      Math.Max(Math.Abs(v.VxMm), Math.Abs(v.VyMm)) > 5 * r.Sigma0Mm;
            table.Row(big, v.Id.ToString(CultureInfo.InvariantCulture),
                FmtSigned(v.VxMm, "0.000000"), FmtSigned(v.VyMm, "0.000000"),
                big ? "偏大" : "");
        }

        return table;
    }

    /// <summary>
    /// 参数式内定向的结果：只把「采用了哪组内参」摆出来，没有残差与 σ₀。
    ///
    /// <para>它以前不进结果——参数式只是把四个数抄进模型，于是「内定向算完了，结果窗口里
    /// 什么都没有」。可它同样是一次内定向的结论，换片、复核、抄进报告都要看它；
    /// 没有多余观测不是不记录的理由，写在页脚里即可。</para>
    /// </summary>
    private static SolveResult ParametricInteriorResult(ImageItem image, InteriorOrientation io)
    {
        var table = new SolveResult
        {
            Kind = ResultKind.Interior,
            TargetId = image.Id,
            Title = $"内定向 · {image.Id}",
        };

        table.Num("焦距", "f", io.FocalLength, "0.0000", "mm", "参数式：直接采用给定值");
        table.Num("主点 x", "x₀", io.X0, "+0.000000;-0.000000", "mm");
        table.Num("主点 y", "y₀", io.Y0, "+0.000000;-0.000000", "mm");
        table.Num("像元尺寸", "δ", io.PixelSizeMm, "0.000000", "mm/px");
        table.Add("影像尺寸", "—", $"{image.Width} × {image.Height}", "px");

        table.Note = "参数式：直接采用给定内参，没有多余观测，所以没有 σ₀ 与框标残差。要精度指标请用框标仿射。";
        return table;
    }

    // ---------- 后方交会 ----------

    private static SolveResult ResectionResult(ImageItem image, ResectionResult r)
    {
        var table = new SolveResult
        {
            Kind = ResultKind.Resection,
            TargetId = image.Id,
            Title = $"后方交会 · {image.Id}",
        };

        table.Num("摄影中心 X", "X_S", r.Xs, "0.000", "m");
        table.Num("摄影中心 Y", "Y_S", r.Ys, "0.000", "m");
        table.Num("摄影中心 Z", "Z_S", r.Zs, "0.000", "m");
        table.Num("姿态角 φ", "φ", r.PhiDeg, "+0.000000;-0.000000", "°", "R = Rκ·Rω·Rφ");
        table.Num("姿态角 ω", "ω", r.OmegaDeg, "+0.000000;-0.000000", "°");
        table.Num("姿态角 κ", "κ", r.KappaDeg, "+0.000000;-0.000000", "°");
        table.Num("单位权中误差", "σ₀", r.Sigma0Mm, "0.0000", "mm");
        table.Num("最大残差", "max|v|", r.Residuals.Count == 0
            ? double.NaN
            : r.Residuals.Max(v => Math.Sqrt(v.VxMm * v.VxMm + v.VyMm * v.VyMm)), "0.0000", "mm");
        table.Num("中误差", "m_Xs", r.MXs, "0.000", "m");
        table.Num("中误差", "m_Ys", r.MYs, "0.000", "m");
        table.Num("中误差", "m_Zs", r.MZs, "0.000", "m");
        table.Add("参与控制点", "n",
            (r.Residuals.Count).ToString(CultureInfo.InvariantCulture), "个");
        table.Add("多余观测", "r", r.Redundancy.ToString(CultureInfo.InvariantCulture), "", "2n − 6");
        table.Add("条件数", "cond", Fmt(r.Condition, "0.0E+0"), "", "越大越病态");
        table.Num("摄影距离", "—", r.MeanRangeM, "0", "m", "到各控制点的平均");

        table.Head("点号", "Vx (mm)", "Vy (mm)", "|V| (mm)", "|V|/σ₀", "说明");
        foreach (var v in r.Residuals)
        {
            var norm = Math.Sqrt(v.VxMm * v.VxMm + v.VyMm * v.VyMm);
            var ratio = r.Sigma0Mm > 0 ? norm / r.Sigma0Mm : 0;
            table.Row(ratio > 3, v.GcpId,
                FmtSigned(v.VxMm, "0.0000"), FmtSigned(v.VyMm, "0.0000"),
                Fmt(norm, "0.0000"), Fmt(ratio, "0.00"),
                ratio > 3 ? "偏大" : "");
        }

        return table;
    }

    /// <summary>参数没能通过闸门时的诊断页：数值照给，但每行都标成不可信。</summary>
    private static SolveResult ResectionDiagnostic(ImageItem? image, ResectionResult r)
    {
        var id = image?.Id ?? "—";
        var table = FailTable(ResultKind.Resection, id, $"后方交会 · {id}",
            $"（rc = {r.ErrorCode}）{r.Message}", notAdopted: true);

        table.Add("摄影中心 X", "X_S", Fmt(r.Xs, "0.000"), "m", "诊断值，不要当结果", true);
        table.Add("摄影中心 Y", "Y_S", Fmt(r.Ys, "0.000"), "m", "", true);
        table.Add("摄影中心 Z", "Z_S", Fmt(r.Zs, "0.000"), "m", "", true);
        table.Add("姿态角 φ", "φ", FmtSigned(r.PhiDeg, "0.0000"), "°", "", true);
        table.Add("姿态角 ω", "ω", FmtSigned(r.OmegaDeg, "0.0000"), "°", "", true);
        table.Add("姿态角 κ", "κ", FmtSigned(r.KappaDeg, "0.0000"), "°", "", true);
        table.Add("单位权中误差", "σ₀", Fmt(r.Sigma0Mm, "0.0000"), "mm", "", true);
        return table;
    }

    // ---------- 前方交会 ----------

    private static SolveResult IntersectionResult(IntersectionRun run)
    {
        var r = run.Result!;
        var p = r.Point;

        var table = new SolveResult
        {
            Kind = ResultKind.Intersection,
            TargetId = $"{run.Left?.Id} / {run.Right?.Id}",
            Title = $"前方交会 · {run.Left?.Id} / {run.Right?.Id}",
        };

        // 坐标统一在 Core 里换过轴，这里只给一套；不标是哪个系、也不解释怎么换的
        table.Num("交会点 X", "X", p.X, "0.000", "m");
        table.Num("交会点 Y", "Y", p.Y, "0.000", "m");
        table.Num("交会点 Z", "Z", p.Z, "0.000", "m");

        table.Num("交会角", "γ", r.ConvergenceAngleDeg, "0.00", "°", "两条视线的夹角");
        table.Num("基线", "b", r.BaseLength, "0.000", "m");
        table.Num("摄影距离（左）", "S₁", r.RangeLeft, "0.0", "m");
        table.Num("摄影距离（右）", "S₂", r.RangeRight, "0.0", "m");
        table.Num("上下视差", "Δy", r.VerticalParallaxMm, "+0.0000;-0.0000", "mm",
            "理想为 0", Math.Abs(r.VerticalParallaxMm) > 0.02);
        table.Num("交会差", "—", r.MissDistance, "0.0000", "m", "两条视线的最近距离");
        table.Num("投影系数（左）", "N₁", r.NLeft, "0.0000");
        table.Num("投影系数（右）", "N₂", r.NRight, "0.0000");
        table.Num("中误差", "m_X", r.MX, "0.000", "m");
        table.Num("中误差", "m_Y", r.MY, "0.000", "m");
        table.Num("中误差", "m_Z", r.MZ, "0.000", "m");

        table.Head("影像", "vx (mm)", "vy (mm)", "说明");
        table.Row(run.Left?.Id ?? "左片", FmtSigned(r.ResidualLeftXmm, "0.0000"),
            FmtSigned(r.ResidualLeftYmm, "0.0000"), "");
        table.Row(run.Right?.Id ?? "右片", FmtSigned(r.ResidualRightXmm, "0.0000"),
            FmtSigned(r.ResidualRightYmm, "0.0000"), "上下视差 = 两行 vy 之差");

        return table;
    }

    // ---------- 模型交会 ----------

    private SolveResult ModelResultTable(ModelIntersectionOutcome outcome)
    {
        var table = new SolveResult
        {
            Kind = ResultKind.Model,
            TargetId = outcome.PairLabel,
            Title = $"模型交会 · {outcome.PairLabel}",
        };

        table.Add("控制点", "—", outcome.Observations.Count.ToString(CultureInfo.InvariantCulture), "个",
            $"其中检查点 {outcome.Observations.Count(o => o.Type == GcpType.CheckPoint)} 个");

        var worst = outcome.Observations.Count == 0
            ? double.NaN
            : outcome.Observations.Min(o => o.ConvergenceAngleDeg);
        table.Num("最差交会角", "min γ", worst, "0.00", "°",
            worst < 15.0 ? "深度不可靠" : "越大越稳", worst < 15.0);

        table.Add("坐标系", "—", "模型系（比例尺未知）", "", "|b| = 1，形状对、尺寸不对");

        table.Head("点号", "模型 X", "模型 Y", "模型 Z", "交会角 (°)");
        foreach (var o in outcome.Observations)
        {
            table.Row(o.ConvergenceAngleDeg < 15.0, o.GcpId,
                Fmt(o.Model.X, "0.0000"),
                Fmt(o.Model.Y, "0.0000"),
                Fmt(o.Model.Z, "0.0000"),
                Fmt(o.ConvergenceAngleDeg, "0.00"));
        }

        table.Note = "模型坐标的比例尺是自由的（|b| = 1）。下一步「绝对定向」用这批点与地面坐标解七参数，才变成米制。";
        return table;
    }

    // ---------- 绝对定向 ----------

    private SolveResult AbsoluteResultTable(AbsoluteOrientationRun run)
    {
        var r = run.Result!;
        var table = new SolveResult
        {
            Kind = ResultKind.Absolute,
            TargetId = run.PairLabel,
            Title = $"绝对定向 · {run.PairLabel}",
        };

        table.Num("尺度", "λ", r.Scale, "0.000000", "", "模型单位 → 米");
        table.Num("旋转", "φ", r.PhiDeg, "+0.0000;-0.0000", "°", "模型系 → 地面系");
        table.Num("旋转", "ω", r.OmegaDeg, "+0.0000;-0.0000", "°");
        table.Num("旋转", "κ", r.KappaDeg, "+0.0000;-0.0000", "°");
        table.Num("平移", "X₀", r.X0, "0.000", "m");
        table.Num("平移", "Y₀", r.Y0, "0.000", "m");
        table.Num("平移", "Z₀", r.Z0, "0.000", "m");

        // 平面 / 高程分开：交会角小的时候高程误差被 H/B 放大，两者能差一个量级。
        // 只报三维合量，2.4 m 看着像"整片都不准"，其实平面可能只有 0.4 m。
        var gsd = run.GroundSampleDistance;
        string AsPixel(double metres) =>
            double.IsNaN(gsd) || gsd <= 0 ? "" : $"{metres / gsd:0.00} px";
        var sigmaPx = double.IsNaN(gsd) || gsd <= 0 ? double.NaN : r.Sigma0 / gsd;

        table.Num("不符值（平面）", "Δxy", run.RmsXy, "0.0000", "m",
            double.IsNaN(gsd) ? "像点精度就该在这个量级" : $"折 {AsPixel(run.RmsXy)}，像点精度就该在这个量级");
        table.Num("不符值（高程）", "Δz", run.RmsZ, "0.0000", "m",
            double.IsNaN(run.DepthAmplification)
                ? "小交会角下这一项会被放大"
                : $"深度放大 H/B = {run.DepthAmplification:0.0} 倍");
        table.Num("控制点不符值", "rms", r.Rms, "0.0000", "m", "三维合量");

        // 判据：σ₀ 折算成像元，与后方交会的 VL-10（σ₀ ≤ 10δ）同一条规矩。
        // 旧的「验收 2σ₀」永远成立：rms/σ₀ = √((3n−7)/n) ≤ √3 < 2，对任何点数都判绿，是块装饰。
        table.Num("单位权中误差", "σ₀", r.Sigma0, "0.0000", "m",
            double.IsNaN(sigmaPx)
                ? ""
                : $"{sigmaPx:0.00} px　判据 ≤ 10 px",
            !double.IsNaN(sigmaPx) && sigmaPx > 10.0);
        table.Num("最大不符值", "max", r.MaxAbsResidual, "0.0000", "m");
        table.Add("参与 / 检查点", "—", $"{run.Used.Count} / {run.Checks.Count}", "个",
            $"多余观测 {r.Redundancy}");

        // 点位配置体检：σ3/σ1 小 = 控制点接近共面，高程全靠倾角外推
        var flatness = r.Singular.Count >= 3 && r.Singular[0] > 0
            ? r.Singular[2] / r.Singular[0]
            : double.NaN;
        table.Add("点位配置", "σ₃/σ₁", Fmt(flatness, "0.0E+0"), "",
            double.IsNaN(flatness) ? ""
                : flatness < 1e-3 ? "接近共面：高程弱" : "展得开",
            !double.IsNaN(flatness) && flatness < 1e-3);

        if (r.MirrorSuspected)
        {
            table.Add("数据镜像", "!", "疑似左右手系搞反", "", "轴序已核过：查点号配对与粗差", true);
        }

        if (run.Checks.Count > 0)
        {
            table.Num("检查点最大偏差", "Δxy", run.CheckPlanimetricMax, "0.000", "m", "未参与解算");
            table.Num("检查点最大偏差", "Δz", run.CheckHeightMax, "0.000", "m", "未参与解算");
        }

        table.Head("点号", "不符值 (m)", "v/σ₀", "状态");
        foreach (var o in run.Used)
        {
            var ratio = r.Sigma0 > 0 ? o.ResidualLength / r.Sigma0 : double.NaN;
            table.Row(ratio > 2.0, o.GcpId, Fmt(o.ResidualLength, "0.0000"), Fmt(ratio, "0.00"),
                "参与");
        }
        foreach (var c in run.Checks)
        {
            table.Row(false, c.GcpId, "—", "—", "检查点");
        }

        table.Note = "按「接受」后这批数写进 pairs[].absolute；重开工程直接读回来，不用再解一次。";
        return table;
    }

    // ---------- 相对定向 ----------

    private SolveResult RelativeResultTable(RelativeRun run)
    {
        var r = run.Result!;
        var pair = run.Left is null || run.Right is null
            ? null
            : _project.FindPair(run.Left.Id, run.Right.Id);

        var table = new SolveResult
        {
            Kind = ResultKind.Relative,
            TargetId = $"{run.Left?.Id} → {run.Right?.Id}",
            Title = $"相对定向 · {run.Left?.Id} → {run.Right?.Id}",
        };

        table.Num("基线", "b_x", r.Bx, "+0.0000000;-0.0000000", "", "单位基线方向，|b| = 1");
        table.Num("基线", "b_y", r.By, "+0.0000000;-0.0000000", "");
        table.Num("基线", "b_z", r.Bz, "+0.0000000;-0.0000000", "");

        // 教材把航向基线归一成 b_x = 1，读起来直观。但基线不在 x 方向时这个折算会把
        // b_y/b_x 放大成几十上百（本例 gage 是 b_y，算出来 +44.5），是个纯粹的陷阱——
        // 只有基线主要沿 x 才给这两行。
        if (pair?.Relative is { } stored && Math.Abs(stored.Bx) > 0.5)
        {
            table.Num("教材式基线", "b_y", stored.TextbookBy, "+0.00000;-0.00000", "", "按 b_x = 1 折算");
            table.Num("教材式基线", "b_z", stored.TextbookBz, "+0.00000;-0.00000", "");
        }

        table.Add("基准片", "—", run.Left?.Id ?? "—", "", "原点在这个摄影中心");
        table.Num("右片转角", "φ₂", r.PhiDeg, "+0.0000;-0.0000", "°");
        table.Num("右片转角", "ω₂", r.OmegaDeg, "+0.0000;-0.0000", "°");
        table.Num("右片转角", "κ₂", r.KappaDeg, "+0.0000;-0.0000", "°");
        table.Add("冻结的基线分量", "—", GaugeName(r.Gauge), "",
            r.Gauge == 0 ? "航向像对，与教材的 b_x = 1 一致" : "基线主要不在 x 方向");
        table.Num("q 中误差", "q_rms", r.QRmsMm, "0.0000", "mm");
        table.Num("最大 |q|", "q_max", r.QMaxAbsMm, "0.0000", "mm");
        table.Num("单位权中误差", "σ₀", r.Sigma0Mm, "0.0000", "mm");
        table.Add("参与 / 总数", "—",
            $"{r.UsedCount} / {run.Pairs.Count}", "对",
            r.RejectedCount > 0 ? $"剔除 {r.RejectedCount} 个" : "");
        table.Add("条件数", "cond", Fmt(r.Condition, "0.0E+0"), "", "越大越病态");
        table.Num("焦距", "f", run.Left?.Interior?.FocalLength ?? double.NaN, "0.00", "mm",
            "两片同机");

        table.Head("点号", "q (mm)", "q/σ₀", "状态", "说明");
        for (var k = 0; k < run.Pairs.Count; k++)
        {
            var used = k < r.Used.Count && r.Used[k];
            var q = k < r.ResidualQ.Count ? r.ResidualQ[k] : double.NaN;
            var ratio = r.Sigma0Mm > 0 ? Math.Abs(q) / r.Sigma0Mm : double.NaN;

            table.Row(!used, run.Pairs[k].TiePointId,
                FmtSigned(q, "0.0000"),
                Fmt(ratio, "0.00"),
                used ? "参与" : "已剔除",
                used ? "" : "粗差，没进解算");
        }

        return table;
    }
}

/// <summary>
/// 结果里的数值行。<see cref="SolveResult"/> 在 <c>Core</c>，不该知道界面怎么排版，
/// 也不该知道「NaN 写成 —」这种约定；数字写法属于界面（<see cref="Format"/>），所以这条
/// 便利方法留在这一层。
/// </summary>
internal static class SolveResultFormatting
{
    /// <summary>数值行。<paramref name="format"/> 用不变区域性，免得换个系统语言小数点变成逗号。</summary>
    public static SolveResult Num(this SolveResult table, string name, string symbol, double value,
        string format, string unit = "", string note = "", bool warn = false) =>
        table.Add(name, symbol, Format.Fixed(value, format), unit, note, warn);
}
