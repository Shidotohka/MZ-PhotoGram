using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App.Report;

/// <summary>哪一条作业线的报告。两条线的成果不在同一个坐标系里，所以不合成一份。</summary>
internal enum ReportLine
{
    /// <summary>后方交会–前方交会：控制点定姿，直接出地面坐标。</summary>
    BackIntersection,

    /// <summary>相对定向–绝对定向：同名点定形，模型坐标再由七参数拉成米制。</summary>
    RelativeAbsolute,
}

/// <summary>前方交会算出来的一个地面点。</summary>
/// <remarks>
/// 前方交会<b>不写工程</b>（《工程文件格式》§9.6：它出的是这一次点的这一个点，
/// 既不属于哪个对象，也不是下一步的输入）。所以报告里的这一段只能来自会话——
/// 重开工程后它会是空的，报告照样生成，只是那一段写「未计算」。
/// </remarks>
internal sealed record ReportPoint(string Pair, IntersectionResult Ground);

/// <summary>
/// 把一条作业线的成果排成 HTML。**只管数据 → 表格**：不弹窗、不写盘、不开浏览器，
/// 那三件事在 <c>MainWindow.Report.cs</c> 里。
///
/// <para><b>为什么报告不直接抄结果窗口的表格。</b>窗口是"逐份细看"：一份结果一张
/// 键值表，带说明列与逐点残差。报告是"汇总成表"：一行一个对象，横向摊开。
/// 两者版式不同是应该的，但<b>数只能有一个来源</b>——都来自工程里的结果块
/// （经 <see cref="StoredResults"/> 还原成任务结果对象），不是各自再算一遍，
/// 也不是从窗口的字符串里反解。判据同理，两边都走 <see cref="Judgement"/>：
/// 同一份结果不该在窗口里是绿的、在报告里染红。</para>
///
/// <para><b>只写已入账的成果</b>：内定向、后方交会、相对定向、模型交会、绝对定向都取工程里的块
/// （没按「接受」的不在里面，那说明它还不是成果）。唯一的例外是前方交会，
/// 见 <see cref="ReportPoint"/>。</para>
///
/// <para><b>两条线共用一套骨架</b>：模板、节、单元格写法、标红全一样，差别只在排哪几张表——
/// 所以这里只有 <c>Build</c> 一个出口，按 <see cref="ReportLine"/> 选表。</para>
/// </summary>
internal static class ReportBuilder
{
    /// <summary>模板相对程序目录的位置。改版式只改这个文件，不用重新编译。</summary>
    private const string TemplateRelativePath = @"Report\ReportTemplate.html";

    /// <summary>逐点残差最多列多少行。再多的点表没人看，报告也不该有几百页。</summary>
    private const int MaxResidualRows = 300;

    private const string SectionTag = "<template id=\"tpl-section\">";

    /// <summary>表头一格。<paramref name="Numeric"/> 决定右对齐还是左对齐（文字列靠左）。</summary>
    private readonly record struct HeadCell(string Text, bool Numeric);

    public static string TemplatePath => Path.Combine(AppContext.BaseDirectory, TemplateRelativePath);

    public static string MethodName(ReportLine line) => line switch
    {
        ReportLine.BackIntersection => "后方交会–前方交会法",
        _ => "相对定向–绝对定向法",
    };

    /// <summary>文件名里那一段：`实验_相对绝对_相对绝对报告.html`。</summary>
    public static string FileTag(ReportLine line) =>
        line == ReportLine.BackIntersection ? "后交前交报告" : "相对绝对报告";

    /// <summary>
    /// 生成整份报告的 HTML。模板缺失或被改坏时抛异常，由调用方翻成人话。
    /// </summary>
    /// <param name="points">前方交会点（只有后交–前交那条线用；另一条传空表）。</param>
    public static string Build(Project project, ReportLine line, IReadOnlyList<ReportPoint> points,
        DateTime generatedAt, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(points);

        var page = File.ReadAllText(TemplatePath, Encoding.UTF8);

        // 模板开头的注释是写给"改模板的人"看的，里面逐个列着占位符的名字。
        // 不删掉的话，那段注释会被当正文填一遍——正文就整节重复一份（踩过）。
        page = Regex.Replace(page, "<!--[\\s\\S]*?-->", string.Empty);

        var sections = new SectionList(TakeSectionTemplate(ref page));

        // 两条线的第一步与它的残差表相同：内定向是共有的，且都是后面几步的输入
        AddInterior(sections, project);
        AddFiducialResiduals(sections, project);

        if (line == ReportLine.BackIntersection)
        {
            AddResection(sections, project);
            AddResectionResiduals(sections, project);
            AddIntersection(sections, points);
        }
        else
        {
            AddRelative(sections, project);
            AddRelativeResiduals(sections, project);
            AddModel(sections, project);
            AddAbsolute(sections, project);
            AddAbsoluteQuality(sections, project);
            AddAbsoluteResiduals(sections, project);
        }

        return page
            .Replace("{{DOC_TITLE}}", Esc($"{ProjectName(project)} · {FileTag(line)}"))
            .Replace("{{PROJECT}}", Esc(ProjectName(project)))
            .Replace("{{METHOD}}", MethodName(line))
            .Replace("{{META}}", Esc(Meta(projectPath, generatedAt)))
            .Replace("{{SUMMARY}}", Summary(project, line, points.Count))
            .Replace("{{SECTIONS}}", string.Join("\n", sections.Html))
            .Replace("{{LEGEND}}", Legend(line));
    }

    /// <summary>
    /// 这条线上有没有够格写成报告的东西。判据是**这条线自己的第一步算完没有**：
    /// 只有内定向不算成果（两条线都有内定向，可它俩的成果都还没有）。
    /// </summary>
    public static bool HasAnything(Project project, ReportLine line)
    {
        ArgumentNullException.ThrowIfNull(project);

        return line == ReportLine.BackIntersection
            ? project.Images.Any(i => i.Exterior is not null)
            : project.Pairs.Any(p => p.Relative is not null || p.Model is not null || p.Absolute is not null);
    }

    /// <summary>这条线缺的那一步，说给用户听。</summary>
    public static string MissingHint(ReportLine line) => line == ReportLine.BackIntersection
        ? "还没有可写进报告的外方位：做完内定向与后方交会，再按「接受」入账"
        : "还没有可写进报告的相对定向成果：做完相对定向（以及后面的模型交会/绝对定向），按「接受」入账";

    // ===================== 两线共有的节 =====================

    /// <summary>内定向：每片一行。参数式也列（它的 f/x₀/y₀ 是后面几步真正的输入），只是没有 σ₀。</summary>
    private static void AddInterior(SectionList sections, Project project)
    {
        var rows = new StringBuilder();
        var hasParametric = false;

        foreach (var image in project.Images)
        {
            if (image.Interior is not { } interior) continue;

            // 框标仿射有六系数与评价；参数式只有四个内参，评价一律「—」
            var fiducial = StoredResults.Interior(image);
            hasParametric |= fiducial is null;

            rows.Append("<tr>")
                .Append(Text(image.Id))
                .Append(Num(interior.FocalLength, "0.00"))
                .Append(Sgn(interior.X0, "0.0000"))
                .Append(Sgn(interior.Y0, "0.0000"))
                .Append(Num(interior.PixelSizeMm, "0.000000"))
                .Append(Num(fiducial?.Sigma0Mm ?? double.NaN, "0.0000",
                    fiducial is not null && Judgement.InteriorSigma0(fiducial.Sigma0Mm)))
                .Append(Num(fiducial?.MaxAbsResidualMm ?? double.NaN, "0.0000"))
                .Append(Text(fiducial is null ? "—" : fiducial.Redundancy.ToString(CultureInfo.InvariantCulture)))
                .AppendLine("</tr>");
        }

        if (rows.Length == 0) return;

        sections.Add("内定向", new[]
        {
            Head("影像"), HeadN("f (mm)"), HeadN("x₀ (mm)"), HeadN("y₀ (mm)"), HeadN("δ (mm)"),
            HeadN("σ₀ (mm)"), HeadN("max|v| (mm)"), HeadN("r"),
        }, rows, hasParametric
            ? "参数式内定向直接采用给定内参，没有多余观测，所以 σ₀、最大残差与多余观测都是「—」。"
            : "");
    }

    /// <summary>框标残差：一行一个框标。没有框标残差（参数式）就整节不出现。</summary>
    private static void AddFiducialResiduals(SectionList sections, Project project)
    {
        var rows = new StringBuilder();

        foreach (var image in project.Images)
        {
            if (StoredResults.Interior(image) is not { } fiducial) continue;

            foreach (var v in fiducial.Residuals)
            {
                var big = Judgement.FiducialResidual(v.VxMm, v.VyMm, fiducial.Sigma0Mm);
                rows.Append("<tr>")
                    .Append(Text(image.Id))
                    .Append(Text(v.Id.ToString(CultureInfo.InvariantCulture)))
                    .Append(Sgn(v.VxMm, "0.0000", big))
                    .Append(Sgn(v.VyMm, "0.0000", big))
                    .AppendLine("</tr>");
            }
        }

        if (rows.Length == 0) return;

        sections.Add("框标残差",
            new[] { Head("影像"), Head("框标"), HeadN("vx (mm)"), HeadN("vy (mm)") }, rows, "");
    }

    // ===================== 后交–前交 =====================

    /// <summary>后方交会：每片一行，六外方位 + σ₀ + 多余观测。</summary>
    private static void AddResection(SectionList sections, Project project)
    {
        var rows = new StringBuilder();

        foreach (var image in project.Images)
        {
            if (StoredResults.Resection(image) is not { Result: { } r }) continue;

            rows.Append("<tr>")
                .Append(Text(image.Id))
                .Append(Num(r.Xs, "0.000"))
                .Append(Num(r.Ys, "0.000"))
                .Append(Num(r.Zs, "0.000"))
                .Append(Sgn(r.PhiDeg, "0.0000"))
                .Append(Sgn(r.OmegaDeg, "0.0000"))
                .Append(Sgn(r.KappaDeg, "0.0000"))
                .Append(Num(r.Sigma0Mm, "0.0000"))
                .Append(Text(r.Redundancy.ToString(CultureInfo.InvariantCulture)))
                .AppendLine("</tr>");
        }

        if (rows.Length == 0) return;

        sections.Add("后方交会", new[]
        {
            Head("影像"), HeadN("Xₛ (m)"), HeadN("Yₛ (m)"), HeadN("Zₛ (m)"),
            HeadN("φ (°)"), HeadN("ω (°)"), HeadN("κ (°)"), HeadN("σ₀ (mm)"), HeadN("r"),
        }, rows, "");
    }

    /// <summary>后方交会残差：一行一片一点。这是"外方位可不可信"的证据。</summary>
    private static void AddResectionResiduals(SectionList sections, Project project)
    {
        var rows = new StringBuilder();

        foreach (var image in project.Images)
        {
            if (StoredResults.Resection(image) is not { Result: { } r }) continue;

            foreach (var v in r.Residuals)
            {
                var norm = Math.Sqrt(v.VxMm * v.VxMm + v.VyMm * v.VyMm);
                var ratio = r.Sigma0Mm > 0 ? norm / r.Sigma0Mm : double.NaN;
                var big = Judgement.ResectionResidual(ratio);

                rows.Append("<tr>")
                    .Append(Text(image.Id))
                    .Append(Text(v.GcpId))
                    .Append(Sgn(v.VxMm, "0.0000", big))
                    .Append(Sgn(v.VyMm, "0.0000", big))
                    .Append(Num(norm, "0.0000", big))
                    .Append(Num(ratio, "0.00", big))
                    .AppendLine("</tr>");
            }
        }

        if (rows.Length == 0) return;

        sections.Add("控制点残差", new[]
        {
            Head("影像"), Head("点号"), HeadN("Vx (mm)"), HeadN("Vy (mm)"),
            HeadN("|V| (mm)"), HeadN("|V|/σ₀"),
        }, rows, "");
    }

    /// <summary>前方交会：一行一点。来自会话，重开工程后为空。</summary>
    private static void AddIntersection(SectionList sections, IReadOnlyList<ReportPoint> points)
    {
        HeadCell[] head =
        {
            Head("像对"), HeadN("X (m)"), HeadN("Y (m)"), HeadN("Z (m)"), HeadN("γ (°)"),
            HeadN("m_X (m)"), HeadN("m_Y (m)"), HeadN("m_Z (m)"),
        };

        var rows = new StringBuilder();
        foreach (var point in points.Take(MaxResidualRows))
        {
            var r = point.Ground;
            var small = Judgement.SmallConvergenceAngle(r.ConvergenceAngleDeg);

            rows.Append("<tr>")
                .Append(Text(point.Pair))
                .Append(Num(r.Point.X, "0.000"))
                .Append(Num(r.Point.Y, "0.000"))
                .Append(Num(r.Point.Z, "0.000"))
                .Append(Num(r.ConvergenceAngleDeg, "0.00", small))
                .Append(Num(r.MX, "0.000"))
                .Append(Num(r.MY, "0.000"))
                .Append(Num(r.MZ, "0.000"))
                .AppendLine("</tr>");
        }

        string note;
        if (points.Count == 0)
        {
            // 空表比"整节不见"好：读者知道这一步没做，而不是以为漏印了
            rows.Append("<tr><td class=\"e\" colspan=\"").Append(head.Length)
                .Append("\">未计算</td></tr>");
            note = "前方交会出的是单次临时点，不写进工程；重开工程后这一段需要重新交会。";
        }
        else if (points.Count > MaxResidualRows)
        {
            note = $"共 {points.Count} 个点，此处列前 {MaxResidualRows} 个（按交会顺序）。";
        }
        else
        {
            note = "";
        }

        sections.Add("前方交会", head, rows, note);
    }

    // ===================== 相对–绝对 =====================

    /// <summary>相对定向：每对一行，五个元素 + q 中误差 + 参与数。</summary>
    private static void AddRelative(SectionList sections, Project project)
    {
        var rows = new StringBuilder();

        foreach (var pair in project.Pairs)
        {
            if (StoredResults.Relative(project, pair) is not { Result: { } r } run) continue;

            rows.Append("<tr>")
                .Append(Text(PairLabel(pair)))
                .Append(Sgn(r.Bx, "0.0000000"))
                .Append(Sgn(r.By, "0.0000000"))
                .Append(Sgn(r.Bz, "0.0000000"))
                .Append(Sgn(r.PhiDeg, "0.0000"))
                .Append(Sgn(r.OmegaDeg, "0.0000"))
                .Append(Sgn(r.KappaDeg, "0.0000"))
                .Append(Num(r.QRmsMm, "0.0000"))
                .Append(Num(r.Sigma0Mm, "0.0000"))
                .Append(Text($"{r.UsedCount} / {run.Pairs.Count}" +
                             (r.RejectedCount > 0
                                 ? $"（剔除 {r.RejectedCount}）"
                                 : "")))
                .AppendLine("</tr>");
        }

        if (rows.Length == 0) return;

        sections.Add("相对定向", new[]
        {
            Head("像对"), HeadN("b_x"), HeadN("b_y"), HeadN("b_z"),
            HeadN("φ₂ (°)"), HeadN("ω₂ (°)"), HeadN("κ₂ (°)"),
            HeadN("q_rms (mm)"), HeadN("σ₀ (mm)"), Head("参与 / 总数"),
        }, rows, "基线按单位长给出（|b| = 1）：相对定向只定方向，尺度由绝对定向的 λ 补回来。");
    }

    /// <summary>相对定向残差：一行一对点。q 是上下视差残差，剔除的点整行标红。</summary>
    private static void AddRelativeResiduals(SectionList sections, Project project)
    {
        var rows = new StringBuilder();
        var shown = 0;
        var total = 0;

        foreach (var pair in project.Pairs)
        {
            if (StoredResults.Relative(project, pair) is not { Result: { } r } run) continue;

            for (var k = 0; k < run.Pairs.Count; k++)
            {
                total++;
                if (shown >= MaxResidualRows) continue;

                var used = k < r.Used.Count && r.Used[k];
                var q = k < r.ResidualQ.Count ? r.ResidualQ[k] : double.NaN;
                var ratio = r.Sigma0Mm > 0 ? Math.Abs(q) / r.Sigma0Mm : double.NaN;
                shown++;

                rows.Append("<tr>")
                    .Append(Text(PairLabel(pair)))
                    .Append(Text(run.Pairs[k].TiePointId))
                    .Append(Sgn(q, "0.0000", !used))
                    .Append(Num(ratio, "0.00", !used))
                    .Append(Warn(used ? "参与" : "已剔除", !used))
                    .AppendLine("</tr>");
            }
        }

        if (rows.Length == 0) return;

        sections.Add("相对定向残差", new[]
        {
            Head("像对"), Head("点号"), HeadN("q (mm)"), HeadN("q/σ₀"), Head("状态"),
        }, rows, total > shown
            ? $"共 {total} 对同名点，此处列前 {shown} 对（按解算顺序）。"
            : "");
    }

    /// <summary>模型交会：一行一点。模型坐标是自由比例尺，表头就不能写 (m)。</summary>
    private static void AddModel(SectionList sections, Project project)
    {
        var rows = new StringBuilder();

        foreach (var pair in project.Pairs)
        {
            if (StoredResults.Model(project, pair) is not { } outcome) continue;

            foreach (var o in outcome.Observations)
            {
                var small = Judgement.SmallConvergenceAngle(o.ConvergenceAngleDeg);

                rows.Append("<tr>")
                    .Append(Text(PairLabel(pair)))
                    .Append(Text(o.GcpId))
                    .Append(Num(o.Model.X, "0.0000"))
                    .Append(Num(o.Model.Y, "0.0000"))
                    .Append(Num(o.Model.Z, "0.0000"))
                    .Append(Num(o.ConvergenceAngleDeg, "0.00", small))
                    .AppendLine("</tr>");
            }
        }

        if (rows.Length == 0) return;

        sections.Add("模型交会", new[]
        {
            Head("像对"), Head("点号"), HeadN("模型 X"), HeadN("模型 Y"), HeadN("模型 Z"),
            HeadN("γ (°)"),
        }, rows,
            "模型坐标是自由比例尺（无量纲），形状对、尺寸不对；米制要靠下一步的七参数 λ 拉回来。");
    }

    /// <summary>绝对定向七参数：每对一行。</summary>
    private static void AddAbsolute(SectionList sections, Project project)
    {
        var rows = new StringBuilder();

        foreach (var pair in project.Pairs)
        {
            if (StoredResults.Absolute(project, pair) is not { Result: { } r }) continue;

            rows.Append("<tr>")
                .Append(Text(PairLabel(pair)))
                .Append(Num(r.Scale, "0.000000"))
                .Append(Sgn(r.PhiDeg, "0.0000"))
                .Append(Sgn(r.OmegaDeg, "0.0000"))
                .Append(Sgn(r.KappaDeg, "0.0000"))
                .Append(Num(r.X0, "0.000"))
                .Append(Num(r.Y0, "0.000"))
                .Append(Num(r.Z0, "0.000"))
                .AppendLine("</tr>");
        }

        if (rows.Length == 0) return;

        sections.Add("绝对定向", new[]
        {
            Head("像对"), HeadN("λ"), HeadN("φ (°)"), HeadN("ω (°)"), HeadN("κ (°)"),
            HeadN("X₀ (m)"), HeadN("Y₀ (m)"), HeadN("Z₀ (m)"),
        }, rows, "");
    }

    /// <summary>
    /// 绝对定向的精度：每对一行。平面与高程分开报——交会角小的时候高程被 $H/B$ 放大，
    /// 两者能差一个量级，只报三维合量会让"整片不准"和"只有高程不准"看起来一样。
    /// </summary>
    private static void AddAbsoluteQuality(SectionList sections, Project project)
    {
        var rows = new StringBuilder();
        var mirror = false;

        foreach (var pair in project.Pairs)
        {
            if (StoredResults.Absolute(project, pair) is not { } run || run.Result is not { } r) continue;

            var gsd = run.GroundSampleDistance;
            var sigmaPx = double.IsNaN(gsd) || gsd <= 0 ? double.NaN : r.Sigma0 / gsd;
            var flatness = r.Singular.Count >= 3 && r.Singular[0] > 0
                ? r.Singular[2] / r.Singular[0]
                : double.NaN;

            mirror |= r.MirrorSuspected;

            rows.Append("<tr>")
                .Append(Text(PairLabel(pair)))
                .Append(Num(r.Sigma0, "0.0000", Judgement.AbsoluteSigma0InPixels(sigmaPx)))
                .Append(Num(sigmaPx, "0.00", Judgement.AbsoluteSigma0InPixels(sigmaPx)))
                .Append(Num(run.RmsXy, "0.0000"))
                .Append(Num(run.RmsZ, "0.0000"))
                .Append(Num(run.Checks.Count == 0 ? double.NaN : run.CheckPlanimetricMax, "0.000"))
                .Append(Num(run.Checks.Count == 0 ? double.NaN : run.CheckHeightMax, "0.000"))
                .Append(Text($"{run.Used.Count} / {run.Checks.Count}"))
                .Append(Num(flatness, "0.0E+0", Judgement.FlatModel(flatness)))
                .AppendLine("</tr>");
        }

        if (rows.Length == 0) return;

        var note = "Δxy/Δz 是参与解算的控制点的不符值 RMS；检查点那两列不参与解算，只评精度。";
        if (mirror)
        {
            note += "　<span class=\"mark\">数据镜像：</span>疑似左右手系搞反——轴序已核过，查点号配对与粗差。";
        }

        sections.Add("绝对定向精度", new[]
        {
            Head("像对"), HeadN("σ₀ (m)"), HeadN("σ₀ (px)"), HeadN("Δxy rms (m)"), HeadN("Δz rms (m)"),
            HeadN("检查点 Δxy"), HeadN("检查点 Δz"), Head("参与 / 检查"), HeadN("σ₃/σ₁"),
        }, rows, note);
    }

    /// <summary>绝对定向不符值：一行一点。检查点的行不标红——它本来就不参与解算。</summary>
    private static void AddAbsoluteResiduals(SectionList sections, Project project)
    {
        var rows = new StringBuilder();
        var shown = 0;
        var total = 0;

        foreach (var pair in project.Pairs)
        {
            if (pair.Absolute is not { } block) continue;
            if (StoredResults.Absolute(project, pair) is not { Result: { } r }) continue;

            foreach (var point in block.Points)
            {
                total++;
                if (shown >= MaxResidualRows) continue;
                shown++;

                var length = Math.Sqrt(point.Dxy * point.Dxy + point.Dz * point.Dz);
                var ratio = r.Sigma0 > 0 ? length / r.Sigma0 : double.NaN;
                var big = !point.Check && Judgement.AbsoluteResidual(ratio);

                rows.Append("<tr>")
                    .Append(Text(PairLabel(pair)))
                    .Append(Text(point.GcpId))
                    .Append(Num(point.Dxy, "0.0000", big))
                    .Append(Sgn(point.Dz, "0.0000", big))
                    .Append(Num(ratio, "0.00", big))
                    .Append(Text(point.Check ? "检查点" : "参与"))
                    .AppendLine("</tr>");
            }
        }

        if (rows.Length == 0) return;

        sections.Add("绝对定向不符值", new[]
        {
            Head("像对"), Head("点号"), HeadN("Δxy (m)"), HeadN("Δz (m)"), HeadN("v/σ₀"), Head("状态"),
        }, rows, total > shown
            ? $"共 {total} 个点，此处列前 {shown} 个（按解算顺序）。"
            : "");
    }

    // ===================== 摘要与页脚 =====================

    private static string Summary(Project project, ReportLine line, int intersectionCount)
    {
        var control = project.Gcps.Count(g => g.Type != GcpType.CheckPoint);
        var check = project.Gcps.Count(g => g.Type == GcpType.CheckPoint);

        var items = new List<string>
        {
            Chip("影像", $"{project.Images.Count} 张"),
            Chip("像点", $"{project.Images.Sum(i => i.Points.Count)} 个"),
            Chip("控制点", check > 0 ? $"{control} 个（检查 {check}）" : $"{control} 个"),
            Chip("像对", $"{project.Pairs.Count} 个"),
        };

        // 两条线各有一样"这一步特有的东西"，摘要里提一句比通用五个格子有用
        items.Add(line == ReportLine.BackIntersection
            ? Chip("前方交会", $"{intersectionCount} 点")
            : Chip("同名点", $"{project.TiePoints.Count} 对"));

        return string.Join("\n    ", items);
    }

    private static string Chip(string name, string value) => $"<li><b>{Esc(name)}</b>{Esc(value)}</li>";

    private static string Meta(string? projectPath, DateTime generatedAt) =>
        (projectPath is null ? "工程未保存" : Path.GetFileName(projectPath)) +
        $"　·　生成于 {generatedAt:yyyy-MM-dd HH:mm}";

    /// <summary>标红说明。阈值从 <see cref="Judgement"/> 取，不在这里另抄一份。</summary>
    private static string Legend(ReportLine line)
    {
        static string V(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        var tail = line == ReportLine.BackIntersection
            ? $"内定向 σ₀ &gt; {V(Judgement.InteriorSigma0Mm)} mm　·　" +
              $"框标残差 &gt; {V(Judgement.FiducialResidualSigmas)}σ₀　·　" +
              $"控制点残差 &gt; {V(Judgement.ResectionResidualSigmas)}σ₀　·　" +
              $"交会角 &lt; {V(Judgement.SmallConvergenceAngleDeg)}°"
            : $"内定向 σ₀ &gt; {V(Judgement.InteriorSigma0Mm)} mm　·　" +
              $"框标残差 &gt; {V(Judgement.FiducialResidualSigmas)}σ₀　·　" +
              $"被剔除的同名点　·　交会角 &lt; {V(Judgement.SmallConvergenceAngleDeg)}°　·　" +
              $"σ₀ &gt; {V(Judgement.AbsoluteSigma0Px)} px　·　" +
              $"不符值 &gt; {V(Judgement.AbsoluteResidualSigmas)}σ₀　·　" +
              $"σ₃/σ₁ &lt; {V(Judgement.FlatModelRatio)}";

        return "<span class=\"mark\">标红</span>＝超出判据：" + tail;
    }

    private static string ProjectName(Project project) =>
        string.IsNullOrWhiteSpace(project.Name) ? "未命名工程" : project.Name;

    private static string PairLabel(StereoPair pair) => $"{pair.LeftImageId} / {pair.RightImageId}";

    // ===================== 模板与标签 =====================

    /// <summary>
    /// 把 <c>&lt;template id="tpl-section"&gt;</c> 那一块取出来并从页面里删掉：
    /// 骨架只该出现在生成出来的正文里，不该留在成品文件的末尾。
    /// </summary>
    private static string TakeSectionTemplate(ref string page)
    {
        var match = Regex.Match(page, Regex.Escape(SectionTag) + @"(?<body>[\s\S]*?)</template>");
        if (!match.Success)
            throw new InvalidDataException(
                $"报告模板里找不到 {SectionTag} 段落：{TemplatePath}（模板被改坏了？）");

        var body = match.Groups["body"].Value;
        page = page.Remove(match.Index, match.Length);
        return body;
    }

    /// <summary>各节的收集器：管编号，也管把表格骨架填成一段 HTML。</summary>
    private sealed class SectionList
    {
        private readonly string _template;
        private int _no;

        public SectionList(string template) => _template = template;

        public List<string> Html { get; } = new();

        public void Add(string title, HeadCell[] head, StringBuilder rows, string note = "")
        {
            var headHtml = new StringBuilder(head.Length * 32);
            foreach (var cell in head) headHtml.Append(Cell(cell));

            Html.Add(_template
                .Replace("{{NO}}", (++_no).ToString("00", CultureInfo.InvariantCulture))
                .Replace("{{TITLE}}", Esc(title))
                .Replace("{{HEAD}}", headHtml.ToString())
                .Replace("{{ROWS}}", rows.ToString())
                .Replace("{{NOTE}}", note));
        }
    }

    // ===================== 单元格 =====================

    private static HeadCell Head(string text) => new(text, false);

    private static HeadCell HeadN(string text) => new(text, true);

    private static string Cell(HeadCell head) =>
        $"<th class=\"{(head.Numeric ? "n" : "t")}\">{Esc(head.Text)}</th>";

    private static string Text(string value) => $"<td class=\"t\">{Esc(value)}</td>";

    /// <summary>文字格，但可以标红（「已剔除」那种）。</summary>
    private static string Warn(string value, bool warn) =>
        $"<td class=\"t{(warn ? " w" : "")}\">{Esc(value)}</td>";

    private static string Num(double value, string format, bool warn = false) =>
        $"<td class=\"n{(warn ? " w" : "")}\">{Format.Fixed(value, format)}</td>";

    private static string Sgn(double value, string format, bool warn = false) =>
        $"<td class=\"n{(warn ? " w" : "")}\">{Format.Signed(value, format)}</td>";

    /// <summary>
    /// 转义。值都是数字与点号，理论上不会带 <c>&lt;&amp;</c>；但报告里也有影像名与
    /// 工程名（用户起的名字），拼进 HTML 前一律转义，免得一个 <c>&lt;</c> 把整页吃歪。
    /// </summary>
    private static string Esc(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
