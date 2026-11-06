using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 「结果残差」页：各模块解出的元素与逐点残差都摆在这一页，<b>右栏不放结果</b>。
///
/// <para>
/// 为什么集中到一页：结果是拿来比、拿来贴进报告的，等宽文字块排版对不齐也选不出来；
/// 而且四个模块各写一套文字，判「哪个点偏大」的写法就会各不相同。这里统一成
/// <b>左元素表 + 右残差表</b>，列名随模块变，行高、字号、警示色只有一份。
/// </para>
/// <para>
/// 结果页是<b>快照</b>：它记的是「上一次按解算时算出的东西」，标题里带着是哪张片、
/// 哪个像对。改了观测（删点、补点）不会自动重算，要再按一次解算——这和作业员的预期一致，
/// 没有人希望删一个点之后结论悄悄变了。
/// </para>
/// <para>
/// 标题上<b>只有「任务 · 对象」</b>：成功与否、迭代几次、什么时刻，都进日志
/// （<see cref="Log"/>）。过程信息摆在结果页上，人会把它们当成结果的一部分去引用。
/// </para>
/// </summary>
public partial class MainWindow
{
    // ===================== 表格的行 =====================

    /// <summary>元素表的一行：解出来的是什么。</summary>
    internal sealed class ResultRow
    {
        public string Name { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;

        /// <summary>这一行的数不可信（诊断值、残差偏大）。界面上染成错误色。</summary>
        public bool Warn { get; init; }
    }

    /// <summary>
    /// 残差表的一行。列数随模块变（内定向两列、后方交会四列、相对定向三列），
    /// 所以用字符串数组而不是固定字段；列名与顺序由 <see cref="ResultTable.Headers"/> 定。
    /// </summary>
    internal sealed class ResidualRow
    {
        public string[] Cells { get; init; } = Array.Empty<string>();
        public bool Warn { get; init; }
    }

    /// <summary>一次解算要摆到界面上的全部内容。构造过程就是「把结果翻译成人能读的几行」。</summary>
    internal sealed class ResultTable
    {
        public string Title { get; init; } = "结果残差";

        /// <summary>整份结果不可信（没收敛、被判不采用）。标题染成错误色，并写「未解出」。</summary>
        public bool Warn { get; init; }

        public List<ResultRow> Items { get; } = new();
        public List<string> Headers { get; } = new();
        public List<ResidualRow> Rows { get; } = new();

        /// <summary>页脚：口径与警告（敢不敢用这份结果）。比塞进表格更容易读。调用方可以补写（例如把解算提醒拼进去）。</summary>
        public string Note { get; set; } = string.Empty;
        public bool NoteWarn { get; set; }

        public ResultTable Add(string name, string symbol, string value, string unit = "",
            string note = "", bool warn = false)
        {
            Items.Add(new ResultRow
            {
                Name = name, Symbol = symbol, Value = value, Unit = unit, Note = note, Warn = warn,
            });
            return this;
        }

        /// <summary>数值行。<paramref name="format"/> 用不变区域性，免得换个系统语言小数点变成逗号。</summary>
        public ResultTable Num(string name, string symbol, double value, string format,
            string unit = "", string note = "", bool warn = false) =>
            Add(name, symbol, Fmt(value, format), unit, note, warn);

        public ResultTable Head(params string[] headers)
        {
            Headers.AddRange(headers);
            return this;
        }

        public ResultTable Row(params string[] cells) => Row(false, cells);

        public ResultTable Row(bool warn, params string[] cells)
        {
            Rows.Add(new ResidualRow { Cells = cells, Warn = warn });
            return this;
        }
    }

    // ===================== 显示 =====================

    /// <summary>把一份结果摆到页上，并切到这一页。所有模块都从这儿进。</summary>
    private void ShowResult(ResultTable table)
    {
        ResTitleText.Text = table.Title;
        ResTitleText.Foreground = table.Warn ? ErrBadge : MutBadge;

        ResultGrid.ItemsSource = table.Items;
        BuildResidualColumns(table.Headers);
        ResidualGrid.ItemsSource = table.Rows;

        // 没有逐点残差（解算没通过）就把右表整块收起来，把宽度让给元素表
        var hasResiduals = table.Headers.Count > 0;
        ResidualGrid.Visibility = hasResiduals ? Visibility.Visible : Visibility.Collapsed;
        ResColumnSplitter.Visibility = hasResiduals ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(ResultGrid, hasResiduals ? 1 : 3);

        ResNoteText.Text = table.Note;
        ResNoteText.Foreground = table.NoteWarn ? WarnBadge : DimBadge;
        ResNoteText.Visibility = string.IsNullOrEmpty(table.Note)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ResEmptyText.Visibility = Visibility.Collapsed;

        // 198 px 只够看三行，等于没给人看：第一次出结果时把面板撑开
        EnsureResultPanelHeight();

        // 解完直接把这一页翻出来。结果在折叠线以下、要用户自己去找，等于没显示
        ResTab.IsChecked = true;
    }

    /// <summary>用户自己拖过分隔条之后，就别再替他改高度。</summary>
    private bool _bottomPanelDragged;

    private void BottomSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _bottomPanelDragged = true;

    /// <summary>结果页出内容时，把底部面板撑到至少 240 px。</summary>
    private void EnsureResultPanelHeight()
    {
        if (_bottomPanelDragged || BottomPanelRow is null) return;

        var height = BottomPanelRow.Height;
        if (!height.IsAbsolute || height.Value < 240)
            BottomPanelRow.Height = new GridLength(240);
    }

    /// <summary>退回「尚未解算」。换工程时用：上一份结果对着旧数据，留着必然被误读。</summary>
    private void ClearResult()
    {
        if (ResultGrid is null) return;

        ResultGrid.ItemsSource = null;
        ResidualGrid.ItemsSource = null;
        ResidualGrid.Columns.Clear();
        ResidualGrid.Visibility = Visibility.Collapsed;
        ResColumnSplitter.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(ResultGrid, 3);

        ResTitleText.Text = "结果残差 · 尚未解算";
        ResTitleText.Foreground = MutBadge;
        ResNoteText.Text = string.Empty;
        ResNoteText.Visibility = Visibility.Collapsed;
        ResEmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 残差表的列按 <paramref name="headers"/> 现生成。第一列定宽（点号，宽度跳来跳去最难读），
    /// 中间按内容自适应，最后那一列「说明」吃掉剩下的宽度。
    /// </summary>
    private void BuildResidualColumns(IReadOnlyList<string> headers)
    {
        ResidualGrid.Columns.Clear();

        for (var i = 0; i < headers.Count; i++)
        {
            var column = new DataGridTextColumn
            {
                Header = headers[i],
                Binding = new Binding($"Cells[{i}]") { Mode = BindingMode.OneWay },
                MinWidth = i == 0 ? 76 : 76,
            };

            column.Width = i == 0
                ? new DataGridLength(96)
                : i == headers.Count - 1 && headers.Count > 2
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : new DataGridLength(1, DataGridLengthUnitType.Auto);

            ResidualGrid.Columns.Add(column);
        }
    }

    // ===================== 供离屏渲染工具断言 =====================

    /// <summary>当前结果页上有什么。给 Snap 用——它验的是"界面真的显示了这几行"，不是"算对了"。</summary>
    internal sealed record ResultSnapshot(
        string Title,
        IReadOnlyList<ResultRow> Items,
        IReadOnlyList<string> Headers,
        IReadOnlyList<ResidualRow> Rows,
        string Note);

    internal ResultSnapshot SnapshotResult() => new(
        ResTitleText.Text,
        ResultGrid.ItemsSource is IEnumerable<ResultRow> items ? items.ToList() : Array.Empty<ResultRow>(),
        ResidualGrid.Columns.Select(c => c.Header?.ToString() ?? string.Empty).ToList(),
        ResidualGrid.ItemsSource is IEnumerable<ResidualRow> rows ? rows.ToList() : Array.Empty<ResidualRow>(),
        ResNoteText.Text);

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

    /// <summary>解算没通过时的那一页：一行「未解出」，原因写在页脚。</summary>
    private static ResultTable FailTable(string title, string message, bool notAdopted = false)
    {
        var table = new ResultTable
        {
            // 失败必须写在标题上：结果页是快照，翻回来的人先看到的就是标题这一行
            Title = title + (notAdopted ? " · 未采用" : " · 未解出"),
            Warn = true,
            Note = message,
            NoteWarn = true,
        };

        table.Add(notAdopted ? "未采用" : "未解出", "✗", "—");
        return table;
    }

    // ---------- 内定向 ----------

    private static ResultTable InteriorResult(ImageItem image, FiducialOrientationResult r)
    {
        var table = new ResultTable
        {
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
        table.Num("焦距", "f", image.Interior?.FocalLength ?? double.NaN, "0.00", "mm",
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

    // ---------- 后方交会 ----------

    private static ResultTable ResectionResult(ImageItem image, ResectionResult r)
    {
        var table = new ResultTable
        {
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
    private static ResultTable ResectionDiagnostic(ImageItem? image, ResectionResult r)
    {
        var table = FailTable($"后方交会 · {image?.Id ?? "—"}",
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

    private static ResultTable IntersectionResult(IntersectionRun run)
    {
        var r = run.Result!;
        var p = r.Point;

        var table = new ResultTable
        {
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

    // ---------- 绝对定向 ----------

    private ResultTable AbsoluteResultTable(AbsoluteOrientationRun run)
    {
        var r = run.Result!;
        var table = new ResultTable { Title = $"绝对定向 · {run.PairLabel}" };

        table.Num("尺度", "λ", r.Scale, "0.000000", "", "模型单位 → 米");
        table.Num("旋转", "φ", r.PhiDeg, "+0.0000;-0.0000", "°", "模型系 → 地面系");
        table.Num("旋转", "ω", r.OmegaDeg, "+0.0000;-0.0000", "°");
        table.Num("旋转", "κ", r.KappaDeg, "+0.0000;-0.0000", "°");
        table.Num("平移", "X₀", r.X0, "0.000", "m");
        table.Num("平移", "Y₀", r.Y0, "0.000", "m");
        table.Num("平移", "Z₀", r.Z0, "0.000", "m");

        var limit = 2.0 * r.Sigma0;
        table.Num("控制点不符值", "rms", r.Rms, "0.0000", "m",
            $"验收 2σ₀ = {Fmt(limit, "0.0000")} m", r.Rms > limit);
        table.Num("单位权中误差", "σ₀", r.Sigma0, "0.0000", "m");
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
            table.Add("数据镜像", "!", "疑似左右手系搞反", "", "检查地面坐标 X/Y 口径", true);
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

        table.Note = "结果不落盘：.pht 的 models[] 仍为空，重开工程要再解一次。";
        return table;
    }

    // ---------- 相对定向 ----------

    private ResultTable RelativeResultTable(RelativeRun run)
    {
        var r = run.Result!;
        var pair = run.Left is null || run.Right is null
            ? null
            : _project.FindPair(run.Left.Id, run.Right.Id);

        var table = new ResultTable
        {
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
