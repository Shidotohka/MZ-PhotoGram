/*
 * photogrammetry.h —— 摄影测量算法库的 C ABI
 *
 * 约定：
 *   · 所有结构体为固定布局 POD，不含 STL 容器、不含指针图。
 *   · 输入缓冲区由调用方持有，函数不保留引用。
 *   · 输出数组由调用方预分配，长度在输入中给出；不需要时传 NULL。
 *   · 返回值 0 为成功，非 0 为错误码，配 pgm_last_error() 取描述。
 *   · C++ 异常不跨越 ABI 边界，内部统一捕获并转错误码。
 *   · 字符串一律 UTF-8。
 */
#ifndef PHOTOMETRY_H_INCLUDED
#define PHOTOMETRY_H_INCLUDED

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  ifdef PGM_BUILDING_DLL
#    define PGM_API __declspec(dllexport)
#  else
#    define PGM_API __declspec(dllimport)
#  endif
#else
#  define PGM_API
#endif

/* ------------------------------------------------------------ 错误码 */

#define PGM_OK                    0
#define PGM_ERR_NULL_ARGUMENT     1
#define PGM_ERR_TOO_FEW_POINTS    2
#define PGM_ERR_DEGENERATE        3
#define PGM_ERR_INVALID_ARGUMENT  4
#define PGM_ERR_INTERNAL          5
#define PGM_ERR_AXIS_SIGN         6   /* b2 >= 0，y 轴符号异常；结果已写入 out，供调用方诊断 */
#define PGM_ERR_NOT_CONVERGED     7   /* 迭代到上限仍未收敛；out 中是最后一次迭代值，仅供诊断 */

/* ------------------------------------------------------------ 内定向 */

/*
 * 六参数仿射内定向的输入。
 *
 * 四个数组长度均为 count，一一对应：
 *   fiducial_x_mm[k], fiducial_y_mm[k]  —— 框标的像平面坐标（mm，原点像主点，y 向上）
 *   image_i[k], image_j[k]              —— 该框标在影像上的像元坐标（允许小数）
 *
 * count 至少为 4。3 个框标在数学上可解，但多余观测为 0，残差恒为 0，无法检核。
 */
typedef struct pgm_fiducial_input
{
    int32_t       count;
    const double* fiducial_x_mm;
    const double* fiducial_y_mm;
    const double* image_i;
    const double* image_j;
    int32_t       image_width;    /* px */
    int32_t       image_height;   /* px */
} pgm_fiducial_input;

/*
 * 六参数仿射内定向的输出。
 *
 * 模型：
 *     x_m = a0 + a1*I + a2*J
 *     y_m = b0 + b1*I + b2*J
 */
typedef struct pgm_fiducial_output
{
    double a0, a1, a2;
    double b0, b1, b2;

    double s_x, s_y;              /* 沿 I / J 方向的比例，mm/px */
    double pixel_size_mm;         /* (s_x + s_y) / 2 */
    double x0_mm, y0_mm;          /* 像主点相对影像中心的偏移，mm */
    double principal_i, principal_j;  /* 像主点的像元坐标 */
    double theta_i_deg, theta_j_deg;  /* I / J 轴方向角，度 */

    double sigma0_mm;             /* 单位权中误差 */
    double max_abs_residual_mm;   /* 残差绝对值最大者 */

    int32_t used_count;           /* 实际参与解算的框标数 */
    int32_t redundancy;           /* 2n - 6 */

    /* 逐点残差，单位 mm，v = 计算值 - 观测值。
       调用方预分配 count 个；不需要时置 NULL。 */
    double* residual_x_mm;
    double* residual_y_mm;
} pgm_fiducial_output;

/* ------------------------------------------------------------ 后方交会 */

/*
 * 空间后方交会的输入。每个控制点提供一组像点坐标与地面坐标，一一对应。
 *
 * 像平面坐标 image_x_mm / image_y_mm：原点像主点，x 向右、y 向上，
 * 已由内定向从像元坐标换算，且已完成畸变改正。
 *
 * 地面坐标 ground_x / ground_y / ground_z：右手系，Z 向上。
 * 长度单位可以是米或毫米，但三个数组必须一致；focal_length_mm 恒为毫米。
 *
 * 初始值：use_initial 为 0 时忽略 initial_*，由程序按控制点分布估计；
 * 为 1 时直接使用给定初值（斜摄影、航高未知等自动估计失效的场合）。
 * 角度单位为弧度。
 *
 * 迭代控制：任一项 <= 0 取默认值 —— max_iterations 50；
 * tolerance_length 取 1e-7 乘平均摄影距离；tolerance_angle 取 1e-9 rad。
 */
typedef struct pgm_resection_input
{
    int32_t       count;
    const double* image_x_mm;
    const double* image_y_mm;
    const double* ground_x;
    const double* ground_y;
    const double* ground_z;

    double        focal_length_mm;

    int32_t       use_initial;
    double        initial_xs;
    double        initial_ys;
    double        initial_zs;
    double        initial_phi_rad;
    double        initial_omega_rad;
    double        initial_kappa_rad;

    int32_t       max_iterations;
    double        tolerance_length;
    double        tolerance_angle;
} pgm_resection_input;

/*
 * 空间后方交会的输出。
 *
 * 旋转矩阵 rotation 为行主序的 3x3，按 R = R_phi * R_omega * R_kappa 构造，
 * 把地面向量转到像空间；像空间 z 轴指向摄影者一侧，故垂直摄影时 W < 0。
 * 共线条件为 x = -f*U/W, y = -f*V/W。
 */
typedef struct pgm_resection_output
{
    double xs, ys, zs;            /* 摄影中心，单位同输入地面坐标 */
    double phi_rad, omega_rad, kappa_rad;
    double phi_deg, omega_deg, kappa_deg;
    double rotation[9];

    int32_t iterations;
    int32_t used_count;
    int32_t redundancy;           /* 2n - 6 */

    double sigma0_mm;             /* 单位权中误差 */
    double max_abs_residual_mm;

    double m_xs, m_ys, m_zs;      /* 参数中误差，单位同地面坐标 */
    double m_phi_rad, m_omega_rad, m_kappa_rad;
    double condition;             /* 列平衡后法方程的条件数 λmax/λmin */

    /* 逐点残差，单位 mm，v = 计算值 - 观测值。
       调用方预分配 count 个；不需要时置 NULL。 */
    double* residual_x_mm;
    double* residual_y_mm;
} pgm_resection_output;

/* ------------------------------------------------------------ 前方交会 */

/*
 * 一张片的摄影中心与姿态，角度为弧度。
 */
typedef struct pgm_camera_pose
{
    double xs, ys, zs;
    double phi_rad, omega_rad, kappa_rad;
} pgm_camera_pose;

/*
 * 空间前方交会的输入：两张片的外方位元素 + 一对同名像点。
 *
 * 像点坐标单位 mm，原点像主点，x 向右、y 向上，已由内定向换算并做畸变改正，
 * 与 pgm_resection 的输入同一定义。
 *
 * image_sigma_mm 是像点坐标的先验中误差，用来把误差传到交会点上；传 0 表示
 * 不评精度（输出 m_x/m_y/m_z 全为 0）。
 *
 * 一次只交会一个点。批量的循环与"哪些点接受"由调用方负责，见
 * Docs/界面设计规范.md 8.1 的三层职责边界。
 */
typedef struct pgm_intersection_input
{
    double          focal_length_mm;
    pgm_camera_pose left;
    pgm_camera_pose right;
    double          left_x_mm;
    double          left_y_mm;
    double          right_x_mm;
    double          right_y_mm;
    double          image_sigma_mm;
} pgm_intersection_input;

/*
 * 空间前方交会的输出。
 *
 * 地面坐标与各类距离的单位同输入的外方位元素；像点量与中误差的单位为 mm。
 *
 * n_left / n_right 即教材的投影系数 N1、N2。教材用 (5-13) 的第 (1)(3) 两式联立
 * 解它，本实现改为三式最小二乘，理由见 Docs/算法笔记.md 7.1；正常航向像对上两者同值。
 *
 * miss_distance 是两条视线（异面直线）的最近距离，即"交会点无法同时落在两条视线上"
 * 的剩余量，单位同地面坐标。四个 residual_* 是把交会点反投影回两片后与量测像点之差
 * （v = 计算值 - 观测值）；vertical_parallax_mm = vy左 - vy右，即剩余上下视差。
 */
typedef struct pgm_intersection_output
{
    double ground_x, ground_y, ground_z;

    double n_left, n_right;
    double convergence_angle_deg;   /* 交会角：两条视线的夹角 */
    double base_length;             /* 基线长度 */
    double range_left, range_right; /* 摄影中心到交会点的距离 */

    double miss_distance;
    double residual_left_x_mm, residual_left_y_mm;
    double residual_right_x_mm, residual_right_y_mm;
    double vertical_parallax_mm;

    double m_x, m_y, m_z;           /* 交会点中误差，单位同地面坐标 */
} pgm_intersection_output;

/* ------------------------------------------------------------ 相对定向 */

/*
 * 相对定向的输入：若干同名点对。像平面坐标单位 mm，原点像主点，x 向右、y 向上，
 * 已由内定向换算并做畸变改正，与 pgm_resection / pgm_forward_intersection 同一定义。
 *
 * count 至少 5（5 个未知数）；给 6 才有 1 个多余观测，才谈得上 σ0。
 *
 * image_sigma_mm 是像点坐标的先验中误差，只用于粗差判据的下限（3σ），不影响解算结果；
 * 传 0 表示不知道，此时下限取 0.01 mm。
 *
 * 粗差不是一个一个按 3σ0 判的——那个判据在小冗余时自相矛盾（粗差抬高 σ0，门槛跟着涨，
 * 它永远剔不掉自己），详见 relative_orientation.cpp 里的说明。实际做法是：把 |q| 最大的
 * 那个点拿掉重解，其余点的单位权中误差若缩小 2.5 倍以上才认定它是粗差。最多剔 4 个，
 * 且剔完至少留 7 对。所以 rejected_count 可能少于你用眼睛数出来的错点，那是有意的。
 *
 * max_iterations <= 0 取 50；tolerance <= 0 取 1e-10（对归一化后的改正数）。
 */
typedef struct pgm_relative_orientation_input
{
    int32_t       count;
    const double* left_x_mm;
    const double* left_y_mm;
    const double* right_x_mm;
    const double* right_y_mm;
    double        focal_length_mm;

    double        image_sigma_mm;
    int32_t       max_iterations;
    double        tolerance;
} pgm_relative_orientation_input;

/*
 * 相对定向的输出：5 个相对方位元素 + 模型比例尺 + 精度 + 上下视差。
 *
 * 坐标系：像空间辅助坐标系，原点在左片摄影中心、三轴平行于左片像空间系。基线
 * b_* 是单位方向（|b| = 1），模型比例尺由它规定——b 的长度是自由网的规准，绝对
 * 定向的 λ 再把它拉回米制，所以别把模型坐标当米制坐标用。
 *
 * 三个角是右片相对左片，按 R = R_kappa * R_omega * R_phi 分解，与 pgm_resection
 * 的输出同一约定。rotation 是"辅助系方向 -> 右片像空间"的 3x3（行主序），可以连基线
 * 一起当成一组外方位元素喂给 pgm_forward_intersection 求模型坐标：
 *     左片 (0,0,0,0,0,0)，右片 (b_x,b_y,b_z,phi,omega,kappa)
 *
 * q_* 是上下视差：右片同名点到**左片同名点所定核线**的垂直距离，单位 mm。
 *
 *     核面法向 n = b × d1，d1 = (x1, y1, -f)
 *     q = -f · (d2·n) / (|n|·|d2|)，d2 = R · (x2, y2, -f)
 *
 * 定向正确且无观测误差时应为零，q_rms_mm 就是 M6 的验收指标。别图省事写成
 * q = y1 - (-f·u2/w2)：那个量只把姿态找平、没扣掉基线方向，旁向像对上恒等于
 * f·B_v/Z（实测 76 mm）且不随精度下降，会把好结果报成坏结果。
 *
 * sigma0_mm 是单位权中误差（行已除以 |b|·f，所以与 q 同量纲）。两者只按参与解算的
 * 点统计，所以被剔除的点不影响它们；二者一般相差 mean(|d2|/f)·sqrt(n/(n-5))。
 *
 * 退出码：PGM_ERR_TOO_FEW_POINTS（少于 5 对）、PGM_ERR_DEGENERATE（平均视差为零、
 * 法方程秩亏、迭代发散）、PGM_ERR_NOT_CONVERGED（到上限未收敛，out 中是最后一次
 * 迭代值，不要当结果用）。其余错误码下 out 被清零，但两个缓冲区指针原样保留。
 */
typedef struct pgm_relative_orientation_output
{
    double b_x, b_y, b_z;               /* 单位基线方向，|b| = 1 */
    double phi_rad, omega_rad, kappa_rad;
    double phi_deg, omega_deg, kappa_deg;
    double rotation[9];

    int32_t gauge;                      /* 实际冻结的基线分量：0 = u, 1 = v, 2 = w */
    int32_t iterations;
    int32_t used_count;                 /* 实际参与解算的点对数 */
    int32_t rejected_count;             /* 被判为粗差剔掉的点对数 */
    int32_t redundancy;                 /* used_count - 5 */

    double sigma0_mm;                   /* 单位权中误差，mm */
    double q_rms_mm;                    /* 上下视差 RMS，mm */
    double q_max_abs_mm;
    double condition;                   /* 法方程条件数 λmax/λmin */

    /* 逐点上下视差，mm，被剔除的点也给值仅供参考。调用方预分配 count 个；
       不需要时置 NULL。 */
    double*  residual_q_mm;
    /* 逐点 1 = 参与解算，0 = 被当粗差剔除。调用方预分配 count 个；
       不需要时置 NULL。 */
    int32_t* used_flags;
} pgm_relative_orientation_output;

/* ------------------------------------------------------------ 函数 */

/* 六参数仿射最小二乘。成功返回 PGM_OK 并填满 out（含残差，若提供了缓冲区）。 */
PGM_API int32_t pgm_fiducial_orientation(const pgm_fiducial_input*  in,
                                         pgm_fiducial_output*       out);

/*
 * 空间后方交会：共线条件方程 + 迭代最小二乘。
 *
 * 成功返回 PGM_OK。返回 PGM_ERR_NOT_CONVERGED 时 out 已填满，但那是最后一次
 * 迭代值，不是解，不要当结果使用。其余错误码下 out 只保证被清零。
 */
PGM_API int32_t pgm_resection(const pgm_resection_input*  in,
                              pgm_resection_output*       out);

/*
 * 空间前方交会：由两张片的外方位元素与一对同名像点求地面坐标。
 *
 * 成功返回 PGM_OK。两条视线平行（交会角为零）返回 PGM_ERR_DEGENERATE；
 * 投影系数为负（交会点落在相机后方，说明同名点配错或外方位元素有误）返回
 * PGM_ERR_INVALID_ARGUMENT。失败时 out 只保证被清零。
 */
PGM_API int32_t pgm_forward_intersection(const pgm_intersection_input*  in,
                                         pgm_intersection_output*       out);

/*
 * 相对定向：共面条件方程 + 迭代最小二乘，解 5 个相对方位元素。
 *
 * 不用任何地面坐标，约束完全来自内部几何（同名光线对对相交）。成功返回 PGM_OK；
 * 返回 PGM_ERR_NOT_CONVERGED 时 out 已填满，但那是最后一次迭代值，不是解。
 */
PGM_API int32_t pgm_relative_orientation(const pgm_relative_orientation_input*  in,
                                         pgm_relative_orientation_output*       out);

/*
 * 诊断用：在给定的基线分量与姿态处输出共面条件的误差方程。
 *
 * out_a 每点 6 个系数，列序为 b_u, b_v, b_w, alpha, beta, gamma（后三个是绕辅助系
 * u/v/w 轴的小旋转）；out_f 是共面条件的残差。rotation 是"辅助系方向 -> 像点方向"
 * 的 3x3 行主序，与 pgm_relative_orientation 输出的 rotation 同一约定。
 *
 * 提供给自检程序做有限差分对表，正常解算流程不需要调用。
 */
PGM_API int32_t pgm_relative_orientation_design(int32_t       count,
                                                const double* left_x_mm,
                                                const double* left_y_mm,
                                                const double* right_x_mm,
                                                const double* right_y_mm,
                                                double        focal_length_mm,
                                                double        b_u, double b_v, double b_w,
                                                const double* rotation,
                                                double*       out_a,
                                                double*       out_f);

/*
 * 共线条件方程的正算：由外方位元素把地面点投影到像平面坐标（mm）。
 *
 * 供检核、前方交会和自检使用。物点落在相机背后（W >= 0）返回
 * PGM_ERR_INVALID_ARGUMENT。坐标系与 pgm_resection 完全一致。
 */
PGM_API int32_t pgm_project_point(double  focal_length_mm,
                                  double  xs, double ys, double zs,
                                  double  phi_rad, double omega_rad, double kappa_rad,
                                  double  ground_x, double ground_y, double ground_z,
                                  double* out_x_mm, double* out_y_mm);

/*
 * 诊断用：在给定外方位元素处输出误差方程的设计矩阵 A（2n 行 6 列，行主序）
 * 与常数项 l = 观测值 - 计算值。列序为 Xs, Ys, Zs, phi, omega, kappa。
 *
 * 提供给自检程序做有限差分对表，正常解算流程不需要调用。
 */
PGM_API int32_t pgm_resection_design(int32_t       count,
                                     const double* image_x_mm,
                                     const double* image_y_mm,
                                     const double* ground_x,
                                     const double* ground_y,
                                     const double* ground_z,
                                     double        focal_length_mm,
                                     double        xs, double ys, double zs,
                                     double        phi_rad, double omega_rad, double kappa_rad,
                                     double*       out_a,
                                     double*       out_l);

/* ------------------------------------------------------------ 核线几何 */

/*
 * 核线几何的输入：相对定向的结果 (b, R) 加主距。
 *
 *   bx, by, bz   基线单位方向，表达在辅助系（= 左片像空间系：X 右、Y 上、Z 指向摄影者）。
 *                就是 pgm_relative_orientation 输出的 b_u,b_v,b_w；只取方向，长度被忽略。
 *   rotation     辅助系 -> 右片像空间的 3x3，行主序。就是 pgm_relative_orientation 的 rotation。
 *   focal_length 主距 mm，必须与相对定向时用的同一个值。
 *
 * 三个角装成矩阵用 pgm_epipolar_rotation，自检与 M6.5 都走它，免得各处自己拼教材阵。
 */
typedef struct pgm_epipolar_input
{
    double bx, by, bz;
    double rotation[9];
    double focal_length_mm;
} pgm_epipolar_input;

/* 像平面上的直线 a*x + b*y + c = 0（x/y 为像平面 mm）。系数已按 a^2+b^2 = 1 归一化。 */
typedef struct pgm_line2
{
    double a, b, c;
} pgm_line2;

/*
 * 由三个角装出教材形式的旋转阵 R = R_kappa * R_omega * R_phi（行主序 9 个）。
 * 方向与 pgm_resection / pgm_relative_orientation 的 rotation 一致：辅助系 -> 像空间。
 */
PGM_API int32_t pgm_epipolar_rotation(double  phi_rad, double omega_rad, double kappa_rad,
                                      double* out_rotation9);

/*
 * 给定左像点，输出它与它对应的**右核线**，另外把左核线也带出来。
 *
 *   n = b × (x1, y1, -f)     核面法向量（辅助系）
 *   左核线：n_u x + n_v y = f n_w
 *   右核线：m = R n，m_u x2 + m_v y2 = f m_w
 *
 * 左右两条是**同名核线**：同一个核面切两张像片切出来的。同名点在右片上的候选只能落在这条线上，
 * 这就是把匹配从二维压成一维的依据。
 *
 * 平面与像平面平行（直线在无穷远）或点与基线重合时返回 PGM_ERR_DEGENERATE。
 */
PGM_API int32_t pgm_epipolar_lines(const pgm_epipolar_input* in,
                                   double x1_mm, double y1_mm,
                                   pgm_line2* out_left, pgm_line2* out_right);

/*
 * 两个核点（像平面 mm）。核点 = 基线与像平面的交点。
 *
 *   e1 = (-f b_x / b_z, -f b_y / b_z)
 *   e2 = (-f v_u / v_w, -f v_v / v_w)，v = R b
 *
 * 注意 e2 用的是 R b 而不是 R^T b：[核线算法.md] 里写 R^T 是因为那份推导的 R 是
 * 「像 -> 辅助系」，与本库出口的 rotation（辅助系 -> 像）互为转置。
 *
 * 近垂直摄影时 b_z、v_w 都接近 0，核点跑到无穷远：这时 *out_at_infinity 置 1，
 * 坐标写 0，调用方应当按「核线近似平行」处理，不要拿这个 0 当真。
 */
PGM_API int32_t pgm_epipolar_epipoles(const pgm_epipolar_input* in,
                                      double*   out_e1_x, double* out_e1_y, int32_t* out_e1_at_infinity,
                                      double*   out_e2_x, double* out_e2_y, int32_t* out_e2_at_infinity);

/*
 * 右像点到「左像点所定核线」的**有符号**垂直距离（mm）：正值在法向一侧。
 * 超过约 0.1 mm 基本可以判为错配——这是匹配后最便宜的一道粗差闸门。
 * 与 pgm_epipolar_lines 同样的退化情况返回 PGM_ERR_DEGENERATE。
 */
PGM_API int32_t pgm_epipolar_distance(const pgm_epipolar_input* in,
                                      double x1_mm, double y1_mm,
                                      double x2_mm, double y2_mm,
                                      double* out_distance_mm);

/*
 * 核面转角 theta ∈ [0, pi)：把「过基线、过这个像点的那个核面」参数化。
 *
 * side = 0 用左像点（辅助系里直接算），side = 1 用右像点（先乘 R^T 转回辅助系）。
 * 同名点必须落在同一个核面上，所以两边算出来的 theta 必须相等——核线重采样就是把
 * 第 j 行绑到第 j 个 theta 上，这一条相等就是「同名点同行」的来源。
 */
PGM_API int32_t pgm_epipolar_row(const pgm_epipolar_input* in, int32_t side,
                                 double x_mm, double y_mm, double* out_theta_rad);

/*
 * 反向：给定核面转角 theta，输出该核面在两张像片上切出来的两条核线。
 * 重采样按「第 j 行 ↔ 第 j 个 theta」走，两幅核线影像的同一行来自同一个核面，
 * 于是行内任意一对同列射线天然共面。
 */
PGM_API int32_t pgm_epipolar_lines_at(const pgm_epipolar_input* in, double theta_rad,
                                      pgm_line2* out_left, pgm_line2* out_right);

/*
 * 核线重采样：把两幅影像各自沿核线方向重新采样成两幅「核线影像」。
 *
 * 第 j 行绑到第 j 个核面：theta_j = pi * (j + 0.5) / rows。同一行里第 k 个采样点取
 *      t = (k - (cols-1)/2) * step_mm
 *      p = 核线的最近点 + t * 方向
 * 左右两幅用**同一个** step / cols，所以同名点的行号一定相等，列号差就是视差。
 *
 * 采样用双线性。采样点落在像幅外时该像素写 0，并把 mask 置 0（mask 可为 NULL）。
 * 输出缓冲由调用方预分配：rows x cols 的 8 位灰度，step 是每行字节数。
 * 这是逐像素的活，**一次调用做完两幅**，不要按行/按像素回调。
 */
typedef struct pgm_epipolar_rectify_input
{
    pgm_epipolar_input epipolar;

    int32_t rows;        /* 核线影像行数，一行 = 一个核面 */
    int32_t cols;        /* 每行采样点数，左右两幅必须相同 */
    double  step_mm;     /* 沿核线的采样间隔，一般取像元尺寸 */

    const uint8_t* left_pixels;
    int32_t        left_width, left_height, left_step;
    const uint8_t* right_pixels;
    int32_t        right_width, right_height, right_step;

    double pixel_size_mm;                 /* 像元 -> mm */
    double cx_left_px,  cy_left_px;       /* 主点（像元，列/行） */
    double cx_right_px, cy_right_px;
} pgm_epipolar_rectify_input;

typedef struct pgm_epipolar_rectify_output
{
    uint8_t* left_image;   int32_t left_step;    /* rows x cols */
    uint8_t* right_image;  int32_t right_step;
    uint8_t* left_mask;                          /* rows x cols，可为 NULL */
    uint8_t* right_mask;
    pgm_line2* left_lines;                       /* rows 条，可为 NULL */
    pgm_line2* right_lines;
} pgm_epipolar_rectify_output;

PGM_API int32_t pgm_epipolar_rectify(const pgm_epipolar_rectify_input*  in,
                                     const pgm_epipolar_rectify_output* out);

/* ------------------------------------------------------------ 绝对定向 */

/*
 * 七参数相似变换的输入（摄影测量叫绝对定向，大地测量叫 Helmert，统计叫 Procrustes）。
 *
 * 模型方程：
 *     g = λ R m + t
 *
 * m 是模型坐标（相对定向 + 前方交会的结果，比例尺以基线长为单位），
 * g 是同一批控制点的地面坐标（野外实测），一一对应。
 * 两者的长度单位不必相同——λ 会把单位差一起吸收。
 *
 * count 至少为 3。控制点近共线时，绕该直线的旋转不可测，返回 PGM_ERR_DEGENERATE。
 * 控制点共面**可以**解（野外常见），但高程方向全靠倾角外推，看 out->singular。
 */
typedef struct pgm_absolute_orientation_input
{
    int32_t       count;
    const double* model_x;
    const double* model_y;
    const double* model_z;
    const double* ground_x;
    const double* ground_y;
    const double* ground_z;
} pgm_absolute_orientation_input;

/*
 * 绝对定向的输出。R 与 λ、t 一起满足 g = λ R m + t。
 */
typedef struct pgm_absolute_orientation_output
{
    double scale;                 /* λ > 0 */
    double rotation[9];           /* 行主序，模型系 → 地面系，det = +1 */
    double translation[3];        /* t = (X0, Y0, Z0)，与地面坐标同单位 */

    /* 把 rotation 按教材形式 R = Rκ·Rω·Rφ 分解得到的三个角。
       注意：这里 R 是「模型 → 地面」，与后方交会那个「地面 → 像空间」的 R
       互为转置，两处的角不是同一组量。角只用于显示与复算，不参与解算。 */
    double phi_rad, omega_rad, kappa_rad;
    double phi_deg, omega_deg, kappa_deg;

    int32_t used_count;
    int32_t redundancy;           /* 3n - 7 */

    double sigma0;                /* 单位权中误差，与地面坐标同单位 */
    double rms;                   /* 控制点不符值 RMS = sqrt(Σ|v|²/n)，同单位 */
    double max_abs_residual;

    /* H = Σ m' g'^T 的奇异值，降序。
       σ2/σ1 ≈ 0 ⇒ 控制点共线（解不出来）；σ3/σ1 ≈ 0 ⇒ 控制点共面（能解，高程弱）。 */
    double singular[3];
    double condition;             /* σ1/σ2；共线时给 1e300 */

    /* 1 = 控制点数据是镜像的（左右手系搞反）。此时最好的「真旋转」也贴不上，
       症状是残差大且系统性——不是算法错，是坐标轴换了。 */
    int32_t mirror_suspected;

    /* 逐点不符值 v = g' - λR m'。residual_xyz 长度 3*count（X,Y,Z 交错），
       residual_length 长度 count。调用方预分配；不需要时置 NULL。 */
    double* residual_xyz;
    double* residual_length;
} pgm_absolute_orientation_output;

/*
 * 七参数相似变换：闭合解（Procrustes），全局最优、无迭代。
 * 成功返回 PGM_OK。PGM_ERR_DEGENERATE 时 out 只保证被清零。
 */
PGM_API int32_t pgm_absolute_orientation(const pgm_absolute_orientation_input* in,
                                         pgm_absolute_orientation_output*      out);

/* 库版本字符串，形如 "1.0.0"。 */
PGM_API const char* pgm_version(void);

/* 最近一次调用失败的原因（UTF-8）。线程局部，返回的指针在下一次调用前有效。 */
PGM_API const char* pgm_last_error(void);

#ifdef __cplusplus
}
#endif

#endif /* PHOTOMETRY_H_INCLUDED */
