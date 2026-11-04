# native —— C++ 算法层

**只做纯计算。** 编译成 `photogrammetry.dll`，通过 **C ABI** 暴露给托管侧。

工具链：**MSVC（Visual Studio 2022）** + CMake。

---

## 输入约定（调用方负责）

DLL 假设收到的数据已经洗干净，自己不做任何适配：

- **右手系**：$X$ 指东、$Y$ 指北、$Z$ 向上。判手性、换轴是托管层的事。
- **单位统一**：像平面 mm、地面 m、角度弧度。
- **点号已对齐**：第 $i$ 个像点与第 $i$ 个地面点确实同一物点。

这里**不出现** `if (isMirrored) swap(x, y)` 这类针对特定数据源的补丁。共线方程只认数，不问这份控制点文件是哪种行业约定写出来的——那是 `src/Core` 的职责，边界见[界面设计规范 §8.1](../Docs/界面设计规范.md)。

反过来，因为信任调用方，上游判错时 DLL 察觉不到，只会照着一个镜像的输入解出一个「最不坏」的结果。所以托管侧的 VL-10 / VL-11 兜底闸门不能撤。

---

## 目录

```
native/
├── CMakeLists.txt
├── include/
│   └── photogrammetry.h           对外 C ABI，托管侧唯一依赖
├── src/
│   ├── internal.hpp               库内部声明
│   ├── geometry.hpp               3×3 线性代数与教材旋转矩阵，各算法共用
│   ├── linalg.hpp                 对称阵特征分解与求解（从 resection.cpp 提出来共用）
│   ├── error.cpp                  错误码、版本、pgm_last_error
│   ├── interior_orientation.cpp   内定向：六参数仿射最小二乘
│   ├── resection.cpp              空间后方交会：共线方程 + 迭代最小二乘
│   ├── forward_intersection.cpp   空间前方交会：三式最小二乘解投影系数
│   ├── relative_orientation.cpp   相对定向：共面条件迭代 + 规准 + 留一法粗差剔除
│   └── epipolar.cpp               核线几何：核线、核点、核面转角、点到核线距离
└── tests/
    ├── test_interior_orientation.cpp   读 Data/sim 的合成数据做验收
    ├── test_resection.cpp              后方交会自检：约定对表、雅可比、往返、噪声、退化
    ├── test_forward_intersection.cpp   前方交会自检：往返、与教材对表、旁向反证、精度
    ├── test_relative_orientation.cpp   相对定向自检：七段（含旁向 30%、纯旁向、粗差）
    └── test_epipolar.cpp               核线自检：八段（穿过同名点、过核点、重采样、退化）
```

$R$ 的约定只写一份，放在 `src/geometry.hpp`，各算法都 include 它。这份约定写歪了程序照样出数——上次外方位解错就栽在这类「两份约定」上。

后续算法按同样方式加文件：`absolute_orientation.cpp`（M7）。

---

## 构建

```powershell
cmake -S native -B native/build -G "Visual Studio 17 2022" -A x64
cmake --build native/build --config Release
```

产物：

```
native/build/bin/Release/photogrammetry.dll
native/build/bin/Release/pgm_test.exe
native/build/bin/Release/pgm_resection_test.exe
native/build/bin/Release/pgm_intersection_test.exe
native/build/bin/Release/pgm_relative_orientation_test.exe
native/build/bin/Release/pgm_epipolar_test.exe
```

`src/Directory.Build.props` 会把 DLL 复制到托管项目的输出目录。
**顺序不能反**：先编 native，再编托管，否则 P/Invoke 找不到 DLL。

---

## C ABI 约定

- 输入输出一律**固定布局结构体**，不含 STL 容器、不含指针图、不用 `bool`（用 `int32_t`）。
- 字符串用 `const char*`（UTF-8），由调用方持有，函数不保留引用。
- 输入缓冲区由调用方持有；**输出数组由调用方预分配**，长度在输入结构体里给出，不需要时传 `NULL`。
- 返回值 `0` 为成功，非 `0` 为错误码，配 `pgm_last_error()` 取描述（线程局部）。
- **C++ 异常不跨 ABI 边界**，接口内部统一 `catch(...)` 转错误码。

### 错误码

| 值 | 宏 | 含义 |
|---|---|---|
| 0 | `PGM_OK` | 成功 |
| 1 | `PGM_ERR_NULL_ARGUMENT` | 指针为空 |
| 2 | `PGM_ERR_TOO_FEW_POINTS` | 点数不足（内定向框标 < 4，后方交会控制点 < 4，相对定向同名点对 < 5） |
| 3 | `PGM_ERR_DEGENERATE` | 几何构型退化：近似共线、点重合、法方程秩亏 |
| 4 | `PGM_ERR_INVALID_ARGUMENT` | 参数非法、含非有限值、物点落在相机背后 |
| 5 | `PGM_ERR_INTERNAL` | 未预期异常 |
| 6 | `PGM_ERR_AXIS_SIGN` | $b_2 \ge 0$，y 轴符号异常；**结果仍写入 out**，供调用方诊断 |
| 7 | `PGM_ERR_NOT_CONVERGED` | 迭代到上限仍未收敛；**out 中是最后一次迭代值**，仅供诊断，不是解 |

---

## 现有接口：内定向

```c
int32_t pgm_fiducial_orientation(const pgm_fiducial_input* in,
                                 pgm_fiducial_output*      out);
```

模型：

$$x_m = a_0 + a_1 I + a_2 J,\qquad y_m = b_0 + b_1 I + b_2 J$$

实现要点：

1. **重心化**。像元坐标可达数千，$\sum I^2 \sim 10^7$，不重心化法方程病态。
2. **解耦**。$x$、$y$ 两组方程互不交叉，正规矩阵分块对角且两块相同，重心化后 3×3 退化为 $1 + 2\times2$。
3. **退化判据用相关系数** $\rho$，与影像尺寸无关：$|\rho| \ge 0.9999$ 拒绝。

算法推导见 [Docs/算法笔记.md](../Docs/算法笔记.md) 第 5 章。

---

## 现有接口：空间后方交会

```c
int32_t pgm_resection(const pgm_resection_input*  in,
                      pgm_resection_output*       out);

int32_t pgm_project_point(double f, double xs, double ys, double zs,
                          double phi, double omega, double kappa,
                          double X, double Y, double Z,
                          double* out_x_mm, double* out_y_mm);

int32_t pgm_resection_design(...);   /* 诊断用：输出设计矩阵与常数项 */
```

模型：

$$x_m=-f\frac{U}{W},\quad y_m=-f\frac{V}{W},\qquad
\begin{bmatrix}U\\V\\W\end{bmatrix}=R\begin{bmatrix}X-X_S\\Y-Y_S\\Z-Z_S\end{bmatrix},\quad
R=R_\kappa R_\omega R_\varphi$$

实现要点：

1. **旋转阵取教材形式**。$R_\varphi,R_\omega$ 是右手旋转阵的转置；换成常见形式角度会整体差一个号，而且往返检验查不出来（正投影与反解用的是同一个错 $R$）。自检程序拿教材展开式对表。
2. **列平衡**。地面坐标米、像点毫米、角度弧度，三套量纲进同一个法方程条件数会到 $10^6$；按列范数归一化后降到 $10^2$。
3. **角元素用左乘求导**。三个常量阵 $M_\varphi,M_\omega,M_\kappa$ 左乘即可，不必展开成三角式。
4. **初值**用四参数相似变换估计（近垂直摄影假设）；斜摄影由调用方给初值。
5. **阻尼**。整步若让残差平方和变大，逐次减半直到下降；初值不好时不至于发散。
6. **精度**：$\sigma_0=\sqrt{V^\top V/(2n-6)}$，参数中误差由 $\operatorname{diag}(N^{-1})$ 换回原量纲。

算法推导见 [Docs/算法笔记.md](../Docs/算法笔记.md) 第 6 章。

---

## 现有接口：空间前方交会

```c
int32_t pgm_forward_intersection(const pgm_intersection_input*  in,
                                 pgm_intersection_output*       out);
```

一次交会一个点：输入两张片的外方位元素（`pgm_camera_pose`）与一对同名像点，输出地面坐标、投影系数、交会角、基线、摄影距离、两条视线的最近距离、四个反投影残差与剩余上下视差、以及 $m_X/m_Y/m_Z$。

模型：

$$N_1\mathbf u_1-N_2\mathbf u_2=\mathbf B,\qquad
\mathbf u=R^\top\begin{bmatrix}x\\y\\-f\end{bmatrix},\qquad
\mathbf P=\tfrac12(\mathbf P_1+\mathbf P_2)$$

实现要点：

1. **三式最小二乘解 $N_1,N_2$，不用教材的 (1)(3) 两式**。教材那一步隐含「基线在航向」：旁向像对分母 $u_1w_2-u_2w_1$ 恒为零（实测 $0.000000\text{e}+00$），近旁向时分母只有 $-4$ 的量级，左片 $x$ 偏 0.01 mm 就让地面点跳 796 m。三式最小二乘的退化判据是 $\sin\theta\to0$，$\theta$ 就是交会角。航向像对上两解同值（差 $1.1\times10^{-13}$）。
2. **$\mathbf u=R^\top[x\ y\ -f]^\top$**，与教材前方交会那个 $R'$ 差一次转置。混用整体错一个符号且照样出数。
3. **精度用数值协方差**：$m$ 由 $\operatorname{Cov}(\mathbf P)=\sigma^2\mathbf J\mathbf J^\top$ 的对角线给出，$\mathbf J$ 中心差分求。不假设 $Z$ 是深度方向，换轴不会错。
4. **拒收两种情况**：两条视线平行（$\sin\theta<10^{-9}$，`PGM_ERR_DEGENERATE`）；投影系数为负即交会点落在相机后方（`PGM_ERR_INVALID_ARGUMENT`，与后方交会 $W\ge0$ 同等处理）。
5. **交会角 < 15° 的告警不在这里**。DLL 只在数学上解不出来时拒解，「这个解要不要接受」归托管层（VL-03），见[界面设计规范 §8.1](../Docs/界面设计规范.md)。

算法推导见 [Docs/算法笔记.md](../Docs/算法笔记.md) 第 7 章。

---

## 现有接口：相对定向

```c
int32_t pgm_relative_orientation(const pgm_relative_orientation_input*  in,
                                 pgm_relative_orientation_output*       out);

int32_t pgm_relative_orientation_design(...);   /* 诊断用：输出共面条件的误差方程 */
```

输入一串同名像点（左右片各一对像平面坐标 mm）与主距，输出 5 个相对方位元素、$\sigma_0$、$q_{\mathrm{rms}}$、条件数、实际冻结的基线分量（`gauge`），以及逐点的 $q$ 与剔除标记。

模型（共面条件，上下视差为观测残差）：

$$\begin{vmatrix}b_x&b_y&b_z\\u_1&v_1&w_1\\u_2&v_2&w_2\end{vmatrix}=0,\qquad
\mathbf u=R^\top\begin{bmatrix}x\\y\\-f\end{bmatrix}$$

实现要点：

1. **初值取平均视差方向**、解完按 $\sqrt{b_x^2+b_y^2+b_z^2}$ 规准。$|b|=1$ 是自由网的规准，模型比例尺由它定；绝对定向的 $\lambda$ 再拉回米制。
2. **教材形式（冻结 $b_x=B$、初值 $(1,0,0)$）隐含航向像对**：旁向像对 $b_x\to0$ 时那一列塌成零，实测会停在残差不为零的假驻点。改成"冻结基线主分量"后，旁向与纯旁向像对都是 4 轮收敛，航向像对自动退回教材形式（`gauge` 上报冻结的是哪一个）。
3. **粗差用留一法**：拿掉候选点重解，$\sigma_0$ 缩 2.5 倍才算粗差；$|q|>3\sigma_0$ 那套在 $n\le14$ 时剔不掉自己。最多 4 轮，剔完至少留 7 对。
4. **两套残差**：$\sigma_0$（残差单位权）与 $q_{\mathrm{rms}}$（上下视差均方根）比值确定，实测 1.599 对理论 1.608。
5. **与 M6.5 的接口**：解出的三个角与 `rotation` 按本库约定输出，左片取 $(0,0,0,0,0,0)$、右片取 $(\mathbf b,\varphi,\omega,\kappa)$ 就能直接喂 `pgm_forward_intersection`。

推导、规准与失败码见 [Docs/相对定向算法.md](../Docs/相对定向算法.md) §11。

---

## 现有接口：核线几何

```c
int32_t pgm_epipolar_rotation(double phi, double omega, double kappa, double* r9);

int32_t pgm_epipolar_lines(const pgm_epipolar_input* in, double x1, double y1,
                           pgm_line2* left, pgm_line2* right);
int32_t pgm_epipolar_epipoles(const pgm_epipolar_input* in, double* e1x, double* e1y, int32_t* e1_inf,
                                                          double* e2x, double* e2y, int32_t* e2_inf);
int32_t pgm_epipolar_distance(const pgm_epipolar_input* in, double x1, double y1,
                                                          double x2, double y2, double* dist);
int32_t pgm_epipolar_row(const pgm_epipolar_input* in, int32_t side, double x, double y, double* theta);
int32_t pgm_epipolar_lines_at(const pgm_epipolar_input* in, double theta, pgm_line2* left, pgm_line2* right);
```

输入就是相对定向的结果 $(\mathbf b,R)$ 加主距；输出是像平面上的直线 $ax+by+c=0$（系数已归一化，点到线距离直接就是 $|ax+by+c|$）、两个核点、有符号距离，以及核面转角 $\theta\in[0,\pi)$。

模型（共面条件换个未知数解）：

$$\mathbf n=\mathbf b\times\mathbf d_1,\qquad \mathbf m=R\,\mathbf n,\qquad m_u x_2+m_v y_2=f\,m_w$$

实现要点：

1. **同一平面切两片**。$\mathbf n$ 是左核线、$\mathbf m=R\mathbf n$ 是右核线，两者必过同一个核点。这是「同名点必须同行」的全部几何，没有近似。
2. **$\theta$ 是行号**。$\theta$ 把「过基线的核面」参数化：$\mathbf n_\theta=\cos\theta\,\mathbf u_1+\sin\theta\,\mathbf u_2$（$\mathbf u_{1,2}\perp\mathbf b$）。同名点必然给出同一个 $\theta$（左右两片各算一次，实测差 $4\times10^{-16}$ rad），核线重采样就把第 $j$ 行绑到第 $j$ 个 $\theta$ 上。
3. **$\mathbf b$ 只取方向**，长度随便传；`prepare` 里先归一化，所以相对定向的 $|\mathbf b|=1$ 与界面上的米制基线都对。
4. **右核点用 $R\mathbf b$，不是 $R^\top\mathbf b$**。两份文档里的 $R$ 方向相反（[核线算法.md](../Docs/核线算法.md) 用的是「像 → 辅助系」，本库出口是「辅助系 → 像」），照抄公式会得到一个差 200 mm 的"核点"——自检第 3 段抓的就是这个。
5. **退化就明说**：主距非正、零基线、非有限值 → `PGM_ERR_INVALID_ARGUMENT`；$\mathbf n\to0$（前方运动、像点落在主点上）或核线与像平面平行 → `PGM_ERR_DEGENERATE`。核点在无穷远不算错误，用 `at_infinity` 标志报出来。

**这一层不依赖任何第三方库**，理由见[界面设计规范 ADR-22](../Docs/界面设计规范.md)：核线只有几行乘加，交给 OpenCV 反而多一层黑盒；OpenCV 只用在 M10 的特征匹配里。

自检：`pgm_epipolar_test`，八段，合成像对 200 点，左/右偏离 $10^{-14}$ mm 量级。

**核线重采样**（`pgm_epipolar_rectify`）：把两幅影像各自沿核线重采样成两幅核线影像，**一次调用做完两幅**。第 $j$ 行绑到第 $j$ 个核面（$\theta_j=\pi(j+\tfrac12)/\text{rows}$），同一行第 $k$ 个采样点取 $t=(k-\frac{\text{cols}-1}{2})\cdot\text{step}$，沿核线方向双线性取值；越界写 0 并置掩膜位。左右两幅共用同一组 `step`/`cols`，所以同名点的行号必然相等，列号差就是视差。自检第 7 段用解析纹理验证采样位置：2732 个采样点与解析值最大差 1 个灰度级。

---

## 现有接口：计算机视觉匹配（第二个 DLL）

**这个模块链接 OpenCV**，单独编成 `photogrammetry_cv.dll`；`photogrammetry.dll` 仍然是零第三方依赖（ADR-22）。
头文件是 `include/photogrammetry_cv.h`，与 `photogrammetry.h` 分开：谁要匹配谁才链 OpenCV。

```c
int32_t pgm_cv_match(const pgm_cv_match_input* in, int32_t capacity,
                     double* out_left_i,  double* out_left_j,
                     double* out_right_i, double* out_right_j,
                     double* out_score, double* out_epipolar_mm,
                     int32_t* out_count, int32_t* out_inliers, int32_t* out_detected);
```

一次调用做完五件事，**中间不回调调用方**：

1. SIFT 检特征（每片封顶 8000 条；5120² 原片不封顶会出十万级特征，匹配时间全耗在算距离上）
2. 最近邻 + Lowe 比值检验（0.8）
3. **互为最佳**校验：反向再查一遍，挡住重复纹理
4. 核线过滤：$|$点到核线距离$|$ > 容差（默认 0.10 mm）直接丢，调的是本库的 `pgm_epipolar_distance`
5. RANSAC 复核基础矩阵（3 px），只留内点

一次调用的数据量：`5120 × 5120` 两幅灰度缓冲进，最多 `capacity` 组像元坐标出。**不是逐点调用**——
跨 ABI 的粒度是"一次匹配任务"，这是 §8.2 的硬规定。

构建（需要 vcpkg 装好 OpenCV）：

```powershell
cmake -S native -B native/build -G "Visual Studio 17 2022" -A x64 `
      -DCMAKE_TOOLCHAIN_FILE=C:/vcpkg/scripts/buildsystems/vcpkg.cmake
cmake --build native/build --config Release
native\build\bin\Release\pgm_cv_match_test.exe
```

`photogrammetry_cv` 的 POST_BUILD 会把 `opencv_*.dll` 与 `z.dll`/`jpeg62.dll` 等一起拷到 exe 旁边；
**换机器跑要连这些 DLL 一起带走**。没装 OpenCV 时 CMake 只打一行提示，其余算法照常编、照常测。

实测（Hammer `01-156` × `01-155`，两片同航线、`5120²`、像元 0.04503 mm、$f=152.72$ mm）：

```
检出 8001 特征/片　配上 987 对（RANSAC 内点 987）
相对定向：4 轮　987/987 参与　q_rms 0.0168 mm　σ0 0.0190 mm　冻结 b_u
核线过滤后：968 对　|核线距离| 中位数 0.0060 mm，最大 0.021 mm
```

**已知不行的一档**：`PointPos/` 里那两张 512² 缩略图（`01-156` × `02-165`，跨航线）。
SIFT 能配出 106 对、RANSAC 也认，但相对定向不收敛（`PGM_ERR_NOT_CONVERGED`）——
10 倍降采样之后像点定位精度与重叠区都不够。**匹配要用原片**，缩略图只适合看构图。

---

## 验证

三层，从下到上：

| 层 | 命令 | 验的是什么 |
|---|---|---|
| 算法本身（内定向） | `native\build\bin\Release\pgm_test.exe Data/sim` | DLL 算得对不对 |
| 算法本身（后方交会） | `native\build\bin\Release\pgm_resection_test.exe` | 同上，六段检查 |
| 算法本身（前方交会） | `native\build\bin\Release\pgm_intersection_test.exe` | 同上，七段检查 |
| 算法本身（相对定向） | `native\build\bin\Release\pgm_relative_orientation_test.exe` | 同上，七段检查 |
| 算法本身（核线几何） | `native\build\bin\Release\pgm_epipolar_test.exe` | 同上，八段检查（含核线重采样） |
| 匹配（第二个 DLL，需 OpenCV） | `native\build\bin\Release\pgm_cv_match_test.exe` | SIFT → 核线 → RANSAC 全链路，真实像对约 40 s |
| 托管调用 | `dotnet run --project src/Snap/Snap.csproj -c Debug -- --fiducial --dir Data/sim` | 封送对不对、DLL 找得到 |
| 界面 | `Data/sim/README.md` 第 3 节 | 手动点 8 个框标能不能复现 |

第一层的覆盖：

```
[PASS] 理想取点 sigma=0             精确复原答案（1e-8 内），σ0 = 0，冗余 10
[PASS] 手工取点 sigma=0.5px         σ0 ≈ 0.03 mm，主点偏差 < 1 px
[PASS] 手工取点 sigma=1.5px         σ0 ≈ 0.066 mm ≈ 1.5 px，与输入噪声同步
[PASS] 仅 3 个框标应被拒绝           rc=2
[PASS] 框标共线应被拒绝             rc=3
[PASS] 像元 j 取反应触发 b2 符号错误  rc=6
```

σ₀ 能反推出输入噪声的水平，说明平差在统计上正确，而不只是"算出一组数"。

后方交会那一层：

```
1 旋转矩阵与教材展开式对表      偏差 0.000e+00 mm
2 往返检验                     4 次迭代，6 个元素恢复 ≤ 3e-16
3 设计矩阵 vs 中心差分          相对 2.7e-10
4 带噪声（0.5 px，20 次实现）   σ0 均值 0.02296 vs 0.02252 mm；真误差 RMS/中误差 0.85–1.12
5 退化与非法输入               点数不足、共线、重合、相机背后、迭代上限、主距为 0、空指针
6 手算对表                     4 个方向点的 6 列偏导，可对着计算器核
```

前方交会那一层：

```
1 旋转矩阵与教材展开式对表      偏差 0.000e+00 mm（geometry.hpp 抽出来之后仍然一致）
2 往返检验                     9 个点最大坐标差 8.9e-12 m；无噪声时上下视差 3.6e-15 mm
3 与教材 (5-14) 对表           N 最大差 1.1e-13，坐标最大差 8.4e-12 m
4 反证：旁向像对               纯旁向分母 0.000000e+00（除零）；近旁向 0.01 mm 偏差
                               让教材解跳 796 m，本实现 0.094 m，差 8501 倍
5 精度传播 vs 蒙特卡洛         4000 次实现，m_X/m_Y/m_Z 相对差 0.2–1.5%
6 残余量真的在测东西           注入 0.05 mm 上下视差 → 最近距离 0.9499 m（手估 0.96 m）
7 退化与输入错误               零基线、同名点配错、主距非正、负中误差、空指针
```

相对定向那一层（七段，见 [Docs/相对定向算法.md](../Docs/相对定向算法.md#L179) 的实测表）：

```
1 合成往返                     基线 1.2e-15、角 5.8e-16；无噪声时 q_rms 与 σ0 归零
2 雅可比对表                   解析式 vs 中心差分，六列最大 9.9e-11
3 旁向 30% 像对                4 轮收敛，q_rms 6.5e-15 mm，gauge=1（冻结 b_y）
4 纯旁向（b_x=0）              4 轮收敛，解出的 b_y 有界
5 噪声 0.02 mm                 200 次全部收敛，q_rms 均值 0.0165 mm，与蒙特卡洛一致
6 退化闸门                     4 对点、重合点、共线各有明确错误码
7 粗差                         干净数据 0 剔；1 mm / 2 mm 的错点各剔 1 个，剔完元素回到 1e-9
```

---

## 新增一个算法的步骤

1. 在 `include/photogrammetry.h` 加输入/输出结构体与函数声明。
2. 在 `src/` 加一个 `.cpp` 实现，`try/catch` 包住全部逻辑。
3. 在 `CMakeLists.txt` 的 `add_library` 里加该文件。
4. 在 `tests/` 加验收程序，接进 `add_executable` 与 `add_test`。
5. 在 `src/Core/NativeAlgorithms.cs` 加 `DllImport` 声明与托管封装。
6. 更新 `Docs/算法笔记.md` 对应章节与[路线图](../Docs/路线图.md)状态。
