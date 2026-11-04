using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 匹配质量的独立窗口：一张核线残差直方图 + 一张交会角直方图。
///
/// <para>为什么要单开一个窗口：主窗五区已经占满，图表塞进结果页会把表格挤没；
/// 而这两张图是"看一眼就关"的东西，不该常住。数据由 <see cref="AutoIntersectBatchRun"/> 给。</para>
/// </summary>
public partial class MatchReportWindow : Window
{
    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6F, 0xA8));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xD9));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x58, 0x63, 0x6E));

    private readonly IReadOnlyList<AutoIntersectPoint> _points;

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
    }

    /// <summary>等宽分箱后画柱状图，横轴标最小/中位/最大。</summary>
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

        // 留出底部 22 px 放刻度
        var plotHeight = height - 22;
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
            Canvas.SetTop(rect, plotHeight - h);
            canvas.Children.Add(rect);
        }

        var axis = new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = width, Y1 = plotHeight + 1, Y2 = plotHeight + 1,
            Stroke = AxisBrush, StrokeThickness = 1,
        };
        canvas.Children.Add(axis);

        AddLabel(canvas, min.ToString("0.###", CultureInfo.InvariantCulture), 0, plotHeight + 4);
        AddLabel(canvas, max.ToString("0.###", CultureInfo.InvariantCulture), width - 48, plotHeight + 4);
        AddLabel(canvas, $"峰值 {peak} 个", 6, 2);
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
