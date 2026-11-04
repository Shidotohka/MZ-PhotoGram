using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            ShowResult(FailTable(
                $"前方交会 · {_interLeftImage?.Id ?? "—"} / {_interRightImage?.Id ?? "—"}", run.Message));
            LogSolve("前方交会", PairName(), false, run.Message);
            DiagSummary.Text = "✗ " + run.Message;
            DiagSummary.Foreground = ErrBadge;
            return run;
        }

        var result = run.Result!;
        var table = IntersectionResult(run);

        LogSolve("前方交会", PairName(), true,
            $"交会角 {result.ConvergenceAngleDeg:0.00}°　基线 {result.BaseLength:0.0} m　" +
            $"上下视差 {result.VerticalParallaxMm:0.0000} mm　m_XY {Math.Max(result.MX, result.MY):0.000} m");

        if (run.Warnings.Count > 0)
        {
            foreach (var warning in run.Warnings) Log("!", warning);
            DiagSummary.Text = $"! 交会完成，但有 {run.Warnings.Count} 条提醒（见日志）";
            DiagSummary.Foreground = WarnBadge;
        }
        else
        {
            DiagSummary.Text = "✓ 前方交会完成";
            DiagSummary.Foreground = OkBadge;
        }

        ShowResult(table);
        return run;
    }

    /// <summary>像对的名字：日志与结果标题共用一套写法。</summary>
    private string PairName() =>
        $"{_interLeftImage?.Id ?? "—"} / {_interRightImage?.Id ?? "—"}";

    // ---------- 自动匹配（SIFT + 核线 + RANSAC → 逐点交会） ----------

    /// <summary>
    /// 不用手点：两张片各自解码成灰度，一次调 <see cref="AutoIntersectTask"/>，
    /// 匹配出来的每一对都交会一次，然后把整批点摆进结果页。
    /// </summary>
    private async void AutoMatchIntersect_Click(object sender, RoutedEventArgs e)
    {
        var views = Workspace.VisibleViews().ToList();
        if (views.Count != 2 || views[0].Image is not { } left || views[1].Image is not { } right)
        {
            DiagSummary.Text = "✗ 自动匹配要两张片：把左片拖进左窗、右片拖进右窗。";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        IntersectionAutoButton.IsEnabled = false;
        SetBusy(true, "正在解码两张片…");
        try
        {
            var leftSource = await EnsureBitmapAsync(left);
            var rightSource = await EnsureBitmapAsync(right);
            if (leftSource is null || rightSource is null) return;

            SetBusy(true, "正在匹配…（SIFT 找特征，要几秒到几十秒）");
            var lg = ToGray8(leftSource);
            var rg = ToGray8(rightSource);

            var run = await Task.Run(() => AutoIntersectTask.Run(
                left, right, lg.Pixels, lg.Step, rg.Pixels, rg.Step));

            ShowAutoIntersect(run, left, right);
        }
        catch (Exception ex)
        {
            DiagSummary.Text = "✗ 自动匹配失败：" + ex.Message;
            DiagSummary.Foreground = ErrBadge;
            LogSolve("自动匹配", PairName(), false, ex.Message);
            MessageBox.Show(this, ex.Message, "自动匹配失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, null);
            IntersectionAutoButton.IsEnabled = true;
        }
    }

    /// <summary>把一次自动交会摆到结果页上：左边概要、右边逐点。</summary>
    private void ShowAutoIntersect(AutoIntersectRun run, ImageItem left, ImageItem right)
    {
        var title = $"前方交会 · {left.Id} / {right.Id}";

        if (!run.Success)
        {
            ShowResult(FailTable(title, run.Message));
            LogSolve("自动匹配", PairName(), false, run.Message);
            DiagSummary.Text = "✗ " + run.Message;
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        var points = run.Points;
        var angles = points.Select(p => p.Ground.ConvergenceAngleDeg).OrderBy(a => a).ToList();
        var medianAngle = angles[angles.Count / 2];

        var table = new ResultTable { Title = title }
            .Num("匹配对", "n", run.Matched, "0", "对")
            .Num("RANSAC 内点", "n", run.Inliers, "0", "对")
            .Num("检出特征", "n", run.Detected, "0", "个")
            .Num("交会成功", "n", points.Count, "0", "点")
            .Num("交会失败", "n", run.Failed, "0", "对")
            .Num("交会角中位数", "γ", medianAngle, "0.00", "°")
            .Head("点号", "左片 px", "右片 px", "X (m)", "Y (m)", "Z (m)", "交会角");

        const int kShown = 300;
        for (var k = 0; k < points.Count && k < kShown; k++)
        {
            var p = points[k];
            table.Row(
                (k + 1).ToString(),
                $"{p.LeftI:0.0}, {p.LeftJ:0.0}",
                $"{p.RightI:0.0}, {p.RightJ:0.0}",
                Fmt(p.Ground.Point.X, "0.000"),
                Fmt(p.Ground.Point.Y, "0.000"),
                Fmt(p.Ground.Point.Z, "0.000"),
                Format.Fixed(p.Ground.ConvergenceAngleDeg, "0.00"));
        }

        if (points.Count > kShown)
        {
            table.Note = $"只列前 {kShown} 个点，共 {points.Count} 个。";
        }

        LogSolve("自动匹配", PairName(), true,
            $"匹配 {run.Matched} 对　内点 {run.Inliers}　特征 {run.Detected}　" +
            $"交会 {points.Count} 点（失败 {run.Failed}）　交会角中位 {medianAngle:0.00}°");

        foreach (var warning in run.Warnings) Log("!", warning);

        DiagSummary.Text = run.Warnings.Count == 0
            ? $"✓ 自动交会完成，{points.Count} 个点"
            : $"! 自动交会完成，{points.Count} 个点，有 {run.Warnings.Count} 条提醒（见日志）";
        DiagSummary.Foreground = run.Warnings.Count == 0 ? OkBadge : WarnBadge;

        ShowResult(table);
    }

    /// <summary>任意位图 → 8 位灰度缓冲（步长 = 宽度，因为每像素一字节）。</summary>
    private static (byte[] Pixels, int Step) ToGray8(System.Windows.Media.Imaging.BitmapSource source)
    {
        System.Windows.Media.Imaging.BitmapSource gray = source.Format == PixelFormats.Gray8
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

        var width = gray.PixelWidth;
        var height = gray.PixelHeight;
        var step = width;
        var pixels = new byte[step * height];
        gray.CopyPixels(pixels, step, 0);
        return (pixels, step);
    }

    private static ImagePixel? PixelOf(ImagePoint? point) =>
        point is { } p ? new ImagePixel(p.PixelX, p.PixelY) : null;
}
