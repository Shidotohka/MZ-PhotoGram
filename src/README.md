# 摄影测量工作台 · 源码

## 目录结构

```
src/
├── PhotogrammetryWorkbench.sln     解决方案（App + Core）
├── Directory.Build.props           公共编译设置
├── NuGet.config                    不引用外部包，保证离线可还原
├── App/                            WPF 界面层
│   ├── App.xaml / App.xaml.cs
│   ├── app.manifest                Per-Monitor V2 DPI 感知
│   ├── Themes/Theme.xaml           浅色调色板与控件样式（见规范 4.5）
│   ├── ViewTransform.cs            单个视窗的 (s, tx, ty)
│   ├── TreeNode.cs                 数据树的节点模型
│   ├── ImageView.xaml(.cs)         单个视窗：影像 + 变换 + 像点/框标取点与命中
│   ├── ImageWorkspace.xaml(.cs)    工作区四态：单图 / 双图 / 模型 / 报告
│   ├── ResultWindow.xaml(.cs)      结果与残差窗口（非模态、单实例、只显示当前模块；结果集合的视图）
│   ├── FiducialEditorWindow.xaml(.cs)  框标坐标的手工录入与改正
│   ├── Services/
│   │   └── ImageLibrary.cs           影像解码与缓存（只管路径 → 冻结位图）
│   ├── Format.cs                    数字格式化：Fixed / Signed / 小数位，一处收口
│   └── MainWindow/                五区骨架，按职责拆成 14 个 partial
│       ├── MainWindow.xaml          界面骨架（StartupUri 指向这里）
│       ├── MainWindow.xaml.cs       外壳：菜单、工程读写、视图工具条、状态栏、页签、快捷键
│       ├── MainWindow.Interior.cs   内定向（含框标表的行类型）
│       ├── MainWindow.Gcp.cs        控制点、刺点、放大影像
│       ├── MainWindow.Intersection.cs 前方交会：配对、解算、自动匹配
│       ├── MainWindow.Relative.cs   相对定向：同名点配对、解算、点表
│       ├── MainWindow.Results.cs    结果：各模块的「元素 + 残差」装配、结果集合与窗口入口
│       ├── MainWindow.Model.cs      模型交会
│       ├── MainWindow.Absolute.cs   绝对定向
│       ├── MainWindow.Tree.cs       数据树
│       ├── MainWindow.Modules.cs    作业方法与模块切换
│       ├── MainWindow.Library.cs    影像库：添加、去重 Id、载入工作区
│       ├── MainWindow.Resection.cs  后方交会（流程在 Core/Tasks，这里只剩显示）
│       ├── MainWindow.Log.cs        日志页
│       └── MainWindow.Tooling.cs    给 Snap 的 internal 入口登记表
├── Core/                           数据适配与流程控制（不含任何界面依赖，也不含平差计算）
│   ├── Coordinates.cs              四种坐标类型 + 像素↔像平面换算 + 地面轴序（手性判定、换轴）
│   ├── Results.cs                  结果集合：SolveResult（元素 + 残差 + 页脚）与 ResultStore（会话级留存）
│   ├── Model.cs                    工程实体：相机 / 框标 / 影像 / 控制点 / 像点 / 像对
│   ├── ProjectFile.cs              工程索引读写（原子写、备份轮转、相对路径）
│   ├── CameraFile.cs               相机检校文件读取（E-FOTO .cmr / CSV）
│   ├── ControlPointFile.cs         控制点文件解析（RTF / CSV）
│   ├── RtfText.cs                  RTF 取纯文本（够用的一档，不追求完整规范）
│   ├── ZoomImageIndex.cs           放大影像（点之记）目录发现与按点号取图
│   ├── FiducialHelper.cs           框标方位描述与对称性判断
│   ├── NativeAbi.cs                photogrammetry.dll 的托管侧 ABI：结构体布局 + DllImport，不含策略
│   ├── NativeAlgorithms.cs         原生算法的调用封装 + 数据适配（判手性、换轴、结果过闸门）
│   └── Tasks/
│       ├── ResectionTask.cs        后方交会流程：收集观测 → 判手性 → 换轴 → 解算 → 写回，无界面依赖
│       ├── IntersectionTask.cs     前方交会流程：查两张片的可用性 → 像素换像平面 → 交会 → 过闸门
│       └── RelativeOrientationTask.cs 相对定向流程：攒同名点对 → 闸门 → 解算 → 写回工程
└── Snap/                           开发工具：离屏渲染与自检（不参与解决方案构建）
```

现状的耦合点与目标结构（`Core/Tasks/`、`App/Services/`、`MainWindow` 拆 partial）见
[重构计划](../Docs/重构计划.md)。那份文档也是重构期间的验收基线。

**`App` 没有对外 API。** `MainWindow` 的 public 成员只有构造函数，其余 8 个给离屏工具用的入口
一律 `internal`，靠 `App.csproj` 里的 `InternalsVisibleTo("Snap")` 放行，全表与规则见
[`MainWindow.Tooling.cs`](App/MainWindow/MainWindow.Tooling.cs)。跨程序集想用这些入口，
先得改 `csproj`，编译器会把这件事摆到台面上。

**平差计算不在 `src/` 里。** 解共线方程、迭代、投影、精度评定都在顶层 [`native/`](../native/README.md)，
编译成 `photogrammetry.dll`，通过 C ABI 暴露，`Core` 用 P/Invoke 调用。

`Core` 做的是**数据适配与流程控制**：读文件、认格式、统一单位、判地面轴序并换轴、绑定点号、
把结果过闸门。这些都要"知道这份数据长什么样"，跟共线方程无关。判归属只问一句：
这段代码需不需要知道数据从哪来——需要留在 C#，不需要才下沉到 C++。边界见
[界面设计规范 §8.1](../Docs/界面设计规范.md)。

## 环境

- .NET SDK 10.0.301，目标框架 `net10.0-windows` / `net10.0`
- MSVC（Visual Studio 2022 Community）＋ CMake，构建 `native/`
- 托管侧不依赖任何 NuGet 包

选 `net10.0-windows` 而非 `net8.0-windows`：本机 8.0 引用包版本与 SDK 10 期望的不一致，会触发还原阶段联网下载。

## 构建与运行

```powershell
dotnet build src/App/App.csproj -c Debug
dotnet run --project src/App/App.csproj
```

多进程构建在受限环境下会静默失败（MSBuild 节点间通信被拦截），此时加 `-m:1`：

```powershell
dotnet build src/PhotogrammetryWorkbench.sln -c Debug -m:1
```

## 当前进度

**进度以 [Docs/路线图.md](../Docs/路线图.md) 为唯一真源。**

M1–M7、M9–M11 完成，两条作业方法在界面上都能出结果；下一步是 M8 报告。
**结构重构的六步已全部完成**，每步都要求回归基线逐字不变、截图逐字节相同——
计划、分步记录与验收基线见[重构计划](../Docs/重构计划.md)。

内定向的验收是在真实航摄数据上做的：E-FOTO Hammer 两片各点 8 个框标，δ 相差 0.004%，σ₀ 0.012–0.020 mm。基准见 [Data/real/README.md](../Data/real/README.md)。

## 视图变换约定

$$q = s\,p + t$$

由 `ViewTransform` 承载。**每个 `ImageView` 持有自己的实例，视窗之间互不影响。**

早期版本三个视窗共享同一个 `ViewTransform`，导致双图强制同步——这是缺陷，已修复。见[算法笔记 §3](../Docs/算法笔记.md)与规范 ADR-06。

锚定缩放：先求光标处的像点，改完比例后反解平移量，使该像点仍落在光标下。比例夹紧后必须用夹紧后的值重算倍数。

## 工程文件

`ProjectFile.Save/Load`：JSON 索引，只存影像路径与参数，不嵌入影像数据。
保存时把绝对路径换算成相对工程目录的路径（`/` 分隔）；写入走「临时文件 → 备份轮转 → 替换」，并用独占锁防止两个进程同时写。

自检（不启动界面）：

```powershell
dotnet run --project src/Snap/Snap.csproj -c Debug -- --selftest Design/sample-project.pht
```

## 离屏渲染

无需手动截图，直接把当前界面渲染成 PNG：

```powershell
dotnet run --project src/Snap/Snap.csproj -c Debug -- --out out.png --module 相对定向
dotnet run --project src/Snap/Snap.csproj -c Debug -- --out out.png --module 后方交会 `
    --image Design/sample-target.png --zoom 2.2
```

参数：

| 参数 | 用途 |
|---|---|
| `--out` | 输出 PNG 路径 |
| `--module` | 目标模块；会自动切到包含该模块的作业方法 |
| `--via` | 先切到另一个模块再回来，用于复现跨模块的状态残留 |
| `--image` `--loadsecond` `--zoom` `--points` | 视窗内容与缩放 |
| `--project` `--gcp` `--camera` | 载入工程 / 控制点 / 相机文件 |
| `--openmenu` `--tab` | 展开某个菜单、切底部页签 |
| `--selftest` `--probe` `--control` `--fiducial` `--intersect` `--relative` | 六套数值与闸门自检，不截图 |
| `--results` | 结果集合的覆盖语义（并存 / 覆盖 / 顺序 / 计数 / 事件 / 清空），不截图 |
| `--result-shot` | 把已打开的结果窗口单独渲染成 PNG（它不在主窗的渲染树里） |
| `--abi` | 打印 ABI 布局：每个结构体的尺寸、字段顺序与偏移，以及全部 DllImport 签名 |
| `--interior-demo` | 界面端到端：给当前影像装一组框标，按「解算内定向」，再检查结果页的行（配合 `--camera`） |
| `--relative-demo` | 界面端到端：两张片各点 12 对同名点 → 解算，再检查结果页的行与列（配合 `--image` + `--loadsecond`） |
| `--intersect-demo` | 界面端到端：给两张片装上一组已知外方位，各点一个同名点，按解算并检查结果页的行与列（配合 `--image` + `--loadsecond`） |

**动过 `NativeAbi.cs` 就必须跑 `--abi` 并跟改前对表。**封送布局错一个字段（类型、顺序、`SizeConst`）不会编译报错，只会静默算错。改前改后各存一份输出，把 `declaring=` 那一列剥掉再比——它只会因为类型换了宿主而变。

改过 `App` 之后必须重新构建 `Snap`，否则跑的还是旧 DLL——`--no-build` 不会把新的 `App.dll` 复制过去，症状是**截图还是旧界面，而且不报错**。

## 测试数据

`Data/sim/` 下是脚本生成的合成数据（Python 3.10 + Pillow），带真值，用于手动验收。见 [Data/sim/README.md](../Data/sim/README.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [路线图](../Docs/路线图.md) | 里程碑、状态、验收、数据策略（进度唯一真源） |
| [重构计划](../Docs/重构计划.md) | 耦合点清单、分层目标、分步计划、回归基线 |
| [界面设计规范](../Docs/界面设计规范.md) | 坐标、数据模型、界面结构、交互、校验、工程文件格式、ADR |
| [工程文件格式](../Docs/工程文件格式.md) | `.pht` 的字段字典、版本、写入与容错、手工编辑规则 |
| [算法笔记](../Docs/算法笔记.md) | 算法推导、数字算例、自检方法 |
| [相对定向算法](../Docs/相对定向算法.md) | 相对定向的推导、规准与初值、C++ 原生层设计与七段自检实测 |
| [外方位解错复盘](../Docs/外方位解错复盘.md) | 残差表怎么读、地面轴序为什么是镜像、一次真实解错的完整排查 |
