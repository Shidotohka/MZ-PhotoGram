// 核线几何的验收程序。
//
// 全部用合成像对，真值已知：左片位姿取 (0,0,0,0,0,0)，右片取 (B·b, φ, ω, κ)，
// 于是「相对定向的 (b, R)」与「两片的绝对外方位」是同一组几何的两种写法。
// 像点由 pgm_project_point 正投影得到，核线由 pgm_epipolar_* 算出——
// 两条独立代码路径，对不上就是约定写歪了。
//
//   1 合成像对      —— 右点是否落在「左点所定核线」上，左点是否在左核线上
//   2 同名核线      —— 由左点与由右点算出的核面转角 theta 是否一致（同行）
//   3 核点          —— 所有核线是否都过核点（左过 e1、右过 e2）
//   4 距离函数      —— 沿法向挪 1 mm，是否读回 1 mm
//   5 标准立体      —— 核点跑到无穷远、右核线近似水平
//   6 退化与输入错误 —— 前方运动、零基线、主距为零等是否按预期拒绝
//
// 用法： pgm_epipolar_test
// 返回： 0 全部通过，1 有失败项

#include "photogrammetry.h"

#include <cmath>
#include <cstdio>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;

// 与 Hammer 相机同量级
constexpr double kFocal = 152.72;

int g_failures = 0;

void say(bool ok, const char* what, const char* detail)
{
    std::printf("  [%s] %-30s %s\n", ok ? "PASS" : "FAIL", what, detail);
    if (!ok) ++g_failures;
}

struct Vec3
{
    double x, y, z;
};

struct Pose
{
    double xs, ys, zs;
    double phi, omega, kappa;
};

// 记录一个失败码是否等于期望值
void say_rc(int32_t rc, int32_t want, const char* what)
{
    char detail[96];
    std::snprintf(detail, sizeof detail, "rc = %d（期望 %d）", rc, want);
    say(rc == want, what, detail);
}

struct Synthetic
{
    double b[3];
    double phi, omega, kappa;
    double baseline;
    std::vector<Vec3> ground;
    std::vector<double> lx, ly;   // 左片像点（mm）
    std::vector<double> rx, ry;   // 右片像点（mm）
    pgm_epipolar_input epi;
};

bool build_scene(Synthetic* sc, const double b_in[3], double phi, double omega, double kappa,
                 double baseline, int count)
{
    const double nb = std::sqrt(b_in[0] * b_in[0] + b_in[1] * b_in[1] + b_in[2] * b_in[2]);
    if (!(nb > 0.0)) return false;

    for (int i = 0; i < 3; ++i) sc->b[i] = b_in[i] / nb;
    sc->phi = phi;
    sc->omega = omega;
    sc->kappa = kappa;
    sc->baseline = baseline;

    double rot[9];
    if (pgm_epipolar_rotation(phi, omega, kappa, rot) != PGM_OK) return false;

    sc->epi.bx = sc->b[0];
    sc->epi.by = sc->b[1];
    sc->epi.bz = sc->b[2];
    for (int i = 0; i < 9; ++i) sc->epi.rotation[i] = rot[i];
    sc->epi.focal_length_mm = kFocal;

    const Pose left{ 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    const Pose right{ baseline * sc->b[0], baseline * sc->b[1], baseline * sc->b[2],
                      phi, omega, kappa };

    // 简单确定性序列，不用 <random>：任何编译器重跑都是同一组点
    unsigned long long seed = 20261104ULL;
    auto next = [&seed]() {
        seed = seed * 6364136223846793005ULL + 1442695040888963407ULL;
        return static_cast<double>((seed >> 11) & ((1ULL << 53) - 1)) /
               static_cast<double>(1ULL << 53);
    };

    sc->ground.clear();
    sc->lx.clear(); sc->ly.clear();
    sc->rx.clear(); sc->ry.clear();

    int made = 0;
    while (made < count) {
        const Vec3 p{ -12.0 + 24.0 * next(),
                      -12.0 + 24.0 * next(),
                      -45.0 + 20.0 * next() };

        double lxp = 0.0, lyp = 0.0, rxp = 0.0, ryp = 0.0;
        const int32_t rl = pgm_project_point(kFocal, left.xs, left.ys, left.zs,
                                             left.phi, left.omega, left.kappa,
                                             p.x, p.y, p.z, &lxp, &lyp);
        const int32_t rr = pgm_project_point(kFocal, right.xs, right.ys, right.zs,
                                             right.phi, right.omega, right.kappa,
                                             p.x, p.y, p.z, &rxp, &ryp);
        if (rl != PGM_OK || rr != PGM_OK) continue;

        // 只要落在像幅内（±115 mm，与 Hammer 幅面同量级）的点
        if (std::fabs(lxp) > 115.0 || std::fabs(lyp) > 115.0) continue;
        if (std::fabs(rxp) > 115.0 || std::fabs(ryp) > 115.0) continue;

        sc->ground.push_back(p);
        sc->lx.push_back(lxp); sc->ly.push_back(lyp);
        sc->rx.push_back(rxp); sc->ry.push_back(ryp);
        ++made;
    }
    return true;
}

}  // namespace

int main()
{
    std::printf("核线几何验收（合成像对）\n");
    std::printf("主距 %.2f mm，基线取 1 个长度单位，点距基线 20~45 个单位\n\n", kFocal);

    // 场景：一般构型（基线三个分量都有），小倾角
    const double b_general[3] = { 1.0, 0.08, 0.03 };
    Synthetic sc{};
    if (!build_scene(&sc, b_general, 0.004, -0.003, 0.02, 1.0, 200)) {
        std::printf("  [FAIL] 造场景失败\n");
        return 1;
    }

    // ---------------------------------------------------------------- 1
    std::printf("== 1 合成像对：核线是否穿过同名点 ==\n");
    {
        double worst_right = 0.0, worst_left = 0.0, worst_signed = 0.0;
        int pairs = 0;

        for (size_t k = 0; k < sc.lx.size(); ++k) {
            pgm_line2 left{}, right{};
            const int32_t rc = pgm_epipolar_lines(&sc.epi, sc.lx[k], sc.ly[k], &left, &right);
            if (rc != PGM_OK) continue;

            const double dl = std::fabs(left.a * sc.lx[k] + left.b * sc.ly[k] + left.c);
            const double dr = std::fabs(right.a * sc.rx[k] + right.b * sc.ry[k] + right.c);
            if (dl > worst_left) worst_left = dl;
            if (dr > worst_right) worst_right = dr;
            if (dl > worst_signed) worst_signed = dl;
            ++pairs;
        }

        char detail[160];
        std::snprintf(detail, sizeof detail, "%d 对，左点偏离 %.2e mm，右点偏离 %.2e mm",
                      pairs, worst_left, worst_right);
        say(pairs == 200 && worst_left < 1e-9 && worst_right < 1e-9,
            "左右核线都穿过同名像点", detail);
    }

    // ---------------------------------------------------------------- 2
    std::printf("== 2 同名核线：两边算出的核面转角是否一致 ==\n");
    {
        double worst = 0.0;
        int compared = 0;
        for (size_t k = 0; k < sc.lx.size(); ++k) {
            double tl = 0.0, tr = 0.0;
            if (pgm_epipolar_row(&sc.epi, 0, sc.lx[k], sc.ly[k], &tl) != PGM_OK) continue;
            if (pgm_epipolar_row(&sc.epi, 1, sc.rx[k], sc.ry[k], &tr) != PGM_OK) continue;

            double d = std::fabs(tl - tr);
            // theta 在 [0, pi)，两个方向相反的核面是同一个平面：取到边界的差值
            if (d > kPi / 2) d = kPi - d;
            if (d > worst) worst = d;
            ++compared;
        }

        char detail[128];
        std::snprintf(detail, sizeof detail, "%d 对，最大差 %.2e rad", compared, worst);
        say(compared == 200 && worst < 1e-9, "同名点落在同一个核面上", detail);
    }

    // ---------------------------------------------------------------- 3
    std::printf("== 3 核点：所有核线是否都过核点 ==\n");
    {
        double e1x = 0.0, e1y = 0.0, e2x = 0.0, e2y = 0.0;
        int32_t e1_inf = -1, e2_inf = -1;
        const int32_t rc = pgm_epipolar_epipoles(&sc.epi, &e1x, &e1y, &e1_inf,
                                                 &e2x, &e2y, &e2_inf);

        double worst1 = 0.0, worst2 = 0.0;
        if (rc == PGM_OK) {
            for (size_t k = 0; k < sc.lx.size(); ++k) {
                pgm_line2 left{}, right{};
                if (pgm_epipolar_lines(&sc.epi, sc.lx[k], sc.ly[k], &left, &right) != PGM_OK) continue;
                const double d1 = std::fabs(left.a * e1x + left.b * e1y + left.c);
                const double d2 = std::fabs(right.a * e2x + right.b * e2y + right.c);
                if (d1 > worst1) worst1 = d1;
                if (d2 > worst2) worst2 = d2;
            }
        }

        char detail[160];
        std::snprintf(detail, sizeof detail, "e1(%.3f, %.3f) 偏差 %.2e，e2(%.3f, %.3f) 偏差 %.2e",
                      e1x, e1y, worst1, e2x, e2y, worst2);
        say(rc == PGM_OK && e1_inf == 0 && e2_inf == 0 && worst1 < 1e-9 && worst2 < 1e-9,
            "核线全部过核点", detail);
    }

    // ---------------------------------------------------------------- 4
    std::printf("== 4 距离函数：沿法向挪 1 mm 是否读回 1 mm ==\n");
    {
        double worst = 0.0;
        int checked = 0;
        for (size_t k = 0; k < sc.lx.size(); k += 7) {
            pgm_line2 left{}, right{};
            if (pgm_epipolar_lines(&sc.epi, sc.lx[k], sc.ly[k], &left, &right) != PGM_OK) continue;

            // 把右点沿核线法向挪 1 mm
            const double mx = sc.rx[k] + right.a * 1.0;
            const double my = sc.ry[k] + right.b * 1.0;

            double base = 0.0, moved = 0.0;
            if (pgm_epipolar_distance(&sc.epi, sc.lx[k], sc.ly[k], sc.rx[k], sc.ry[k], &base) != PGM_OK)
                continue;
            if (pgm_epipolar_distance(&sc.epi, sc.lx[k], sc.ly[k], mx, my, &moved) != PGM_OK)
                continue;

            const double d = std::fabs(std::fabs(moved - base) - 1.0);
            if (d > worst) worst = d;
            ++checked;
        }

        char detail[128];
        std::snprintf(detail, sizeof detail, "%d 个点，最大误差 %.2e mm", checked, worst);
        say(checked > 20 && worst < 1e-9, "距离函数线性", detail);
    }

    // ---------------------------------------------------------------- 5
    std::printf("== 5 标准立体：核点在无穷远、核线水平 ==\n");
    {
        Synthetic std_pair{};
        const double b_std[3] = { 1.0, 0.0, 0.0 };
        const bool ok_scene = build_scene(&std_pair, b_std, 0.0, 0.0, 0.0, 1.0, 50);

        double e1x = 0.0, e1y = 0.0, e2x = 0.0, e2y = 0.0;
        int32_t e1_inf = 0, e2_inf = 0;
        const int32_t rc = ok_scene
            ? pgm_epipolar_epipoles(&std_pair.epi, &e1x, &e1y, &e1_inf, &e2x, &e2y, &e2_inf)
            : PGM_ERR_INTERNAL;

        double worst_horiz = 0.0;
        int horizontal = 0;
        if (ok_scene) {
            for (size_t k = 0; k < std_pair.lx.size(); ++k) {
                pgm_line2 left{}, right{};
                if (pgm_epipolar_lines(&std_pair.epi, std_pair.lx[k], std_pair.ly[k],
                                       &left, &right) != PGM_OK) continue;
                // 水平线的法向是 (0, ±1)：|a| 应当接近 0、|b| 接近 1
                const double off = std::fabs(right.a);
                if (off > worst_horiz) worst_horiz = off;
                ++horizontal;
            }
        }

        char detail[160];
        std::snprintf(detail, sizeof detail, "e1 无穷远=%d e2 无穷远=%d，核线法向 |a| 最大 %.2e",
                      e1_inf, e2_inf, worst_horiz);
        say(rc == PGM_OK && e1_inf == 1 && e2_inf == 1 && horizontal > 30 && worst_horiz < 1e-12,
            "核点在无穷远、核线是水平线", detail);

        // 基线方向换成 2 倍（只取方向）：结果必须一模一样
        pgm_epipolar_input scaled = sc.epi;
        scaled.bx = 2.0 * sc.epi.bx;
        scaled.by = 2.0 * sc.epi.by;
        scaled.bz = 2.0 * sc.epi.bz;

        pgm_line2 l1{}, r1{}, l2{}, r2{};
        const int32_t rc1 = pgm_epipolar_lines(&sc.epi, sc.lx[0], sc.ly[0], &l1, &r1);
        const int32_t rc2 = pgm_epipolar_lines(&scaled, sc.lx[0], sc.ly[0], &l2, &r2);
        const double diff = std::fabs(l1.a - l2.a) + std::fabs(r1.c - r2.c);
        char detail2[128];
        std::snprintf(detail2, sizeof detail2, "系数差 %.2e", diff);
        say(rc1 == PGM_OK && rc2 == PGM_OK && diff < 1e-12, "基线只取方向、不受长度影响", detail2);
    }

    // ---------------------------------------------------------------- 6
    std::printf("== 6 退化与输入错误 ==\n");
    {
        pgm_line2 left{}, right{};
        double theta = 0.0;
        double dist = 0.0;

        say_rc(pgm_epipolar_lines(nullptr, 0.0, 0.0, &left, &right), PGM_ERR_NULL_ARGUMENT,
               "空输入被拒");

        pgm_epipolar_input bad = sc.epi;
        bad.focal_length_mm = 0.0;
        say_rc(pgm_epipolar_lines(&bad, 0.0, 0.0, &left, &right), PGM_ERR_INVALID_ARGUMENT,
               "主距为 0 被拒");

        bad = sc.epi;
        bad.bx = 0.0; bad.by = 0.0; bad.bz = 0.0;
        say_rc(pgm_epipolar_lines(&bad, 0.0, 0.0, &left, &right), PGM_ERR_INVALID_ARGUMENT,
               "零基线被拒");

        bad = sc.epi;
        bad.bx = std::nan("");
        say_rc(pgm_epipolar_lines(&bad, 0.0, 0.0, &left, &right), PGM_ERR_INVALID_ARGUMENT,
               "基线含 NaN 被拒");

        // 前方运动：基线沿光轴，像点落在主点附近，核面不确定
        pgm_epipolar_input forward = sc.epi;
        forward.bx = 0.0; forward.by = 0.0; forward.bz = -1.0;
        double rot_id[9];
        pgm_epipolar_rotation(0.0, 0.0, 0.0, rot_id);
        for (int i = 0; i < 9; ++i) forward.rotation[i] = rot_id[i];
        say_rc(pgm_epipolar_lines(&forward, 0.0, 0.0, &left, &right), PGM_ERR_DEGENERATE,
               "前方运动（点在主点）被判退化");

        say_rc(pgm_epipolar_row(&sc.epi, 2, 1.0, 1.0, &theta), PGM_ERR_INVALID_ARGUMENT,
               "side 只能 0/1");
        say_rc(pgm_epipolar_distance(&sc.epi, sc.lx[0], sc.ly[0], std::nan(""), 0.0, &dist),
               PGM_ERR_INVALID_ARGUMENT,
               "像点含 NaN 被拒");
        say_rc(pgm_epipolar_lines_at(&sc.epi, std::nan(""), &left, &right), PGM_ERR_INVALID_ARGUMENT,
               "核面转角含 NaN 被拒");
    }

    // ---------------------------------------------------------------- 7
    std::printf("== 7 核线重采样：沿核线采样是否取到正确位置 ==\n");
    {
        const int W = 512, H = 512;
        const double pix = 0.5;
        const double cx = 0.5 * (W - 1), cy = 0.5 * (H - 1);

        // 解析纹理：值只由像平面 mm 坐标决定，所以重采样后每个采样点都能对出真值。
        auto texture = [](double x, double y) {
            return 100.0 + 70.0 * std::sin(x / 9.0) * std::cos(y / 13.0)
                         + 40.0 * std::sin((x + y) / 21.0);
        };
        auto make_image = [&](std::vector<uint8_t>& img) {
            img.assign(static_cast<size_t>(W) * static_cast<size_t>(H), 0);
            for (int j = 0; j < H; ++j) {
                for (int i = 0; i < W; ++i) {
                    const double x = (static_cast<double>(i) - cx) * pix;
                    const double y = (cy - static_cast<double>(j)) * pix;
                    double v = texture(x, y);
                    v = std::max(0.0, std::min(255.0, v));
                    img[static_cast<size_t>(j) * static_cast<size_t>(W) + static_cast<size_t>(i)] =
                        static_cast<uint8_t>(std::lround(v));
                }
            }
        };

        std::vector<uint8_t> limg, rimg;
        make_image(limg);
        make_image(rimg);

        const int32_t rows = 64, cols = 96;
        const double step = 2.0;
        std::vector<uint8_t> lout(static_cast<size_t>(rows) * cols), rout(static_cast<size_t>(rows) * cols);
        std::vector<uint8_t> lmask(static_cast<size_t>(rows) * cols), rmask(static_cast<size_t>(rows) * cols);
        std::vector<pgm_line2> llines(static_cast<size_t>(rows)), rlines(static_cast<size_t>(rows));

        pgm_epipolar_rectify_input ri{};
        ri.epipolar = sc.epi;
        ri.rows = rows;
        ri.cols = cols;
        ri.step_mm = step;
        ri.left_pixels = limg.data();
        ri.left_width = W;
        ri.left_height = H;
        ri.left_step = W;
        ri.right_pixels = rimg.data();
        ri.right_width = W;
        ri.right_height = H;
        ri.right_step = W;
        ri.pixel_size_mm = pix;
        ri.cx_left_px = cx;
        ri.cy_left_px = cy;
        ri.cx_right_px = cx;
        ri.cy_right_px = cy;

        pgm_epipolar_rectify_output ro{};
        ro.left_image = lout.data();
        ro.left_step = cols;
        ro.right_image = rout.data();
        ro.right_step = cols;
        ro.left_mask = lmask.data();
        ro.right_mask = rmask.data();
        ro.left_lines = llines.data();
        ro.right_lines = rlines.data();

        const int32_t rc = pgm_epipolar_rectify(&ri, &ro);

        double worst = 0.0;
        int checked = 0;
        int masked = 0;
        if (rc == PGM_OK) {
            const double half = 0.5 * (static_cast<double>(cols) - 1.0);
            for (int32_t row = 0; row < rows; ++row) {
                const pgm_line2& line = llines[static_cast<size_t>(row)];
                const double fx = -line.c * line.a;
                const double fy = -line.c * line.b;
                const double dx = -line.b;
                const double dy = line.a;
                for (int32_t k = 0; k < cols; ++k) {
                    if (lmask[static_cast<size_t>(row) * cols + k] == 0) { ++masked; continue; }
                    const double t = (static_cast<double>(k) - half) * step;
                    const double x = fx + t * dx;
                    const double y = fy + t * dy;
                    double want = texture(x, y);
                    want = std::max(0.0, std::min(255.0, want));
                    const double got = static_cast<double>(lout[static_cast<size_t>(row) * cols + k]);
                    const double err = std::fabs(got - want);
                    if (err > worst) worst = err;
                    ++checked;
                }
            }
        }

        char detail[200];
        std::snprintf(detail, sizeof detail, "%d 个采样点对出解析值，最大差 %.1f 灰度级（越界 %d 个）",
                      checked, worst, masked);
        say(rc == PGM_OK && checked > 1000 && worst <= 4.0, "沿核线采样取到正确位置", detail);
    }

    // ---------------------------------------------------------------- 8
    std::printf("== 8 重采样的退化与输入错误 ==\n");
    {
        std::vector<uint8_t> img(64 * 64, 128), out(64 * 64, 0);
        pgm_epipolar_rectify_input ri{};
        ri.epipolar = sc.epi;
        ri.rows = 8; ri.cols = 8; ri.step_mm = 1.0;
        ri.left_pixels = img.data(); ri.left_width = 64; ri.left_height = 64; ri.left_step = 64;
        ri.right_pixels = img.data(); ri.right_width = 64; ri.right_height = 64; ri.right_step = 64;
        ri.pixel_size_mm = 0.5;
        ri.cx_left_px = 31.5; ri.cy_left_px = 31.5; ri.cx_right_px = 31.5; ri.cy_right_px = 31.5;

        pgm_epipolar_rectify_output ro{};
        ro.left_image = out.data(); ro.left_step = 8;
        ro.right_image = out.data(); ro.right_step = 8;

        pgm_epipolar_rectify_input bad = ri;
        bad.rows = 0;
        say_rc(pgm_epipolar_rectify(&bad, &ro), PGM_ERR_INVALID_ARGUMENT, "行数为 0 被拒");

        bad = ri;
        bad.step_mm = 0.0;
        say_rc(pgm_epipolar_rectify(&bad, &ro), PGM_ERR_INVALID_ARGUMENT, "采样间隔为 0 被拒");

        pgm_epipolar_rectify_output bad_out = ro;
        bad_out.left_image = nullptr;
        say_rc(pgm_epipolar_rectify(&ri, &bad_out), PGM_ERR_NULL_ARGUMENT, "输出缓冲为空被拒");
    }

    std::printf("\n");
    if (g_failures == 0) {
        std::printf("EPOLAR PASS\n");
        return 0;
    }
    std::printf("EPOLAR FAIL（%d 项）\n", g_failures);
    return 1;
}
