using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 匹配质量的独立窗口：两个页签——直方图（核线残差 + 交会角）与点云（交会出的三维散点）。
///
/// <para>为什么要单开一个窗口：主窗五区已经占满，图表塞进结果窗口会把表格挤没；
/// 而这里是"看一眼就关"的东西，不该常住。数据由 <see cref="AutoIntersectBatchRun"/> 给。</para>
///
/// <para>点云那一页看的是<b>稀疏连接点</b>——每点是一对同名点交会出来的结果，不是稠密点云。
/// 它的用处是肉眼找整片错位、飞点、以及"是不是贴在一个面上"；数量对得上就说明定向站得住。</para>
/// </summary>
public partial class MatchReportWindow : Window
{
    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6F, 0xA8));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xD9));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x58, 0x63, 0x6E));

    private readonly IReadOnlyList<AutoIntersectPoint> _points;
    private readonly PointCloudView _cloud;

    public MatchReportWindow(AutoIntersectBatchRun run)
    {
        InitializeComponent();
        _points = run.Points;

        var residuals = _points.Select(p => Math.Abs(p.EpipolarMm)).ToList();
        var angles = _points.Select(p => p.Ground.ConvergenceAngleDeg).ToList();

        var pairs = run.Pairs.Count;
        var skipped = run.Skipped.Count;
        SummaryText.Text =
            $"{pairs} 对像对，匹配 {run.Matched} 对，交会出 {_points.Count} 个点，" +
            $"跳过 {skipped} 张影像。\n" +
            $"核线残差中位数 {Median(residuals):0.0000} mm，" +
            $"交会角中位数 {Median(angles):0.00}°，" +
            $"最小 {SafeMin(angles):0.00}°。";

        ResidualCanvas.SizeChanged += (_, _) => Draw(ResidualCanvas, residuals, "mm");
        AngleCanvas.SizeChanged += (_, _) => Draw(AngleCanvas, angles, "°");

        _cloud = new PointCloudView(CloudHost);
        var ground = _points.Select(p => p.Ground.Point).ToList();
        _cloud.SetPoints(ground);

        // 相对定向给的几何出的是模型坐标（|b| = 1，比例尺未知），不能写成 "m"。
        var succeeded = run.Pairs.Where(p => p.Run.Success).ToList();
        var modelUnits = succeeded.Count > 0 && succeeded.All(p => p.Run.IsModelCoordinates);

        if (ground.Count == 0)
        {
            CloudEmpty.Visibility = Visibility.Visible;
            CloudInfo.Text = string.Empty;
        }
        else
        {
            var extents = modelUnits
                ? $"X {_cloud.ExtentX:0.###} · Y {_cloud.ExtentY:0.###} · Z {_cloud.ExtentZ:0.###}（模型单位，比例尺未知）"
                : $"X {_cloud.ExtentX:0.#} m · Y {_cloud.ExtentY:0.#} m · Z {_cloud.ExtentZ:0.#} m";

            CloudInfo.Text =
                $"{ground.Count} 个交会点　{extents}　拖动旋转 · 滚轮缩放 · 双击复位";
        }

        // 切到点云页签时视图才第一次拿到真实尺寸，这里补一次重画（SizeChanged 通常已经管到，兜底）。
        ReportTabs.SelectionChanged += (_, _) => _cloud.Render();
    }

    /// <summary>等宽分箱后画柱状图，横轴标最小/最大。</summary>
    private void Draw(Canvas canvas, IReadOnlyList<double> values, string unit)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 40 || height < 40 || values.Count == 0) return;

        var min = values.Min();
        var max = values.Max();
        if (!(max > min))
        {
            max = min + 1.0;
        }

        var bins = Math.Clamp((int)Math.Sqrt(values.Count), 8, 30);
        var counts = new int[bins];
        var span = max - min;
        foreach (var v in values)
        {
            var k = (int)((v - min) / span * bins);
            counts[Math.Clamp(k, 0, bins - 1)]++;
        }

        var peak = counts.Max();
        if (peak == 0) return;

        // 底部 22 px 放刻度，顶部 12 px 放"峰值"——这样最高的柱子也不会压住标签。
        const double bottom = 22;
        const double top = 12;
        var plotHeight = height - bottom - top;
        var barWidth = width / bins - 1.5;

        for (var b = 0; b < bins; b++)
        {
            var h = counts[b] / (double)peak * plotHeight;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1.0, barWidth),
                Height = Math.Max(0.5, h),
                Fill = BarBrush,
                Opacity = 0.85,
                ToolTip = $"{min + span * b / bins:0.####} – {min + span * (b + 1) / bins:0.####} {unit}：{counts[b]} 个",
            };
            Canvas.SetLeft(rect, b * (barWidth + 1.5));
            Canvas.SetTop(rect, top + plotHeight - h);
            canvas.Children.Add(rect);
        }

        var axisY = top + plotHeight + 1;
        var axis = new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = width, Y1 = axisY, Y2 = axisY,
            Stroke = AxisBrush, StrokeThickness = 1,
        };
        canvas.Children.Add(axis);

        AddLabel(canvas, min.ToString("0.###", CultureInfo.InvariantCulture), 0, axisY + 3);
        AddLabel(canvas, max.ToString("0.###", CultureInfo.InvariantCulture), width - 48, axisY + 3);
        AddLabel(canvas, $"峰值 {peak} 个", 6, 0);
    }

    private static void AddLabel(Canvas canvas, string text, double left, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = LabelBrush,
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        canvas.Children.Add(label);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static double SafeMin(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : values.Min();
}
