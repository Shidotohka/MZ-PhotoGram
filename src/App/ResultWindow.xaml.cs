using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 结果与残差的独立窗口：<b>只看当前这一步</b>的结果。
///
/// <para><b>为什么按模块划作用域、不做一个全工程的结果列表。</b>模型坐标与地面坐标、
/// 相对定向的 q 与后方交会的 Vx 并排放在一张列表里，比着比着就会把两套口径混在一起；
/// 左侧那一栏还会吃掉表格的宽度。结果本身就分步：哪一步的结果，回主窗切到那一步再看。
/// 同一个模块对多张片/多个像对解过时，用「对象」下拉在本步之内切换。</para>
///
/// <para><b>非模态、单实例。</b>非模态是为了「看残差 → 找到偏大的点 → 回图上重刺」
/// 这条循环不被锁住；单实例是规模问题——6 个模块各开一个，很快就管不过来。</para>
///
/// <para><b>内容只有一套排版。</b>列名随模块变、行高与警示色只有一份，与主窗的点表
/// 共用同一套样式（ADR-20 修订、ADR-25）。</para>
/// </summary>
public partial class ResultWindow : Window
{
    private readonly ResultStore _store;
    private readonly Func<SolveResult, bool>? _accept;
    private readonly Func<SolveResult, bool>? _discard;
    private bool _loading;

    /// <summary>解算后是否自动打开窗口。会话内有效：关掉工程不该把用户的偏好一起清掉。</summary>
    public static bool AutoOpen { get; set; } = true;

    /// <summary>上次关闭时的尺寸。单实例窗口关掉再开，别每次都跳回默认大小。</summary>
    private static Size? _lastSize;

    /// <summary>这个窗口现在管哪一步的结果。Snap 用它验「解完跟到那一步」。</summary>
    public ResultKind Scope { get; private set; }

    /// <param name="accept">接受：由主窗负责写工程、记日志、刷新界面。返回值表示做成了没有。</param>
    /// <param name="discard">不采纳：由主窗负责从集合里删掉并记一行日志。</param>
    public ResultWindow(ResultStore store, ResultKind scope,
        Func<SolveResult, bool>? accept = null, Func<SolveResult, bool>? discard = null)
    {
        InitializeComponent();

        _store = store;
        _accept = accept;
        _discard = discard;
        AutoOpenCheck.IsChecked = AutoOpen;

        ShowKind(scope, store.LatestOf(scope));
    }

    /// <summary>
    /// 把窗口切到某一步：列该步的全部结果，选中 <paramref name="select"/>（不给就选最近解出的那份）。
    /// 主窗在「解算完」「换工程」「按结果按钮」三处调它。
    /// </summary>
    public void ShowKind(ResultKind kind, SolveResult? select)
    {
        Scope = kind;
        Title = $"结果与残差 · {ResultKinds.Label(kind)}";

        var items = _store.Of(kind);

        _loading = true;
        ObjectBox.ItemsSource = items;
        _loading = false;

        // 只有一份时不摆下拉：一个只有一项的下拉框只会让人以为还能切到别处
        ObjectPanel.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ObjectBox.IsEnabled = items.Count > 1;
        StoreSummary.Text = items.Count == 0
            ? $"{ResultKinds.Label(kind)}还没有结果"
            : $"{ResultKinds.Label(kind)} · {items.Count} 份（会话内保留）";

        var target = select is not null && items.Contains(select) ? select : _store.LatestOf(kind);
        if (target is not null && items.Contains(target)) ObjectBox.SelectedItem = target;
        else ObjectBox.SelectedIndex = items.Count > 0 ? 0 : -1;

        ShowSelected();
    }

    private void Object_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    /// <summary>把选中的那一份摆到两张表上。没有选中就退回空状态。</summary>
    private void ShowSelected()
    {
        if (_loading) return;

        if (ObjectBox.SelectedItem is not SolveResult result)
        {
            ShowEmpty();
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        // 标题只写「任务 · 对象」；成败与时刻不在标题里（ADR-21）
        DetailTitle.Text = result.Title;
        DetailTitle.Foreground = result.Failed
            ? (Brush)FindResource("ErrBrush")
            : (Brush)FindResource("TextBrush");

        ResultGrid.ItemsSource = result.Items;

        BuildResidualColumns(result.Headers);
        ResidualGrid.ItemsSource = result.Rows;

        // 没有逐点残差（解算没通过、或参数式内定向）就把右表收起来，把宽度让给元素表
        var hasResiduals = result.Headers.Count > 0;
        ResidualGrid.Visibility = hasResiduals ? Visibility.Visible : Visibility.Collapsed;
        ColumnSplitter.Visibility = hasResiduals ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(ResultGrid, hasResiduals ? 1 : 3);

        NoteText.Text = result.Note;
        NoteText.Foreground = result.NoteWarn
            ? (Brush)FindResource("ErrBrush")
            : (Brush)FindResource("DimBrush");
        NoteText.Visibility = string.IsNullOrEmpty(result.Note)
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateActions(result);
    }

    /// <summary>
    /// 两个判决按钮与徽标的状态。规矩（ADR-26）：
    /// 失败的结果不能接受（没解出来就没有可存的数）；已入账的两颗都禁用（要改就重解）；
    /// 没有可入账内容的结果（`OnAccept` 为 null，比如前方交会）也不能接受。
    /// </summary>
    private void UpdateActions(SolveResult result)
    {
        var accepted = result.State == ResultState.Accepted;

        AcceptButton.IsEnabled = !accepted && !result.Failed && result.OnAccept is not null;
        DiscardButton.IsEnabled = !accepted;

        StateBadge.Visibility = Visibility.Visible;
        StateBadgeText.Text = result.Failed
            ? "未解出"
            : accepted ? "已入账" : "待定 · 未入账";
        StateBadgeText.Foreground = (Brush)FindResource(
            result.Failed ? "ErrBrush" : accepted ? "OkBrush" : "WarnBrush");
    }

    private void ShowEmpty()
    {
        DetailTitle.Text = string.Empty;
        ResultGrid.ItemsSource = null;
        ResidualGrid.ItemsSource = null;
        ResidualGrid.Columns.Clear();
        ResidualGrid.Visibility = Visibility.Collapsed;
        ColumnSplitter.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(ResultGrid, 3);

        NoteText.Text = string.Empty;
        NoteText.Visibility = Visibility.Collapsed;

        StateBadge.Visibility = Visibility.Collapsed;
        AcceptButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;

        EmptyText.Text = $"{ResultKinds.Label(Scope)}还没有结果\n\n" +
                         "解算之后结果会留在这里，换到别的步骤也不会被顶掉。";
        EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 残差表的列按 <paramref name="headers"/> 现生成。第一列定宽（点号，宽度跳来跳去最难读），
    /// 中间按内容自适应，最后那一列「说明」吃掉剩下的宽度。
    /// </summary>
    private void BuildResidualColumns(IReadOnlyList<string> headers)
    {
        ResidualGrid.Columns.Clear();

        for (var i = 0; i < headers.Count; i++)
        {
            var column = new DataGridTextColumn
            {
                Header = headers[i],
                Binding = new Binding($"Cells[{i}]") { Mode = BindingMode.OneWay },
                MinWidth = 76,
            };

            column.Width = i == 0
                ? new DataGridLength(96)
                : i == headers.Count - 1 && headers.Count > 2
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : new DataGridLength(1, DataGridLengthUnitType.Auto);

            ResidualGrid.Columns.Add(column);
        }
    }

    // ---------- 工具条 ----------

    private void AutoOpen_Changed(object sender, RoutedEventArgs e) =>
        AutoOpen = AutoOpenCheck.IsChecked == true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 接受：交给主窗写工程 + 记日志，然后原地刷新（徽标变「已入账」，两颗按钮禁用）。
    /// 关窗不算判决——待定的结果留着，下次打开这个窗口还能接受（ADR-26 ⑤）。
    /// </summary>
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectBox.SelectedItem is not SolveResult result) return;
        if (_accept?.Invoke(result) != true) return;
        ShowKind(Scope, result);
    }

    /// <summary>不采纳：丢掉这份，选中落到同一对象的其余份（通常就没有了）。</summary>
    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectBox.SelectedItem is not SolveResult result) return;
        if (_discard?.Invoke(result) != true) return;
        ShowKind(Scope, null);
    }

    /// <summary>
    /// 复制当前这份结果：制表符分隔，表头齐全，粘进 Excel 或报告里能直接对齐。
    /// 只在「看一眼、抄几个数」这一步省事——真正的报告导出是 M8 的事。
    /// </summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectBox.SelectedItem is not SolveResult result) return;

        var text = new StringBuilder();
        text.AppendLine(result.Title);
        text.AppendLine($"{result.KindLabel}　{result.TargetId}　{result.SolvedAt:yyyy-MM-dd HH:mm:ss}" +
                        (result.Failed ? "　未解出" : ""));
        text.AppendLine();

        text.AppendLine(string.Join("\t", "项目", "符号", "数值", "单位", "说明"));
        foreach (var row in result.Items)
        {
            text.AppendLine(string.Join("\t", row.Name, row.Symbol, row.Value, row.Unit, row.Note));
        }

        if (result.Headers.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(string.Join("\t", result.Headers));
            foreach (var row in result.Rows)
            {
                text.AppendLine(string.Join("\t", row.Cells));
            }
        }

        if (!string.IsNullOrEmpty(result.Note))
        {
            text.AppendLine();
            text.AppendLine(result.Note);
        }

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占着是常事，不值得为它弹窗打断作业
        }
    }

    // ---------- 尺寸 ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        // 只记尺寸、不记位置：主窗可能已经换了显示器，位置由 CenterOwner 重新给
        if (_lastSize is { } size && size.Width >= MinWidth && size.Height >= MinHeight)
        {
            Width = size.Width;
            Height = size.Height;
        }

        base.OnSourceInitialized(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal) _lastSize = new Size(Width, Height);
        base.OnClosing(e);
    }
}
