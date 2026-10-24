using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 给离屏渲染工具 <c>src/Snap</c> 的入口登记处。
///
/// <para><b>为什么需要这张表。</b>Snap 要重放界面：载入工程、切模块、刺点、解算、出图。
/// 这些动作在界面上都走<b>私有</b>的点击处理器（先弹文件对话框，所以工具没法直接用），
/// 于是每个动作在窗口里都有一个「跳过对话框」的孪生方法。它们长得和业务方法一样，
/// 时间一长就分不清哪个能改、哪个改了会断掉离屏检查。</para>
///
/// <para><b>怎么区分的。</b>全部标 <c>internal</c>，并在 <c>App.csproj</c> 里开
/// <c>InternalsVisibleTo("Snap")</c>。<c>MainWindow</c> 对外的 public 只剩构造函数——
/// 这是个应用程序集，本来就不该有对外 API。谁想跨程序集用这些入口，先得改
/// <c>csproj</c>，编译器会把这件事摆到台面上，不会悄悄发生。</para>
///
/// <para><b>加新入口之前先问一句</b>：界面上的按钮能不能直接调私有处理器？
/// 能，就别往这张表里加。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 当前工程的只读入口。Snap 用它读控制点、影像与结果，替代原来
    /// <c>GcpCount</c> / <c>ControlPoints</c> / <c>CurrentProject</c> 三种写法。
    /// </summary>
    internal Project CurrentProject => _project;

    // ── 入口全表 ────────────────────────────────────────────────────────────
    //
    // 成员                        所在文件                  做什么
    // ───────────────────────────────────────────────────────────────────────
    // OpenProjectAsync            MainWindow.xaml.cs       读 .pht、解析影像路径、填影像库与数据树
    // SelectModuleForTooling      MainWindow.Modules.cs    按模块名选中，并自动切到包含它的作业方法
    // LoadCameraFrom              MainWindow.Interior.cs   读相机检校文件并挂到当前影像
    // LoadControlPointsFrom       MainWindow.Gcp.cs        读控制点文件、填点表、扫放大影像
    // BindGcpAt                   MainWindow.Gcp.cs        把一个控制点刺到指定像素（跳过鼠标）
    // SelectGcp                   MainWindow.Gcp.cs        点表里选中一行（触发参考影像与刺点模式）
    // SolveResectionNow           MainWindow.Resection.cs  跑一次后方交会并刷新点表与残差页
    // CurrentProject              本文件                    读当前工程
    //
    // 只有最后一条住在本文件里，其余七条留在它们各自的职责文件内：这些方法要碰
    // x:Name 字段和私有状态，挪出来只会把两份代码拉远，收益只有「好找」，
    // 而上面这张表给的是同一份地图。
    //
    // Snap 用得着、但走 Window 基类的成员（Show / Close / UpdateLayout / Dispatcher /
    // Content）不在此列。
}
