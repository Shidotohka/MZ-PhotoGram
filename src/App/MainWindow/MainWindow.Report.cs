using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Photogrammetry.App.Report;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 成果报告：把**这条线上已入账的成果**排成一份 HTML，存盘并交给浏览器打印。
///
/// <para><b>报告不是一步作业，所以它不在模块栏里。</b>它没有"解算"，也没有可接受的
/// 结果——它只是把前面几步的成果换个版式摆出来。入口在「查看 ▾」。</para>
///
/// <para><b>一条作业线一份报告。</b>后交–前交与相对–绝对给的是两种坐标系下的两种成果，
/// 合成一份会让人分不清哪张表是模型坐标、哪张是地面坐标。所以按线分文件，文件名也写明。</para>
///
/// <para><b>模板在文件里，不在代码里</b>（<c>Report/ReportTemplate.html</c>）：改字号、
/// 改列宽、加页眉都不用重编译。这里只管"什么时候生成、存到哪、生成完干什么"。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 本会话里交会出来的地面点（报告用）。
    ///
    /// <para>前方交会不写工程（它出的是单次临时点），所以重开工程后这里是空的——
    /// 报告照出，那一段写「未计算」。工程里能留住的只有"点"本身，不是"这一次交会"。</para>
    /// </summary>
    private readonly List<ReportPoint> _reportPoints = new();

    /// <summary>同一点重解一次不该在报告里多出一行，按「像对 + 位置」去重。</summary>
    private readonly HashSet<(string Pair, long X, long Y, long Z)> _reportPointKeys = new();

    private void ExportReport_Click(object sender, RoutedEventArgs e) =>
        ExportReport(ReportLine.BackIntersection);

    private void ExportRelativeReport_Click(object sender, RoutedEventArgs e) =>
        ExportReport(ReportLine.RelativeAbsolute);

    /// <summary>
    /// 导出某一条线的报告：查有没有成果 → 选路径 → 生成 → 开浏览器。
    ///
    /// <para><b>入口按线分两个，不做一个"选哪种"的对话框</b>：报告是"这条线做完了，导一份"，
    /// 用户心里想的是哪条线，菜单上就该有哪一条。两条线各点各的，也不需要判断"你当前在哪个模块"。</para>
    /// </summary>
    private void ExportReport(ReportLine line)
    {
        if (!ReportBuilder.HasAnything(_project, line))
        {
            DiagSummary.Text = "! " + ReportBuilder.MissingHint(line);
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"导出成果报告（{ReportBuilder.MethodName(line)}）",
            Filter = "网页（单文件）|*.html",
            DefaultExt = ".html",
            AddExtension = true,
            FileName = SafeFileName(_project.Name) + "_" + ReportBuilder.FileTag(line) + ".html",
        };

        if (_projectPath is { } projectPath && Path.GetDirectoryName(projectPath) is { Length: > 0 } dir)
        {
            dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog(this) != true) return;
        WriteAndOpenReport(dialog.FileName, line);
    }

    /// <summary>生成 → 写字 → 开浏览器。三件事各自可能失败，失败就说到哪一步为止。</summary>
    private bool WriteAndOpenReport(string path, ReportLine line)
    {
        string html;
        try
        {
            html = ReportBuilder.Build(_project, line, _reportPoints, DateTime.Now, _projectPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "报告生成失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            LogSolve("报告", ReportBuilder.MethodName(line), false, ex.Message);
            return false;
        }

        try
        {
            // 明确写 UTF-8：报告里有中文，靠系统默认编码会变成乱码
            File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "报告保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            LogSolve("报告", ReportBuilder.MethodName(line), false, "保存失败：" + ex.Message);
            return false;
        }

        LogSolve("报告", ReportBuilder.MethodName(line), true, $"已生成 {Path.GetFileName(path)}");
        DiagSummary.Text = "✓ 报告已生成，正在用浏览器打开";
        DiagSummary.Foreground = OkBadge;

        try
        {
            // UseShellExecute：交给系统按 .html 的默认程序打开（浏览器），不由我们决定用哪个
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 存是存住了，打不开也得说一句——不然用户以为没生成成功
            Log("!", "报告已保存，但没能打开浏览器：" + ex.Message);
            DiagSummary.Text = "! 报告已保存，但没能打开浏览器（手动双击该文件即可）";
            DiagSummary.Foreground = WarnBadge;
        }

        return true;
    }

    /// <summary>记下一个交会点。只记地面坐标的交会——模型坐标不是米制，写进"X (m)"就是谎话。</summary>
    private void RememberIntersection(string pair, IntersectionResult ground)
    {
        var key = (
            pair,
            (long)Math.Round(ground.Point.X * 1000),
            (long)Math.Round(ground.Point.Y * 1000),
            (long)Math.Round(ground.Point.Z * 1000));

        if (_reportPointKeys.Add(key)) _reportPoints.Add(new ReportPoint(pair, ground));
    }

    /// <summary>文件名里不能有这些字符。工程名是用户起的，什么字符都可能出现。</summary>
    private static string SafeFileName(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "未命名工程" : cleaned.Trim();
    }

    // ===================== 供离屏渲染工具断言 =====================

    /// <summary>报告正文，不写盘也不开浏览器。Snap 用它验"表格里真的有这几节、这几个数"。</summary>
    internal string BuildReportForTooling(ReportLine line) =>
        ReportBuilder.Build(_project, line, _reportPoints, DateTime.Now, _projectPath);

    /// <summary>报告里会有几个交会点。Snap 用它验"交会点确实进了报告"。</summary>
    internal int ReportPointCountForTooling => _reportPoints.Count;

    /// <summary>报告模板在不在。模板没被拷到输出目录时，报告一按就失败，得能提前验出来。</summary>
    internal static string ReportTemplatePathForTooling => ReportBuilder.TemplatePath;
}
