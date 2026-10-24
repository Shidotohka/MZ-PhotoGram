// 相对定向：共面条件 + 迭代最小二乘。算法与推导见 Docs/相对定向算法.md。
//
// 输入是若干同名点对（左片、右片各一个像平面坐标，mm，原点像主点，x 向右 y 向上），
// 输出 5 个相对方位元素：基线方向（2 个自由度）+ 右片相对左片的三个角。
//
// 坐标约定（与 pgm_resection、pgm_forward_intersection 同一条链，别再另起一套）：
//   · 像空间辅助坐标系 = 左片像空间系，原点在左片摄影中心，取 R_1 = I。
//   · 像点方向 d = (x, y, -f)，w = -f < 0，地面在 w < 0 一侧。
//   · **本文件里的 R 是教材的那个 R**：把像点方向转到辅助系，即
//         d2 = R * (x2, y2, -f)
//     而 geometry.hpp 的 R 是反的（地面 -> 像空间），两者互为转置。出口的
//     rotation[] 按几何库方向（辅助系 -> 像）给出，所以出函数前转置一次。
//     混用会整体差一个符号，而且**照样出数**——见 Docs/算法笔记.md 7.6。
//   · 每次更新左乘：R <- R(ω) * R。右乘是另一个量，改起来也照样出数。
//
// 这三条约定一起决定了几件事：共面条件在初值处的残差等于 -|b|·f 乘上下视差、
// 上下视差的正负号、以及模型点落在 w < 0 一侧（交会出的 t > 0）。
//
// 规准（为什么不无条件冻结 b_x）：
//   教材取 b_x = B 常数，隐含"两片沿航线前后排列"。旁向像对（相邻航线、30% 旁向
//   重叠）的基线主要落在 v 方向，b_x -> 0 时 (1, b_y, b_z) 表示不出这条基线，
//   f(x1-x2) 那一列随之塌成零，方程秩亏——实测会停在一个残差明显不为零的假驻点。
//   这里改成"冻结初始基线方向的主分量"，航向像对算出来仍落在 b_x 上，自动退回教材。
//
// 初值取平均视差方向：竖直摄影、平坦地面下 (p, q) ∝ (b_u, b_v)，平均视差方向就是
// 基线方向。这一条让旁向像对从 17 轮不收敛变成 4 轮收敛（同上文档第六节）。

#include "photogrammetry.h"
#include "internal.hpp"
#include "geometry.hpp"
#include "linalg.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace {

using pgm::Mat3;
using pgm::mat_mul;
using pgm::mat_vec;
using pgm::dot3;
using pgm::symmetric_eigen;
using pgm::solve_from_eigen;

constexpr double kPi = 3.14159265358979323846;

// 未知数：两个基线分量 + 三个旋转改正数
constexpr int kUnknowns = 5;

constexpr int kMinPairs = 5;
constexpr int kMinPairsForReject = 7;   // 剔点后至少留 6 对，才有 1 个多余观测
constexpr int kMaxRejectRounds = 2;

constexpr int    kDefaultMaxIterations = 50;
constexpr double kDefaultTolerance = 1e-10;

// 平均视差小于它就没法定基线方向（同名点全重合）
constexpr double kMinParallaxMm = 1e-6;

// 不评精度时的剔点门槛下限。无噪声数据 σ0 = 0，不能拿 0 当门槛去剔点。
constexpr double kRejectFloorMm = 1e-2;

// |b| 的上限。超了说明迭代窜了，不当结果用。
constexpr double kMaxBaseline = 1e6;

double deg(double rad) { return rad * 180.0 / kPi; }

// ---------------------------------------------------------------- 旋转

// 角向量 -> 旋转阵（罗德里格斯）：R = I + a*[w]x + b*[w]x^2，按 |w| 展开。
// 小角度用泰勒级数避开 sin(0)/0。这里给的是**精确**旋转，不是一阶近似：迭代收敛
// 靠线性化求改正数，不靠把旋转本身做成一阶的。
void rotation_from_vector(const double w[3], Mat3* out)
{
    const double theta2 = w[0] * w[0] + w[1] * w[1] + w[2] * w[2];
    const double theta = std::sqrt(theta2);
    double a, b;
    if (theta < 1e-8) {
        a = 1.0 - theta2 / 6.0;
        b = 0.5 - theta2 / 24.0;
    } else {
        a = std::sin(theta) / theta;
        b = (1.0 - std::cos(theta)) / theta2;
    }
    const double x = w[0], y = w[1], z = w[2];
    out->m[0] = 1.0 - b * (y * y + z * z);
    out->m[1] = -a * z + b * x * y;
    out->m[2] = a * y + b * x * z;
    out->m[3] = a * z + b * x * y;
    out->m[4] = 1.0 - b * (x * x + z * z);
    out->m[5] = -a * x + b * y * z;
    out->m[6] = -a * y + b * x * z;
    out->m[7] = a * x + b * y * z;
    out->m[8] = 1.0 - b * (x * x + y * y);
}

Mat3 identity3()
{
    Mat3 r;
    std::memset(&r, 0, sizeof(r));
    r.m[0] = r.m[4] = r.m[8] = 1.0;
    return r;
}

Mat3 transposed(const Mat3& a)
{
    Mat3 t;
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) t.m[i * 3 + j] = a.m[j * 3 + i];
    }
    return t;
}

// R = R_kappa * R_omega * R_phi 的反解。只在出口用一次。
//
// 展开后第三行是 [-cω·sφ, -sω, cω·cφ]，第一行第二列是 sκ·cω、第二行第二列是
// cκ·cω，所以
//     φ = atan2(-R20, R22)，ω = atan2(-R21, sqrt(R20²+R22²))，κ = atan2(R01, R11)
// ω = ±90° 时 φ 与 κ 不可分（cω = 0），此时令 φ = 0，把转角全算进 κ。航摄像片
// 到不了这一步，但输出不该出 NaN。
void angles_from_rotation(const Mat3& r, double* phi, double* omega, double* kappa)
{
    const double cw = std::sqrt(r.m[6] * r.m[6] + r.m[8] * r.m[8]);
    *omega = std::atan2(-r.m[7], cw);
    if (cw > 1e-12) {
        *phi = std::atan2(-r.m[6], r.m[8]);
        *kappa = std::atan2(r.m[1], r.m[4]);
    } else {
        *phi = 0.0;
        *kappa = std::atan2(-r.m[3], r.m[0]);
    }
}

// ---------------------------------------------------------------- 误差方程

struct Samples
{
    int32_t n;
    const double* x1;
    const double* y1;
    const double* x2;
    const double* y2;
    double f;
};

// 一对同名点的一行误差方程，返回共面条件的残差 F。
//
//   F  = d2 · (b × d1)
//   行 = [ d1×d2 (三个基线分量),  (d1·d2)·b - (b·d2)·d1 (三个旋转分量) ]
//
// 六列里的基线三分量必须丢掉一个（规准），调用方按 free[2] 挑出两列。这就是文档
// 第六节的一般式；在初值处它精确退化成方框式那个式子。
double make_row(double f, double x1, double y1, double x2, double y2,
                const double b[3], const Mat3& r, double row6[6])
{
    const double d1[3] = { x1, y1, -f };
    const double img[3] = { x2, y2, -f };
    double d2[3];
    mat_vec(r, img, d2);

    const double n[3] = { b[1] * d1[2] - b[2] * d1[1],
                          b[2] * d1[0] - b[0] * d1[2],
                          b[0] * d1[1] - b[1] * d1[0] };     // b × d1
    const double F = d2[0] * n[0] + d2[1] * n[1] + d2[2] * n[2];

    row6[0] = d1[1] * d2[2] - d1[2] * d2[1];                 // d1 × d2
    row6[1] = d1[2] * d2[0] - d1[0] * d2[2];
    row6[2] = d1[0] * d2[1] - d1[1] * d2[0];

    const double dd = dot3(d1, d2);
    const double bd = dot3(b, d2);
    for (int k = 0; k < 3; ++k) row6[3 + k] = dd * b[k] - bd * d1[k];

    return F;
}

// 上下视差 q（mm）：右片同名点偏离左片同名点所定核线的垂直距离。
//
//   n = b × d1                核面法向量
//   q = -f · (d2·n) / (|n|·|d2|)
//
// 分子是 d2 偏离核面的量，除以 |d2| 化成"射线与核面的夹角正弦"，再乘 f 换成像面上
// 的 mm。负号是为了对上教材的 q = y_左 - y_右：竖直航向像对、R = I 时它精确退化成
// y1 - y2。
//
// **不要图省事写成 q = y1 - (-f·d2_v/d2_w)**：那个量是"把右片射线按 w 归一后投到
// 一个与左片平行的像平面"，它只把姿态找平，没有扣掉基线方向。航向像对上两者近似
// 相等（差在二阶），旁向像对上差得离谱——基线全在 v 方向时它等于 f·B_v/Z，实测
// 76 mm 且**不随定向精度下降**，拿来当精度指标会把好结果报成坏结果。
double vertical_parallax_mm(double f, double x1, double y1, double x2, double y2,
                            const double b[3], const Mat3& r)
{
    const double d1[3] = { x1, y1, -f };
    const double img[3] = { x2, y2, -f };
    double d2[3];
    mat_vec(r, img, d2);

    const double n[3] = { b[1] * d1[2] - b[2] * d1[1],
                          b[2] * d1[0] - b[0] * d1[2],
                          b[0] * d1[1] - b[1] * d1[0] };
    const double nn = std::sqrt(dot3(n, n));
    const double nd = std::sqrt(dot3(d2, d2));
    if (!(nn > 0.0) || !(nd > 0.0)) return 0.0;

    return -f * dot3(d2, n) / (nn * nd);
}

// ---------------------------------------------------------------- 迭代

struct SolveState
{
    double b[3];
    Mat3 r;
    int iterations;
    int n_used;
    double condition;
    double sigma0;
};

// 在给定的点集上解到收敛。b 与 R 既是输入也是输出：剔点后解不会跑远，接着上次的
// 解往下迭代比每次从头来省事。失败时 b/R 里是最后一次迭代值，仅供诊断。
int32_t solve_orientation(const Samples& s, const std::vector<char>& keep, const int free[2],
                          int max_iterations, double tolerance, SolveState* st,
                          std::vector<double>* q)
{
    int n_used = 0;
    for (int32_t i = 0; i < s.n; ++i) {
        if (keep[i]) ++n_used;
    }
    if (n_used < kMinPairs) {
        pgm::set_error("参与解算的同名点对少于 5 对，共面条件解不出来");
        return PGM_ERR_TOO_FEW_POINTS;
    }

    std::vector<double> values;
    std::vector<double> vectors;
    double np[kUnknowns * kUnknowns];
    double rhs[kUnknowns];
    double dx[kUnknowns];

    int done = 0;
    bool converged = false;

    for (int it = 0; it < max_iterations; ++it) {
        const double nb = std::sqrt(st->b[0] * st->b[0] + st->b[1] * st->b[1] +
                                    st->b[2] * st->b[2]);
        if (!(nb > 0.0) || !(nb < kMaxBaseline) || !std::isfinite(nb)) {
            pgm::set_error("基线范数异常，迭代发散");
            return PGM_ERR_DEGENERATE;
        }
        // 行统一除以 |b|·f：同一个因子不改变解，但让残差与 σ0 落在 mm 量纲上
        const double scale = nb * s.f;

        std::memset(np, 0, sizeof(np));
        std::memset(rhs, 0, sizeof(rhs));

        for (int32_t i = 0; i < s.n; ++i) {
            if (!keep[i]) continue;

            double row6[6];
            const double F = make_row(s.f, s.x1[i], s.y1[i], s.x2[i], s.y2[i], st->b, st->r, row6);
            if (!std::isfinite(F)) {
                pgm::set_error("共面条件残差出现非有限值");
                return PGM_ERR_DEGENERATE;
            }

            double a[kUnknowns];
            a[0] = row6[free[0]] / scale;
            a[1] = row6[free[1]] / scale;
            for (int t = 0; t < 3; ++t) a[2 + t] = row6[3 + t] / scale;

            const double l = -F / scale;   // 观测方程 a·x = l

            for (int p = 0; p < kUnknowns; ++p) {
                for (int r2 = 0; r2 < kUnknowns; ++r2) {
                    np[p * kUnknowns + r2] += a[p] * a[r2];
                }
                rhs[p] += a[p] * l;
            }
        }

        double cond = 0.0;
        if (!symmetric_eigen(np, kUnknowns, values, vectors, &cond)) {
            st->condition = cond;
            pgm::set_error("法方程秩亏：同名点退化（重合、共线或视差过小）");
            return PGM_ERR_DEGENERATE;
        }
        st->condition = cond;

        solve_from_eigen(values, vectors, kUnknowns, rhs, dx);

        for (int k = 0; k < 2; ++k) st->b[free[k]] += dx[k];

        Mat3 dr;
        rotation_from_vector(&dx[2], &dr);
        Mat3 next;
        mat_mul(dr, st->r, next);       // 左乘：R <- R(ω) * R
        st->r = next;

        ++done;

        double step = 0.0;
        for (int k = 0; k < kUnknowns; ++k) step = std::max(step, std::fabs(dx[k]));
        if (step < tolerance) {
            converged = true;
            break;
        }
    }

    st->iterations = done;
    st->n_used = n_used;

    // 残差两套：σ0 用解算时那套 mm 量纲的方程残差，q 用几何上下视差。两者应当吻合，
    // 差得远说明收敛没到位——这也是 M6 自检里的一条。
    const double nb = std::sqrt(st->b[0] * st->b[0] + st->b[1] * st->b[1] + st->b[2] * st->b[2]);
    const double scale = (nb > 0.0) ? nb * s.f : s.f;

    double sum_v2 = 0.0;
    for (int32_t i = 0; i < s.n; ++i) {
        double row6[6];
        const double F = make_row(s.f, s.x1[i], s.y1[i], s.x2[i], s.y2[i], st->b, st->r, row6);
        if (keep[i]) sum_v2 += (F / scale) * (F / scale);

        (*q)[i] = vertical_parallax_mm(s.f, s.x1[i], s.y1[i], s.x2[i], s.y2[i], st->b, st->r);
    }

    const int redundancy = n_used - kUnknowns;
    st->sigma0 = (redundancy > 0) ? std::sqrt(sum_v2 / redundancy) : 0.0;

    if (!converged) {
        pgm::set_error("迭代到上限仍未收敛");
        return PGM_ERR_NOT_CONVERGED;
    }
    return PGM_OK;
}

}  // namespace

extern "C" {

int32_t pgm_relative_orientation(const pgm_relative_orientation_input*  in,
                                 pgm_relative_orientation_output*       out)
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
    if (!std::isfinite(in->image_sigma_mm) || in->image_sigma_mm < 0.0) {
        pgm::set_error("像点坐标中误差必须为非负的有限值；不评精度时传 0");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    if (in->count < kMinPairs) {
        pgm::set_error("同名点对少于 5 对，共面条件解不出来");
        return PGM_ERR_TOO_FEW_POINTS;
    }
    if (in->left_x_mm == nullptr || in->left_y_mm == nullptr ||
        in->right_x_mm == nullptr || in->right_y_mm == nullptr) {
        pgm::set_error("同名点坐标数组指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    for (int32_t i = 0; i < in->count; ++i) {
        const double v[4] = { in->left_x_mm[i], in->left_y_mm[i],
                              in->right_x_mm[i], in->right_y_mm[i] };
        if (!pgm::finite_all(v, 4)) {
            pgm::set_error("同名点坐标中含非有限数值");
            return PGM_ERR_INVALID_ARGUMENT;
        }
    }

    const int    max_iterations = (in->max_iterations > 0) ? in->max_iterations
                                                          : kDefaultMaxIterations;
    const double tolerance      = (in->tolerance > 0.0) ? in->tolerance : kDefaultTolerance;

    try {
        Samples s;
        s.n  = in->count;
        s.x1 = in->left_x_mm;
        s.y1 = in->left_y_mm;
        s.x2 = in->right_x_mm;
        s.y2 = in->right_y_mm;
        s.f  = in->focal_length_mm;

        // ---- 初值：平均视差方向就是基线方向
        double sum_p = 0.0, sum_q = 0.0;
        for (int32_t i = 0; i < s.n; ++i) {
            sum_p += s.x1[i] - s.x2[i];
            sum_q += s.y1[i] - s.y2[i];
        }
        sum_p /= s.n;
        sum_q /= s.n;

        if (std::hypot(sum_p, sum_q) < kMinParallaxMm) {
            pgm::set_error("平均视差为零：同名点可能重合，无法确定基线方向");
            return PGM_ERR_DEGENERATE;
        }

        SolveState st;
        std::memset(&st, 0, sizeof(st));
        st.b[0] = sum_p;
        st.b[1] = sum_q;
        st.b[2] = 0.0;

        // 归一化用正的比例因子，保住方向符号：b 与 -b 都满足共面条件，但只有前者
        // 会把模型摆在相机前方
        double bmax = 0.0;
        for (int k = 0; k < 3; ++k) bmax = std::max(bmax, std::fabs(st.b[k]));
        if (!(bmax > 0.0) || !std::isfinite(bmax)) {
            pgm::set_error("无法由平均视差确定基线方向");
            return PGM_ERR_DEGENERATE;
        }
        for (int k = 0; k < 3; ++k) st.b[k] /= bmax;

        // ---- 规准：冻结基线方向的主分量
        int gauge = 0;
        for (int k = 1; k < 3; ++k) {
            if (std::fabs(st.b[k]) > std::fabs(st.b[gauge])) gauge = k;
        }
        int free2[2];
        int t = 0;
        for (int k = 0; k < 3; ++k) {
            if (k != gauge) free2[t++] = k;
        }

        st.r = identity3();
        st.iterations = 0;
        st.n_used = s.n;

        std::vector<char>   keep(static_cast<size_t>(s.n), 1);
        std::vector<double> q(static_cast<size_t>(s.n), 0.0);

        int32_t status = solve_orientation(s, keep, free2, max_iterations, tolerance, &st, &q);

        // ---- 粗差：|q| 超过 max(3σ0, 3σ_像点) 的当粗差剔掉重解，最多两轮。
        // 一个也不许剁掉太多：每轮最多剔 n/5，且解算点数不少于 7（剔完还有 1 个多余观测）。
        int rejected = 0;
        if (status == PGM_OK) {
            const double floor = (in->image_sigma_mm > 0.0) ? 3.0 * in->image_sigma_mm
                                                            : kRejectFloorMm;
            for (int round = 0; round < kMaxRejectRounds; ++round) {
                if (st.n_used < kMinPairsForReject) break;

                const double threshold = std::max(3.0 * st.sigma0, floor);

                std::vector<int> suspects;
                for (int32_t i = 0; i < s.n; ++i) {
                    if (keep[i] && std::fabs(q[i]) > threshold) suspects.push_back(i);
                }
                if (suspects.empty()) break;

                const int budget = std::max(1, st.n_used / 5);
                if (static_cast<int>(suspects.size()) > budget) {
                    std::sort(suspects.begin(), suspects.end(),
                              [&q](int a, int b) { return std::fabs(q[a]) > std::fabs(q[b]); });
                    suspects.resize(static_cast<size_t>(budget));
                }
                for (size_t k = 0; k < suspects.size(); ++k) {
                    keep[static_cast<size_t>(suspects[k])] = 0;
                    ++rejected;
                }

                const int32_t again =
                    solve_orientation(s, keep, free2, max_iterations, tolerance, &st, &q);
                if (again != PGM_OK) {
                    status = again;
                    break;
                }
            }
        }

        if (status != PGM_OK && status != PGM_ERR_NOT_CONVERGED) {
            return status;   // out 已清零
        }

        // ---- 出口：基线归一成单位方向，R 转置回几何库方向并反解三个角
        const double nb = std::sqrt(st.b[0] * st.b[0] + st.b[1] * st.b[1] + st.b[2] * st.b[2]);
        if (!(nb > 0.0) || !std::isfinite(nb)) {
            pgm::set_error("解出的基线范数异常");
            return PGM_ERR_DEGENERATE;
        }

        out->b_x = st.b[0] / nb;
        out->b_y = st.b[1] / nb;
        out->b_z = st.b[2] / nb;

        const Mat3 r_img = transposed(st.r);
        for (int i = 0; i < 9; ++i) out->rotation[i] = r_img.m[i];

        double phi = 0.0, omega = 0.0, kappa = 0.0;
        angles_from_rotation(r_img, &phi, &omega, &kappa);
        out->phi_rad   = phi;
        out->omega_rad = omega;
        out->kappa_rad = kappa;
        out->phi_deg   = deg(phi);
        out->omega_deg = deg(omega);
        out->kappa_deg = deg(kappa);

        out->gauge       = gauge;
        out->iterations  = st.iterations;
        out->used_count  = st.n_used;
        out->redundancy  = st.n_used - kUnknowns;
        out->rejected_count = rejected;
        out->condition   = st.condition;
        out->sigma0_mm   = st.sigma0;

        // 视差统计只用参与解算的点：被剔掉的那些 q 本来就大，混进来会把指标带偏
        double sum_q2 = 0.0, max_q = 0.0;
        for (int32_t i = 0; i < s.n; ++i) {
            if (!keep[i]) continue;
            sum_q2 += q[i] * q[i];
            max_q = std::max(max_q, std::fabs(q[i]));
        }
        out->q_rms_mm     = (st.n_used > 0) ? std::sqrt(sum_q2 / st.n_used) : 0.0;
        out->q_max_abs_mm = max_q;

        if (out->residual_q_mm != nullptr) {
            for (int32_t i = 0; i < s.n; ++i) out->residual_q_mm[i] = q[i];
        }
        if (out->used_flags != nullptr) {
            for (int32_t i = 0; i < s.n; ++i) out->used_flags[i] = keep[i] ? 1 : 0;
        }

        return status;
    }
    catch (...) {
        pgm::set_error("相对定向过程中发生未预期异常");
        return PGM_ERR_INTERNAL;
    }
}

int32_t pgm_relative_orientation_design(int32_t       count,
                                        const double* left_x_mm,
                                        const double* left_y_mm,
                                        const double* right_x_mm,
                                        const double* right_y_mm,
                                        double        focal_length_mm,
                                        double        b_u, double b_v, double b_w,
                                        const double* rotation,
                                        double*       out_a,
                                        double*       out_f)
{
    pgm::set_error("");

    if (count <= 0 || left_x_mm == nullptr || left_y_mm == nullptr ||
        right_x_mm == nullptr || right_y_mm == nullptr ||
        rotation == nullptr || out_a == nullptr || out_f == nullptr) {
        pgm::set_error("指针为空或点数为零");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (!(focal_length_mm > 0.0) || !std::isfinite(focal_length_mm)) {
        pgm::set_error("主距必须为正的有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    // rotation 是"辅助系 -> 像"，本文件内部用的是它的转置
    Mat3 r_img;
    for (int i = 0; i < 9; ++i) r_img.m[i] = rotation[i];
    const Mat3 r_doc = transposed(r_img);

    const double b[3] = { b_u, b_v, b_w };

    for (int32_t i = 0; i < count; ++i) {
        double row6[6];
        out_f[i] = make_row(focal_length_mm, left_x_mm[i], left_y_mm[i],
                            right_x_mm[i], right_y_mm[i], b, r_doc, row6);
        for (int j = 0; j < 6; ++j) out_a[i * 6 + j] = row6[j];
    }
    return PGM_OK;
}

}  // extern "C"
