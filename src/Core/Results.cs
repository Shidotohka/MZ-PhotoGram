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

    /// <summary>
    /// 这一步的结果依赖哪一步的成果。入账时要顺着链把没入账的前置一起带上
    /// （否则会出现"文件里有七参数、没有相对定向"这种自相矛盾的工程）。
    ///
    /// <para>返回 null 表示它不依赖前面任何一步的**成果块**：内定向是链头，前方交会只算临时点。
    /// 后方交会要内方位，相对定向要内方位，模型交会要相对定向，绝对定向要相对定向。</para>
    /// </summary>
    public static ResultKind? DependsOn(ResultKind kind) => kind switch
    {
        ResultKind.Resection => ResultKind.Interior,
        ResultKind.Relative => ResultKind.Interior,
        ResultKind.Model => ResultKind.Relative,
        ResultKind.Absolute => ResultKind.Relative,
        _ => null,
    };
}

/// <summary>一份结果在"入账"这件事上的状态。</summary>
public enum ResultState
{
    /// <summary>刚解出来，只在会话里。界面标「待定」，「接受」可按。</summary>
    Pending,

    /// <summary>已经写进工程（并保存过）。重开工程回来的结果都是这一档。</summary>
    Accepted,

    /// <summary>操作员否掉了。它已从集合里移除，只在日志里留一条。</summary>
    Discarded,
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

    /// <summary>
    /// 入账状态。解算完是 <see cref="ResultState.Pending"/>：它只在会话里，要操作员按「接受」
    /// 才写进工程（ADR-26）。重开工程时读回来的结果是 <see cref="ResultState.Accepted"/>。
    /// </summary>
    public ResultState State { get; set; } = ResultState.Pending;

    /// <summary>入账时刻。没入账为 null。</summary>
    public DateTime? AcceptedAt { get; set; }

    /// <summary>
    /// 入账动作：把这份结果的<b>数值</b>写进工程（`images[].interior`、`pairs[].absolute` …）。
    ///
    /// <para>由解算方在这一份结果生成时挂上来（那时它手里才有一手的结果对象）；
    /// 结果窗口的「接受」只负责调用它。为 null 表示"这份结果没有可入账的东西"——
    /// 例如失败的结果、或者前方交会那种只算临时点的步骤。</para>
    /// </summary>
    public Action? OnAccept { get; set; }

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

    /// <summary>
    /// 接受：把状态标成已入账。**写进工程不在这里做**——那一步要按 kind 落到
    /// `images[]`/`pairs[]` 的对应块上，是 App 层的事（`Core` 不认识工程文件的形状，
    /// 这里也不该认识）。这里只管"这份结果算数了"。
    /// </summary>
    public void Accept(SolveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.State == ResultState.Accepted) return;

        result.State = ResultState.Accepted;
        result.AcceptedAt = DateTime.Now;
    }

    /// <summary>
    /// 不采纳：从集合里移掉（"结果丢失"），状态留成 <see cref="ResultState.Discarded"/>
    /// 供调用方写日志。返回原先有没有（重复不采纳不算错，但别重复记日志）。
    /// </summary>
    public bool Discard(SolveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.State = ResultState.Discarded;

        var key = (result.Kind, result.TargetId);
        if (!_items.TryGetValue(key, out var current) || !ReferenceEquals(current, result)) return false;

        _items.Remove(key);
        _order.Remove(key);
        if (ReferenceEquals(Latest, result)) Latest = _order.Count > 0 ? _items[_order[^1]] : null;
        return true;
    }

    /// <summary>还没入账的份数（界面提示"有 N 份待定"用）。</summary>
    public int PendingCount => _order.Count(key => _items[key].State == ResultState.Pending);

    /// <summary>换工程时清空：上一份结果对着旧数据，留着必然被误读。</summary>
    public void Clear()
    {
        if (_order.Count == 0) return;

        _items.Clear();
        _order.Clear();
        Latest = null;
    }
}
