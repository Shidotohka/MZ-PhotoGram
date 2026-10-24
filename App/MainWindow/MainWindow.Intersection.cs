using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 前方交会的<b>界面部分</b>：点两个同名像点、按一下就出地面坐标。
///
/// <para>流程与检查都在 <see cref="IntersectionTask"/>。本文件只做三件事：
/// 记住用户点在哪两个视窗、把配对状态显示出来、把结果摆到面板上。</para>
///
/// <para>配对用的是影像上的<b>临时像点</b>（未绑定到控制点），所以看得见、能右键取消、
/// 也能被「清空配对」一起收走——不是藏在窗口里的一对数字。</para>
/// </summary>
public partial class MainWindow
{
    // ---------- 前方交会 ----------

    private ImagePoint? _interLeft;
    private ImagePoint? _interRight;
    private ImageItem? _interLeftImage;
    private ImageItem? _interRightImage;

    /// <summary>防止「删旧点 → 触发 SelectionChanged → 又回到这里」的递归。</summary>
    private bool _interPicking;

    /// <summary>刷新配对状态与「解算」按钮的可用性。</summary>
    private void UpdateIntersectionPanel()
    {
        if (IntersectionPanel is null) return;

        IntersectionPairText.Text =
            DescribeSide("左", _interLeftImage, _interLeft) + "\n" +
            DescribeSide("右", _interRightImage, _interRight);

        if (Workspace.VisibleViews().Count() != 2)
        {
            IntersectionHint.Text = "前方交会要两张片：把左片拖进左窗、右片拖进右窗。";
            IntersectionSolveButton.IsEnabled = false;
            return;
        }

        // 用真正的解算入口做一次「能不能解」的判定，失败原因与解算时完全一致，
        // 不在界面里另写一套条件——那种写法迟早会和流程层对不上。
        var probe = IntersectionTask.Solve(
            _interLeftImage, _interRightImage, PixelOf(_interLeft), PixelOf(_interRight));

        IntersectionSolveButton.IsEnabled = probe.Failure == IntersectionFailure.None;
        IntersectionHint.Text = probe.Failure switch
        {
            IntersectionFailure.None => "两点都就位，可以解算。",

            // 「左窗还没有影像」这种话对新手不够用，直接告诉他怎么开始
            IntersectionFailure.NoLeftImage or IntersectionFailure.NoRightImage
                => "前方交会要两张片：把左片拖进左窗、右片拖进右窗。",

            _ => probe.Message,
        };
    }

    /// <summary>一行描述：哪个窗、哪张片、点在哪、能不能用。窄栏里别写太长，会折得看不清。</summary>
    private static string DescribeSide(string slot, ImageItem? image, ImagePoint? point)
    {
        if (image is null) return $"{slot} —　无影像";

        var flags = (image.Interior is { IsValid: true } ? "内✓" : "内✗") + " " +
                    (image.Exterior is not null ? "外✓" : "外✗");

        return point is null
            ? $"{slot} {image.Id}　未点　{flags}"
            : $"{slot} {image.Id}　({point.PixelX}, {point.PixelY})　{flags}";
    }

    /// <summary>用户在一个视窗里选中/新建了像点。判断属于左窗还是右窗，记下来。</summary>
    private void HandleIntersectionPick()
    {
        if (_interPicking) return;
        if (_moduleName != "前方交会") return;
        if (Workspace.ActiveView is not { } view) return;
        if (view.SelectedPoint is not { } point || view.Image is not { } image) return;

        // 只有双图才有左右之分：VisibleViews 在双图模式下按「左、右」顺序给出
        var views = Workspace.VisibleViews().ToList();
        if (views.Count != 2) return;

        _interPicking = true;
        try
        {
            if (view == views[0])
            {
                if (!ReferenceEquals(_interLeft, point)) RemovePickPoint(_interLeftImage, _interLeft);
                _interLeft = point;
                _interLeftImage = image;
            }
            else if (view == views[1])
            {
                if (!ReferenceEquals(_interRight, point)) RemovePickPoint(_interRightImage, _interRight);
                _interRight = point;
                _interRightImage = image;
            }
            else
            {
                return;
            }
        }
        finally
        {
            _interPicking = false;
        }

        UpdateIntersectionPanel();
    }

    /// <summary>清空配对：连同影像上那两个临时像点一起收走，不留没人认领的点。</summary>
    private void ClearIntersectionPair()
    {
        var left = _interLeft;
        var right = _interRight;
        var leftImage = _interLeftImage;
        var rightImage = _interRightImage;

        // 先断引用再删点：CancelSelection 会触发 SelectionChanged，回到 HandleIntersectionPick
        _interLeft = null;
        _interRight = null;
        _interLeftImage = null;
        _interRightImage = null;

        _interPicking = true;
        try
        {
            RemovePickPoint(leftImage, left);
            RemovePickPoint(rightImage, right);
        }
        finally
        {
            _interPicking = false;
        }

        if (IntersectionResultText is not null)
        {
            IntersectionResultText.ClearValue(TextBlock.ForegroundProperty);
            IntersectionResultText.Text = "尚未解算";
        }

        UpdateIntersectionPanel();
    }

    private void ClearIntersectionPair_Click(object sender, RoutedEventArgs e) => ClearIntersectionPair();

    /// <summary>删掉一个临时像点：选中状态下走视窗自己的右键取消逻辑，它才是那条路径的正主。</summary>
    private void RemovePickPoint(ImageItem? image, ImagePoint? point)
    {
        if (point is null) return;

        foreach (var view in Workspace.AllViews())
        {
            if (ReferenceEquals(view.SelectedPoint, point))
            {
                if (!point.IsBound) view.CancelSelection();
                return;
            }
        }

        if (image is null || !image.Points.Remove(point)) return;
        foreach (var view in Workspace.AllViews()) view.RefreshPoints();
    }

    private void SolveIntersection_Click(object sender, RoutedEventArgs e) => SolveIntersectionNow();

    /// <summary>
    /// 跑一次前方交会并把结果摆到面板上。
    /// 也供离屏渲染工具调用（它要靠这条路径验证面板真的显示了结果），见 MainWindow.Tooling.cs。
    /// </summary>
    internal IntersectionRun SolveIntersectionNow()
    {
        var run = IntersectionTask.Solve(
            _interLeftImage, _interRightImage, PixelOf(_interLeft), PixelOf(_interRight));

        if (!run.Success)
        {
            IntersectionResultText.Foreground = ErrBadge;
            IntersectionResultText.Text = "未解出：\n" + run.Message;
            DiagSummary.Text = "✗ " + run.Message;
            DiagSummary.Foreground = ErrBadge;
            return run;
        }

        var result = run.Result!;
        var text = FormatIntersection(run);

        if (run.Warnings.Count > 0)
        {
            IntersectionResultText.Foreground = WarnBadge;
            text += "\n\n" + string.Join("\n", run.Warnings.Select(w => "⚠ " + w));
            DiagSummary.Text = $"! 交会完成，但有 {run.Warnings.Count} 条提醒";
            DiagSummary.Foreground = WarnBadge;
        }
        else
        {
            IntersectionResultText.ClearValue(TextBlock.ForegroundProperty);
            DiagSummary.Text = $"✓ 前方交会完成：交会角 {result.ConvergenceAngleDeg:0.00}°，" +
                               $"m_XY {Math.Max(result.MX, result.MY):0.000} m、m_Z {result.MZ:0.000} m";
            DiagSummary.Foreground = OkBadge;
        }

        IntersectionResultText.Text = text;

        // 右栏是可滚动的，结果框常常在折叠线以下。解完把它滚出来，否则等于没显示。
        IntersectionResultText.BringIntoView();
        return run;
    }

    private static string FormatIntersection(IntersectionRun run)
    {
        var r = run.Result!;
        var p = r.Point;

        var sb = new StringBuilder();
        sb.AppendLine("交会点（数学系：X 东、Y 北、Z 天顶）");
        sb.AppendLine($"  X = {p.X,14:0.000} m");
        sb.AppendLine($"  Y = {p.Y,14:0.000} m");
        sb.AppendLine($"  Z = {p.Z,14:0.000} m");

        // 控制点文件若是 X 指北（左手系写法），上面的 X/Y 与文件里对不上。
        // 两张片的轴序判定一致时才给换算，否则宁可不给，免得给错。
        if (run.Left?.GroundOrder == GroundAxisOrder.NorthEast &&
            run.Right?.GroundOrder == GroundAxisOrder.NorthEast)
        {
            sb.AppendLine("按控制点文件轴序（X 指北、Y 指东）：");
            sb.AppendLine($"  X = {p.Y,14:0.000} m");
            sb.AppendLine($"  Y = {p.X,14:0.000} m");
            sb.AppendLine($"  Z = {p.Z,14:0.000} m");
        }

        sb.AppendLine($"交会角 {r.ConvergenceAngleDeg:0.00}°　基线 {r.BaseLength:0.000} m");
        sb.AppendLine($"摄影距离 {r.RangeLeft:0.0} / {r.RangeRight:0.0} m");
        sb.AppendLine($"上下视差 {r.VerticalParallaxMm:+0.0000;-0.0000} mm　交会差 {r.MissDistance:0.000} m");
        sb.AppendLine($"m_X {r.MX:0.000}　m_Y {r.MY:0.000}　m_Z {r.MZ:0.000} m");
        sb.Append($"投影系数 N 左 {r.NLeft:0.0000}　N 右 {r.NRight:0.0000}");
        return sb.ToString();
    }

    private static ImagePixel? PixelOf(ImagePoint? point) =>
        point is { } p ? new ImagePixel(p.PixelX, p.PixelY) : null;
}
