using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Photogrammetry.App;

/// <summary>
/// 「日志」页：解算过程记在这里，不占结果页的地方。
///
/// <para>
/// 分工是这样定的：<b>结果页</b>只放"解出来的是什么"（元素与逐点残差），
/// <b>诊断页</b>只放"要不要你处理"（失败原因、缺什么、哪张片有问题），
/// <b>日志</b>放过程——什么时候、对哪个对象、迭代几次、σ₀ 多少、条件数多大。
/// 过程信息摆在结果页上，人会把它当成结果的一部分去引用；反过来，失败原因只沉在日志里，
/// 又会没人看见。所以按"要不要采取行动"划线。
/// </para>
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<string> _log = new();

    /// <summary>给离屏渲染工具读的日志行。</summary>
    internal IReadOnlyList<string> LogLines => _log;

    /// <summary>记一行日志。<paramref name="level"/> 用 OK / ! / ✗ 这类短标记，与诊断页的口气一致。</summary>
    private void Log(string level, string message)
    {
        if (LogList is null) return;

        var line = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  [" +
                   level + $"]  {message}";

        _log.Add(line);
        if (LogList.ItemsSource is null) LogList.ItemsSource = _log;

        // 日志只往长了长，不自动滚到底就等于没写
        LogPanel?.ScrollToEnd();
    }

    /// <summary>一次解算的流水：对象、迭代、精度、病态程度。成功失败都记。</summary>
    private void LogSolve(string what, string target, bool ok, string detail)
    {
        Log(ok ? "OK" : "✗", $"{what}{(ok ? "完成" : "未解出")}　{target}　{detail}");
    }

    /// <summary>
    /// 前置条件没满足（没做内定向就去相对定向这一类）：记日志、把底部状态条标红，
    /// 并<b>切到日志页签</b>。
    ///
    /// <para>为什么不弹窗而切页签：各模块现在只显示自己的东西，缺的那一步往往不在当前页上，
    /// 只改一行状态文字等于没提醒；而弹窗会把同一句话挡在屏幕中央，连着点几次就得关几次。</para>
    /// </summary>
    private void GateBlocked(string step, string message)
    {
        Log("✗", $"{step}被前置条件拦住：{message}");
        DiagSummary.Text = "✗ " + message;
        DiagSummary.Foreground = ErrBadge;
        if (BottomTabsLog is not null) BottomTabsLog.IsChecked = true;
    }
}
