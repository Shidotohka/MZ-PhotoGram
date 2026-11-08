// 绝对定向：七参数相似变换 g = λ R m + t 的闭合解。
//
// 推导见 Docs/算法笔记.md §9（绝对定向）。这里只复述落到代码上的那几步：
//
//   1. 重心化，平移精确消掉：m' = m - m̄，g' = g - ḡ
//   2. H = Σ m' g'^T，把它 SVD 成 U Σ V^T
//   3. R = V diag(1,1,δ) U^T，δ = det(U)det(V)（δ = -1 就是手性守卫：数据镜像时
//      最优的正交解是反射，修正后得到的最好真旋转也贴不上，残差会大且呈系统性）
//   4. λ = tr(RH) / Σ|m'|²（与文档的 (σ1+σ2+δσ3)/Σ|m'|² 是同一个量）
//   5. t = ḡ - λ R m̄
//
// 不重新造 SVD：linalg.hpp 里已经有 Jacobi 对称阵特征分解，3x3 的 SVD 就由
// H^T H 的特征分解得到（右奇异向量 = 特征向量，奇异值 = 特征值的平方根，
// 左奇异向量 = H v / σ）。库内已经有的一样东西不写第二份。

#include "photogrammetry.h"

#include "geometry.hpp"
#include "internal.hpp"
#include "linalg.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace {

constexpr int32_t kMinPoints = 3;

// 判共线：σ2/σ1 低于它，绕该直线的旋转不可测。
constexpr double kCollinearTolerance = 1e-10;

// σ3/σ1 低于它，第三奇异方向按不可观处理（共面数据就是这种）。
constexpr double kThirdTolerance = 1e-10;

inline pgm::Mat3 transpose(const pgm::Mat3& a)
{
    return pgm::Mat3{ {
        a.m[0], a.m[3], a.m[6],
        a.m[1], a.m[4], a.m[7],
        a.m[2], a.m[5], a.m[8],
    } };
}

inline double determinant(const pgm::Mat3& a)
{
    return a.m[0] * (a.m[4] * a.m[8] - a.m[5] * a.m[7]) -
           a.m[1] * (a.m[3] * a.m[8] - a.m[5] * a.m[6]) +
           a.m[2] * (a.m[3] * a.m[7] - a.m[4] * a.m[6]);
}

// 3x3 的 SVD，靠 H^T H 的对称特征分解（复用库内 Jacobi）。
// u / v 都是列主：第 k 列 = 第 k 个奇异向量。
struct Svd3
{
    double  sigma[3];
    pgm::Mat3 u;
    pgm::Mat3 v;
};

void svd3(const pgm::Mat3& h, Svd3& out)
{
    // gram = H^T H
    double gram[9];
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) {
            double sum = 0.0;
            for (int k = 0; k < 3; ++k) sum += h.m[k * 3 + i] * h.m[k * 3 + j];
            gram[i * 3 + j] = sum;
        }
    }

    std::vector<double> values, vectors;
    double condition = 0.0;
    // 返回值是秩检查，这里故意忽略：H 秩 2（控制点共面）是合法输入，
    // 只有秩 1（共线）才算解不出来，那个由调用方按 σ2 判。
    pgm::symmetric_eigen(gram, 3, values, vectors, &condition);

    int order[3] = { 0, 1, 2 };
    for (int a = 0; a < 2; ++a) {
        for (int b = a + 1; b < 3; ++b) {
            if (values[order[b]] > values[order[a]]) {
                const int t = order[a];
                order[a]  = order[b];
                order[b]  = t;
            }
        }
    }

    for (int k = 0; k < 3; ++k) {
        const double lambda = values[order[k]];
        out.sigma[k] = std::sqrt(lambda > 0.0 ? lambda : 0.0);
        for (int i = 0; i < 3; ++i) out.v.m[i * 3 + k] = vectors[i * 3 + order[k]];
    }

    // 左奇异向量的前两列：u_k = H v_k / σ_k
    for (int k = 0; k < 2; ++k) {
        const double inv = (out.sigma[k] > 0.0) ? 1.0 / out.sigma[k] : 0.0;
        for (int i = 0; i < 3; ++i) {
            const double sum = h.m[i * 3 + 0] * out.v.m[0 * 3 + k] +
                               h.m[i * 3 + 1] * out.v.m[1 * 3 + k] +
                               h.m[i * 3 + 2] * out.v.m[2 * 3 + k];
            out.u.m[i * 3 + k] = sum * inv;
        }
    }

    // 第三列：σ3 可观时照样除；不可观（共面）时用前两列的叉积补。
    // 叉积的符号不影响最终 R —— 见下面 R 组装后的正交化说明。
    const double relative = out.sigma[2] / (out.sigma[0] > 0.0 ? out.sigma[0] : 1.0);
    if (relative > kThirdTolerance) {
        const double inv = 1.0 / out.sigma[2];
        for (int i = 0; i < 3; ++i) {
            const double sum = h.m[i * 3 + 0] * out.v.m[0 * 3 + 2] +
                               h.m[i * 3 + 1] * out.v.m[1 * 3 + 2] +
                               h.m[i * 3 + 2] * out.v.m[2 * 3 + 2];
            out.u.m[i * 3 + 2] = sum * inv;
        }
    } else {
        for (int i = 0; i < 3; ++i) {
            out.u.m[i * 3 + 2] = out.u.m[(i + 1) % 3 * 3 + 0] * out.u.m[(i + 2) % 3 * 3 + 1] -
                                 out.u.m[(i + 2) % 3 * 3 + 0] * out.u.m[(i + 1) % 3 * 3 + 1];
        }
    }
}

// 把 R 按 R = Rκ·Rω·Rφ 分解（与 geometry.hpp 的 make_rotation 互逆）。
// ω = ±90° 时 φ/κ 互相吞掉，取 φ = 0 的约定。
void rotation_to_angles(const pgm::Mat3& r, double& phi, double& omega, double& kappa)
{
    double s = r.m[7];
    if (s > 1.0) s = 1.0;
    if (s < -1.0) s = -1.0;
    omega = -std::asin(s);

    const double cos_omega = std::sqrt(std::max(0.0, 1.0 - s * s));
    if (cos_omega > 1e-12) {
        phi   = std::atan2(-r.m[6], r.m[8]);
        kappa = std::atan2(r.m[1], r.m[4]);
    } else {
        phi   = 0.0;
        kappa = std::atan2(-r.m[3], r.m[0]);
    }
}

}  // namespace

int32_t pgm_absolute_orientation(const pgm_absolute_orientation_input*  in,
                                 pgm_absolute_orientation_output*       out)
{
    pgm::set_error("");

    if (in == nullptr || out == nullptr) {
        pgm::set_error("输入或输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    // 逐点残差缓冲区是调用方进来之前填好的，memset 会一起清掉——先捞出来。
    // （相对定向那边踩过这个坑：清了之后逐点残差永远写不出来，统计量却正常。）
    double* residual_xyz    = out->residual_xyz;
    double* residual_length = out->residual_length;

    std::memset(out, 0, sizeof(*out));
    out->residual_xyz    = residual_xyz;
    out->residual_length = residual_length;

    if (in->count < kMinPoints) {
        pgm::set_error("控制点少于 3 个，七参数解不出来");
        return PGM_ERR_TOO_FEW_POINTS;
    }
    if (in->model_x == nullptr || in->model_y == nullptr || in->model_z == nullptr ||
        in->ground_x == nullptr || in->ground_y == nullptr || in->ground_z == nullptr) {
        pgm::set_error("模型坐标或地面坐标数组指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    const int32_t n = in->count;
    for (int32_t i = 0; i < n; ++i) {
        const double v[6] = { in->model_x[i], in->model_y[i], in->model_z[i],
                              in->ground_x[i], in->ground_y[i], in->ground_z[i] };
        if (!pgm::finite_all(v, 6)) {
            pgm::set_error("坐标中含非有限数值");
            return PGM_ERR_INVALID_ARGUMENT;
        }
    }

    try {
        // ---- 1 重心 ----
        double model_bar[3]  = { 0.0, 0.0, 0.0 };
        double ground_bar[3] = { 0.0, 0.0, 0.0 };
        for (int32_t i = 0; i < n; ++i) {
            model_bar[0]  += in->model_x[i];
            model_bar[1]  += in->model_y[i];
            model_bar[2]  += in->model_z[i];
            ground_bar[0] += in->ground_x[i];
            ground_bar[1] += in->ground_y[i];
            ground_bar[2] += in->ground_z[i];
        }
        for (int k = 0; k < 3; ++k) {
            model_bar[k]  /= n;
            ground_bar[k] /= n;
        }

        // ---- 2 H = Σ m' g'^T，同时攒 Σ|m'|² ----
        pgm::Mat3 h{ { 0, 0, 0, 0, 0, 0, 0, 0, 0 } };
        double model_scatter = 0.0;
        for (int32_t i = 0; i < n; ++i) {
            const double m[3] = { in->model_x[i] - model_bar[0],
                                  in->model_y[i] - model_bar[1],
                                  in->model_z[i] - model_bar[2] };
            const double g[3] = { in->ground_x[i] - ground_bar[0],
                                  in->ground_y[i] - ground_bar[1],
                                  in->ground_z[i] - ground_bar[2] };
            for (int a = 0; a < 3; ++a) {
                for (int b = 0; b < 3; ++b) h.m[a * 3 + b] += m[a] * g[b];
                model_scatter += m[a] * m[a];
            }
        }

        if (!(model_scatter > 0.0)) {
            pgm::set_error("所有控制点的模型坐标重合，尺度无从定义");
            return PGM_ERR_DEGENERATE;
        }

        // ---- 3 SVD 与手性守卫 ----
        Svd3 svd;
        svd3(h, svd);
        out->singular[0] = svd.sigma[0];
        out->singular[1] = svd.sigma[1];
        out->singular[2] = svd.sigma[2];

        // σ2 ≈ 0 ⇒ 控制点共线 ⇒ 绕该直线的旋转不可测
        if (!(svd.sigma[1] > kCollinearTolerance * svd.sigma[0])) {
            out->condition = 1e300;
            pgm::set_error("控制点近共线：绕这条线的旋转不可测，请补不共线的点");
            return PGM_ERR_DEGENERATE;
        }
        out->condition = svd.sigma[0] / svd.sigma[1];

        const double delta = determinant(svd.u) * determinant(svd.v);

        pgm::Mat3 d{ { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, delta } };
        pgm::Mat3 vt = transpose(svd.v);
        pgm::Mat3 vd;
        pgm::mat_mul(svd.v, d, vd);
        pgm::Mat3 r;
        pgm::mat_mul(vd, transpose(svd.u), r);

        // 正交化：前两列定死，第三列取叉积。
        // 这一手专门对付「控制点共面」：那时第三奇异方向不可观，U 的第三列怎么取
        // 都行，而 R 的第三列必须与前两列构成右手系。前两列对 V 第三列符号的
        // 依赖恰好抵消，所以规范化之后 R 与那个任意符号无关。
        {
            double c0[3] = { r.m[0], r.m[3], r.m[6] };
            double c1[3] = { r.m[1], r.m[4], r.m[7] };
            double len0 = std::sqrt(c0[0] * c0[0] + c0[1] * c0[1] + c0[2] * c0[2]);
            if (!(len0 > 0.0)) {
                pgm::set_error("旋转阵退化，无法正交化");
                return PGM_ERR_DEGENERATE;
            }
            for (int i = 0; i < 3; ++i) c0[i] /= len0;

            double dot = c0[0] * c1[0] + c0[1] * c1[1] + c0[2] * c1[2];
            for (int i = 0; i < 3; ++i) c1[i] -= dot * c0[i];
            const double len1 = std::sqrt(c1[0] * c1[0] + c1[1] * c1[1] + c1[2] * c1[2]);
            if (!(len1 > 0.0)) {
                pgm::set_error("旋转阵退化，无法正交化");
                return PGM_ERR_DEGENERATE;
            }
            for (int i = 0; i < 3; ++i) c1[i] /= len1;

            const double c2[3] = { c0[1] * c1[2] - c0[2] * c1[1],
                                   c0[2] * c1[0] - c0[0] * c1[2],
                                   c0[0] * c1[1] - c0[1] * c1[0] };
            for (int i = 0; i < 3; ++i) {
                r.m[i * 3 + 0] = c0[i];
                r.m[i * 3 + 1] = c1[i];
                r.m[i * 3 + 2] = c2[i];
            }
        }

        // ---- 4 尺度：λ = tr(R H) / Σ|m'|² ----
        double trace_rh = 0.0;
        for (int a = 0; a < 3; ++a) {
            for (int b = 0; b < 3; ++b) trace_rh += r.m[a * 3 + b] * h.m[b * 3 + a];
        }
        const double scale = trace_rh / model_scatter;
        if (!(scale > 0.0)) {
            pgm::set_error("解出的尺度非正：控制点数据可能镜像或退化");
            return PGM_ERR_DEGENERATE;
        }

        // ---- 5 平移：t = ḡ - λ R m̄ ----
        double rm[3];
        pgm::mat_vec(r, model_bar, rm);
        double t[3];
        for (int k = 0; k < 3; ++k) t[k] = ground_bar[k] - scale * rm[k];

        // ---- 6 残差与精度 ----
        double sum_sq = 0.0;
        double max_abs = 0.0;
        for (int32_t i = 0; i < n; ++i) {
            const double m[3] = { in->model_x[i], in->model_y[i], in->model_z[i] };
            double predicted[3];
            pgm::mat_vec(r, m, predicted);
            const double residual[3] = {
                in->ground_x[i] - (scale * predicted[0] + t[0]),
                in->ground_y[i] - (scale * predicted[1] + t[1]),
                in->ground_z[i] - (scale * predicted[2] + t[2]),
            };
            const double length = std::sqrt(residual[0] * residual[0] +
                                            residual[1] * residual[1] +
                                            residual[2] * residual[2]);
            sum_sq += length * length;
            if (length > max_abs) max_abs = length;

            if (out->residual_xyz != nullptr) {
                out->residual_xyz[i * 3 + 0] = residual[0];
                out->residual_xyz[i * 3 + 1] = residual[1];
                out->residual_xyz[i * 3 + 2] = residual[2];
            }
            if (out->residual_length != nullptr) out->residual_length[i] = length;
        }

        out->scale           = scale;
        out->rms             = std::sqrt(sum_sq / n);
        out->max_abs_residual = max_abs;
        out->used_count      = n;
        out->redundancy      = 3 * n - 7;
        out->sigma0          = (out->redundancy > 0)
                                   ? std::sqrt(sum_sq / out->redundancy)
                                   : 0.0;
        for (int k = 0; k < 9; ++k) out->rotation[k] = r.m[k];
        for (int k = 0; k < 3; ++k) out->translation[k] = t[k];

        double phi = 0.0, omega = 0.0, kappa = 0.0;
        rotation_to_angles(r, phi, omega, kappa);
        out->phi_rad = phi;
        out->omega_rad = omega;
        out->kappa_rad = kappa;
        const double deg = 180.0 / 3.14159265358979323846;
        out->phi_deg = phi * deg;
        out->omega_deg = omega * deg;
        out->kappa_deg = kappa * deg;

        // ---- 7 镜像诊断 ----
        // 允许反射时能达到的最好值是 σ1+σ2+σ3；真旋转只能做到 tr(RH)。
        // 两者差得明显，说明数据更想要一个反射——即左右手系搞反了。
        const double allowed = svd.sigma[0] + svd.sigma[1] + svd.sigma[2];
        out->mirror_suspected = ((allowed - trace_rh) > 1e-8 * allowed) ? 1 : 0;

        return PGM_OK;
    } catch (const std::bad_alloc&) {
        pgm::set_error("内存不足");
        return PGM_ERR_INTERNAL;
    } catch (...) {
        pgm::set_error("绝对定向出现未预期异常");
        return PGM_ERR_INTERNAL;
    }
}
