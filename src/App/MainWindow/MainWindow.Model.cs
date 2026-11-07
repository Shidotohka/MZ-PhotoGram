using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

namespace Photogrammetry.App;

/// <summary>
/// 模型交会：把控制点在两片上的像点交会成模型坐标。
///
/// <para><b>为什么控制点刺在这里</b>：绝对定向要的输入就是"每个控制点一个模型坐标"，
/// 而那正是这一步的产物。控制点不是从匹配点云里挑出来的，它有名字、有地面坐标，
/// 名字由刺点给出——所以刺点天然属于模型交会，而不是后方交会。</para>
///
/// <para><b>与后方交会的边界</b>：这里只用「控制点导入」和「刺点」这两件通用设施
/// （都在 <c>MainWindow.Gcp.cs</c>，与后方交会无关），<b>不调用</b>后方交会的面板、
/// 解算或残差表。工作区用自己的形态：左右两张像片，点哪张就在哪张上刺。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>上一次求出的模型点（按控制点号索引）。刺点变化后不清空——它是快照。</summary>
    private IReadOnlyDictionary<string, ModelPointObservation> _modelPoints =
        new Dictionary<string, ModelPointObservation>();

    /// <summary>模型交会面板里的一行。</summary>
    private sealed class ModelRow
    {
        public Gcp? Source { get; init; }
        public string Id { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string LeftPixel { get; init; } = string.Empty;
        public string RightPixel { get; init; } = string.Empty;
        public string ModelX { get; init; } = string.Empty;
        public string ModelY { get; init; } = string.Empty;
        public string ModelZ { get; init; } = string.Empty;
        public string Convergence { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>模型交会用的像对：就是两窗里看得见的那一对。</summary>
    private StereoPair? ModelPair()
    {
        var (left, right) = RelativeImages();
        if (left is null || right is null) return null;
        return _project.FindPair(left.Id, right.Id);
    }

    /// <summary>刷新模型交会面板：像对、能不能求。</summary>
    internal void UpdateModelPanel()
    {
        if (ModelPanel is null) return;

        var (left, right) = RelativeImages();
        if (left is null || right is null)
        {
            ModelPairText.Text = "左片 —　右片 —";
            ModelSolveButton.IsEnabled = false;
            ModelHint.Text = "模型交会要一对像片：把左片放左窗、右片放右窗。先解出相对定向，再选中控制点号在两张片上各点一次。";
            return;
        }

        var pair = _project.FindPair(left.Id, right.Id);
        ModelPairText.Text = $"{left.Id} × {right.Id}" +
                             (pair?.Relative is null ? "　未解相对定向" : "　相对定向已解");

        var outcome = ModelIntersectionTask.Run(_project, pair);
        var ready = outcome.Failure == ModelIntersectionFailure.None;

        ModelSolveButton.IsEnabled = ready;
        ModelHint.Text = ready
            ? $"两片都刺过点的控制点 {outcome.Observations.Count} 个（其中检查点 {outcome.Observations.Count(o => o.Type == GcpType.CheckPoint)} 个），可以求模型点。"
            : outcome.Message;
    }

    private void SolveModel_Click(object sender, RoutedEventArgs e) => SolveModelNow();

    /// <summary>求模型点，把结果摆到结果窗口，并填点表的模型坐标列。</summary>
    internal ModelIntersectionOutcome SolveModelNow()
    {
        var (left, right) = RelativeImages();
        var pair = ModelPair();
        var label = left is null || right is null ? "—" : $"{left.Id} × {right.Id}";

        var outcome = ModelIntersectionTask.Run(_project, pair);

        if (!outcome.Success)
        {
            _modelPoints = new Dictionary<string, ModelPointObservation>();
            ShowResult(FailTable(ResultKind.Model, label, $"模型交会 · {label}", outcome.Message));
            GateBlocked("模型交会", outcome.Message);
            RefreshModelTable();
            UpdateModelPanel();
            return outcome;
        }

        _modelPoints = outcome.Observations.ToDictionary(o => o.GcpId, o => o);

        foreach (var warning in outcome.Warnings) Log("!", warning);

        var worst = outcome.Observations.Min(o => o.ConvergenceAngleDeg);
        LogSolve("模型交会", label, true,
            $"{outcome.Observations.Count} 个控制点　交会角最差 {worst:0.00}°　" +
            $"（模型坐标，比例尺以基线长为单位）");

        DiagSummary.Text = outcome.Warnings.Count > 0
            ? $"! 模型交会完成，{outcome.Warnings.Count} 条提醒（见日志）"
            : $"✓ 模型交会完成　{outcome.Observations.Count} 个模型点";
        DiagSummary.Foreground = outcome.Warnings.Count > 0 ? WarnBadge : OkBadge;

        ShowResult(ModelResultTable(outcome));
        RefreshModelTable();
        UpdateModelPanel();
        return outcome;
    }

    private void ModelGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelGrid.SelectedItem is not ModelRow { Source: { } gcp }) return;

        // 选中点号 → 之后在任一张像片上点击都刺到这个点（左、右窗都生效）
        _activeGcpId = gcp.Id;
        Workspace.SetActiveGcp(gcp.Id);
    }

    /// <summary>重填模型交会点表：刺点状态是实时的，模型坐标是上次求解的快照。</summary>
    private void RefreshModelTable()
    {
        if (ModelGrid is null) return;

        var (left, right) = RelativeImages();
        var keep = _activeGcpId;

        var rows = _project.Gcps.Select(gcp =>
        {
            var onLeft = left?.FindGcpPoint(gcp.Id);
            var onRight = right?.FindGcpPoint(gcp.Id);
            var hasModel = _modelPoints.TryGetValue(gcp.Id, out var model);

            return new ModelRow
            {
                Source = gcp,
                Id = gcp.Id,
                Type = gcp.Type switch
                {
                    GcpType.CheckPoint => "检查点",
                    GcpType.Elevation => "高程点",
                    _ => "控制点",
                },
                LeftPixel = onLeft is null ? "未刺" : $"{onLeft.PixelX}, {onLeft.PixelY}",
                RightPixel = onRight is null ? "未刺" : $"{onRight.PixelX}, {onRight.PixelY}",
                ModelX = hasModel ? model!.Model.X.ToString("F5", CultureInfo.InvariantCulture) : "—",
                ModelY = hasModel ? model!.Model.Y.ToString("F5", CultureInfo.InvariantCulture) : "—",
                ModelZ = hasModel ? model!.Model.Z.ToString("F5", CultureInfo.InvariantCulture) : "—",
                Convergence = hasModel ? model!.ConvergenceAngleDeg.ToString("F2", CultureInfo.InvariantCulture) : "—",
                Note = onLeft is null || onRight is null
                    ? "两片都刺点才能交会"
                    : hasModel ? (model!.ConvergenceAngleDeg < 15.0 ? "交会角小，深度不可靠" : "")
                    : "已刺点，未求模型点",
            };
        }).ToList();

        RefillTable(ModelGrid, rows, r => r.Id == keep);

        MeasureSummary.Text = _project.Gcps.Count == 0
            ? "点表 · 模型坐标：尚未载入控制点文件（在右侧「载入控制点文件…」）"
            : $"点表 · 模型坐标 {_modelPoints.Count} 个"
              + $"　共 {_project.Gcps.Count} 个控制点"
              + "　（模型单位，比例尺以基线长为单位）";
    }
}
