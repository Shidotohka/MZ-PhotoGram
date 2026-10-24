// 相对定向 DLL 的验收程序。
//
// 全部用合成数据，真值已知，逐项对表：
//   1 合成往返          —— 约定自洽（R 与三个角）、基线方向与三个角能否原样恢复、
//                          解出的相对方位喂给前方交会能否得到 t > 0 的模型点
//   2 雅可比 vs 有限差分 —— 设计矩阵的解析偏导是否正确（用独立的行列式差分对表）
//   3 旁向 30% 重叠像对  —— 教材那套 b=(1,0,0) 初值解不出来的构型，本实现要收敛
//   4 纯旁向像对         —— B_x = 0，基线完全落在 v 方向
//   5 带噪声观测         —— q_rms 是否落在理论量级、与蒙特卡洛是否吻合
//   6 退化与输入错误     —— 是否按预期拒绝
//
// 用法： pgm_relative_orientation_test
// 返回： 0 全部通过，1 有失败项

#ifdef _WIN32
#  define NOMINMAX
#  include <windows.h>
#endif

#include "photogrammetry.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;

// 与 Hammer 相机同量级：主距 152.72 mm，幅面取 ±115 mm
constexpr double kFocal = 152.72;

// 合成景深（单位：基线长度）。|b| = 1 是模型规准，所以景深用"基线的几倍"表示。
constexpr double kDepth = 2.0;

int g_failures = 0;

void say(bool ok, const char* what, const char* detail)
{
    std::printf("  [%s] %-34s %s\n", ok ? "PASS" : "FAIL", what, detail);
    if (!ok) ++g_failures;
}

// ---------------------------------------------------------------- 随机数

// 确定性 LCG + Box-Muller。不用 <random>，为的是任何编译器上重跑都得到同一组数。
struct Rng
{
    unsigned long long s;
    explicit Rng(unsigned long long seed) : s(seed) {}

    double uniform()
    {
        s = s * 6364136223846793005ULL + 1442695040888963407ULL;
        return static_cast<double>((s >> 11) & ((1ULL << 53) - 1)) /
               static_cast<double>(1ULL << 53);
    }

    double normal()
    {
        double u1 = uniform();
        if (u1 < 1e-300) u1 = 1e-300;
        const double u2 = uniform();
        return std::sqrt(-2.0 * std::log(u1)) * std::cos(2.0 * kPi * u2);
    }
};

// ---------------------------------------------------------------- 3x3

struct M3
{
    double m[9];
};

M3 transpose(const M3& a)
{
    M3 t;
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) t.m[i * 3 + j] = a.m[j * 3 + i];
    }
    return t;
}

M3 multiply(const M3& a, const M3& b)
{
    M3 o;
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) {
            o.m[i * 3 + j] = a.m[i * 3 + 0] * b.m[0 * 3 + j] +
                             a.m[i * 3 + 1] * b.m[1 * 3 + j] +
                             a.m[i * 3 + 2] * b.m[2 * 3 + j];
        }
    }
    return o;
}

// 教材展开式：R = R_kappa * R_omega * R_phi，把地面向量转到像空间。
// 刻意手写 a1..c3，不经过矩阵连乘，这样它才是库外的一份独立实现。
M3 rot_from_angles(double phi, double omega, double kappa)
{
    const double sp = std::sin(phi),  cp = std::cos(phi);
    const double sw = std::sin(omega), cw = std::cos(omega);
    const double sk = std::sin(kappa), ck = std::cos(kappa);

    const double a1 =  cp * ck - sp * sw * sk;
    const double a2 = -cp * sk - sp * sw * ck;
    const double a3 = -sp * cw;
    const double b1 =  cw * sk;
    const double b2 =  cw * ck;
    const double b3 = -sw;
    const double c1 =  sp * ck + cp * sw * sk;
    const double c2 = -sp * sk + cp * sw * ck;
    const double c3 =  cp * cw;

    M3 r;
    r.m[0] = a1; r.m[1] = b1; r.m[2] = c1;
    r.m[3] = a2; r.m[4] = b2; r.m[5] = c2;
    r.m[6] = a3; r.m[7] = b3; r.m[8] = c3;
    return r;
}

// 角向量 -> 旋转阵（罗德里格斯，精确到二阶以上），用于有限差分扰动
M3 rot_from_vector(double x, double y, double z)
{
    const double th2 = x * x + y * y + z * z;
    const double th = std::sqrt(th2);
    double a, b;
    if (th < 1e-8) {
        a = 1.0 - th2 / 6.0;
        b = 0.5 - th2 / 24.0;
    } else {
        a = std::sin(th) / th;
        b = (1.0 - std::cos(th)) / th2;
    }
    M3 r;
    r.m[0] = 1.0 - b * (y * y + z * z);
    r.m[1] = -a * z + b * x * y;
    r.m[2] =  a * y + b * x * z;
    r.m[3] =  a * z + b * x * y;
    r.m[4] = 1.0 - b * (x * x + z * z);
    r.m[5] = -a * x + b * y * z;
    r.m[6] = -a * y + b * x * z;
    r.m[7] =  a * x + b * y * z;
    r.m[8] = 1.0 - b * (x * x + y * y);
    return r;
}

// ---------------------------------------------------------------- 合成像对

struct Pair
{
    std::vector<double> lx, ly, rx, ry;
    int n;
};

// 景深 kDepth（z = -w），平面范围按 0.55 倍景深散开，且带一点起伏。
void make_scene(int n, unsigned long long seed, std::vector<double>* xyz)
{
    Rng rng(seed);
    xyz->assign(static_cast<size_t>(n) * 3, 0.0);
    for (int i = 0; i < n; ++i) {
        const double w = -(kDepth + 0.15 * rng.uniform());       // w < 0
        (*xyz)[i * 3 + 0] = (2.0 * rng.uniform() - 1.0) * 0.55 * (-w);
        (*xyz)[i * 3 + 1] = (2.0 * rng.uniform() - 1.0) * 0.55 * (-w);
        (*xyz)[i * 3 + 2] = w;
    }
}

// 由真值 (b, R_doc) 把三维点投到两片。R_doc 是"像点方向 -> 辅助系"，即教材那个 R。
void project_pair(const std::vector<double>& xyz, const double b[3], const M3& r_doc, Pair* out)
{
    const int n = static_cast<int>(xyz.size() / 3);
    out->n = n;
    out->lx.assign(n, 0.0);
    out->ly.assign(n, 0.0);
    out->rx.assign(n, 0.0);
    out->ry.assign(n, 0.0);

    const M3 r_t = transpose(r_doc);
    for (int i = 0; i < n; ++i) {
        const double A[3] = { xyz[i * 3 + 0], xyz[i * 3 + 1], xyz[i * 3 + 2] };

        out->lx[i] = -kFocal * A[0] / A[2];
        out->ly[i] = -kFocal * A[1] / A[2];

        const double d[3] = { A[0] - b[0], A[1] - b[1], A[2] - b[2] };
        const double q[3] = { r_t.m[0] * d[0] + r_t.m[1] * d[1] + r_t.m[2] * d[2],
                              r_t.m[3] * d[0] + r_t.m[4] * d[1] + r_t.m[5] * d[2],
                              r_t.m[6] * d[0] + r_t.m[7] * d[1] + r_t.m[8] * d[2] };
        out->rx[i] = -kFocal * q[0] / q[2];
        out->ry[i] = -kFocal * q[1] / q[2];
    }
}

// 独立的共面条件实现：det[b; d1; d2]，按行列式直接展开，不经过 DLL。
double coplanarity(const double b[3], const M3& r_doc, double x1, double y1, double x2, double y2)
{
    const double d1[3] = { x1, y1, -kFocal };
    const double img[3] = { x2, y2, -kFocal };
    double d2[3];
    for (int i = 0; i < 3; ++i) {
        d2[i] = r_doc.m[i * 3 + 0] * img[0] + r_doc.m[i * 3 + 1] * img[1] +
                r_doc.m[i * 3 + 2] * img[2];
    }
    return b[0] * (d1[1] * d2[2] - d1[2] * d2[1])
         - b[1] * (d1[0] * d2[2] - d1[2] * d2[0])
         + b[2] * (d1[0] * d2[1] - d1[1] * d2[0]);
}

// ---------------------------------------------------------------- 调用 DLL

struct Result
{
    int32_t code;
    pgm_relative_orientation_output out;
    std::vector<double>  q;
    std::vector<int32_t> used;
};

Result solve(const Pair& p, double sigma, int max_iterations = 0, double tolerance = 0.0)
{
    Result r;
    std::memset(&r.out, 0, sizeof(r.out));
    r.q.assign(static_cast<size_t>(p.n), 0.0);
    r.used.assign(static_cast<size_t>(p.n), 0);

    pgm_relative_orientation_input in;
    std::memset(&in, 0, sizeof(in));
    in.count = p.n;
    in.left_x_mm = p.lx.data();
    in.left_y_mm = p.ly.data();
    in.right_x_mm = p.rx.data();
    in.right_y_mm = p.ry.data();
    in.focal_length_mm = kFocal;
    in.image_sigma_mm = sigma;
    in.max_iterations = max_iterations;
    in.tolerance = tolerance;

    r.out.residual_q_mm = r.q.data();
    r.out.used_flags = r.used.data();

    r.code = pgm_relative_orientation(&in, &r.out);

    // out 里这两个指针指向上面的 vector，函数返回后立刻摘掉，免得 Result 被复制时
    // 留下悬空指针（数据本身在 r.q / r.used 里）
    r.out.residual_q_mm = nullptr;
    r.out.used_flags = nullptr;
    return r;
}

double max_angle_error(const pgm_relative_orientation_output& o,
                       double phi, double omega, double kappa)
{
    double e = std::fabs(o.phi_rad - phi);
    e = (std::fabs(o.omega_rad - omega) > e) ? std::fabs(o.omega_rad - omega) : e;
    e = (std::fabs(o.kappa_rad - kappa) > e) ? std::fabs(o.kappa_rad - kappa) : e;
    return e;
}

void normalize3(double v[3])
{
    const double n = std::sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
    if (n > 0.0) {
        v[0] /= n;
        v[1] /= n;
        v[2] /= n;
    }
}

double max_baseline_error(const pgm_relative_orientation_output& o, const double b[3])
{
    double t[3] = { b[0], b[1], b[2] };
    normalize3(t);
    double e = std::fabs(o.b_x - t[0]);
    e = (std::fabs(o.b_y - t[1]) > e) ? std::fabs(o.b_y - t[1]) : e;
    e = (std::fabs(o.b_z - t[2]) > e) ? std::fabs(o.b_z - t[2]) : e;
    return e;
}

// ---------------------------------------------------------------- 1 合成往返

void test_round_trip()
{
    std::printf("\n== 1 合成往返 ==\n");

    const double b_true[3] = { 1.0, 0.06, 0.004 };     // 航向为主
    const double phi = 0.004, omega = -0.006, kappa = 0.003;

    // 真值姿态：入口是"地面 -> 像"的 R，本测试投影要的是它的转置
    const M3 r_img = rot_from_angles(phi, omega, kappa);
    const M3 r_doc = transpose(r_img);

    std::vector<double> xyz;
    make_scene(14, 20260101ULL, &xyz);
    Pair p;
    project_pair(xyz, b_true, r_doc, &p);

    const Result r = solve(p, 0.0);

    char d[192];
    std::snprintf(d, sizeof(d), "返回码 %d", static_cast<int>(r.code));
    say(r.code == PGM_OK, "解算返回 PGM_OK", d);

    std::snprintf(d, sizeof(d), "%.2e（判据 <1e-9）", max_baseline_error(r.out, b_true));
    say(max_baseline_error(r.out, b_true) < 1e-9, "基线方向恢复", d);

    std::snprintf(d, sizeof(d), "%.2e（判据 <1e-9）", max_angle_error(r.out, phi, omega, kappa));
    say(max_angle_error(r.out, phi, omega, kappa) < 1e-9, "三个相对方位角恢复", d);

    // rotation 必须与三个角自洽：R(phi,omega,kappa) 应当等于输出的 rotation
    double rd = 0.0;
    const M3 r_check = rot_from_angles(r.out.phi_rad, r.out.omega_rad, r.out.kappa_rad);
    for (int i = 0; i < 9; ++i) {
        const double e = std::fabs(r_check.m[i] - r.out.rotation[i]);
        if (e > rd) rd = e;
    }
    std::snprintf(d, sizeof(d), "%.2e（判据 <1e-12）", rd);
    say(rd < 1e-12, "rotation 与三个角自洽", d);

    // 基线单位化后 |b| = 1
    const double nb = std::sqrt(r.out.b_x * r.out.b_x + r.out.b_y * r.out.b_y +
                                r.out.b_z * r.out.b_z);
    std::snprintf(d, sizeof(d), "|b| = %.15f", nb);
    say(std::fabs(nb - 1.0) < 1e-12, "基线是单位方向", d);

    // 上下视差应当归零，σ0 也应当归零
    std::snprintf(d, sizeof(d), "q_rms %.2e mm，σ0 %.2e mm（判据 <1e-9）",
                  r.out.q_rms_mm, r.out.sigma0_mm);
    say(r.out.q_rms_mm < 1e-9 && r.out.sigma0_mm < 1e-9, "无噪声时 q 与 σ0 归零", d);

    // 拿解出的相对方位当前方交会的位姿：模型点必须落在两架相机前方（t > 0）
    // 左片 (0,0,0,0,0,0)，右片 (b, phi, omega, kappa)，坐标系就是模型系
    int32_t positive = 0;
    for (int i = 0; i < p.n; ++i) {
        pgm_intersection_input ii;
        std::memset(&ii, 0, sizeof(ii));
        ii.focal_length_mm = kFocal;
        ii.left.xs = 0.0; ii.left.ys = 0.0; ii.left.zs = 0.0;
        ii.left.phi_rad = 0.0; ii.left.omega_rad = 0.0; ii.left.kappa_rad = 0.0;
        ii.right.xs = r.out.b_x;
        ii.right.ys = r.out.b_y;
        ii.right.zs = r.out.b_z;
        ii.right.phi_rad = r.out.phi_rad;
        ii.right.omega_rad = r.out.omega_rad;
        ii.right.kappa_rad = r.out.kappa_rad;
        ii.left_x_mm = p.lx[i];
        ii.left_y_mm = p.ly[i];
        ii.right_x_mm = p.rx[i];
        ii.right_y_mm = p.ry[i];

        pgm_intersection_output io;
        std::memset(&io, 0, sizeof(io));
        if (pgm_forward_intersection(&ii, &io) == PGM_OK && io.n_left > 0.0 && io.n_right > 0.0 &&
            io.miss_distance < 1e-9) {
            ++positive;
        }
    }
    std::snprintf(d, sizeof(d), "%d/%d 个点 t>0 且两视线相交", positive, p.n);
    say(positive == p.n, "模型点落在相机前方", d);
}

// ---------------------------------------------------------------- 2 雅可比

void test_jacobian()
{
    std::printf("\n== 2 雅可比 vs 有限差分 ==\n");

    const double b_true[3] = { 1.0, 0.30, -0.05 };
    const M3 r_img = rot_from_angles(0.02, -0.03, 0.015);
    const M3 r_doc = transpose(r_img);

    std::vector<double> xyz;
    make_scene(9, 777ULL, &xyz);
    Pair p;
    project_pair(xyz, b_true, r_doc, &p);

    // DLL 的解析式（列序 b_u, b_v, b_w, alpha, beta, gamma）
    std::vector<double> a(static_cast<size_t>(p.n) * 6);
    std::vector<double> f(static_cast<size_t>(p.n));
    const int32_t rc = pgm_relative_orientation_design(
        p.n, p.lx.data(), p.ly.data(), p.rx.data(), p.ry.data(), kFocal,
        b_true[0], b_true[1], b_true[2], r_img.m, a.data(), f.data());

    char d[192];
    std::snprintf(d, sizeof(d), "返回码 %d", static_cast<int>(rc));
    say(rc == PGM_OK, "设计矩阵接口可用", d);

    // 残差定义对表：DLL 的 F 与测试自己的行列式
    double fe = 0.0;
    for (int i = 0; i < p.n; ++i) {
        const double mine = coplanarity(b_true, r_doc, p.lx[i], p.ly[i], p.rx[i], p.ry[i]);
        const double e = std::fabs(mine - f[i]);
        if (e > fe) fe = e;
    }
    std::snprintf(d, sizeof(d), "%.2e", fe);
    say(fe < 1e-9, "残差 F 与独立实现一致", d);

    // 中心差分：对 6 个参数各扰动一次
    const double h = 1e-6;
    double worst = 0.0;
    const char* names[6] = { "b_u", "b_v", "b_w", "alpha", "beta", "gamma" };
    for (int k = 0; k < 6; ++k) {
        // 该列的量级，用来定相对判据（b_w 列可能在零附近）
        double col_scale = 0.0;
        for (int i = 0; i < p.n; ++i) {
            const double v = std::fabs(a[static_cast<size_t>(i) * 6 + k]);
            if (v > col_scale) col_scale = v;
        }
        if (!(col_scale > 0.0)) col_scale = 1.0;

        double err = 0.0;
        for (int i = 0; i < p.n; ++i) {
            double bp[3] = { b_true[0], b_true[1], b_true[2] };
            double bm[3] = { b_true[0], b_true[1], b_true[2] };
            M3 rp = r_doc, rm = r_doc;
            if (k < 3) {
                bp[k] += h;
                bm[k] -= h;
            } else {
                double wx[3] = { 0.0, 0.0, 0.0 };
                double wy[3] = { 0.0, 0.0, 0.0 };
                wx[k - 3] = h;
                wy[k - 3] = -h;
                rp = multiply(rot_from_vector(wx[0], wx[1], wx[2]), r_doc);
                rm = multiply(rot_from_vector(wy[0], wy[1], wy[2]), r_doc);
            }
            const double fp = coplanarity(bp, rp, p.lx[i], p.ly[i], p.rx[i], p.ry[i]);
            const double fm = coplanarity(bm, rm, p.lx[i], p.ly[i], p.rx[i], p.ry[i]);
            const double num = (fp - fm) / (2.0 * h);
            const double diff = std::fabs(num - a[static_cast<size_t>(i) * 6 + k]);
            if (diff > err) err = diff;
        }
        const double rel = err / col_scale;
        std::snprintf(d, sizeof(d), "%-5s 最大相对差 %.2e", names[k], rel);
        say(rel < 1e-8, "解析偏导对表", d);
        if (rel > worst) worst = rel;
    }
    std::snprintf(d, sizeof(d), "六列最大相对差 %.2e（判据 <1e-8）", worst);
    say(worst < 1e-8, "整体通过", d);
}

// ---------------------------------------------------------------- 3/4 两种像对

// 旁向 / 纯旁向两种像对共用这一段：解算、恢复、视差、规准、条件数。
void test_pair_geometry(const char* title, const double b_true_in[3],
                        const char* expect_note, int expect_gauge)
{
    std::printf("\n== %s ==\n", title);

    double b_true[3] = { b_true_in[0], b_true_in[1], b_true_in[2] };
    normalize3(b_true);

    const double phi = 0.002, omega = -0.003, kappa = 0.0015;
    const M3 r_doc = transpose(rot_from_angles(phi, omega, kappa));

    std::vector<double> xyz;
    make_scene(12, 424242ULL, &xyz);
    Pair p;
    project_pair(xyz, b_true, r_doc, &p);

    const Result r = solve(p, 0.0);

    char d[192];
    std::snprintf(d, sizeof(d), "基线 b = (%.4f, %.4f, %.4f)", b_true[0], b_true[1], b_true[2]);
    std::printf("  %s\n", d);
    std::printf("  %s\n", expect_note);

    std::snprintf(d, sizeof(d), "返回码 %d，迭代 %d 轮", static_cast<int>(r.code), r.out.iterations);
    say(r.code == PGM_OK, "解算返回 PGM_OK", d);

    std::snprintf(d, sizeof(d), "%d 轮（判据 2..10）", r.out.iterations);
    say(r.out.iterations >= 2 && r.out.iterations <= 10, "迭代次数", d);

    std::snprintf(d, sizeof(d), "%.2e（判据 <1e-9）", max_baseline_error(r.out, b_true));
    say(max_baseline_error(r.out, b_true) < 1e-9, "基线方向恢复", d);

    std::snprintf(d, sizeof(d), "%.2e（判据 <1e-9）", max_angle_error(r.out, phi, omega, kappa));
    say(max_angle_error(r.out, phi, omega, kappa) < 1e-9, "三个相对方位角恢复", d);

    std::snprintf(d, sizeof(d), "%.2e mm（判据 <1e-9）", r.out.q_rms_mm);
    say(r.out.q_rms_mm < 1e-9, "上下视差归零", d);

    std::snprintf(d, sizeof(d), "gauge = %d（期望 %d）", r.out.gauge, expect_gauge);
    say(r.out.gauge == expect_gauge, "规准落在基线主分量上", d);

    std::snprintf(d, sizeof(d), "%.3g", r.out.condition);
    say(r.out.condition > 0.0 && r.out.condition < 1e6, "法方程条件数合理", d);
}

// ---------------------------------------------------------------- 5 噪声

void test_noise()
{
    std::printf("\n== 5 带噪声观测 ==\n");

    const double sigma = 0.02;     // mm，约半个像元
    const double b_true[3] = { 1.0, 0.08, 0.003 };
    const double phi = 0.003, omega = -0.004, kappa = 0.002;
    const M3 r_doc = transpose(rot_from_angles(phi, omega, kappa));

    std::vector<double> xyz;
    make_scene(12, 987654321ULL, &xyz);
    Pair clean;
    project_pair(xyz, b_true, r_doc, &clean);

    const int trials = 200;
    double sum = 0.0, sum2 = 0.0;
    double first = 0.0;
    int bad = 0;

    for (int t = 0; t < trials; ++t) {
        Rng rng(static_cast<unsigned long long>(t) * 2654435761ULL + 17ULL);
        Pair noisy = clean;
        for (int i = 0; i < noisy.n; ++i) {
            noisy.lx[i] += sigma * rng.normal();
            noisy.ly[i] += sigma * rng.normal();
            noisy.rx[i] += sigma * rng.normal();
            noisy.ry[i] += sigma * rng.normal();
        }
        const Result r = solve(noisy, sigma);
        if (r.code != PGM_OK) ++bad;
        if (t == 0) first = r.out.q_rms_mm;
        sum += r.out.q_rms_mm;
        sum2 += r.out.q_rms_mm * r.out.q_rms_mm;
    }

    const double mean = sum / trials;
    const double var = sum2 / trials - mean * mean;
    const double sd = (var > 0.0) ? std::sqrt(var) : 0.0;

    char d[192];
    std::snprintf(d, sizeof(d), "%d/%d 次解算成功", trials - bad, trials);
    say(bad == 0, "全部收敛", d);

    std::printf("  q_rms：单次 %.4f mm，%d 次均值 %.4f mm，标准差 %.4f mm\n",
                first, trials, mean, sd);
    std::printf("  像点噪声 sigma = %.3f mm。上下视差只取噪声**投影到核面法向**的分量，\n"
                "  所以量级是 sigma 与 sqrt(2)*sigma 之间（%.4f ~ %.4f mm），5 个参数再吸掉一部分。\n",
                sigma, sigma, std::sqrt(2.0) * sigma);

    std::snprintf(d, sizeof(d), "%.4f mm，占 sigma 的 %.0f%%", mean, 100.0 * mean / sigma);
    say(mean > 0.5 * sigma && mean < sigma, "q_rms 均值落在理论量级", d);

    // 单次结果离均值不应超过 3 倍单次标准差
    const double dev = std::fabs(first - mean);
    std::snprintf(d, sizeof(d), "|%.4f - %.4f| = %.4f，3σ = %.4f", first, mean, dev, 3.0 * sd);
    say(sd > 0.0 && dev <= 3.0 * sd, "单次结果与蒙特卡洛一致", d);

    // 有噪声时 σ0 与 q_rms 的关系是确定的：v = F/(|b|f) ≈ -q·|d2|/f，再差一个
    // √(n/(n-5)) 的单位权改正。对得上说明两套残差算的是同一件事。
    Rng rng(12345ULL);
    Pair noisy = clean;
    for (int i = 0; i < noisy.n; ++i) {
        noisy.lx[i] += sigma * rng.normal();
        noisy.ly[i] += sigma * rng.normal();
        noisy.rx[i] += sigma * rng.normal();
        noisy.ry[i] += sigma * rng.normal();
    }
    const Result r = solve(noisy, sigma);

    double mean_scale = 0.0;
    for (int i = 0; i < noisy.n; ++i) {
        mean_scale += std::sqrt(noisy.rx[i] * noisy.rx[i] + noisy.ry[i] * noisy.ry[i] +
                                kFocal * kFocal) / kFocal;
    }
    mean_scale /= noisy.n;
    const double ratio_theory = mean_scale * std::sqrt(noisy.n / (noisy.n - 5.0));
    const double ratio_actual = (r.out.q_rms_mm > 0.0) ? r.out.sigma0_mm / r.out.q_rms_mm : 0.0;
    const double rel = (ratio_theory > 0.0) ? std::fabs(ratio_actual / ratio_theory - 1.0) : 1.0;

    std::snprintf(d, sizeof(d), "σ0/q_rms = %.3f，理论 %.3f，相对差 %.1f%%",
                  ratio_actual, ratio_theory, 100.0 * rel);
    say(r.code == PGM_OK && rel < 0.15, "σ0 与 q_rms 关系与理论一致", d);
}

// ---------------------------------------------------------------- 6 退化

void test_degenerate()
{
    std::printf("\n== 6 退化与输入错误 ==\n");

    const double b_true[3] = { 1.0, 0.0, 0.0 };
    const M3 r_doc = transpose(rot_from_angles(0.002, -0.003, 0.001));

    std::vector<double> xyz;
    make_scene(8, 5150ULL, &xyz);
    Pair p;
    project_pair(xyz, b_true, r_doc, &p);

    char d[192];

    // 6.1 只有 4 对点
    {
        Pair few = p;
        few.n = 4;
        few.lx.resize(4); few.ly.resize(4); few.rx.resize(4); few.ry.resize(4);
        const Result r = solve(few, 0.0);
        std::snprintf(d, sizeof(d), "返回码 %d", static_cast<int>(r.code));
        say(r.code == PGM_ERR_TOO_FEW_POINTS, "4 对点被拒收", d);
    }

    // 6.2 同名点重合（全部落在同一位置，平均视差为零）
    {
        Pair same = p;
        for (int i = 0; i < same.n; ++i) {
            same.lx[i] = 3.0;
            same.ly[i] = -2.0;
            same.rx[i] = 3.0;
            same.ry[i] = -2.0;
        }
        const Result r = solve(same, 0.0);
        std::snprintf(d, sizeof(d), "返回码 %d", static_cast<int>(r.code));
        say(r.code == PGM_ERR_DEGENERATE, "重合点被拒收", d);
    }

    // 6.3 同名点共线：三维点全落在 u-w 平面内的直线上。此时 b、d1、d2 恒共面，
    // 共面条件对任何姿态都成立——法方程整块为零，只能报秩亏。
    {
        std::vector<double> xyz_line(static_cast<size_t>(8) * 3, 0.0);
        for (int i = 0; i < 8; ++i) {
            xyz_line[i * 3 + 0] = (static_cast<double>(i) - 3.5) * 0.12 * kDepth;
            xyz_line[i * 3 + 1] = 0.0;
            xyz_line[i * 3 + 2] = -kDepth;
        }
        Pair line;
        project_pair(xyz_line, b_true, r_doc, &line);

        const Result r = solve(line, 0.0);
        std::snprintf(d, sizeof(d), "返回码 %d（%s）", static_cast<int>(r.code),
                      r.code == PGM_ERR_DEGENERATE ? "秩亏"
                      : r.code == PGM_ERR_NOT_CONVERGED ? "未收敛" : "见上");
        say(r.code != PGM_OK, "共线点不给出正常解", d);
    }

    // 6.4 输入里有非有限值
    {
        Pair bad = p;
        bad.rx[3] = std::nan("");
        const Result r = solve(bad, 0.0);
        std::snprintf(d, sizeof(d), "返回码 %d", static_cast<int>(r.code));
        say(r.code == PGM_ERR_INVALID_ARGUMENT, "非有限坐标被拒收", d);
    }

    // 6.5 主距为 0
    {
        pgm_relative_orientation_input in;
        std::memset(&in, 0, sizeof(in));
        in.count = p.n;
        in.left_x_mm = p.lx.data(); in.left_y_mm = p.ly.data();
        in.right_x_mm = p.rx.data(); in.right_y_mm = p.ry.data();
        in.focal_length_mm = 0.0;
        pgm_relative_orientation_output out;
        std::memset(&out, 0, sizeof(out));
        const int32_t rc = pgm_relative_orientation(&in, &out);
        std::snprintf(d, sizeof(d), "返回码 %d", static_cast<int>(rc));
        say(rc == PGM_ERR_INVALID_ARGUMENT, "主距为 0 被拒收", d);
    }
}

}  // namespace

int main()
{
    std::printf("相对定向 DLL 验收（合成数据）\n");
    std::printf("主距 %.2f mm，景深 %.2f 倍基线\n", kFocal, kDepth);

    test_round_trip();
    test_jacobian();

    {
        const double b_lateral[3] = { 0.02, 1.0, 0.002 };
        test_pair_geometry("3 旁向 30% 重叠像对", b_lateral,
                           "基线主要落在 v 方向：教材那套 b=(1,0,0) 初值在这里不收敛", 1);
    }
    {
        const double b_pure_lateral[3] = { 0.0, 1.0, 0.002 };
        test_pair_geometry("4 纯旁向像对", b_pure_lateral,
                           "基线完全落在 v 方向：b_x = 0，教材参数化根本表示不出来", 1);
    }

    test_noise();
    test_degenerate();

    std::printf("\n");
    if (g_failures == 0) {
        std::printf("全部通过\n");
        return 0;
    }
    std::printf("有 %d 项失败\n", g_failures);
    return 1;
}
