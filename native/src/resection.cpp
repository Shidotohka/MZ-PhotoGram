// 空间后方交会：共线条件方程 + 迭代最小二乘。算法见 Docs/算法笔记.md 第 6 章。
//
// 坐标约定（三者必须一致，否则解出来的角度会整体错一个符号）：
//   · 像平面坐标 mm，原点像主点，x 向右、y 向上，已完成内定向与畸变改正。
//   · 地面坐标右手系，Z 向上。
//   · 共线条件 x = -f*U/W, y = -f*V/W，其中 [U V W]^T = R * [dX dY dZ]^T，
//     R 就是教材里
//              | a1 b1 c1 |
//              | a2 b2 c2 |
//              | a3 b3 c3 |
//     那一块矩阵（行对应 U/V/W，列对应 dX/dY/dZ）。像空间 z 轴指向摄影者一侧，
//     故垂直摄影时 W = Z - Zs < 0。
//   · 该 R 的分解顺序是 R = R_kappa * R_omega * R_phi，三个初等阵取教材形式
//     （注意它们与通用右手旋转阵差一个符号，正是这个约定使得展开后等于 a1..c3）。

#include "photogrammetry.h"
#include "internal.hpp"
#include "geometry.hpp"
#include "linalg.hpp"

#include <cmath>
#include <cstring>
#include <new>
#include <vector>

namespace {

// R 的约定见 geometry.hpp 的抬头，与前方交会共用同一份。
using pgm::Mat3;
using pgm::Rotation;
using pgm::make_rotation;
using pgm::mat_vec;
using pgm::finite_all;
using pgm::jacobi_eigen;
using pgm::symmetric_eigen;
using pgm::solve_from_eigen;
using pgm::diag_inverse_from_eigen;

constexpr double kPi = 3.14159265358979323846;

// 未知数个数：Xs, Ys, Zs, phi, omega, kappa
constexpr int kUnknowns = 6;

constexpr int    kDefaultMaxIterations    = 50;
constexpr double kDefaultAngleTolerance   = 1e-9;  // rad
constexpr double kDefaultLengthTolerance  = 1e-7;  // 乘平均摄影距离
constexpr int    kMinControlPoints        = 4;

double deg(double rad) { return rad * 180.0 / kPi; }

// dR_θ/dθ = M_θ * R_θ 中的 M_θ（常量）。M_θ = -K_θ，K_θ 是教材的旋转生成元。
void apply_m(int which, const double v[3], double out[3])
{
    switch (which) {
    case 0:  // M_phi   = [[0,0,1],[0,0,0],[-1,0,0]]
        out[0] = v[2]; out[1] = 0.0; out[2] = -v[0];
        break;
    case 1:  // M_omega = [[0,0,0],[0,0,1],[0,-1,0]]
        out[0] = 0.0; out[1] = v[2]; out[2] = -v[1];
        break;
    default: // M_kappa = [[0,1,0],[-1,0,0],[0,0,0]]
        out[0] = v[1]; out[1] = -v[0]; out[2] = 0.0;
        break;
    }
}

// ---------------------------------------------------------------- 共线条件方程

struct Params
{
    double xs, ys, zs;
    double phi, omega, kappa;
};

// 单点的共线条件与六个偏导。返回 false 表示该点落在相机背后（W >= 0）。
//
// 行内列序：Xs, Ys, Zs, phi, omega, kappa。
//
// 角元素用左乘形式最省事：R = R_kappa R_omega R_phi，且 dR_θ/dθ = M_θ R_θ，于是
//     dR/dphi   * d = R_kappa R_omega M_phi   (R_phi d)
//     dR/domega * d = R_kappa M_omega         (R_omega R_phi d)
//     dR/dkappa * d = M_kappa * u，u = R d
bool point_equations(double f, const Rotation& rs, const double d[3],
                     double row_x[kUnknowns], double row_y[kUnknowns],
                     double* x_calc, double* y_calc)
{
    double u[3];
    mat_vec(rs.total, d, u);
    const double big_u = u[0], big_v = u[1], big_w = u[2];
    if (!(big_w < 0.0)) return false;

    const double xc = -f * big_u / big_w;
    const double yc = -f * big_v / big_w;
    const double inv_w2 = 1.0 / (big_w * big_w);

    // 平移：du/dXs = -R 的第 0 列，其余同理。
    for (int j = 0; j < 3; ++j) {
        const double du = -rs.total.m[0 * 3 + j];
        const double dv = -rs.total.m[1 * 3 + j];
        const double dw = -rs.total.m[2 * 3 + j];
        row_x[j] = -f * (du * big_w - big_u * dw) * inv_w2;
        row_y[j] = -f * (dv * big_w - big_v * dw) * inv_w2;
    }

    double g[kUnknowns - 3][3];

    // phi
    {
        double v1[3], m1[3], t1[3];
        mat_vec(rs.phi, d, v1);
        apply_m(0, v1, m1);
        mat_vec(rs.omega, m1, t1);
        mat_vec(rs.kappa, t1, g[0]);
    }
    // omega
    {
        double v2[3], w2[3], m2[3];
        mat_vec(rs.phi, d, v2);
        mat_vec(rs.omega, v2, w2);
        apply_m(1, w2, m2);
        mat_vec(rs.kappa, m2, g[1]);
    }
    // kappa
    apply_m(2, u, g[2]);

    for (int j = 0; j < 3; ++j) {
        row_x[3 + j] = -f * (g[j][0] * big_w - big_u * g[j][2]) * inv_w2;
        row_y[3 + j] = -f * (g[j][1] * big_w - big_v * g[j][2]) * inv_w2;
    }

    *x_calc = xc;
    *y_calc = yc;
    return true;
}

// 组装误差方程 A * dp - v = l，v = 计算值 - 观测值。
bool build_system(const double* gx, const double* gy, const double* gz,
                  const double* ox, const double* oy,
                  int n, double f, const Params& p,
                  double* a, double* l, double* sum_v2)
{
    const Rotation rs = make_rotation(p.phi, p.omega, p.kappa);

    double total = 0.0;
    for (int i = 0; i < n; ++i) {
        const double d[3] = { gx[i] - p.xs, gy[i] - p.ys, gz[i] - p.zs };
        double row_x[kUnknowns];
        double row_y[kUnknowns];
        double xc = 0.0, yc = 0.0;
        if (!point_equations(f, rs, d, row_x, row_y, &xc, &yc)) return false;

        for (int j = 0; j < kUnknowns; ++j) {
            a[(2 * i) * kUnknowns + j]     = row_x[j];
            a[(2 * i + 1) * kUnknowns + j] = row_y[j];
        }
        l[2 * i]     = ox[i] - xc;
        l[2 * i + 1] = oy[i] - yc;
        total += l[2 * i] * l[2 * i] + l[2 * i + 1] * l[2 * i + 1];
    }
    *sum_v2 = total;
    return true;
}

// 只算残差平方和，用于判断一个试探步是否让残差下降。返回 false 表示有点跑到相机背后。
bool residual_sum(const double* gx, const double* gy, const double* gz,
                  const double* ox, const double* oy,
                  int n, double f, const Params& p, double* out)
{
    const Rotation rs = make_rotation(p.phi, p.omega, p.kappa);

    double total = 0.0;
    for (int i = 0; i < n; ++i) {
        const double d[3] = { gx[i] - p.xs, gy[i] - p.ys, gz[i] - p.zs };
        double u[3];
        mat_vec(rs.total, d, u);
        if (!(u[2] < 0.0)) return false;

        const double lx = ox[i] - (-f * u[0] / u[2]);
        const double ly = oy[i] - (-f * u[1] / u[2]);
        total += lx * lx + ly * ly;
    }
    *out = total;
    return true;
}

// ---------------------------------------------------------------- 初值

// 按"近似垂直摄影"假设估计初值：像点与地面点之间做一个四参数相似变换。
//
// 垂直摄影且 phi = omega = 0 时，x = m*(cosκ*ΔX + sinκ*ΔY)、y = m*(-sinκ*ΔX + cosκ*ΔY)，
// 其中 m = f/H。于是 (x, y) 与 (ΔX, ΔY) 是相似变换，可线性求出 m、κ、Xs、Ys，
// 再由 H = f/m 得 Zs = 地面平均高程 + H。
bool estimate_initial(const double* gx, const double* gy, const double* gz,
                      const double* ox, const double* oy,
                      int n, double f, Params* p)
{
    double mx = 0.0, my = 0.0, mz = 0.0;
    for (int i = 0; i < n; ++i) {
        mx += gx[i]; my += gy[i]; mz += gz[i];
    }
    mx /= n; my /= n; mz /= n;

    // 未知数：tx, ty, a, b
    double nn[16] = { 0.0 };
    double rhs[4] = { 0.0 };
    for (int i = 0; i < n; ++i) {
        const double dx = gx[i] - mx;
        const double dy = gy[i] - my;

        const double row1[4] = { 1.0, 0.0, dx, dy };
        const double row2[4] = { 0.0, 1.0, dy, -dx };
        const double obs[2] = { ox[i], oy[i] };
        const double* rows[2] = { row1, row2 };

        for (int k = 0; k < 2; ++k) {
            const double* row = rows[k];
            for (int a = 0; a < 4; ++a) {
                rhs[a] += row[a] * obs[k];
                for (int b = 0; b < 4; ++b) nn[a * 4 + b] += row[a] * row[b];
            }
        }
    }

    std::vector<double> values, vectors;
    double cond = 0.0;
    if (!symmetric_eigen(nn, 4, values, vectors, &cond)) {
        pgm::set_error("控制点平面分布退化，无法估计初值；请手动给出外方位初值");
        return false;
    }
    double sol[4] = { 0.0 };
    solve_from_eigen(values, vectors, 4, rhs, sol);

    const double tx = sol[0], ty = sol[1], a = sol[2], b = sol[3];
    const double m = std::sqrt(a * a + b * b);
    if (!(m > 0.0) || !std::isfinite(m)) {
        pgm::set_error("控制点平面分布退化，相似变换比例为零；请手动给出外方位初值");
        return false;
    }

    const double h = f / m;
    if (!(h > 0.0) || !std::isfinite(h)) {
        pgm::set_error("由控制点反算的摄影高度非正；请手动给出外方位初值");
        return false;
    }

    // [tx; ty] = [[a, b], [-b, a]] * [Sx; Sy]，S = 地面重心 - 摄影中心
    const double m2 = m * m;
    const double sx = (a * tx - b * ty) / m2;
    const double sy = (b * tx + a * ty) / m2;

    p->xs = mx - sx;
    p->ys = my - sy;
    p->zs = mz + h;
    p->phi = 0.0;
    p->omega = 0.0;
    p->kappa = std::atan2(b, a);   // a = m*cosκ，b = m*sinκ
    return true;
}

// ---------------------------------------------------------------- 输入检查

int32_t validate_resection(int32_t count, const double* ox, const double* oy,
                           const double* gx, const double* gy, const double* gz, double f)
{
    if (count < kMinControlPoints) {
        pgm::set_error("后方交会至少需要 4 个控制点；3 个时多余观测为 0，残差恒为 0，无法检核");
        return PGM_ERR_TOO_FEW_POINTS;
    }
    if (ox == nullptr || oy == nullptr || gx == nullptr || gy == nullptr || gz == nullptr) {
        pgm::set_error("像点坐标或地面坐标数组为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (!(f > 0.0) || !std::isfinite(f)) {
        pgm::set_error("主距必须为正的有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    const int n = count;
    if (!finite_all(ox, n) || !finite_all(oy, n) || !finite_all(gx, n) ||
        !finite_all(gy, n) || !finite_all(gz, n)) {
        pgm::set_error("输入中含非有限数值");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    return PGM_OK;
}

}  // namespace

extern "C" {

int32_t pgm_resection(const pgm_resection_input* in, pgm_resection_output* out)
{
    pgm::set_error("");

    if (in == nullptr || out == nullptr) {
        pgm::set_error("输入或输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    // 残差缓冲区由调用方预分配，memset 前先存下指针
    double* const residual_x = out->residual_x_mm;
    double* const residual_y = out->residual_y_mm;

    std::memset(out, 0, sizeof(*out));
    out->residual_x_mm = residual_x;
    out->residual_y_mm = residual_y;

    const int n = in->count;
    const int32_t bad = validate_resection(in->count, in->image_x_mm, in->image_y_mm,
                                           in->ground_x, in->ground_y, in->ground_z,
                                           in->focal_length_mm);
    if (bad != PGM_OK) return bad;

    try {
        Params p{};
        if (in->use_initial != 0) {
            p.xs    = in->initial_xs;
            p.ys    = in->initial_ys;
            p.zs    = in->initial_zs;
            p.phi   = in->initial_phi_rad;
            p.omega = in->initial_omega_rad;
            p.kappa = in->initial_kappa_rad;
            const double given[6] = { p.xs, p.ys, p.zs, p.phi, p.omega, p.kappa };
            if (!finite_all(given, 6)) {
                pgm::set_error("给定的外方位初值中含非有限数值");
                return PGM_ERR_INVALID_ARGUMENT;
            }
        } else if (!estimate_initial(in->ground_x, in->ground_y, in->ground_z,
                                     in->image_x_mm, in->image_y_mm, n,
                                     in->focal_length_mm, &p)) {
            return PGM_ERR_DEGENERATE;
        }

        const int max_iter = (in->max_iterations > 0) ? in->max_iterations : kDefaultMaxIterations;
        const double tol_ang =
            (in->tolerance_angle > 0.0) ? in->tolerance_angle : kDefaultAngleTolerance;

        // 长度阈值默认相对摄影距离，避免与地面坐标量纲挂钩
        double mean_range = 0.0;
        for (int i = 0; i < n; ++i) {
            const double dx = in->ground_x[i] - p.xs;
            const double dy = in->ground_y[i] - p.ys;
            const double dz = in->ground_z[i] - p.zs;
            mean_range += std::sqrt(dx * dx + dy * dy + dz * dz);
        }
        mean_range /= n;
        const double tol_len = (in->tolerance_length > 0.0)
                                   ? in->tolerance_length
                                   : kDefaultLengthTolerance * (mean_range > 0.0 ? mean_range : 1.0);

        const size_t rows = static_cast<size_t>(2 * n);
        std::vector<double> a(rows * kUnknowns);
        std::vector<double> l(rows);
        std::vector<double> col(kUnknowns), np(kUnknowns * kUnknowns), rhs(kUnknowns);
        std::vector<double> y(kUnknowns), delta(kUnknowns);

        bool converged = false;
        int iterations = 0;
        double condition = 0.0;
        double sum_v2 = 0.0;

        for (int iter = 1; iter <= max_iter; ++iter) {
            if (!build_system(in->ground_x, in->ground_y, in->ground_z,
                              in->image_x_mm, in->image_y_mm, n,
                              in->focal_length_mm, p, a.data(), l.data(), &sum_v2)) {
                pgm::set_error("有控制点落在相机背后（W >= 0），检查初值或地面坐标");
                return PGM_ERR_INVALID_ARGUMENT;
            }

            // 列平衡：地面坐标用米、像点用毫米、角度用弧度，三套量纲混在一个
            // 法方程里条件数会差几个数量级，先按列范数归一化再解。
            for (int j = 0; j < kUnknowns; ++j) {
                double s = 0.0;
                for (size_t i = 0; i < rows; ++i) s += a[i * kUnknowns + j] * a[i * kUnknowns + j];
                col[j] = std::sqrt(s);
                if (!(col[j] > 0.0)) {
                    pgm::set_error("法方程某列全为零，未知数不可分辨；控制点分布退化");
                    return PGM_ERR_DEGENERATE;
                }
            }

            for (int i = 0; i < kUnknowns; ++i) {
                double s = 0.0;
                for (size_t k = 0; k < rows; ++k) {
                    s += (a[k * kUnknowns + i] / col[i]) * l[k];
                }
                rhs[i] = s;
                for (int j = 0; j < kUnknowns; ++j) {
                    double t = 0.0;
                    for (size_t k = 0; k < rows; ++k) {
                        t += (a[k * kUnknowns + i] / col[i]) * (a[k * kUnknowns + j] / col[j]);
                    }
                    np[i * kUnknowns + j] = t;
                }
            }

            std::vector<double> values, vectors;
            if (!symmetric_eigen(np.data(), kUnknowns, values, vectors, &condition)) {
                pgm::set_error("控制点几何构型退化，法方程秩亏；请让控制点铺开成面而不是排成一条线");
                return PGM_ERR_DEGENERATE;
            }
            solve_from_eigen(values, vectors, kUnknowns, rhs.data(), y.data());

            for (int j = 0; j < kUnknowns; ++j) delta[j] = y[j] / col[j];

            const bool position_ok = std::fabs(delta[0]) < tol_len &&
                                     std::fabs(delta[1]) < tol_len &&
                                     std::fabs(delta[2]) < tol_len;
            const bool angle_ok = std::fabs(delta[3]) < tol_ang &&
                                  std::fabs(delta[4]) < tol_ang &&
                                  std::fabs(delta[5]) < tol_ang;
            if (position_ok && angle_ok) {
                p.xs    += delta[0];
                p.ys    += delta[1];
                p.zs    += delta[2];
                p.phi   += delta[3];
                p.omega += delta[4];
                p.kappa += delta[5];
                iterations = iter;
                converged = true;
                break;
            }

            // 阻尼：初值不够好时整步可能让残差变大甚至把点甩到相机背后，
            // 那就把步长逐次减半，直到残差平方和下降为止。
            double alpha = 1.0;
            Params trial = p;
            bool accepted = false;
            for (int t = 0; t < 12; ++t) {
                trial = p;
                trial.xs    += alpha * delta[0];
                trial.ys    += alpha * delta[1];
                trial.zs    += alpha * delta[2];
                trial.phi   += alpha * delta[3];
                trial.omega += alpha * delta[4];
                trial.kappa += alpha * delta[5];

                double s = 0.0;
                if (residual_sum(in->ground_x, in->ground_y, in->ground_z,
                                 in->image_x_mm, in->image_y_mm, n,
                                 in->focal_length_mm, trial, &s) &&
                    s <= sum_v2) {
                    accepted = true;
                    break;
                }
                alpha *= 0.5;
            }
            if (!accepted) break;  // 残差已经降不下去，交给下面的收尾逻辑

            p = trial;
            iterations = iter;
        }

        // ---- 在最终点重算：残差、单位权中误差、参数协方差
        if (!build_system(in->ground_x, in->ground_y, in->ground_z,
                          in->image_x_mm, in->image_y_mm, n,
                          in->focal_length_mm, p, a.data(), l.data(), &sum_v2)) {
            pgm::set_error("解算结果使控制点落在相机背后（W >= 0）；迭代发散");
            return PGM_ERR_NOT_CONVERGED;
        }

        double max_abs = 0.0;
        for (int i = 0; i < n; ++i) {
            const double vx = -l[2 * i];
            const double vy = -l[2 * i + 1];
            if (std::fabs(vx) > max_abs) max_abs = std::fabs(vx);
            if (std::fabs(vy) > max_abs) max_abs = std::fabs(vy);
            if (out->residual_x_mm != nullptr) out->residual_x_mm[i] = vx;
            if (out->residual_y_mm != nullptr) out->residual_y_mm[i] = vy;
        }

        const int redundancy = 2 * n - 6;
        const double sigma0 = std::sqrt(sum_v2 / redundancy);

        // 协方差：Cov = sigma0^2 * D^-1 (A'^T A')^-1 D^-1
        for (int j = 0; j < kUnknowns; ++j) {
            double s = 0.0;
            for (size_t i = 0; i < rows; ++i) s += a[i * kUnknowns + j] * a[i * kUnknowns + j];
            col[j] = std::sqrt(s);
        }
        for (int i = 0; i < kUnknowns; ++i) {
            for (int j = 0; j < kUnknowns; ++j) {
                double t = 0.0;
                for (size_t k = 0; k < rows; ++k) {
                    t += (a[k * kUnknowns + i] / col[i]) * (a[k * kUnknowns + j] / col[j]);
                }
                np[i * kUnknowns + j] = t;
            }
        }

        std::vector<double> values, vectors;
        if (!symmetric_eigen(np.data(), kUnknowns, values, vectors, &condition)) {
            pgm::set_error("控制点几何构型退化，法方程秩亏；请让控制点铺开成面而不是排成一条线");
            return PGM_ERR_DEGENERATE;
        }
        std::vector<double> qdiag(kUnknowns, 0.0);
        diag_inverse_from_eigen(values, vectors, kUnknowns, qdiag.data());

        double m_param[kUnknowns];
        for (int j = 0; j < kUnknowns; ++j) {
            m_param[j] = (col[j] > 0.0) ? sigma0 * std::sqrt(qdiag[j]) / col[j] : 0.0;
        }

        const Mat3 r = make_rotation(p.phi, p.omega, p.kappa).total;

        out->xs = p.xs;
        out->ys = p.ys;
        out->zs = p.zs;
        out->phi_rad = p.phi;
        out->omega_rad = p.omega;
        out->kappa_rad = p.kappa;
        out->phi_deg = deg(p.phi);
        out->omega_deg = deg(p.omega);
        out->kappa_deg = deg(p.kappa);
        for (int k = 0; k < 9; ++k) out->rotation[k] = r.m[k];

        out->iterations = iterations;
        out->used_count = n;
        out->redundancy = redundancy;
        out->sigma0_mm = sigma0;
        out->max_abs_residual_mm = max_abs;

        out->m_xs = m_param[0];
        out->m_ys = m_param[1];
        out->m_zs = m_param[2];
        out->m_phi_rad = m_param[3];
        out->m_omega_rad = m_param[4];
        out->m_kappa_rad = m_param[5];
        out->condition = condition;

        if (!converged) {
            pgm::set_error("迭代到上限仍未收敛，控制点分布或初值可能有问题；结果仅供诊断");
            return PGM_ERR_NOT_CONVERGED;
        }
        return PGM_OK;
    }
    catch (const std::bad_alloc&) {
        pgm::set_error("内存不足");
        return PGM_ERR_INTERNAL;
    }
    catch (...) {
        pgm::set_error("解算过程中发生未预期异常");
        return PGM_ERR_INTERNAL;
    }
}

int32_t pgm_project_point(double focal_length_mm,
                          double xs, double ys, double zs,
                          double phi_rad, double omega_rad, double kappa_rad,
                          double ground_x, double ground_y, double ground_z,
                          double* out_x_mm, double* out_y_mm)
{
    pgm::set_error("");

    if (out_x_mm == nullptr || out_y_mm == nullptr) {
        pgm::set_error("输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (!(focal_length_mm > 0.0) || !std::isfinite(focal_length_mm)) {
        pgm::set_error("主距必须为正的有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    const double given[9] = { xs, ys, zs, phi_rad, omega_rad, kappa_rad,
                              ground_x, ground_y, ground_z };
    if (!finite_all(given, 9)) {
        pgm::set_error("输入中含非有限数值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    const Mat3 r = make_rotation(phi_rad, omega_rad, kappa_rad).total;

    const double d[3] = { ground_x - xs, ground_y - ys, ground_z - zs };
    double u[3];
    mat_vec(r, d, u);
    if (!(u[2] < 0.0)) {
        pgm::set_error("物点落在相机背后（W >= 0），检查地面坐标与摄影中心");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    *out_x_mm = -focal_length_mm * u[0] / u[2];
    *out_y_mm = -focal_length_mm * u[1] / u[2];
    return PGM_OK;
}

int32_t pgm_resection_design(int32_t count,
                             const double* image_x_mm,
                             const double* image_y_mm,
                             const double* ground_x,
                             const double* ground_y,
                             const double* ground_z,
                             double focal_length_mm,
                             double xs, double ys, double zs,
                             double phi_rad, double omega_rad, double kappa_rad,
                             double* out_a,
                             double* out_l)
{
    pgm::set_error("");

    if (out_a == nullptr) {
        pgm::set_error("设计矩阵缓冲区为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    const int32_t bad = validate_resection(count, image_x_mm, image_y_mm,
                                           ground_x, ground_y, ground_z, focal_length_mm);
    if (bad != PGM_OK) return bad;

    const double given[6] = { xs, ys, zs, phi_rad, omega_rad, kappa_rad };
    if (!finite_all(given, 6)) {
        pgm::set_error("外方位元素中含非有限数值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    const Params p{ xs, ys, zs, phi_rad, omega_rad, kappa_rad };
    const int n = count;
    std::vector<double> a(static_cast<size_t>(2 * n) * kUnknowns);
    std::vector<double> l(static_cast<size_t>(2 * n), 0.0);
    double sum_v2 = 0.0;

    if (!build_system(ground_x, ground_y, ground_z, image_x_mm, image_y_mm, n,
                      focal_length_mm, p, a.data(), l.data(), &sum_v2)) {
        pgm::set_error("有控制点落在相机背后（W >= 0）");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    std::memcpy(out_a, a.data(), a.size() * sizeof(double));
    if (out_l != nullptr) std::memcpy(out_l, l.data(), l.size() * sizeof(double));
    return PGM_OK;
}

}  // extern "C"
