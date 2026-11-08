using System;
using System.IO;
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
            if (run.Failure == IntersectionFailure.Solver)
            {
                ShowResult(FailTable(ResultKind.Intersection, PairName(),
                    $"前方交会 · {_interLeftImage?.Id ?? "—"} / {_interRightImage?.Id ?? "—"}", run.Message));
                LogSolve("前方交会", PairName(), false, run.Message);
                DiagSummary.Text = "✗ " + run.Message;
                DiagSummary.Foreground = ErrBadge;
            }
            else
            {
                // 前置没满足（缺片、缺内外方位、没点、点出框、焦距不同）：由闸门一句话说明，
                // 不往结果集合里塞一份"未解出"——那还不是一次解算
                GateBlocked("前方交会", run.Message);
            }

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
        // 交会点不写工程，但报告要用：把它记进本会话的那一份（同一点解两次只记一次）
        RememberIntersection(PairName(), result);
        return run;
    }

    /// <summary>像对的名字：日志与结果标题共用一套写法。</summary>
    private string PairName() =>
        $"{_interLeftImage?.Id ?? "—"} / {_interRightImage?.Id ?? "—"}";

    // ---------- 自动匹配（SIFT + 核线 + RANSAC → 逐点交会） ----------

    /// <summary>
    /// 不用手点：把工程里的影像全部解码成灰度，两两匹配、逐对交会。
    /// 条件不够的影像跳过、写一句原因；一对都没配上的像对也记一句。
    /// </summary>
    private async void AutoMatchIntersect_Click(object sender, RoutedEventArgs e)
    {
        var images = CurrentProject.Images.ToList();
        if (images.Count < 2)
        {
            DiagSummary.Text = "✗ 自动匹配至少要两张影像（数据树里）。";
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        IntersectionAutoButton.IsEnabled = false;
        try
        {
            var prepared = new List<AutoIntersectBatchTask.PreparedImage>(images.Count);
            var decoded = 0;

            foreach (var image in images)
            {
                SetBusy(true, $"正在解码 {image.Id}（{++decoded}/{images.Count}）…");

                if (image.FullPath is null || !File.Exists(image.FullPath))
                {
                    Log("!", $"{image.Id}：影像文件不见了，跳过");
                    continue;
                }

                var source = await EnsureBitmapAsync(image);
                if (source is null) continue;

                var gray = ToGray8(source);
                prepared.Add(new AutoIntersectBatchTask.PreparedImage(image, gray.Pixels, gray.Step));
            }

            if (prepared.Count < 2)
            {
                SetBusy(false, null);
                DiagSummary.Text = "✗ 可用的影像不足两张：缺内定向（见日志）。";
                DiagSummary.Foreground = ErrBadge;
                LogSolve("自动匹配", "全部影像", false, "可用的影像不足两张");
                return;
            }

            SetBusy(true, $"正在匹配（{prepared.Count} 张，两两配对）…");

            // 带上工程里的像对：有相对定向结果的像对，即使没做后方交会也能跑（出模型坐标）。
            var pairs = CurrentProject.Pairs.ToList();
            var run = await Task.Run(() => AutoIntersectBatchTask.Run(prepared, pairs));

            ShowAutoIntersect(run, prepared.Count);
        }
        catch (Exception ex)
        {
            DiagSummary.Text = "✗ 自动匹配失败：" + ex.Message;
            DiagSummary.Foreground = ErrBadge;
            LogSolve("自动匹配", "全部影像", false, ex.Message);
            MessageBox.Show(this, ex.Message, "自动匹配失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, null);
            IntersectionAutoButton.IsEnabled = true;
        }
    }

    /// <summary>最近一次自动匹配的结果，供「匹配质量图」窗口使用。</summary>
    private AutoIntersectBatchRun? _lastMatchRun;

    /// <summary>质量图单开一个窗口：主窗五区已经占满，图表塞进来会把表挤没。</summary>
    private void OpenMatchReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastMatchRun is not { Success: true })
        {
            DiagSummary.Text = "还没有匹配结果：先按「自动匹配（全部影像）」。";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        new MatchReportWindow(_lastMatchRun) { Owner = this }.Show();
    }

    /// <summary>把一次「全工作区自动交会」摆到结果窗口里：左边概要、右边逐点。</summary>
    private void ShowAutoIntersect(AutoIntersectBatchRun run, int preparedCount)
    {
        _lastMatchRun = run.Success ? run : null;

        // 相对定向给的几何出的是模型坐标（比例尺未知），绝对外方位出的才是地面米制坐标。
        // 两者混在一张表里会让"Z (m)"变成谎话，所以按「有成功的像对里是不是全模型坐标」决定表头。
        var succeeded = run.Pairs.Where(p => p.Run.Success).ToList();
        var model = succeeded.Count > 0 && succeeded.All(p => p.Run.IsModelCoordinates);
        var mixed = succeeded.Any(p => p.Run.IsModelCoordinates) && !model;

        var title = model ? "前方交会 · 全部影像（模型坐标）" : "前方交会 · 全部影像";

        // 跳过的影像与像对级提醒都进日志：一句一条，不铺在结果窗口里。
        foreach (var skip in run.Skipped) Log("!", $"{skip.ImageId}：{skip.Reason}");
        foreach (var warning in run.Warnings) Log("!", warning);

        if (mixed)
        {
            Log("!", "有些像对用绝对外方位（地面坐标）、有些用相对定向（模型坐标）：" +
                     "表里的 XYZ 不是同一个坐标系，别直接比。");
        }

        foreach (var source in succeeded.Select(p => p.Run.GeometrySource).Distinct())
        {
            Log("·", "几何来源：" + source);
        }

        // 报告只收地面坐标的交会点：模型坐标不是米制，写进"X (m)"就是谎话
        foreach (var pair in succeeded.Where(p => !p.Run.IsModelCoordinates))
        {
            var label = $"{pair.Left.Id} / {pair.Right.Id}";
            foreach (var p in pair.Run.Points) RememberIntersection(label, p.Ground);
        }

        if (!run.Success)
        {
            var message = run.Skipped.Count == 0
                ? "所有像对都没配上点。"
                : $"所有像对都没配上点（{run.Skipped.Count} 张影像被跳过，原因见日志）。";
            ShowResult(FailTable(ResultKind.Intersection, "全部影像", title, message));
            LogSolve("自动匹配", "全部影像", false, message);
            DiagSummary.Text = "✗ " + message;
            DiagSummary.Foreground = ErrBadge;
            return;
        }

        var points = run.Points;

        // 只列异常点：核线残差超 0.1 mm 或交会角小于 15°。正常的几千个点不看。
        var abnormal = points
            .Where(p => p.EpipolarMm > 0.10 || p.Ground.ConvergenceAngleDeg < 15.0)
            .Take(200)
            .ToList();

        var table = new SolveResult
            {
                // 自动匹配出的是前方交会点，归在「前方交会」这一步下；
                // 对象写「全部影像」，与手工指定像对的结果区分开
                Kind = ResultKind.Intersection,
                TargetId = "全部影像",
                Title = title,
            }
            .Num("匹配对", "n", run.Matched, "0", "对")
            .Num("交会点", "n", points.Count, "0", "点")
            .Num("异常", "n", abnormal.Count, "0", "个");

        // 模型坐标没有米制含义，表头就不能写 (m)。
        if (model)
        {
            table.Head("像对", "左片 px", "右片 px", "X", "Y", "Z", "交会角");
        }
        else
        {
            table.Head("像对", "左片 px", "右片 px", "X (m)", "Y (m)", "Z (m)", "交会角");
        }

        foreach (var pair in run.Pairs)
        {
            foreach (var p in pair.Run.Points)
            {
                if (!abnormal.Contains(p)) continue;
                table.Row(
                    $"{pair.Left.Id} / {pair.Right.Id}",
                    $"{p.LeftI:0.0}, {p.LeftJ:0.0}",
                    $"{p.RightI:0.0}, {p.RightJ:0.0}",
                    Fmt(p.Ground.Point.X, "0.000"),
                    Fmt(p.Ground.Point.Y, "0.000"),
                    Fmt(p.Ground.Point.Z, "0.000"),
                    Format.Fixed(p.Ground.ConvergenceAngleDeg, "0.00"));
            }
        }

        if (abnormal.Count == 0)
        {
            table.Note = "没有异常点。";
        }

        LogSolve("自动匹配", "全部影像", true,
            $"{preparedCount} 张　{run.Pairs.Count} 对　匹配 {run.Matched} 对　" +
            $"交会 {points.Count} 点　异常 {abnormal.Count} 个");

        var warnings = run.Warnings.Count + run.Skipped.Count;
        DiagSummary.Text = warnings == 0
            ? $"✓ 自动交会完成，{points.Count} 个点，无异常"
            : $"! 自动交会完成，{points.Count} 个点，{warnings} 条提醒（见日志）";
        DiagSummary.Foreground = warnings == 0 ? OkBadge : WarnBadge;

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
