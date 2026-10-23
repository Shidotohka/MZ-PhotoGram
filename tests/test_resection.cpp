// 后方交会 DLL 的验收程序。
//
// 全部用合成数据，真值已知，逐项对表：
//   1 教科书写法 vs DLL    —— 旋转矩阵构造是否等于 a1..c3 展开式
//   2 往返检验              —— 由真值正投影再反解，6 个元素能否原样恢复
//   3 雅可比 vs 有限差分    —— 设计矩阵的解析偏导是否正确
//   4 带噪声观测            —— 能否稳定收敛，精度是否在理论量级
//   5 各种退化与输入错误    —— 是否按预期拒绝
//
// 用法： pgm_resection_test
// 返回： 0 全部通过，1 有失败项

#ifdef _WIN32
#  define NOMINMAX
#  include <windows.h>
#endif

#include "photogrammetry.h"

#include <cmath>
#include <cstdio>
#include <random>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;

// Hammer 相机的真实主距与像元尺寸：5120 x 0.04503 mm = 230.55 mm 幅面
constexpr double kFocal = 152.72;
constexpr double kPixelMm = 0.04503;
constexpr int    kFrame = 5120;

int g_failures = 0;

void check(bool ok, const char* what)
{
    if (!ok) {
        std::printf("  [FAIL] %s\n", what);
        ++g_failures;
    }
}

struct Six
{
    double xs, ys, zs, phi, omega, kappa;
};

// 作者手工转录的教材展开式，刻意不经过矩阵乘法，用来对表 DLL 的 R 构造。
// 记法：R 的第一列为 (a1,a2,a3)，第二列 (b1,b2,b3)，第三列 (c1,c2,c3)。
void textbook_project(const Six& e, double f,
                      double X, double Y, double Z, double* x, double* y)
{
    const double sp = std::sin(e.phi), cp = std::cos(e.phi);
    const double sw = std::sin(e.omega), cw = std::cos(e.omega);
    const double sk = std::sin(e.kappa), ck = std::cos(e.kappa);

    const double a1 = cp * ck - sp * sw * sk;
    const double a2 = -cp * sk - sp * sw * ck;
    const double a3 = -sp * cw;
    const double b1 = cw * sk;
    const double b2 = cw * ck;
    const double b3 = -sw;
    const double c1 = sp * ck + cp * sw * sk;
    const double c2 = -sp * sk + cp * sw * ck;
    const double c3 = cp * cw;

    const double dX = X - e.xs, dY = Y - e.ys, dZ = Z - e.zs;
    const double U = a1 * dX + b1 * dY + c1 * dZ;
    const double V = a2 * dX + b2 * dY + c2 * dZ;
    const double W = a3 * dX + b3 * dY + c3 * dZ;

    *x = -f * U / W;
    *y = -f * V / W;
}

// 3x3 控制点网，高程略有起伏
void build_control(const Six& t, std::vector<double>& gx, std::vector<double>& gy,
                   std::vector<double>& gz)
{
    const double ox[9] = { -800, 0, 800, -800, 0, 800, -800, 0, 800 };
    const double oy[9] = { -800, -800, -800, 0, 0, 0, 800, 800, 800 };
    const double oz[9] = { 12, -8, 4, -15, 0, 9, 6, -11, 14 };

    for (int i = 0; i < 9; ++i) {
        gx.push_back(t.xs + ox[i]);
        gy.push_back(t.ys + oy[i]);
        gz.push_back(100.0 + oz[i]);
    }
}

bool project_all(const Six& e, double f, const std::vector<double>& gx,
                 const std::vector<double>& gy, const std::vector<double>& gz,
                 std::vector<double>& ox, std::vector<double>& oy)
{
    ox.assign(gx.size(), 0.0);
    oy.assign(gx.size(), 0.0);
    for (size_t i = 0; i < gx.size(); ++i) {
        if (pgm_project_point(f, e.xs, e.ys, e.zs, e.phi, e.omega, e.kappa,
                              gx[i], gy[i], gz[i], &ox[i], &oy[i]) != PGM_OK) {
            std::printf("  [FAIL] 正投影失败：%s\n", pgm_last_error());
            ++g_failures;
            return false;
        }
    }
    return true;
}

int32_t run(const Six& e, bool use_initial, int max_iter,
            double f, const std::vector<double>& gx, const std::vector<double>& gy,
            const std::vector<double>& gz, const std::vector<double>& ox,
            const std::vector<double>& oy, pgm_resection_output* out,
            std::vector<double>* rx, std::vector<double>* ry)
{
    const int n = static_cast<int>(gx.size());
    rx->assign(n, 0.0);
    ry->assign(n, 0.0);

    pgm_resection_input in{};
    in.count = n;
    in.image_x_mm = ox.data();
    in.image_y_mm = oy.data();
    in.ground_x = gx.data();
    in.ground_y = gy.data();
    in.ground_z = gz.data();
    in.focal_length_mm = f;
    in.use_initial = use_initial ? 1 : 0;
    in.initial_xs = e.xs;
    in.initial_ys = e.ys;
    in.initial_zs = e.zs;
    in.initial_phi_rad = e.phi;
    in.initial_omega_rad = e.omega;
    in.initial_kappa_rad = e.kappa;
    in.max_iterations = max_iter;

    *out = pgm_resection_output{};
    out->residual_x_mm = rx->data();
    out->residual_y_mm = ry->data();
    return pgm_resection(&in, out);
}

const char* rc_name(int32_t rc)
{
    switch (rc) {
    case PGM_OK: return "OK";
    case PGM_ERR_NULL_ARGUMENT: return "NULL_ARGUMENT";
    case PGM_ERR_TOO_FEW_POINTS: return "TOO_FEW_POINTS";
    case PGM_ERR_DEGENERATE: return "DEGENERATE";
    case PGM_ERR_INVALID_ARGUMENT: return "INVALID_ARGUMENT";
    case PGM_ERR_INTERNAL: return "INTERNAL";
    case PGM_ERR_AXIS_SIGN: return "AXIS_SIGN";
    case PGM_ERR_NOT_CONVERGED: return "NOT_CONVERGED";
    default: return "?";
    }
}

// ---------------------------------------------------------------- 1 约定

void test_convention()
{
    std::printf("=== 1 旋转矩阵与教材展开式对表 ===\n");

    // 故意用一大角度，任一项符号错都会立刻暴露
    const Six e{ 1234.5, -678.9, 920.0, 12.0 * kPi / 180.0,
                 -8.0 * kPi / 180.0, 47.0 * kPi / 180.0 };

    const double pts[4][3] = {
        { 1350.0, -600.0, 100.0 },
        { 1100.0, -780.0, 60.0 },
        { 1400.0, -700.0, 130.0 },
        { 900.0, -500.0, 90.0 },
    };

    double worst = 0.0;
    for (int k = 0; k < 4; ++k) {
        double xs = 0.0, ys = 0.0;
        if (pgm_project_point(kFocal, e.xs, e.ys, e.zs, e.phi, e.omega, e.kappa,
                              pts[k][0], pts[k][1], pts[k][2], &xs, &ys) != PGM_OK) {
            std::printf("  [FAIL] 正投影失败：%s\n", pgm_last_error());
            ++g_failures;
            return;
        }
        double xt = 0.0, yt = 0.0;
        textbook_project(e, kFocal, pts[k][0], pts[k][1], pts[k][2], &xt, &yt);

        const double dx = std::fabs(xs - xt);
        const double dy = std::fabs(ys - yt);
        if (dx > worst) worst = dx;
        if (dy > worst) worst = dy;
    }

    std::printf("  最大偏差 %.3e mm（阈值 1e-10）\n", worst);
    check(worst < 1e-10, "DLL 的 R 与教材 a1..c3 展开式不一致");

    // 语义自检：垂直摄影、κ=0 时，正东的点落在 +x，正北的点落在 +y，
    // 且 x/ΔX = f/H。这三条是坐标约定的"常识"，与实现无关。
    const Six nadir{ 0.0, 0.0, 1500.0, 0.0, 0.0, 0.0 };
    double ex = 0.0, ey = 0.0, nx = 0.0, ny = 0.0;
    pgm_project_point(kFocal, nadir.xs, nadir.ys, nadir.zs, 0, 0, 0,
                      25.0, 0.0, 100.0, &ex, &ey);
    pgm_project_point(kFocal, nadir.xs, nadir.ys, nadir.zs, 0, 0, 0,
                      0.0, 25.0, 100.0, &nx, &ny);

    const double expect = kFocal * 25.0 / 1400.0;
    std::printf("  东向 25 m -> x=%+.6f mm（应 %+.6f），北向 -> y=%+.6f mm\n", ex, expect, ny);
    check(std::fabs(ex - expect) < 1e-9, "正东的点没有落在 +x，坐标约定与教材不一致");
    check(std::fabs(ey) < 1e-9, "正东的点不应有 y 分量");
    check(std::fabs(nx) < 1e-9, "正北的点不应有 x 分量");
    check(std::fabs(ny - expect) < 1e-9, "正北的点没有落在 +y，坐标约定与教材不一致");
}

// ---------------------------------------------------------------- 2 往返

void test_roundtrip()
{
    std::printf("\n=== 2 往返检验：正投影后反解 ===\n");

    const Six truth{ 500000.0, 4000000.0, 1600.0,
                     0.35 * kPi / 180.0, -0.25 * kPi / 180.0, 6.0 * kPi / 180.0 };

    std::vector<double> gx, gy, gz, ox, oy;
    build_control(truth, gx, gy, gz);
    if (!project_all(truth, kFocal, gx, gy, gz, ox, oy)) return;

    double xmax = 0.0;
    for (size_t i = 0; i < ox.size(); ++i) {
        if (std::fabs(ox[i]) > xmax) xmax = std::fabs(ox[i]);
        if (std::fabs(oy[i]) > xmax) xmax = std::fabs(oy[i]);
    }
    const double half = 0.5 * kFrame * kPixelMm;
    std::printf("  幅面半宽 %.2f mm，像点最大 %.2f mm\n", half, xmax);
    check(xmax < half, "控制点投影出幅面，测试配置有问题");

    // 2a 自动估计初值
    {
        pgm_resection_output out;
        std::vector<double> rx, ry;
        const int32_t rc = run(truth, false, 0, kFocal, gx, gy, gz, ox, oy, &out, &rx, &ry);

        std::printf("  自动初值：rc=%s 迭代 %d 次 sigma0=%.3e mm 条件数 %.1f\n",
                    rc_name(rc), out.iterations, out.sigma0_mm, out.condition);
        if (rc != PGM_OK) std::printf("          %s\n", pgm_last_error());
        check(rc == PGM_OK, "自动估计初值后未收敛");
        check(out.iterations <= 10, "迭代次数超过 10");

        const double dxyz[3] = { out.xs - truth.xs, out.ys - truth.ys, out.zs - truth.zs };
        const double dang[3] = { out.phi_rad - truth.phi, out.omega_rad - truth.omega,
                                 out.kappa_rad - truth.kappa };
        std::printf("          dXs=%+.3e dYs=%+.3e dZs=%+.3e m\n", dxyz[0], dxyz[1], dxyz[2]);
        std::printf("          dphi=%+.3e domega=%+.3e dkappa=%+.3e rad\n",
                    dang[0], dang[1], dang[2]);

        for (int k = 0; k < 3; ++k) {
            check(std::fabs(dxyz[k]) < 1e-6, "位置未恢复到 1e-6");
            check(std::fabs(dang[k]) < 1e-6, "角度未恢复到 1e-6 rad");
        }
        check(out.sigma0_mm < 1e-9, "无噪声数据残差不为零");
        check(out.redundancy == 12, "多余观测数应为 2*9-6 = 12");
        check(out.used_count == 9, "参与解算的点数应为 9");
    }

    // 2b 显式给一组偏掉的初值，检验收敛域
    {
        Six guess = truth;
        guess.xs += 60.0;
        guess.ys -= 45.0;
        guess.zs += 80.0;
        guess.phi += 0.6 * kPi / 180.0;
        guess.omega -= 0.5 * kPi / 180.0;
        guess.kappa += 2.0 * kPi / 180.0;

        pgm_resection_output out;
        std::vector<double> rx, ry;
        const int32_t rc = run(guess, true, 0, kFocal, gx, gy, gz, ox, oy, &out, &rx, &ry);
        std::printf("  偏离初值：rc=%s 迭代 %d 次 dZs=%+.3e m\n",
                    rc_name(rc), out.iterations, out.zs - truth.zs);
        check(rc == PGM_OK, "从偏离初值出发未收敛");
        check(std::fabs(out.zs - truth.zs) < 1e-6, "偏离初值下 Zs 未恢复");
        check(std::fabs(out.kappa_rad - truth.kappa) < 1e-6, "偏离初值下 kappa 未恢复");
    }
}

// ---------------------------------------------------------------- 3 雅可比

void test_jacobian()
{
    std::printf("\n=== 3 设计矩阵 vs 中心差分 ===\n");

    const Six e{ 500000.0, 4000000.0, 1600.0,
                 2.5 * kPi / 180.0, -1.7 * kPi / 180.0, 11.0 * kPi / 180.0 };

    std::vector<double> gx, gy, gz;
    build_control(e, gx, gy, gz);
    std::vector<double> ox, oy;
    if (!project_all(e, kFocal, gx, gy, gz, ox, oy)) return;

    const int n = static_cast<int>(gx.size());
    std::vector<double> a(static_cast<size_t>(2 * n) * 6);
    std::vector<double> l(static_cast<size_t>(2 * n));

    const int32_t rc = pgm_resection_design(n, ox.data(), oy.data(), gx.data(), gy.data(),
                                            gz.data(), kFocal, e.xs, e.ys, e.zs,
                                            e.phi, e.omega, e.kappa, a.data(), l.data());
    if (rc != PGM_OK) {
        std::printf("  [FAIL] 设计矩阵生成失败：%s\n", pgm_last_error());
        ++g_failures;
        return;
    }

    // 差分步长：平移用 1 m（函数在 H=1500 m 的尺度上变化，截断误差可忽略），
    // 角度用 1e-6 rad。两者都不至于让舍入误差放大。
    const double step[6] = { 1.0, 1.0, 1.0, 1e-6, 1e-6, 1e-6 };

    double worst = 0.0;
    int worst_i = -1, worst_j = -1;
    int proj_fail = 0;
    double a_max = 0.0;
    for (int j = 0; j < 6; ++j) {
        for (int i = 0; i < n; ++i) {
            double xp = 0.0, yp = 0.0, xm = 0.0, ym = 0.0;

            Six ep = e, em = e;
            double* fields[6] = { &ep.xs, &ep.ys, &ep.zs, &ep.phi, &ep.omega, &ep.kappa };
            double* fields_m[6] = { &em.xs, &em.ys, &em.zs, &em.phi, &em.omega, &em.kappa };
            *fields[j] += step[j];
            *fields_m[j] -= step[j];

            const int32_t rc_p = pgm_project_point(kFocal, ep.xs, ep.ys, ep.zs, ep.phi,
                                                   ep.omega, ep.kappa, gx[i], gy[i], gz[i], &xp, &yp);
            const int32_t rc_m = pgm_project_point(kFocal, em.xs, em.ys, em.zs, em.phi,
                                                   em.omega, em.kappa, gx[i], gy[i], gz[i], &xm, &ym);
            if (rc_p != PGM_OK || rc_m != PGM_OK) ++proj_fail;

            const double nx = (xp - xm) / (2.0 * step[j]);
            const double ny = (yp - ym) / (2.0 * step[j]);

            const double ax = a[(2 * i) * 6 + j];
            const double ay = a[(2 * i + 1) * 6 + j];
            if (std::fabs(ax) > a_max) a_max = std::fabs(ax);
            if (std::fabs(ay) > a_max) a_max = std::fabs(ay);

            const double diff = std::fabs(ax - nx) + std::fabs(ay - ny);
            if (diff > worst) {
                worst = diff;
                worst_i = i;
                worst_j = j;
            }
        }
    }

    // 用全矩阵的最大元作尺度，避免"两边都是零"被局部小尺度放大成假失败
    const double rel = worst / (a_max > 0.0 ? a_max : 1.0);
    std::printf("  最大绝对偏差 %.3e，相对 %.3e（矩阵最大元 %.3g），最差点 i=%d j=%d，投影失败 %d 次\n",
                worst, rel, a_max, worst_i, worst_j, proj_fail);
    check(proj_fail == 0, "差分扰动下正投影失败");
    check(rel < 1e-7, "解析偏导与中心差分不符");

    // 常数项应为 观测 - 计算，无噪声时应接近零
    double max_l = 0.0;
    for (int k = 0; k < 2 * n; ++k) {
        if (std::fabs(l[k]) > max_l) max_l = std::fabs(l[k]);
    }
    std::printf("  真值处常数项最大 %.3e mm\n", max_l);
    check(max_l < 1e-9, "真值处常数项不为零");
}

// ---------------------------------------------------------------- 4 噪声

void test_noise()
{
    std::printf("\n=== 4 带噪声观测：20 次实现 ===\n");

    const Six truth{ 500000.0, 4000000.0, 1600.0,
                     0.35 * kPi / 180.0, -0.25 * kPi / 180.0, 6.0 * kPi / 180.0 };

    std::vector<double> gx, gy, gz, ox0, oy0;
    build_control(truth, gx, gy, gz);
    if (!project_all(truth, kFocal, gx, gy, gz, ox0, oy0)) return;

    const double sigma_px = 0.5;
    const double expect_sigma = sigma_px * kPixelMm;

    const int trials = 20;
    double mean_sigma = 0.0;
    double rms[6] = { 0.0 };
    double mean_m[6] = { 0.0 };
    int worst_iter = 0;

    for (int t = 0; t < trials; ++t) {
        std::vector<double> ox = ox0, oy = oy0;
        std::mt19937 rng(20241121u + static_cast<unsigned>(t));
        std::normal_distribution<double> noise(0.0, expect_sigma);
        for (size_t i = 0; i < ox.size(); ++i) {
            ox[i] += noise(rng);
            oy[i] += noise(rng);
        }

        pgm_resection_output out;
        std::vector<double> rx, ry;
        const int32_t rc = run(truth, false, 0, kFocal, gx, gy, gz, ox, oy, &out, &rx, &ry);
        if (rc != PGM_OK) {
            std::printf("  [FAIL] 第 %d 次实现 rc=%s %s\n", t, rc_name(rc), pgm_last_error());
            ++g_failures;
            return;
        }
        if (out.iterations > worst_iter) worst_iter = out.iterations;

        const double err[6] = { out.xs - truth.xs, out.ys - truth.ys, out.zs - truth.zs,
                                out.phi_rad - truth.phi, out.omega_rad - truth.omega,
                                out.kappa_rad - truth.kappa };
        const double m[6] = { out.m_xs, out.m_ys, out.m_zs,
                              out.m_phi_rad, out.m_omega_rad, out.m_kappa_rad };
        mean_sigma += out.sigma0_mm;
        for (int k = 0; k < 6; ++k) {
            rms[k] += err[k] * err[k];
            mean_m[k] += m[k];
        }
    }

    mean_sigma /= trials;
    for (int k = 0; k < 6; ++k) {
        rms[k] = std::sqrt(rms[k] / trials);
        mean_m[k] /= trials;
    }

    std::printf("  取点误差 %.1f px = %.5f mm，理论 sigma0 应为 %.5f mm\n",
                sigma_px, expect_sigma, expect_sigma);
    std::printf("  实测 sigma0 均值 %.5f mm（相对偏差 %+.2f%%），最长迭代 %d 次\n",
                mean_sigma, (mean_sigma - expect_sigma) / expect_sigma * 100.0, worst_iter);
    check(worst_iter <= 10, "带噪声数据迭代次数超过 10");
    check(std::fabs(mean_sigma - expect_sigma) < 0.05 * expect_sigma,
          "sigma0 均值与取点误差不符");

    // 精度评定自检：参数真误差的 RMS 应与报告的中误差同量级
    std::printf("  参数   真误差 RMS      报告中误差      比值\n");
    for (int k = 0; k < 6; ++k) {
        const double ratio = (mean_m[k] > 0.0) ? rms[k] / mean_m[k] : 0.0;
        std::printf("    %d  %.6f  %.6f  %.2f\n", k, rms[k], mean_m[k], ratio);
        check(ratio > 0.3 && ratio < 3.0, "参数真误差 RMS 与报告中误差差太多，协方差可能有误");
    }
}

// ---------------------------------------------------------------- 5 退化

void test_rejections()
{
    std::printf("\n=== 5 退化与非法输入 ===\n");

    const Six truth{ 500000.0, 4000000.0, 1600.0, 0.0, 0.0, 0.0 };

    std::vector<double> gx, gy, gz, ox, oy;
    build_control(truth, gx, gy, gz);
    if (!project_all(truth, kFocal, gx, gy, gz, ox, oy)) return;

    pgm_resection_output out;
    std::vector<double> rx, ry;

    // 5a 只有 3 个控制点
    {
        std::vector<double> sx(gx.begin(), gx.begin() + 3), sy(gy.begin(), gy.begin() + 3),
            sz(gz.begin(), gz.begin() + 3), sox(ox.begin(), ox.begin() + 3),
            soy(oy.begin(), oy.begin() + 3);
        const int32_t rc = run(truth, false, 0, kFocal, sx, sy, sz, sox, soy, &out, &rx, &ry);
        std::printf("  3 个控制点：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_TOO_FEW_POINTS, "3 个控制点应被拒绝");
    }

    // 5b 控制点共线（给真值初值，绕开初值估计，直接测法方程秩亏）
    {
        std::vector<double> sx, sy, sz, sox, soy;
        for (int i = 0; i < 5; ++i) {
            const double x = truth.xs - 700.0 + 350.0 * i;
            sx.push_back(x);
            sy.push_back(truth.ys);
            sz.push_back(100.0);
        }
        if (!project_all(truth, kFocal, sx, sy, sz, sox, soy)) return;
        const int32_t rc = run(truth, true, 0, kFocal, sx, sy, sz, sox, soy, &out, &rx, &ry);
        std::printf("  控制点共线：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_DEGENERATE, "共线控制点应被判为退化");
    }

    // 5c 控制点重合
    {
        std::vector<double> sx(5, truth.xs), sy(5, truth.ys), sz(5, 100.0), sox(5, 0.0),
            soy(5, 0.0);
        const int32_t rc = run(truth, false, 0, kFocal, sx, sy, sz, sox, soy, &out, &rx, &ry);
        std::printf("  控制点重合：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_DEGENERATE, "重合控制点应被判为退化");
    }

    // 5d 初值把地面点放到相机背后
    {
        Six below = truth;
        below.zs = 50.0;  // 摄影中心低于地面
        const int32_t rc = run(below, true, 0, kFocal, gx, gy, gz, ox, oy, &out, &rx, &ry);
        std::printf("  初值在相机背后：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_INVALID_ARGUMENT, "W >= 0 应被拒绝");
    }

    // 5e 迭代上限只有 1 次
    {
        Six guess = truth;
        guess.xs += 80.0;
        guess.kappa += 0.05;
        const int32_t rc = run(guess, true, 1, kFocal, gx, gy, gz, ox, oy, &out, &rx, &ry);
        std::printf("  只迭代 1 次：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_NOT_CONVERGED, "迭代 1 次不应宣布收敛");
        check(out.iterations == 1, "应报告迭代了 1 次");
        check(out.used_count == 9, "未收敛时也应填出诊断信息");
    }

    // 5f 主距非正
    {
        pgm_resection_input in{};
        in.count = 9;
        in.image_x_mm = ox.data();
        in.image_y_mm = oy.data();
        in.ground_x = gx.data();
        in.ground_y = gy.data();
        in.ground_z = gz.data();
        in.focal_length_mm = 0.0;
        pgm_resection_output o2{};
        const int32_t rc = pgm_resection(&in, &o2);
        std::printf("  主距为 0：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_INVALID_ARGUMENT, "主距为 0 应被拒绝");
    }

    // 5g 空指针
    {
        const int32_t rc = pgm_resection(nullptr, nullptr);
        std::printf("  空指针：rc=%s  %s\n", rc_name(rc), pgm_last_error());
        check(rc == PGM_ERR_NULL_ARGUMENT, "空指针应被拒绝");
    }
}

// ---------------------------------------------------------------- 6 手算对表

// 附件里要给读者手算的算例：相机在 (0,0,1000)，三个角全为 0，
// 地面点取正东南西北四个方向各 100 m 处。这一组数可以直接对着公式按计算器。
void test_handcheck()
{
    std::printf("\n=== 6 手算对表（f=%.2f，相机 (0,0,1000)，phi=omega=kappa=0）===\n", kFocal);

    const double gx[4] = { 100.0, -100.0, 0.0, 0.0 };
    const double gy[4] = { 0.0, 0.0, 100.0, -100.0 };
    const double gz[4] = { 0.0, 0.0, 0.0, 0.0 };
    std::vector<double> ox(4), oy(4);
    for (int i = 0; i < 4; ++i) {
        const int32_t rc = pgm_project_point(kFocal, 0.0, 0.0, 1000.0, 0.0, 0.0, 0.0,
                                             gx[i], gy[i], gz[i], &ox[i], &oy[i]);
        if (rc != PGM_OK) {
            std::printf("  [FAIL] 正投影失败：%s\n", pgm_last_error());
            ++g_failures;
            return;
        }
    }

    std::vector<double> a(4 * 2 * 6);
    std::vector<double> l(4 * 2);
    const int32_t rc = pgm_resection_design(4, ox.data(), oy.data(), gx, gy, gz, kFocal,
                                            0.0, 0.0, 1000.0, 0.0, 0.0, 0.0, a.data(), l.data());
    if (rc != PGM_OK) {
        std::printf("  [FAIL] 设计矩阵生成失败：%s\n", pgm_last_error());
        ++g_failures;
        return;
    }

    std::printf("    点        x/mm     y/mm      d/dXs    d/dYs    d/dZs       d/dphi     d/domega   d/dkappa\n");
    for (int i = 0; i < 4; ++i) {
        std::printf("  (%4.0f,%4.0f)  %8.4f %8.4f", gx[i], gy[i], ox[i], oy[i]);
        std::printf("  |");
        for (int j = 0; j < 6; ++j) std::printf(" %10.5f", a[(2 * i) * 6 + j]);
        std::printf("\n                        %8s %8s", "", "");
        std::printf("  |");
        for (int j = 0; j < 6; ++j) std::printf(" %10.5f", a[(2 * i + 1) * 6 + j]);
        std::printf("\n");
    }

    // 手算可核的三条：x/ΔX = f/H、d/dXs = -f/H、d/dphi = -(f + x^2/f)
    const double h = 1000.0;
    const double x1 = kFocal * 100.0 / h;
    check(std::fabs(ox[0] - x1) < 1e-12, "正东点像点坐标不等于 f*ΔX/H");
    check(std::fabs(a[0] - (-kFocal / h)) < 1e-12, "d/dXs 不等于 -f/H");
    check(std::fabs(a[3] - (-(kFocal + x1 * x1 / kFocal))) < 1e-12, "d/dphi 不等于 -(f + x^2/f)");
    check(std::fabs(a[5]) < 1e-12, "正东点的 d/dkappa 应为 0");
    check(std::fabs(a[(2 * 0 + 1) * 6 + 5] - (-x1)) < 1e-12, "d/dkappa 的纵坐标分量应为 -x");
}

}  // namespace

int main()
{
#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif

    std::printf("=== pgm_resection 自检 ===\n");
    std::printf("版本 %s   f=%.2f mm  delta=%.5f mm/px\n\n", pgm_version(), kFocal, kPixelMm);

    test_convention();
    test_roundtrip();
    test_jacobian();
    test_noise();
    test_rejections();
    test_handcheck();

    std::printf("\n%s\n", g_failures == 0 ? "DLL SELFTEST PASS" : "DLL SELFTEST FAIL");
    return g_failures == 0 ? 0 : 1;
}
