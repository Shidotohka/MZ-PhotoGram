using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 相对定向的<b>界面部分</b>：在两张片上各点一个同名像点，攒成一对；攒够 6 对才能解。
///
/// <para>流程与检查都在 <see cref="RelativeOrientationTask"/>。本文件只做三件事：记住用户
/// 正在配的是哪两个点、把点对摆进点表、把结果放到面板上。</para>
///
/// <para>点对<b>进点表</b>（底部「点表」页签的第三个表），不是临时的数字：它们随工程存进
/// <c>.pht</c>，下次打开还在，解算结果与逐点视差也写在一起。像点一旦绑上同名点号，
/// 它就属于那个点对，右键不再能直接删掉——要删请删点队那一行。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>点表里同名点的一行。数值按固定小数格式化好再交给 DataGrid。</summary>
    private sealed class TieRow
    {
        public TiePoint? Source { get; init; }
        public string Id { get; init; } = "";
        public string LeftX { get; init; } = "";
        public string LeftY { get; init; } = "";
        public string RightX { get; init; } = "";
        public string RightY { get; init; } = "";
        public string Q { get; init; } = "";
        public string State { get; init; } = "";
        public string Note { get; init; } = "";

        public static TieRow From(TiePoint tie,
            (ImageItem Image, ImagePoint Point) left,
            (ImageItem Image, ImagePoint Point) right,
            ImageItem? leftImage, ImageItem? rightImage)
        {
            var lmm = MmOf(leftImage, left.Point);
            var rmm = MmOf(rightImage, right.Point);

            return new TieRow
            {
                Source = tie,
                Id = tie.Id,
                LeftX = lmm is { } l ? Fmt(l.X) : "—",
                LeftY = lmm is { } l2 ? Fmt(l2.Y) : "—",
                RightX = rmm is { } r ? Fmt(r.X) : "—",
                RightY = rmm is { } r2 ? Fmt(r2.Y) : "—",
                Q = tie.Q is { } q ? Fmt(q) : "",
                State = tie.Rejected ? "已剔除" : tie.Q is not null ? "参与" : "未解",
                Note = Describe(left, right, lmm is not null, rmm is not null),
            };
        }

        private static string Fmt(double v) => Format.Signed(v, "0.0000");

        /// <summary>像点 → 像平面坐标。没内定向时给 null，表里显示「—」而不是假的 0。</summary>
        private static ImagePointMm? MmOf(ImageItem? image, ImagePoint? point)
        {
            var io = image?.Interior;
            if (point is null || image is null || io is null || !io.IsValid) return null;

            return CoordinateConverter.PixelToImagePlane(
                new ImagePixel(point.PixelX, point.PixelY), io, image.Width, image.Height);
        }

        private static string Describe(
            (ImageItem Image, ImagePoint Point) left,
            (ImageItem Image, ImagePoint Point) right,
            bool hasLeft, bool hasRight)
        {
            if (left.Image is null && right.Image is null) return "与本像对无关";
            if (left.Image is null) return "缺左片";
            if (right.Image is null) return "缺右片";
            if (!hasLeft || !hasRight) return "没有内定向";
            return "";
        }
    }

    // ---------- 相对定向 ----------

    /// <summary>正在配的那一对：左窗里的点、右窗里的点。两者都到齐就落成一个同名点。</summary>
    private ImagePoint? _relLeft;
    private ImagePoint? _relRight;

    private ImageItem? _relLeftImage;
    private ImageItem? _relRightImage;

    /// <summary>防止「删旧点 → 触发 SelectionChanged → 又回到这里」的递归。</summary>
    private bool _relPicking;

    /// <summary>点表里选中的同名点号，刷新表时用来恢复选中行。</summary>
    private string? _selectedTieId;

    /// <summary>当前两窗里的影像。双图模式下按「左、右」顺序。</summary>
    private (ImageItem? Left, ImageItem? Right) RelativeImages()
    {
        var views = Workspace.VisibleViews().ToList();
        if (views.Count != 2) return (null, null);
        return (views[0].Image, views[1].Image);
    }

    /// <summary>刷新相对定向面板：配对状态、点对数、能不能解。</summary>
    private void UpdateRelativePanel()
    {
        if (RelativePanel is null) return;

        var (left, right) = RelativeImages();
        RelativePairText.Text = DescribeRelativeSide("左", left) + "\n" + DescribeRelativeSide("右", right);

        if (left is null || right is null)
        {
            RelativeHint.Text = "相对定向要两张片：把左片拖进左窗、右片拖进右窗，然后在两张片上各点一次同名像点。";
            RelativeSolveButton.IsEnabled = false;
            return;
        }

        var prep = RelativeOrientationTask.Prepare(left, right, _project);
        RelativeSolveButton.IsEnabled = prep.Ready;

        var pending = _relLeft is null && _relRight is null
            ? ""
            : "\n\t正在配：" + (_relLeft is null ? "左片未点" : "左片已点") + "、" +
              (_relRight is null ? "右片未点" : "右片已点");

        RelativeHint.Text = prep.Ready
            ? $"已有 {prep.Count} 对同名点，可以解算。再多点几对会更稳（多余观测越多，粗差越容易被发现）。"
            : prep.Message + pending;
    }

    private static string DescribeRelativeSide(string slot, ImageItem? image)
    {
        if (image is null) return $"{slot} —　无影像";

        var flags = (image.Interior is { IsValid: true } ? "内✓" : "内✗");
        return $"{slot} {image.Id}　{flags}";
    }

    /// <summary>用户在一个视窗里选中/新建了像点。判断属于左窗还是右窗，凑够一对就落成同名点。</summary>
    private void HandleRelativePick()
    {
        if (_relPicking) return;
        if (_moduleName != "相对定向") return;
        if (Workspace.ActiveView is not { } view) return;
        if (view.SelectedPoint is not { } point || view.Image is not { } image) return;

        var views = Workspace.VisibleViews().ToList();
        if (views.Count != 2) return;

        var (left, right) = (views[0].Image, views[1].Image);
        if (left is null || right is null || left.Id == right.Id) return;

        // 已经属于某个点对的点不再参与配对：它该由点表来管，再点一次只会让人以为配错了
        if (point.IsBound && point.BoundTiePointId is not null)
        {
            DiagSummary.Text = $"! {point.Id} 已属于同名点 {point.BoundTiePointId}；换个位置点新的点";
            DiagSummary.Foreground = WarnBadge;
            return;
        }

        _relPicking = true;
        try
        {
            if (view == views[0])
            {
                if (!ReferenceEquals(_relLeft, point)) RemovePickPoint(_relLeftImage, _relLeft);
                _relLeft = point;
                _relLeftImage = image;
            }
            else if (view == views[1])
            {
                if (!ReferenceEquals(_relRight, point)) RemovePickPoint(_relRightImage, _relRight);
                _relRight = point;
                _relRightImage = image;
            }
            else
            {
                return;
            }

            TryCommitTiePoint();
        }
        finally
        {
            _relPicking = false;
        }

        UpdateRelativePanel();
    }

    /// <summary>两边都点齐了就把它们绑成一对同名点，并清空待配状态准备下一对。</summary>
    private void TryCommitTiePoint()
    {
        if (_relLeft is not { } leftPoint || _relRight is not { } rightPoint) return;
        if (_relLeftImage is null || _relRightImage is null) return;

        var tieId = _project.NextTiePointId();

        leftPoint.BoundTiePointId = tieId;
        rightPoint.BoundTiePointId = tieId;
        _project.TiePoints.Add(new TiePoint { Id = tieId });

        _relLeft = null;
        _relRight = null;
        _relLeftImage = null;
        _relRightImage = null;

        _selectedTieId = tieId;
        foreach (var view in Workspace.AllViews()) view.RefreshPoints();
        RefreshTieTable();
        BottomTabsMeasure.IsChecked = true;
    }

    /// <summary>取消正在配的那一对：连同影像上那两个临时点一起收走。</summary>
    private void CancelRelativePair()
    {
        var left = _relLeft;
        var right = _relRight;
        var leftImage = _relLeftImage;
        var rightImage = _relRightImage;

        _relLeft = null;
        _relRight = null;
        _relLeftImage = null;
        _relRightImage = null;

        _relPicking = true;
        try
        {
            RemovePickPoint(leftImage, left);
            RemovePickPoint(rightImage, right);
        }
        finally
        {
            _relPicking = false;
        }

        UpdateRelativePanel();
    }

    private bool HasPendingRelativePair => _relLeft is not null || _relRight is not null;

    private void TieGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TieGrid.SelectedItem is TieRow { Source: { } tie }) _selectedTieId = tie.Id;
    }

    /// <summary>右键「在两张片上选中」：把这一对的两个像点都选中，方便核对点位。</summary>
    private void TieLocate_Click(object sender, RoutedEventArgs e)
    {
        if (TieGrid.SelectedItem is not TieRow { Source: { } tie }) return;

        var members = _project.TieMembers(tie.Id);
        foreach (var (_, point) in members) Workspace.SelectPoint(point);
        UpdateRelativePanel();
    }

    private void DeleteSelectedTie_Click(object sender, RoutedEventArgs e)
    {
        if (TieGrid.SelectedItem is not TieRow { Source: { } tie }) return;
        DeleteTiePoint(tie);
    }

    /// <summary>删掉一对同名点：点号本身、它名下的像点、以及影像上的标记一起走。</summary>
    private void DeleteTiePoint(TiePoint tie)
    {
        var members = _project.TieMembers(tie.Id);

        _relPicking = true;
        try
        {
            foreach (var (image, point) in members)
            {
                image.Points.Remove(point);
                foreach (var view in Workspace.AllViews())
                {
                    if (ReferenceEquals(view.SelectedPoint, point)) view.Select(null);
                }
            }

            foreach (var view in Workspace.AllViews()) view.RefreshPoints();
            _project.TiePoints.Remove(tie);
        }
        finally
        {
            _relPicking = false;
        }

        if (_selectedTieId == tie.Id) _selectedTieId = null;

        RefreshTieTable();
        UpdateRelativePanel();

        DiagSummary.Text = $"✓ 已删掉同名点 {tie.Id}（{members.Count} 个像点）";
        DiagSummary.Foreground = OkBadge;
    }

    /// <summary>清空本像对的同名点。与本像对无关的点对不动——它们属于别的像对。</summary>
    private void ClearRelativeTies_Click(object sender, RoutedEventArgs e)
    {
        var (left, right) = RelativeImages();
        if (left is null || right is null) return;

        var doomed = _project.TiePoints
            .Where(t =>
            {
                var members = _project.TieMembers(t.Id);
                return members.Any(m => m.Image.Id == left.Id || m.Image.Id == right.Id);
            })
            .ToList();

        if (doomed.Count == 0)
        {
            DiagSummary.Text = "本像对还没有同名点。";
            DiagSummary.Foreground = DimBadge;
            return;
        }

        _relPicking = true;
        try
        {
            foreach (var tie in doomed)
            {
                foreach (var (_, point) in _project.TieMembers(tie.Id))
                {
                    foreach (var view in Workspace.AllViews())
                    {
                        if (ReferenceEquals(view.SelectedPoint, point)) view.Select(null);
                    }
                }
            }
        }
        finally
        {
            _relPicking = false;
        }

        foreach (var tie in doomed) DeleteTiePointQuiet(tie);

        _selectedTieId = null;
        RefreshTieTable();
        UpdateRelativePanel();

        DiagSummary.Text = $"✓ 已清空本像对的 {doomed.Count} 对同名点";
        DiagSummary.Foreground = OkBadge;
        BottomTabsMeasure.IsChecked = true;
    }

    /// <summary>清空时用的删除：不逐条刷界面，也不逐条报诊断——最后统一说一句。</summary>
    private void DeleteTiePointQuiet(TiePoint tie)
    {
        foreach (var (image, point) in _project.TieMembers(tie.Id))
        {
            image.Points.Remove(point);
        }
        _project.TiePoints.Remove(tie);
    }

    /// <summary>重建点表里的同名点表：只列与当前像对有关的点对。</summary>
    private void RefreshTieTable()
    {
        if (TieGrid is null) return;

        var (left, right) = RelativeImages();
        var rows = new List<TieRow>();

        foreach (var tie in _project.TiePoints)
        {
            var members = _project.TieMembers(tie.Id);
            if (members.Count == 0) continue;

            var lm = left is null
                ? default
                : members.FirstOrDefault(m => m.Image.Id == left.Id);
            var rm = right is null
                ? default
                : members.FirstOrDefault(m => m.Image.Id == right.Id);

            // 与本像对无关的点对不进这张表：表是给当前这一步看的
            if (left is not null && right is not null && lm.Image is null && rm.Image is null) continue;

            rows.Add(TieRow.From(tie, lm, rm, left, right));
        }

        RefillTable(TieGrid, rows, r => r.Id == _selectedTieId);

        var ready = left is not null && right is not null;
        MeasureSummary.Text = _project.TiePoints.Count == 0
            ? "点表 · 同名点：还没有。在左右两张片上各点一次同名像点，就攒成一对"
            : $"点表 · 同名点 {_project.TiePoints.Count} 个（本像对 {rows.Count} 对）"
              + (ready ? "" : "　把两张片放进左右窗才能看到本像对的点对");
    }

    private void SolveRelative_Click(object sender, RoutedEventArgs e) => SolveRelativeNow();

    /// <summary>跑一次相对定向，把结果摆到「结果残差」页。</summary>
    internal RelativeRun SolveRelativeNow()
    {
        var (left, right) = RelativeImages();
        var run = RelativeOrientationTask.Run(_project, left, right);

        if (!run.Success)
        {
            ShowResult(FailTable($"相对定向 · {left?.Id ?? "—"} → {right?.Id ?? "—"}", run.Message));
            GateBlocked("相对定向", run.Message);
            return run;
        }

        var table = RelativeResultTable(run);
        var r = run.Result!;

        LogSolve("相对定向", $"{left?.Id} → {right?.Id}", true,
            $"{r.Iterations} 轮　{r.UsedCount}/{run.Pairs.Count} 对　σ₀ {r.Sigma0Mm:0.0000} mm　" +
            $"q_rms {r.QRmsMm:0.0000} mm　条件数 {r.Condition:0.0E+0}　冻结 {GaugeName(r.Gauge)}");

        if (run.Warnings.Count > 0)
        {
            // 提醒的全文进日志：结果页只留「是什么」，解释摆在那里会盖住表
            foreach (var warning in run.Warnings) Log("!", warning);
            DiagSummary.Text = $"! 相对定向完成，但有 {run.Warnings.Count} 条提醒（见日志）";
            DiagSummary.Foreground = WarnBadge;
        }
        else
        {
            DiagSummary.Text = "✓ 相对定向完成";
            DiagSummary.Foreground = OkBadge;
        }

        ShowResult(table);

        RefreshTieTable();
        UpdateRelativePanel();
        RefreshTree();
        return run;
    }
}
