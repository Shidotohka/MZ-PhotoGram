using System;
using System.Collections.Generic;
using System.Linq;

namespace Photogrammetry.Core;

/// <summary>
/// 一份结果属于哪一步。窗口左列表按它分组，也是「同一份结果被重解时覆盖谁」的键的一半。
///
/// <para>为什么不直接存模块名字符串：模块名是界面上的按钮文字，改叫法就会把
/// 会话里的结果弄丢一半；这里枚举是数据，名字只在显示时映射（<see cref="ResultKinds.Label"/>）。</para>
/// </summary>
public enum ResultKind
{
    Interior,
    Resection,
    Intersection,
    Relative,
    Model,
    Absolute,
}

/// <summary>结果种类的显示名。只在这一处映射，界面与报告共用。</summary>
public static class ResultKinds
{
    public static string Label(ResultKind kind) => kind switch
    {
        ResultKind.Interior => "内定向",
        ResultKind.Resection => "后方交会",
        ResultKind.Intersection => "前方交会",
        ResultKind.Relative => "相对定向",
        ResultKind.Model => "模型交会",
        ResultKind.Absolute => "绝对定向",
        _ => kind.ToString(),
    };
}

/// <summary>元素表的一行：解出来的是什么。</summary>
public sealed class ResultRow
{
    public string Name { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    /// <summary>这一行的数不可信（诊断值、残差偏大）。界面上染成错误色。</summary>
    public bool Warn { get; init; }
}

/// <summary>
/// 残差表的一行。列数随模块变（内定向两列、后方交会四列、相对定向三列），
/// 所以用字符串数组而不是固定字段；列名与顺序由 <see cref="SolveResult.Headers"/> 定。
/// </summary>
public sealed class ResidualRow
{
    public string[] Cells { get; init; } = Array.Empty<string>();
    public bool Warn { get; init; }
}

/// <summary>
/// 一次解算的<b>快照</b>：算出来的是什么（元素）、每个点差多少（残差）、敢不敢用（页脚）。
///
/// <para>它不引用任何界面类型：左元素表 + 右残差表的排版由界面负责，
/// 报告的 HTML 也吃这同一份（M8）。</para>
///
/// <para><b>是快照，不是实时值</b>：改了观测（删点、补点）不会自动重算，
/// 要再按一次解算——没有人希望删一个点之后结论悄悄变了。</para>
/// </summary>
public sealed class SolveResult
{
    /// <summary>属于哪一步。</summary>
    public ResultKind Kind { get; init; }

    /// <summary>这份结果对的是谁：影像 Id、像对「左 / 右」、或「全部影像」。与 <see cref="Kind"/> 一起构成覆盖键。</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>标题只写「任务 · 对象」。成功与否、迭代几次这类过程信息归日志，不进标题（ADR-21）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>解算时刻。由 <see cref="ResultStore.Put"/> 在入店时补上。</summary>
    public DateTime SolvedAt { get; set; }

    /// <summary>整份结果不可信（没收敛、被判不采用）。窗口里标题染红，列表里写「未解出」。</summary>
    public bool Failed { get; init; }

    public List<ResultRow> Items { get; } = new();
    public List<string> Headers { get; } = new();
    public List<ResidualRow> Rows { get; } = new();

    /// <summary>页脚：口径与警告（敢不敢用这份结果）。比塞进表格更容易读。</summary>
    public string Note { get; set; } = string.Empty;
    public bool NoteWarn { get; set; }

    /// <summary>模块名（显示用）。</summary>
    public string KindLabel => ResultKinds.Label(Kind);

    /// <summary>左列表第二行：时刻（失败再多一句）。模块与对象在 <see cref="Title"/> 里，不在这里重复。</summary>
    public string Meta
    {
        get
        {
            var meta = SolvedAt.ToString("HH:mm:ss");
            return Failed ? meta + " · 未解出" : meta;
        }
    }

    public SolveResult Add(string name, string symbol, string value, string unit = "",
        string note = "", bool warn = false)
    {
        Items.Add(new ResultRow
        {
            Name = name, Symbol = symbol, Value = value, Unit = unit, Note = note, Warn = warn,
        });
        return this;
    }

    public SolveResult Head(params string[] headers)
    {
        Headers.AddRange(headers);
        return this;
    }

    public SolveResult Row(params string[] cells) => Row(false, cells);

    public SolveResult Row(bool warn, params string[] cells)
    {
        Rows.Add(new ResidualRow { Cells = cells, Warn = warn });
        return this;
    }
}

/// <summary>
/// 会话内的结果集合：<b>一份结果不再顶掉上一份</b>。
///
/// <para>这是这次改动的根：旧的做法是结果页上只有一个槽，第二步算完第一步就没了，
/// 想回看只能重算。现在按 <c>(模块, 对象)</c> 存，同一对重解时覆盖、不同对象并存。</para>
///
/// <para>为什么在 <c>Core</c> 而不是界面层：报告（M8）要在没有窗口的情况下读它生成 HTML，
/// 而它本身只是数据；窗口只是它的一个视图。</para>
///
/// <para><b>会话级</b>：不写进 <c>.pht</c>，重开工程清空。成果落盘是另一件事
/// （见《工程文件格式》§9.1 与 §12）。</para>
/// </summary>
public sealed class ResultStore
{
    private readonly Dictionary<(ResultKind Kind, string Target), SolveResult> _items = new();
    private readonly List<(ResultKind Kind, string Target)> _order = new();

    /// <summary>最近一次放入的结果。没有结果时为 null。</summary>
    public SolveResult? Latest { get; private set; }

    public int Count => _order.Count;

    /// <summary>全部结果，按第一次出现的顺序（即作业顺序）。同一对象重解不改变位置。</summary>
    public IReadOnlyList<SolveResult> All => _order.Select(key => _items[key]).ToList();

    /// <summary>放入一份结果。同一 <c>(模块, 对象)</c> 覆盖旧的，不堆重复。</summary>
    public void Put(SolveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.SolvedAt == default) result.SolvedAt = DateTime.Now;

        var key = (result.Kind, result.TargetId);
        if (!_items.ContainsKey(key)) _order.Add(key);
        _items[key] = result;
        Latest = result;
    }

    public SolveResult? Get(ResultKind kind, string targetId) =>
        _items.TryGetValue((kind, targetId), out var result) ? result : null;

    /// <summary>某一模块下有几份结果（同一模块可以对多张片、多个像对解算）。</summary>
    public int CountOf(ResultKind kind) => _order.Count(key => key.Kind == kind);

    /// <summary>某一模块的全部结果，按第一次出现的顺序。窗口的「对象」下拉吃它。</summary>
    public IReadOnlyList<SolveResult> Of(ResultKind kind) =>
        _order.Where(key => key.Kind == kind).Select(key => _items[key]).ToList();

    /// <summary>某一模块最近解出的那份（按解算时刻，不按放进来的顺序）。</summary>
    public SolveResult? LatestOf(ResultKind kind) =>
        Of(kind).OrderByDescending(result => result.SolvedAt).FirstOrDefault();

    /// <summary>换工程时清空：上一份结果对着旧数据，留着必然被误读。</summary>
    public void Clear()
    {
        if (_order.Count == 0) return;

        _items.Clear();
        _order.Clear();
        Latest = null;
    }
}
