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
│   ├── FiducialEditorWindow.xaml(.cs)  框标坐标的手工录入与改正
│   └── MainWindow/                五区骨架，按职责拆成 7 个 partial
│       ├── MainWindow.xaml          界面骨架（StartupUri 指向这里）
│       ├── MainWindow.xaml.cs       333 行　外壳：菜单、工程读写、视图工具条、状态栏、底部页签
│       ├── MainWindow.Interior.cs   520 行　内定向
│       ├── MainWindow.Gcp.cs        506 行　控制点、刺点、放大影像
│       ├── MainWindow.Resection.cs  277 行　后方交会
│       ├── MainWindow.Tree.cs       202 行　数据树
│       ├── MainWindow.Library.cs    155 行　影像库
│       └── MainWindow.Modules.cs    133 行　作业方法与模块切换
├── Core/                           数据适配与流程控制（不含任何界面依赖，也不含平差计算）
│   ├── Coordinates.cs              四种坐标类型 + 像素↔像平面换算 + 地面轴序（手性判定、换轴）
│   ├── Model.cs                    工程实体：相机 / 框标 / 影像 / 控制点 / 像点 / 像对 / 模型 / 结果
│   ├── ProjectFile.cs              工程索引读写（原子写、备份轮转、相对路径）
│   ├── CameraFile.cs               相机检校文件读取（E-FOTO .cmr / CSV）
│   ├── ControlPointFile.cs         控制点文件读取（RTF / CSV）+ 放大影像发现
│   ├── FiducialHelper.cs           框标方位描述与对称性判断
│   ├── NativeAlgorithms.cs         原生算法的 P/Invoke 封装 + 数据适配（判手性、换轴、结果过闸门）
│   └── Tasks/
│       └── ResectionTask.cs        后方交会流程：收集观测 → 判手性 → 换轴 → 解算 → 写回，无界面依赖
└── Snap/                           开发工具：离屏渲染与自检（不参与解决方案构建）
```

现状的耦合点与目标结构（`Core/Tasks/`、`App/Services/`、`MainWindow` 拆 partial）见
[重构计划](../Docs/重构计划.md)。那份文档也是重构期间的验收基线。

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

M1–M4 完成，M5 的前方交会 DLL 与七段验收完成，界面已按三种作业方法分流。
当前阶段是**结构重构**，期间不进新功能——计划与回归基线见[重构计划](../Docs/重构计划.md)。

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
| `--selftest` `--probe` `--control` `--fiducial` | 四套自检，不截图 |

改过 `App` 之后必须重新构建 `Snap`，否则跑的还是旧 DLL——`--no-build` 不会把新的 `App.dll` 复制过去，症状是**截图还是旧界面，而且不报错**。

## 测试数据

`Data/sim/` 下是脚本生成的合成数据（Python 3.10 + Pillow），带真值，用于手动验收。见 [Data/sim/README.md](../Data/sim/README.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [路线图](../Docs/路线图.md) | 里程碑、状态、验收、数据策略（进度唯一真源） |
| [重构计划](../Docs/重构计划.md) | 耦合点清单、分层目标、分步计划、回归基线 |
| [界面设计规范](../Docs/界面设计规范.md) | 坐标、数据模型、界面结构、交互、校验、工程文件格式、ADR |
| [算法笔记](../Docs/算法笔记.md) | 算法推导、数字算例、自检方法 |
