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
/// <para><b>只写已入账的成果</b>：内定向与后方交会取工程里的块（没按「接受」的不在里面，
/// 那说明它还不是成果）。唯一的例外是前方交会，见 <see cref="ReportPoint"/>。</para>
/// </summary>
internal static class ReportBuilder
{
    /// <summary>模板相对程序目录的位置。改版式只改这个文件，不用重新编译。</summary>
    private const string TemplateRelativePath = @"Report\ReportTemplate.html";

    /// <summary>前方交会最多列多少个点。再多的点表没人看，报告也不该有几百页。</summary>
    private const int MaxIntersectionRows = 500;

    private const string SectionTag = "<template id=\"tpl-section\">";

    /// <summary>表头一格。<paramref name="Numeric"/> 决定右对齐还是左对齐（文字列靠左）。</summary>
    private readonly record struct HeadCell(string Text, bool Numeric);

    public static string TemplatePath => Path.Combine(AppContext.BaseDirectory, TemplateRelativePath);

    /// <summary>
    /// 生成整份报告的 HTML。模板缺失或被改坏时抛异常，由调用方翻成人话。
    /// </summary>
    public static string Build(Project project, IReadOnlyList<ReportPoint> points,
        DateTime generatedAt, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(points);

        var page = File.ReadAllText(TemplatePath, Encoding.UTF8);

        // 模板开头的注释是写给"改模板的人"看的，里面逐个列着占位符的名字。
        // 不删掉的话，那段注释会被当正文填一遍——正文就整节重复一份（踩过）。
        page = Regex.Replace(page, "<!--[\\s\\S]*?-->", string.Empty);

        var sectionTemplate = TakeSectionTemplate(ref page);

        var sections = new List<string>();
        var no = 0;

        AddInterior(sections, ref no, sectionTemplate, project);
        AddFiducialResiduals(sections, ref no, sectionTemplate, project);
        AddResection(sections, ref no, sectionTemplate, project);
        AddResectionResiduals(sections, ref no, sectionTemplate, project);
        AddIntersection(sections, ref no, sectionTemplate, points);

        return page
            .Replace("{{DOC_TITLE}}", Esc($"{ProjectName(project)} · 后交–前交 成果报告"))
            .Replace("{{PROJECT}}", Esc(ProjectName(project)))
            .Replace("{{METHOD}}", "后方交会–前方交会法")
            .Replace("{{META}}", Esc(Meta(projectPath, generatedAt)))
            .Replace("{{SUMMARY}}", Summary(project, points))
            .Replace("{{SECTIONS}}", string.Join("\n", sections))
            .Replace("{{LEGEND}}", Legend());
    }

    /// <summary>报告正文里至少要有一个数才值得生成：没有任何内定向，说明这条线还没开始。</summary>
    public static bool HasAnything(Project project) =>
        project.Images.Any(i => i.Interior is not null || i.Exterior is not null);

    // ===================== 各节 =====================

    /// <summary>内定向：每片一行。参数式也列（它的 f/x₀/y₀ 是后面几步真正的输入），只是没有 σ₀。</summary>
    private static void AddInterior(List<string> sections, ref int no, string template, Project project)
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

        sections.Add(RenderSection(template, ++no, "内定向", new[]
        {
            Head("影像"), HeadN("f (mm)"), HeadN("x₀ (mm)"), HeadN("y₀ (mm)"), HeadN("δ (mm)"),
            HeadN("σ₀ (mm)"), HeadN("max|v| (mm)"), HeadN("r"),
        }, rows, hasParametric
            ? "参数式内定向直接采用给定内参，没有多余观测，所以 σ₀、最大残差与多余观测都是「—」。"
            : ""));
    }

    /// <summary>框标残差：一行一个框标。没有框标残差（参数式）就整节不出现。</summary>
    private static void AddFiducialResiduals(List<string> sections, ref int no, string template,
        Project project)
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

        sections.Add(RenderSection(template, ++no, "框标残差",
            new[] { Head("影像"), Head("框标"), HeadN("vx (mm)"), HeadN("vy (mm)") }, rows, ""));
    }

    /// <summary>后方交会：每片一行，六外方位 + σ₀ + 多余观测。</summary>
    private static void AddResection(List<string> sections, ref int no, string template, Project project)
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

        sections.Add(RenderSection(template, ++no, "后方交会", new[]
        {
            Head("影像"), HeadN("Xₛ (m)"), HeadN("Yₛ (m)"), HeadN("Zₛ (m)"),
            HeadN("φ (°)"), HeadN("ω (°)"), HeadN("κ (°)"), HeadN("σ₀ (mm)"), HeadN("r"),
        }, rows, ""));
    }

    /// <summary>后方交会残差：一行一片一点。这是"外方位可不可信"的证据。</summary>
    private static void AddResectionResiduals(List<string> sections, ref int no, string template,
        Project project)
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

        sections.Add(RenderSection(template, ++no, "控制点残差", new[]
        {
            Head("影像"), Head("点号"), HeadN("Vx (mm)"), HeadN("Vy (mm)"),
            HeadN("|V| (mm)"), HeadN("|V|/σ₀"),
        }, rows, ""));
    }

    /// <summary>前方交会：一行一点。来自会话，重开工程后为空。</summary>
    private static void AddIntersection(List<string> sections, ref int no, string template,
        IReadOnlyList<ReportPoint> points)
    {
        HeadCell[] head =
        {
            Head("像对"), HeadN("X (m)"), HeadN("Y (m)"), HeadN("Z (m)"), HeadN("γ (°)"),
            HeadN("m_X (m)"), HeadN("m_Y (m)"), HeadN("m_Z (m)"),
        };

        var rows = new StringBuilder();
        foreach (var point in points.Take(MaxIntersectionRows))
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
        else if (points.Count > MaxIntersectionRows)
        {
            note = $"共 {points.Count} 个点，此处列前 {MaxIntersectionRows} 个（按交会顺序）。";
        }
        else
        {
            note = "";
        }

        sections.Add(RenderSection(template, ++no, "前方交会", head, rows, note));
    }

    // ===================== 摘要与页脚 =====================

    private static string Summary(Project project, IReadOnlyList<ReportPoint> points)
    {
        var control = project.Gcps.Count(g => g.Type != GcpType.CheckPoint);
        var check = project.Gcps.Count(g => g.Type == GcpType.CheckPoint);

        var items = new List<string>
        {
            Chip("影像", $"{project.Images.Count} 张"),
            Chip("像点", $"{project.Images.Sum(i => i.Points.Count)} 个"),
            Chip("控制点", check > 0 ? $"{control} 个（检查 {check}）" : $"{control} 个"),
            Chip("像对", $"{project.Pairs.Count} 个"),
            Chip("前方交会", $"{points.Count} 点"),
        };

        return string.Join("\n    ", items);
    }

    private static string Chip(string name, string value) => $"<li><b>{Esc(name)}</b>{Esc(value)}</li>";

    private static string Meta(string? projectPath, DateTime generatedAt) =>
        (projectPath is null ? "工程未保存" : Path.GetFileName(projectPath)) +
        $"　·　生成于 {generatedAt:yyyy-MM-dd HH:mm}";

    /// <summary>标红说明。阈值从 <see cref="Judgement"/> 取，不在这里另抄一份。</summary>
    private static string Legend()
    {
        static string V(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        return "<span class=\"mark\">标红</span>＝超出判据：" +
               $"内定向 σ₀ &gt; {V(Judgement.InteriorSigma0Mm)} mm　·　" +
               $"框标残差 &gt; {V(Judgement.FiducialResidualSigmas)}σ₀　·　" +
               $"控制点残差 &gt; {V(Judgement.ResectionResidualSigmas)}σ₀　·　" +
               $"交会角 &lt; {V(Judgement.SmallConvergenceAngleDeg)}°";
    }

    private static string ProjectName(Project project) =>
        string.IsNullOrWhiteSpace(project.Name) ? "未命名工程" : project.Name;

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

    private static string RenderSection(string template, int no, string title, HeadCell[] head,
        StringBuilder rows, string note)
    {
        var headHtml = new StringBuilder(head.Length * 32);
        foreach (var cell in head) headHtml.Append(Cell(cell));

        return template
            .Replace("{{NO}}", no.ToString("00", CultureInfo.InvariantCulture))
            .Replace("{{TITLE}}", Esc(title))
            .Replace("{{HEAD}}", headHtml.ToString())
            .Replace("{{ROWS}}", rows.ToString())
            .Replace("{{NOTE}}", Esc(note));
    }

    // ===================== 单元格 =====================

    private static HeadCell Head(string text) => new(text, false);

    private static HeadCell HeadN(string text) => new(text, true);

    private static string Cell(HeadCell head) =>
        $"<th class=\"{(head.Numeric ? "n" : "t")}\">{Esc(head.Text)}</th>";

    private static string Text(string value) => $"<td class=\"t\">{Esc(value)}</td>";

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
