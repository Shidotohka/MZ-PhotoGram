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
 * image_sigma_mm 是像点坐标的先验中误差，用于粗差门槛 3σ；传 0 表示不知道，此时
 * 门槛取 3σ0 与 0.01 mm 中的大者（无噪声数据 σ0 = 0，不能拿 0 当门槛）。
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
 * 的输出同一约定。rotation 是把辅助系方向转到像空间的 3x3（行主序），可以连基线
 * 一起当成一组外方位元素喂给 pgm_forward_intersection 求模型坐标：
 *     左片 (0,0,0,0,0,0)，右片 (b_x,b_y,b_z,phi,omega,kappa)
 *
 * q_* 是上下视差：把右片射线按 w 归一后投回与左片平行的像平面，q = y_左 - y_右。
 * 定向正确且无观测误差时应为零，单位 mm。q_rms_mm 就是 M6 的验收指标；sigma0_mm
 * 是解算的单位权中误差（同一量纲），两者应当接近。
 *
 * 退出码：PGM_ERR_TOO_FEW_POINTS（少于 5 对）、PGM_ERR_DEGENERATE（平均视差为零、
 * 法方程秩亏、迭代发散）、PGM_ERR_NOT_CONVERGED（到上限未收敛，out 中是最后一次
 * 迭代值，不要当结果用）。其余错误码下 out 只保证被清零。
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

/* 库版本字符串，形如 "1.0.0"。 */
PGM_API const char* pgm_version(void);

/* 最近一次调用失败的原因（UTF-8）。线程局部，返回的指针在下一次调用前有效。 */
PGM_API const char* pgm_last_error(void);

#ifdef __cplusplus
}
#endif

#endif /* PHOTOMETRY_H_INCLUDED */
