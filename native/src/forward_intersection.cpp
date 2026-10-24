// 空间前方交会：由两张片的外方位元素与一对同名像点求地面坐标。
//
// 教材出处是点投影系数法，公式 (5-13) / (5-14)；推导与取舍见 Docs/算法笔记.md 第 7 章。
//
// 与教材的唯一差别在"解 N1、N2 时用几个方程"：
//
//   教材把 (5-13) 的三个方程取第 (1)、(3) 两个联立（X 与 Z），抛弃第 (2) 个（Y），
//   Y 最后取两条视线的均值。这一步隐含了"基线主要在 X 方向（两片沿航线前后排列）"
//   这个前提，换成旁向布设就不成立，而且有两种下场：
//
//     · 纯旁向（Bu = Bw = 0，两片只差 Y）：两个分子同时为零，(1)(3) 的系数行列式
//       D = u1*w2 - u2*w1 也同时为零 —— 0/0，直接除零。实测 D = 0.000000e+00。
//     · 近旁向（Bu 很小但非零）：D 与分子一起变小，商对无噪声数据仍然正确，
//       但 D 相对它自己的量级（|u1||u2| 的量级）已经极小，方程的噪声被放大。
//       实测 D = -4.05 mm²（航向像对约 1e5），左片 x 偏 0.01 mm 就让地面点跳
//       796 m；同一偏差下本实现只动 0.094 m，差 8501 倍。
//
//   这里改成对三个方程一起做最小二乘，极小化 |N1*u - N2*v - B|^2。正常航向像对上
//   三式相容，最小二乘解与教材解同值（实测差 1e-13）；旁向像对上依然可解。
//   退化判据也从"左右视差为零"变成"两条视线平行"——后者才是真正解不出来的情形，
//   而且它就是交会角。
//
// 坐标约定与 pgm_resection 完全一致，见 geometry.hpp 抬头。

#include "photogrammetry.h"
#include "internal.hpp"
#include "geometry.hpp"

#include <cmath>
#include <cstring>

namespace {

constexpr double kPi = 3.14159265358979323846;

// 两条视线夹角的正弦下界。低于这个值就是纯退化（平行视线），不是"精度差"。
constexpr double kMinSinAngle = 1e-9;

// 求交会点协方差时对像点坐标加的扰动量，0.001 mm = 1 微米。
constexpr double kPerturbMm = 1e-3;

double deg(double rad) { return rad * 180.0 / kPi; }

// ---------------------------------------------------------------- 视线

// 一条视线：起点摄影中心，方向是地面坐标系里的指向（不归一化，模长无所谓）。
struct Ray
{
    double origin[3];
    double dir[3];
};

// 由外方位元素与像点坐标建视线。
//
// 地面向量 d 与像点 (x, y) 的关系：共线条件 x = -f*U/W、y = -f*V/W，其中
// (U,V,W) = R*d。于是 (x, y, -f) = (-f/W)*(U,V,W)，而物点在相机前方 W < 0，
// 所以 -f/W > 0，得到 d ∝ R^T * (x, y, -f)。
//
// 注意：教材写的 [u v w]^T = R' * [x y -f]^T 里那个 R' 正是这里 R 的转置。
// 两处对 R 的定义方向相反，本身不算错，但抄公式时混用会整体错一个符号，
// 错完还能出数，所以这里显式写成 mat_vec_t。
bool make_ray(const pgm_camera_pose& pose, double f, double x_mm, double y_mm, Ray* ray)
{
    const pgm::Mat3 r = pgm::make_rotation(pose.phi_rad, pose.omega_rad, pose.kappa_rad).total;
    const double img[3] = { x_mm, y_mm, -f };
    pgm::mat_vec_t(r, img, ray->dir);

    ray->origin[0] = pose.xs;
    ray->origin[1] = pose.ys;
    ray->origin[2] = pose.zs;

    const double len2 = pgm::dot3(ray->dir, ray->dir);
    return std::isfinite(len2) && len2 > 0.0;
}

// ---------------------------------------------------------------- 交会

struct Core
{
    double point[3];    // 交会点，取两条视线公垂线的中点
    double n[2];        // 投影系数 N1、N2
    double foot[2][3];  // 两条视线上各自离对方最近的点
    double miss;        // 两视线的最近距离 |foot0 - foot1|
    double angle_rad;   // 交会角
    double base;        // 基线长度
    double range[2];    // 摄影中心到交会点的距离
};

// 三式最小二乘解投影系数，再取公垂线中点作为交会点。
//
// 法方程（对 N1、N2 求偏导置零）：
//     [ u·u   -u·v ] [N1]   [  u·B ]
//     [ -u·v   v·v ] [N2] = [ -v·B ]
// 行列式 = |u|^2|v|^2 - (u·v)^2 = |u|^2|v|^2 sin^2θ，θ 就是交会角。
// 解出后残差 r = N1*u - N2*v - B 同时垂直于 u 和 v，所以 foot 就是两条视线的
// 最近点，中点即"异面直线的公垂线中点"。
bool intersect(const Ray& a, const Ray& b, Core* core)
{
    const double aa = pgm::dot3(a.dir, a.dir);
    const double vv = pgm::dot3(b.dir, b.dir);
    const double uv = pgm::dot3(a.dir, b.dir);

    const double base_vec[3] = { b.origin[0] - a.origin[0],
                                 b.origin[1] - a.origin[1],
                                 b.origin[2] - a.origin[2] };
    const double bb = pgm::dot3(base_vec, base_vec);
    const double ub = pgm::dot3(a.dir, base_vec);
    const double vb = pgm::dot3(b.dir, base_vec);

    if (!(aa > 0.0) || !(vv > 0.0)) return false;

    const double det = aa * vv - uv * uv;
    const double sin2 = det / (aa * vv);
    if (!(sin2 > 0.0)) return false;                 // 两条视线重合或反向
    const double sin_angle = std::sqrt(sin2);
    if (!(sin_angle > kMinSinAngle)) return false;   // 平行，交会角为 0

    core->n[0] = (ub * vv - uv * vb) / det;
    core->n[1] = (uv * ub - aa * vb) / det;

    double miss2 = 0.0;
    for (int k = 0; k < 3; ++k) {
        core->foot[0][k] = a.origin[k] + core->n[0] * a.dir[k];
        core->foot[1][k] = b.origin[k] + core->n[1] * b.dir[k];
        const double d = core->foot[0][k] - core->foot[1][k];
        miss2 += d * d;
        core->point[k] = 0.5 * (core->foot[0][k] + core->foot[1][k]);
    }
    core->miss = std::sqrt(miss2);
    core->angle_rad = std::atan2(sin_angle, uv / std::sqrt(aa * vv));
    core->base = std::sqrt(bb);

    for (int i = 0; i < 2; ++i) {
        const double* o = (i == 0) ? a.origin : b.origin;
        double s2 = 0.0;
        for (int k = 0; k < 3; ++k) {
            const double d = core->point[k] - o[k];
            s2 += d * d;
        }
        core->range[i] = std::sqrt(s2);
    }
    return true;
}

// 一次完整的交会：img 依次是左片 x、y 与右片 x、y（mm）。
bool solve_pair(double f, const pgm_camera_pose& pa, const pgm_camera_pose& pb,
                const double img[4], Core* core)
{
    Ray ra, rb;
    if (!make_ray(pa, f, img[0], img[1], &ra)) return false;
    if (!make_ray(pb, f, img[2], img[3], &rb)) return false;
    if (!intersect(ra, rb, core)) return false;
    return std::isfinite(core->point[0]) && std::isfinite(core->point[1]) &&
           std::isfinite(core->point[2]);
}

// ---------------------------------------------------------------- 精度

// 把像点坐标的偶然误差传到交会点上。
//
// 交会点 P 由 4 个像点坐标决定，取 J = dP/d(x1, y1, x2, y2)（3x4，中心差分），
// 则 Cov(P) = σ_img^2 * J * J^T，对角线的平方根就是 m_X、m_Y、m_Z。
//
// 这么做而不是套 m_XY = (S/f)σ、m_Z = m_XY/sinθ 那组公式，是因为那组公式默认
// "Z 是深度方向"，而地面坐标系可能叫东-北-天也可能叫北-东-天；直接给 3x3 的
// 对角线不需要任何方向假设，换轴也不会错。
bool propagate_sigma(double f, const pgm_camera_pose& pa, const pgm_camera_pose& pb,
                     const double img[4], double sigma_mm, double m[3])
{
    double jac[3][4];
    for (int k = 0; k < 4; ++k) {
        double lo[4], hi[4];
        for (int t = 0; t < 4; ++t) {
            lo[t] = img[t];
            hi[t] = img[t];
        }
        lo[k] -= kPerturbMm;
        hi[k] += kPerturbMm;

        Core c_lo, c_hi;
        if (!solve_pair(f, pa, pb, lo, &c_lo)) return false;
        if (!solve_pair(f, pa, pb, hi, &c_hi)) return false;

        for (int r = 0; r < 3; ++r) {
            jac[r][k] = (c_hi.point[r] - c_lo.point[r]) / (2.0 * kPerturbMm);
        }
    }

    for (int r = 0; r < 3; ++r) {
        double s = 0.0;
        for (int k = 0; k < 4; ++k) s += jac[r][k] * jac[r][k];
        m[r] = sigma_mm * std::sqrt(s);
    }
    return true;
}

// ---------------------------------------------------------------- 输入检查

bool finite_pose(const pgm_camera_pose& p)
{
    const double v[6] = { p.xs, p.ys, p.zs, p.phi_rad, p.omega_rad, p.kappa_rad };
    return pgm::finite_all(v, 6);
}

}  // namespace

extern "C" {

int32_t pgm_forward_intersection(const pgm_intersection_input* in,
                                 pgm_intersection_output*      out)
{
    pgm::set_error("");

    if (in == nullptr || out == nullptr) {
        pgm::set_error("输入或输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    std::memset(out, 0, sizeof(*out));

    if (!(in->focal_length_mm > 0.0) || !std::isfinite(in->focal_length_mm)) {
        pgm::set_error("主距必须为正的有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    if (!finite_pose(in->left) || !finite_pose(in->right)) {
        pgm::set_error("外方位元素中含非有限数值");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    const double img[4] = { in->left_x_mm, in->left_y_mm, in->right_x_mm, in->right_y_mm };
    if (!pgm::finite_all(img, 4)) {
        pgm::set_error("同名像点坐标中含非有限数值");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    if (!std::isfinite(in->image_sigma_mm) || in->image_sigma_mm < 0.0) {
        pgm::set_error("像点坐标中误差必须为非负的有限值；不评精度时传 0");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    try {
        Core core;
        if (!solve_pair(in->focal_length_mm, in->left, in->right, img, &core)) {
            pgm::set_error("两条视线平行，交会角为零；同名点或外方位元素无法交会");
            return PGM_ERR_DEGENERATE;
        }

        // 投影系数为负说明交会点落在相机后方，这对同名点不可能是同一个地物点。
        // 与后方交会里 W >= 0 的处理保持一致。
        if (!(core.n[0] > 0.0) || !(core.n[1] > 0.0)) {
            pgm::set_error("投影系数为负，交会点落在相机后方；同名点配对或外方位元素有误");
            return PGM_ERR_INVALID_ARGUMENT;
        }

        // 把交会点反投影回两片，得到四个像点残差（v = 计算值 - 观测值）。
        double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
        const int32_t e_l = pgm_project_point(in->focal_length_mm,
                                              in->left.xs, in->left.ys, in->left.zs,
                                              in->left.phi_rad, in->left.omega_rad,
                                              in->left.kappa_rad,
                                              core.point[0], core.point[1], core.point[2],
                                              &xl, &yl);
        const int32_t e_r = (e_l == PGM_OK)
            ? pgm_project_point(in->focal_length_mm,
                                in->right.xs, in->right.ys, in->right.zs,
                                in->right.phi_rad, in->right.omega_rad,
                                in->right.kappa_rad,
                                core.point[0], core.point[1], core.point[2],
                                &xr, &yr)
            : PGM_OK;
        if (e_l != PGM_OK || e_r != PGM_OK) {
            pgm::set_error("交会点反投影失败，落到相机后方；检查外方位元素");
            return PGM_ERR_INVALID_ARGUMENT;
        }

        double m[3] = { 0.0, 0.0, 0.0 };
        if (in->image_sigma_mm > 0.0) {
            if (!propagate_sigma(in->focal_length_mm, in->left, in->right, img,
                                 in->image_sigma_mm, m)) {
                pgm::set_error("交会点精度传播失败，扰动后的视线无法交会；交会角可能过小");
                return PGM_ERR_DEGENERATE;
            }
        }

        out->ground_x = core.point[0];
        out->ground_y = core.point[1];
        out->ground_z = core.point[2];

        out->n_left = core.n[0];
        out->n_right = core.n[1];
        out->convergence_angle_deg = deg(core.angle_rad);
        out->base_length = core.base;
        out->range_left = core.range[0];
        out->range_right = core.range[1];

        out->miss_distance = core.miss;
        out->residual_left_x_mm = xl - in->left_x_mm;
        out->residual_left_y_mm = yl - in->left_y_mm;
        out->residual_right_x_mm = xr - in->right_x_mm;
        out->residual_right_y_mm = yr - in->right_y_mm;
        out->vertical_parallax_mm = out->residual_left_y_mm - out->residual_right_y_mm;

        out->m_x = m[0];
        out->m_y = m[1];
        out->m_z = m[2];
        return PGM_OK;
    }
    catch (...) {
        pgm::set_error("交会过程中发生未预期异常");
        return PGM_ERR_INTERNAL;
    }
}

}  // extern "C"
