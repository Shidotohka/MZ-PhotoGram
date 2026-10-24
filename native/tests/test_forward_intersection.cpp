// 前方交会 DLL 的验收程序。
//
// 全部用合成数据，真值已知，逐项对表：
//   1 旋转矩阵构造       —— 教材 a1..c3 展开式 vs DLL 的正投影
//   2 往返检验           —— 真值投影成一对像点，再交会，能否原样回到真值
//   3 与教材公式对表     —— 三式最小二乘 vs (5-14)，航向像对上必须同值
//   4 反证：旁向像对     —— 教材固定用 (1)(3) 式，纯旁向时 0/0，近旁向时放大 8501 倍
//   5 精度传播           —— 解析协方差 vs 蒙特卡洛实测标准差
//   6 残余量真的在测东西 —— 注入上下视差，看 miss / q 是否跟着长
//   7 退化与输入错误     —— 是否按预期拒绝
//
// 用法： pgm_intersection_test
// 返回： 0 全部通过，1 有失败项

#ifdef _WIN32
#  define NOMINMAX
#  include <windows.h>
#endif

#include "photogrammetry.h"

#include <cmath>
#include <cstdio>
#include <random>

namespace {

constexpr double kPi = 3.14159265358979323846;

// Hammer 相机的真实主距与像元尺寸：5120 x 0.04503 mm = 230.55 mm 幅面
constexpr double kFocal = 152.72;

int g_failures = 0;

void check(bool ok, const char* what)
{
    if (!ok) {
        std::printf("  [FAIL] %s\n", what);
        ++g_failures;
    }
}

void section(const char* title)
{
    std::printf("\n== %s ==\n", title);
}

struct Six
{
    double xs, ys, zs, phi, omega, kappa;
};

pgm_camera_pose pose_of(const Six& s)
{
    pgm_camera_pose p;
    p.xs = s.xs;
    p.ys = s.ys;
    p.zs = s.zs;
    p.phi_rad = s.phi;
    p.omega_rad = s.omega;
    p.kappa_rad = s.kappa;
    return p;
}

// ---------------------------------------------------------------- 独立的教材实现
//
// 刻意不复用 DLL 里的矩阵乘法，直接从教材的 a1..c3 展开式写一遍，用来对表。
// 排列为 [[a1,b1,c1],[a2,b2,c2],[a3,b3,c3]]，行对应 U/V/W，列对应 dX/dY/dZ。

void textbook_coeff(const Six& e, double a[9])
{
    const double sp = std::sin(e.phi), cp = std::cos(e.phi);
    const double sw = std::sin(e.omega), cw = std::cos(e.omega);
    const double sk = std::sin(e.kappa), ck = std::cos(e.kappa);

    a[0] = cp * ck - sp * sw * sk;
    a[1] = cw * sk;
    a[2] = sp * ck + cp * sw * sk;
    a[3] = -cp * sk - sp * sw * ck;
    a[4] = cw * ck;
    a[5] = -sp * sk + cp * sw * ck;
    a[6] = -sp * cw;
    a[7] = -sw;
    a[8] = cp * cw;
}

void textbook_project(const Six& e, double f, double X, double Y, double Z,
                      double* x, double* y)
{
    double a[9];
    textbook_coeff(e, a);
    const double dX = X - e.xs, dY = Y - e.ys, dZ = Z - e.zs;
    const double U = a[0] * dX + a[1] * dY + a[2] * dZ;
    const double V = a[3] * dX + a[4] * dY + a[5] * dZ;
    const double W = a[6] * dX + a[7] * dY + a[8] * dZ;
    *x = -f * U / W;
    *y = -f * V / W;
}

// 像点 -> 像空间辅助坐标：教材写 [u v w]^T = R' * [x y -f]^T，
// 而 R' 正是上面那张 R 的转置，即 u_k = sum_j R[j][k] * img[j]，用下标展开写死。
void aux_coord(const double a[9], double f, double x, double y, double u[3])
{
    u[0] = a[0] * x + a[3] * y + a[6] * (-f);
    u[1] = a[1] * x + a[4] * y + a[7] * (-f);
    u[2] = a[2] * x + a[5] * y + a[8] * (-f);
}

// 教材 (5-13)(5-14)：只用第 (1)(3) 式解 N1、N2，Y 取两条视线的均值。
// 返回 false 表示分母为零（左右视差为零，方程退化成齐次式）。
bool textbook_intersection(const Six& L, const Six& Rr, double f,
                           double xl, double yl, double xr, double yr,
                           double* X, double* Y, double* Z,
                           double* n1_out, double* n2_out, double* denom_out)
{
    double al[9], ar[9];
    textbook_coeff(L, al);
    textbook_coeff(Rr, ar);

    double u1[3], u2[3];
    aux_coord(al, f, xl, yl, u1);
    aux_coord(ar, f, xr, yr, u2);

    const double Bu = Rr.xs - L.xs;
    const double Bw = Rr.zs - L.zs;

    const double D = u1[0] * u2[2] - u2[0] * u1[2];
    *denom_out = D;
    // 判"分母是否为零"必须相对于它自己的量纲（mm^2）来说。航向像对约 1e5，
    // 纯旁向像对是 0，近旁向可以小到 1e0 以下 —— 到那个程度解已经不可用了。
    const double scale = std::fabs(u1[2] * u2[2]);
    if (std::fabs(D) <= 1e-12 * scale) return false;

    const double n1 = (Bu * u2[2] - Bw * u2[0]) / D;
    const double n2 = (Bu * u1[2] - Bw * u1[0]) / D;

    *n1_out = n1;
    *n2_out = n2;
    *X = L.xs + n1 * u1[0];
    *Z = L.zs + n1 * u1[2];
    *Y = 0.5 * ((L.ys + n1 * u1[1]) + (Rr.ys + n2 * u2[1]));
    return true;
}

// ---------------------------------------------------------------- 调用 DLL

struct Result
{
    int32_t status;
    pgm_intersection_output out;
    const char* error;
};

Result intersect_raw(const Six& L, const Six& Rr, double f,
                     double xl, double yl, double xr, double yr, double sigma_mm)
{
    pgm_intersection_input in;
    in.focal_length_mm = f;
    in.left = pose_of(L);
    in.right = pose_of(Rr);
    in.left_x_mm = xl;
    in.left_y_mm = yl;
    in.right_x_mm = xr;
    in.right_y_mm = yr;
    in.image_sigma_mm = sigma_mm;

    Result r;
    r.out = pgm_intersection_output{};
    r.status = pgm_forward_intersection(&in, &r.out);
    r.error = pgm_last_error();
    return r;
}

// 由真值地面点生成一对像点（用 DLL 的正投影，与交会走同一套外方位约定）。
bool project_pair(const Six& L, const Six& Rr, double f,
                  double X, double Y, double Z,
                  double* xl, double* yl, double* xr, double* yr)
{
    if (pgm_project_point(f, L.xs, L.ys, L.zs, L.phi, L.omega, L.kappa,
                          X, Y, Z, xl, yl) != PGM_OK) return false;
    if (pgm_project_point(f, Rr.xs, Rr.ys, Rr.zs, Rr.phi, Rr.omega, Rr.kappa,
                          X, Y, Z, xr, yr) != PGM_OK) return false;
    return true;
}

// ---------------------------------------------------------------- 1 旋转矩阵

void test_r_construction()
{
    section("1 旋转矩阵构造：教材展开式 vs DLL 正投影");

    const Six e = { 10000.0, 5000.0, 3000.0, 0.0015, -0.0025, 0.0155 };
    const double pts[4][3] = {
        { 10500.0, 5200.0, 120.0 },
        { 10300.0, 4800.0, 80.0 },
        { 10800.0, 5300.0, 160.0 },
        { 10200.0, 5100.0, 100.0 },
    };

    double worst = 0.0;
    for (int i = 0; i < 4; ++i) {
        double tx = 0.0, ty = 0.0, dx = 0.0, dy = 0.0;
        textbook_project(e, kFocal, pts[i][0], pts[i][1], pts[i][2], &tx, &ty);
        const int32_t st = pgm_project_point(kFocal, e.xs, e.ys, e.zs, e.phi, e.omega, e.kappa,
                                             pts[i][0], pts[i][1], pts[i][2], &dx, &dy);
        check(st == PGM_OK, "正投影返回 PGM_OK");
        if (st != PGM_OK) continue;
        const double ex = std::fabs(tx - dx), ey = std::fabs(ty - dy);
        if (ex > worst) worst = ex;
        if (ey > worst) worst = ey;
    }
    std::printf("  四个点的最大偏差 = %.3e mm\n", worst);
    check(worst < 1e-9, "教材展开式与 DLL 的 R 构造一致（差 < 1e-9 mm）");
}

// ---------------------------------------------------------------- 2 往返

void test_round_trip()
{
    section("2 往返检验：真值 -> 像点 -> 交会 -> 回到真值");

    // 航向像对：基线 1000 m 沿 X，航高约 2900 m，交会角约 20°
    const Six L = { 10000.0, 5000.0, 3000.0, 0.0015, -0.0025, 0.0155 };
    const Six Rr = { 11000.0, 5007.0, 3002.0, 0.0012, -0.0020, 0.0140 };

    double worst = 0.0;
    double worst_angle = 1e9, worst_q = 0.0, worst_miss = 0.0;
    int n = 0;

    for (int a = -1; a <= 1; ++a) {
        for (int b = -1; b <= 1; ++b) {
            const double X = 10500.0 + 300.0 * a;
            const double Y = 5000.0 + 300.0 * b;
            const double Z = 100.0 + 10.0 * a - 5.0 * b;

            double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
            if (!project_pair(L, Rr, kFocal, X, Y, Z, &xl, &yl, &xr, &yr)) {
                check(false, "真值点能正投影到两片");
                continue;
            }

            const Result r = intersect_raw(L, Rr, kFocal, xl, yl, xr, yr, 0.02);
            if (r.status != PGM_OK) {
                std::printf("  [FAIL] 交会返回 %d：%s\n", r.status, r.error);
                ++g_failures;
                continue;
            }

            const double d = std::sqrt((r.out.ground_x - X) * (r.out.ground_x - X) +
                                       (r.out.ground_y - Y) * (r.out.ground_y - Y) +
                                       (r.out.ground_z - Z) * (r.out.ground_z - Z));
            if (d > worst) worst = d;
            if (r.out.convergence_angle_deg < worst_angle) worst_angle = r.out.convergence_angle_deg;
            const double q = std::fabs(r.out.vertical_parallax_mm);
            if (q > worst_q) worst_q = q;
            if (r.out.miss_distance > worst_miss) worst_miss = r.out.miss_distance;
            ++n;
        }
    }

    std::printf("  %d 个点，最大坐标差 = %.3e m\n", n, worst);
    std::printf("  最小交会角 = %.3f°，最大上下视差 = %.3e mm，最大最近距离 = %.3e m\n",
                worst_angle, worst_q, worst_miss);
    check(n == 9, "9 个点全部解算成功");
    check(worst < 1e-8, "交会点与真值之差 < 1e-8 m");
    check(worst_q < 1e-9, "无噪声时剩余上下视差 = 0");
    check(worst_miss < 1e-9, "无噪声时两条视线严格相交");
    check(worst_angle > 15.0, "交会角 > 15°（VL-03 的警告线）");
}

// ---------------------------------------------------------------- 3 与教材对表

void test_versus_textbook()
{
    section("3 与教材公式对表：三式最小二乘 vs (5-14)，航向像对");

    const Six L = { 10000.0, 5000.0, 3000.0, 0.0015, -0.0025, 0.0155 };
    const Six Rr = { 11000.0, 5007.0, 3002.0, 0.0012, -0.0020, 0.0140 };

    double worst_n = 0.0, worst_pos = 0.0;
    for (int a = -1; a <= 1; ++a) {
        for (int b = -1; b <= 1; ++b) {
            const double X = 10500.0 + 300.0 * a;
            const double Y = 5000.0 + 300.0 * b;
            const double Z = 100.0 + 10.0 * a - 5.0 * b;

            double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
            project_pair(L, Rr, kFocal, X, Y, Z, &xl, &yl, &xr, &yr);

            double tx = 0.0, ty = 0.0, tz = 0.0, tn1 = 0.0, tn2 = 0.0, den = 0.0;
            const bool ok = textbook_intersection(L, Rr, kFocal, xl, yl, xr, yr,
                                                  &tx, &ty, &tz, &tn1, &tn2, &den);
            check(ok, "教材公式在航向像对上分母非零");
            if (!ok) continue;

            const Result r = intersect_raw(L, Rr, kFocal, xl, yl, xr, yr, 0.0);
            check(r.status == PGM_OK, "交会返回 PGM_OK");
            if (r.status != PGM_OK) continue;

            const double dn = std::fabs(r.out.n_left - tn1) + std::fabs(r.out.n_right - tn2);
            const double dp = std::fabs(r.out.ground_x - tx) + std::fabs(r.out.ground_y - ty) +
                              std::fabs(r.out.ground_z - tz);
            if (dn > worst_n) worst_n = dn;
            if (dp > worst_pos) worst_pos = dp;

            if (a == 0 && b == 0) {
                std::printf("  中心点：N1 教材 %.12f / 本实现 %.12f\n", tn1, r.out.n_left);
                std::printf("          N2 教材 %.12f / 本实现 %.12f\n", tn2, r.out.n_right);
            }
        }
    }
    std::printf("  九个点：N 的最大差 = %.3e，坐标最大差 = %.3e m\n", worst_n, worst_pos);
    check(worst_n < 1e-9, "投影系数与教材 (5-14) 一致");
    check(worst_pos < 1e-8, "地面坐标与教材解法一致");
}

// ---------------------------------------------------------------- 4 反证

double dist3(const double a[3], const double b[3])
{
    const double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}

void test_cross_baseline()
{
    section("4 反证：旁向像对上教材公式的两种下场");

    // 基线纯沿 Y（1000 m），Bu = Bw = 0。
    const Six L = { 10000.0, 5000.0, 3000.0, 0.0020, -0.0010, 0.0100 };
    const Six Rr = { 10000.0, 6000.0, 3000.0, 0.0010, 0.0020, 0.0200 };

    const double X = 10400.0, Y = 5500.0, Z = 120.0;

    double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
    if (!project_pair(L, Rr, kFocal, X, Y, Z, &xl, &yl, &xr, &yr)) {
        check(false, "旁向像对也能正投影");
        return;
    }

    double tx = 0.0, ty = 0.0, tz = 0.0, tn1 = 0.0, tn2 = 0.0, den = 0.0;
    const bool ok = textbook_intersection(L, Rr, kFocal, xl, yl, xr, yr,
                                          &tx, &ty, &tz, &tn1, &tn2, &den);

    std::printf("  4a 纯旁向像对（基线 1000 m 全在 Y）\n");
    std::printf("     教材分母 u1*w2-u2*w1 = %.6e mm²\n", den);
    check(!ok, "教材公式在纯旁向像对上无法求解：分母恒为零");

    const Result r = intersect_raw(L, Rr, kFocal, xl, yl, xr, yr, 0.02);
    check(r.status == PGM_OK, "本实现在纯旁向像对上仍返回 PGM_OK");
    if (r.status == PGM_OK) {
        const double tr[3] = { X, Y, Z };
        const double got[3] = { r.out.ground_x, r.out.ground_y, r.out.ground_z };
        std::printf("     本实现 X = %.6f  Y = %.6f  Z = %.6f，与真值差 %.3e m\n",
                    got[0], got[1], got[2], dist3(got, tr));
        check(dist3(got, tr) < 1e-8, "纯旁向像对上仍交会出真值");
        check(r.out.n_left > 0.0 && r.out.n_right > 0.0, "投影系数为正");
    }

    // 4b 近旁向：基线主要在 Y，但带 0.5 m 的 X 分量。实际作业里两条航线不会严格平行，
    //    这时分母不再为零，公式"能算"，但条件数极差 —— 才是真正危险的那种。
    //
    //    注意：分母 D 只取决于两条视线的方向，与相机位置无关。所以必须用新相机重新
    //    投影一遍像点；沿用 4a 的像点会让 D 一点不变（这一点本身也是个坑）。
    const Six L2 = { 10000.0, 5000.0, 3000.0, 0.0020, -0.0010, 0.0100 };
    const Six R2 = { 10000.5, 6000.0, 3000.0, 0.0010, 0.0020, 0.0200 };

    double xl2 = 0.0, yl2 = 0.0, xr2 = 0.0, yr2 = 0.0;
    if (!project_pair(L2, R2, kFocal, X, Y, Z, &xl2, &yl2, &xr2, &yr2)) {
        check(false, "近旁向像对也能正投影");
        return;
    }

    std::printf("  4b 近旁向像对（基线 1000 m，X 方向只差 0.5 m）\n");

    double p0[3], p1[3];
    const bool ok_b = textbook_intersection(L2, R2, kFocal, xl2, yl2, xr2, yr2,
                                            &p0[0], &p0[1], &p0[2], &tn1, &tn2, &den);
    if (!ok_b) {
        check(false, "近旁向时教材分母不再为零");
        return;
    }
    std::printf("     教材分母 = %.6e mm²（航向像对约 1e5）\n", den);
    std::printf("     教材解       X = %.3f  Y = %.3f  Z = %.3f\n", p0[0], p0[1], p0[2]);

    textbook_intersection(L2, R2, kFocal, xl2 + 0.01, yl2, xr2, yr2,
                          &p1[0], &p1[1], &p1[2], &tn1, &tn2, &den);
    std::printf("     左片 x +0.01  X = %.3f  Y = %.3f  Z = %.3f\n", p1[0], p1[1], p1[2]);

    const Result q0 = intersect_raw(L2, R2, kFocal, xl2, yl2, xr2, yr2, 0.0);
    const Result q1 = intersect_raw(L2, R2, kFocal, xl2 + 0.01, yl2, xr2, yr2, 0.0);
    check(q0.status == PGM_OK && q1.status == PGM_OK, "本实现在近旁向像对上正常返回");
    if (q0.status != PGM_OK || q1.status != PGM_OK) return;

    const double a0[3] = { q0.out.ground_x, q0.out.ground_y, q0.out.ground_z };
    const double a1[3] = { q1.out.ground_x, q1.out.ground_y, q1.out.ground_z };
    const double move_textbook = dist3(p1, p0);
    const double move_ours = dist3(a1, a0);

    std::printf("     同样的 0.01 mm 偏差，地面点位移：教材公式 %.3f m，本实现 %.4f m，差 %.0f 倍\n",
                move_textbook, move_ours,
                (move_ours > 0.0) ? move_textbook / move_ours : 0.0);
    check(move_ours > 0.0, "本实现的位移非零");
    check(move_textbook > 10.0 * move_ours,
          "近旁向时教材公式把 0.01 mm 的像点误差放大十倍以上");
}

// ---------------------------------------------------------------- 5 精度

void test_precision()
{
    section("5 精度传播：解析协方差 vs 蒙特卡洛");

    const Six L = { 10000.0, 5000.0, 3000.0, 0.0015, -0.0025, 0.0155 };
    const Six Rr = { 11000.0, 5007.0, 3002.0, 0.0012, -0.0020, 0.0140 };

    const double X = 10500.0, Y = 5000.0, Z = 100.0;
    const double sigma = 0.02;  // mm，约 0.44 像元

    double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
    project_pair(L, Rr, kFocal, X, Y, Z, &xl, &yl, &xr, &yr);

    const Result ref = intersect_raw(L, Rr, kFocal, xl, yl, xr, yr, sigma);
    check(ref.status == PGM_OK, "标称点交会成功");
    if (ref.status != PGM_OK) return;

    std::printf("  解析：m_X = %.4f m，m_Y = %.4f m，m_Z = %.4f m\n",
                ref.out.m_x, ref.out.m_y, ref.out.m_z);
    const double S = 0.5 * (ref.out.range_left + ref.out.range_right);
    std::printf("  摄影距离 = %.1f m，基线 = %.1f m，交会角 = %.3f°\n",
                S, ref.out.base_length, ref.out.convergence_angle_deg);

    // 与教材那组经验公式对照：m_平面 ≈ (S/f)σ，m_深度 ≈ m_平面/sinθ。
    // 两者差 2^(1/2) 的两个因子 —— 教材公式算的是"一条视线的位移"，而交会点由
    // 两条独立视线共同确定：平面内平均一次（除以 2^(1/2)），深度上叠加一次
    // （乘以 2^(1/2)）。所以实测 m_X 只有 (S/f)σ 的 0.7 倍，m_Z 是它的 1.4/sinθ 倍。
    {
        const double theta = ref.out.convergence_angle_deg * kPi / 180.0;
        const double m_plane = S / kFocal * sigma;
        std::printf("  教材量级：(S/f)σ = %.4f m，除以 sinθ = %.4f m\n",
                    m_plane, m_plane / std::sin(theta));
        std::printf("  实测比值：m_X / (S/f)σ = %.3f，m_Z / (S/f)σ = %.3f，1/sinθ = %.3f\n",
                    ref.out.m_x / m_plane, ref.out.m_z / m_plane, 1.0 / std::sin(theta));
    }

    std::mt19937 rng(20240607u);
    std::normal_distribution<double> nd(0.0, sigma);

    const int kSamples = 4000;
    double s[3] = { 0.0, 0.0, 0.0 };
    double s2[3] = { 0.0, 0.0, 0.0 };
    int used = 0;
    for (int i = 0; i < kSamples; ++i) {
        const double ax = xl + nd(rng), ay = yl + nd(rng);
        const double bx = xr + nd(rng), by = yr + nd(rng);
        const Result r = intersect_raw(L, Rr, kFocal, ax, ay, bx, by, 0.0);
        if (r.status != PGM_OK) continue;
        const double v[3] = { r.out.ground_x - X, r.out.ground_y - Y, r.out.ground_z - Z };
        for (int k = 0; k < 3; ++k) {
            s[k] += v[k];
            s2[k] += v[k] * v[k];
        }
        ++used;
    }

    double empirical[3];
    for (int k = 0; k < 3; ++k) {
        const double mean = s[k] / used;
        empirical[k] = std::sqrt(s2[k] / used - mean * mean);
    }
    std::printf("  实测：m_X = %.4f m，m_Y = %.4f m，m_Z = %.4f m（%d 次）\n",
                empirical[0], empirical[1], empirical[2], used);

    const double analytic[3] = { ref.out.m_x, ref.out.m_y, ref.out.m_z };
    for (int k = 0; k < 3; ++k) {
        const double rel = std::fabs(empirical[k] - analytic[k]) / analytic[k];
        std::printf("  第 %d 轴相对差 = %.2f%%\n", k, 100.0 * rel);
        check(rel < 0.08, "解析中误差与实测标准差吻合（5% 量级，留出统计涨落）");
    }
}

// ---------------------------------------------------------------- 6 残余量

void test_residuals()
{
    section("6 残余量是否真的在测东西");

    const Six L = { 10000.0, 5000.0, 3000.0, 0.0015, -0.0025, 0.0155 };
    const Six Rr = { 11000.0, 5007.0, 3002.0, 0.0012, -0.0020, 0.0140 };

    const double X = 10500.0, Y = 5000.0, Z = 100.0;
    double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
    project_pair(L, Rr, kFocal, X, Y, Z, &xl, &yl, &xr, &yr);

    // 在右片 y 上注入 0.05 mm 的上下视差
    const double bias = 0.05;
    const Result r = intersect_raw(L, Rr, kFocal, xl, yl, xr, yr + bias, 0.0);
    check(r.status == PGM_OK, "注入视差后仍能交会");
    if (r.status != PGM_OK) return;

    std::printf("  注入 %.3f mm 上下视差后：\n", bias);
    std::printf("    最近距离 = %.6f m，剩余上下视差 = %.6f mm\n",
                r.out.miss_distance, r.out.vertical_parallax_mm);
    std::printf("    左片残差 = (%.3e, %.3e) mm，右片残差 = (%.3e, %.3e) mm\n",
                r.out.residual_left_x_mm, r.out.residual_left_y_mm,
                r.out.residual_right_x_mm, r.out.residual_right_y_mm);

    check(r.out.miss_distance > 1e-4, "最近距离随视差增大");
    check(std::fabs(r.out.vertical_parallax_mm) > 1e-2, "剩余上下视差随视差增大");
}

// ---------------------------------------------------------------- 7 退化

void test_errors()
{
    section("7 退化与输入错误");

    const Six L = { 10000.0, 5000.0, 3000.0, 0.0, 0.0, 0.0 };
    const Six Rr = { 11000.0, 5000.0, 3000.0, 0.0, 0.0, 0.0 };

    const double X = 10500.0, Y = 5000.0, Z = 100.0;
    double xl = 0.0, yl = 0.0, xr = 0.0, yr = 0.0;
    project_pair(L, Rr, kFocal, X, Y, Z, &xl, &yl, &xr, &yr);

    {
        // 基线为零：两片同位置，两条视线重合
        const Result r = intersect_raw(L, L, kFocal, xl, yl, xl, yl, 0.0);
        std::printf("  零基线      -> 返回 %d：%s\n", r.status, r.error);
        check(r.status == PGM_ERR_DEGENERATE, "零基线按退化拒绝");
    }
    {
        // 同名点配错：右片像点挪到几十毫米外，交会点跑到相机后方
        const Result r = intersect_raw(L, Rr, kFocal, xl, yl, xr + 80.0, yr + 60.0, 0.0);
        std::printf("  同名点配错  -> 返回 %d：%s\n", r.status, r.error);
        check(r.status == PGM_ERR_INVALID_ARGUMENT, "配错的同名点被拒绝");
    }
    {
        pgm_intersection_input in;
        in.focal_length_mm = -1.0;
        in.left = pose_of(L);
        in.right = pose_of(Rr);
        in.left_x_mm = xl;
        in.left_y_mm = yl;
        in.right_x_mm = xr;
        in.right_y_mm = yr;
        in.image_sigma_mm = 0.0;
        pgm_intersection_output out;
        const int32_t st = pgm_forward_intersection(&in, &out);
        std::printf("  主距为负    -> 返回 %d：%s\n", st, pgm_last_error());
        check(st == PGM_ERR_INVALID_ARGUMENT, "主距非正被拒绝");
    }
    {
        const Result r = intersect_raw(L, Rr, kFocal, xl, yl, xr, yr, -0.5);
        std::printf("  中误差为负  -> 返回 %d：%s\n", r.status, r.error);
        check(r.status == PGM_ERR_INVALID_ARGUMENT, "负的像点中误差被拒绝");
    }
    {
        pgm_intersection_output out;
        const int32_t st = pgm_forward_intersection(nullptr, &out);
        check(st == PGM_ERR_NULL_ARGUMENT, "空指针被拒绝");
    }
}

}  // namespace

int main()
{
    std::printf("前方交会 DLL 验收（版本 %s）\n", pgm_version());

    test_r_construction();
    test_round_trip();
    test_versus_textbook();
    test_cross_baseline();
    test_precision();
    test_residuals();
    test_errors();

    std::printf("\n");
    if (g_failures == 0) {
        std::printf("全部通过\n");
        return 0;
    }
    std::printf("%d 项失败\n", g_failures);
    return 1;
}
