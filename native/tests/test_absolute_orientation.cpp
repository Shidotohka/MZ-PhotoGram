// 绝对定向（七参数相似变换）的验收程序。
//
// 全部合成数据，真值已知：给一组模型点，用已知的 (λ, R, t) 正变换出地面坐标，
// 再让 pgm_absolute_orientation 反解，看能不能把三个量原样找回。
//
//   1 无噪声往返   —— λ、R、t 是否恢复到 1e-12
//   2 角度复算     —— 返回的三个角用 make_rotation 拼回去是否等于 R
//   3 含噪声       —— σ0 是否与所加噪声同量级，逐点残差是否写出来
//   4 留点检核     —— 只用前 n-2 个点解，去预测后 2 个点
//   5 数据镜像     —— 地面坐标左手法则，是否报 mirror_suspected 且残差爆炸
//   6 控制点共面   —— Z 全相等是否仍能解（这是野外常态）
//   7 控制点共线   —— 是否按 PGM_ERR_DEGENERATE 拒绝
//   8 输入错误     —— 点数不足、指针为空、NaN
//
// 用法： pgm_absolute_orientation_test
// 返回： 0 全部通过，1 有失败项

#include "photogrammetry.h"

#include <cmath>
#include <cstdio>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;

int g_failures = 0;

void say(bool ok, const char* what, const char* detail)
{
    std::printf("  [%s] %-34s %s\n", ok ? "PASS" : "FAIL", what, detail);
    if (!ok) ++g_failures;
}

void say_rc(int32_t rc, int32_t want, const char* what)
{
    char detail[96];
    std::snprintf(detail, sizeof detail, "rc = %d（期望 %d）", rc, want);
    say(rc == want, what, detail);
}

// ---------------------------------------------------------------- 小工具

struct Vec3
{
    double v[3];
};

Vec3 rotation_apply(const double r[9], const Vec3& m)
{
    Vec3 out;
    for (int i = 0; i < 3; ++i) {
        out.v[i] = r[i * 3 + 0] * m.v[0] + r[i * 3 + 1] * m.v[1] + r[i * 3 + 2] * m.v[2];
    }
    return out;
}

// 与 geometry.hpp 的 make_rotation 同一套：R = Rκ·Rω·Rφ。
// 测试里自己拼一份，是为了不把库内头文件拉进来——顺便独立验证一次约定。
void make_rotation(double phi, double omega, double kappa, double r[9])
{
    const double cp = std::cos(phi), sp = std::sin(phi);
    const double cw = std::cos(omega), sw = std::sin(omega);
    const double ck = std::cos(kappa), sk = std::sin(kappa);

    const double rp[9] = { cp, 0.0, sp, 0.0, 1.0, 0.0, -sp, 0.0, cp };
    const double rw[9] = { 1.0, 0.0, 0.0, 0.0, cw, sw, 0.0, -sw, cw };
    const double rk[9] = { ck, sk, 0.0, -sk, ck, 0.0, 0.0, 0.0, 1.0 };

    double tmp[9];
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) {
            tmp[i * 3 + j] = rw[i * 3 + 0] * rp[0 * 3 + j] +
                             rw[i * 3 + 1] * rp[1 * 3 + j] +
                             rw[i * 3 + 2] * rp[2 * 3 + j];
        }
    }
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) {
            r[i * 3 + j] = rk[i * 3 + 0] * tmp[0 * 3 + j] +
                           rk[i * 3 + 1] * tmp[1 * 3 + j] +
                           rk[i * 3 + 2] * tmp[2 * 3 + j];
        }
    }
}

double max_abs_diff(const double* a, const double* b, int n)
{
    double m = 0.0;
    for (int i = 0; i < n; ++i) {
        const double d = std::fabs(a[i] - b[i]);
        if (d > m) m = d;
    }
    return m;
}

// 一台固定的「真值」：λ、R、t
constexpr double kScale = 1.7;
constexpr double kPhi = 2.1 * kPi / 180.0;
constexpr double kOmega = -1.3 * kPi / 180.0;
constexpr double kKappa = 37.4 * kPi / 180.0;
const double kT[3] = { 1000.0, 2000.0, 300.0 };

// 六个控制点的模型坐标：不共线、不共面，且离重心有距离
const Vec3 kModel[6] = {
    { { 0.0, 0.0, 0.0 } },
    { { 1000.0, 0.0, 0.0 } },
    { { 0.0, 1000.0, 0.0 } },
    { { 0.0, 0.0, 1000.0 } },
    { { 1000.0, 1000.0, 0.0 } },
    { { 500.0, 300.0, 1200.0 } },
};

}  // namespace

int main()
{
    double r_true[9];
    make_rotation(kPhi, kOmega, kKappa, r_true);

    // 由模型点算地面坐标（真值正变换）
    std::vector<double> mx, my, mz, gx, gy, gz;
    for (int i = 0; i < 6; ++i) {
        const Vec3 g = rotation_apply(r_true, kModel[i]);
        mx.push_back(kModel[i].v[0]);
        my.push_back(kModel[i].v[1]);
        mz.push_back(kModel[i].v[2]);
        gx.push_back(kScale * g.v[0] + kT[0]);
        gy.push_back(kScale * g.v[1] + kT[1]);
        gz.push_back(kScale * g.v[2] + kT[2]);
    }

    std::vector<double> residual_xyz(static_cast<size_t>(6) * 3, 0.0);
    std::vector<double> residual_length(6, 0.0);

    pgm_absolute_orientation_input in{};
    in.count    = 6;
    in.model_x  = mx.data();
    in.model_y  = my.data();
    in.model_z  = mz.data();
    in.ground_x = gx.data();
    in.ground_y = gy.data();
    in.ground_z = gz.data();

    pgm_absolute_orientation_output out{};
    out.residual_xyz    = residual_xyz.data();
    out.residual_length = residual_length.data();

    std::printf("绝对定向验收（七参数相似变换）\n");
    std::printf("==============================================================\n\n");
    std::printf("1 无噪声往返\n");

    const int32_t rc = pgm_absolute_orientation(&in, &out);
    say_rc(rc, PGM_OK, "六点闭合解");

    char detail[192];
    std::snprintf(detail, sizeof detail, "λ = %.15f（真值 %.1f）", out.scale, kScale);
    say(std::fabs(out.scale - kScale) < 1e-12, "尺度恢复", detail);

    const double dr = max_abs_diff(out.rotation, r_true, 9);
    std::snprintf(detail, sizeof detail, "max|ΔR| = %.2e", dr);
    say(dr < 1e-12, "旋转恢复", detail);

    const double dt = max_abs_diff(out.translation, kT, 3);
    std::snprintf(detail, sizeof detail, "max|Δt| = %.2e m", dt);
    say(dt < 1e-9, "平移恢复", detail);

    std::snprintf(detail, sizeof detail, "rms = %.3e，σ0 = %.3e", out.rms, out.sigma0);
    say(out.rms < 1e-10, "不符值为零", detail);
    say(out.used_count == 6 && out.redundancy == 3 * 6 - 7, "点数与多余观测",
        (std::snprintf(detail, sizeof detail, "n = %d，r = %d", out.used_count, out.redundancy), detail));

    std::printf("\n2 角度复算\n");
    double rebuilt[9];
    make_rotation(out.phi_rad, out.omega_rad, out.kappa_rad, rebuilt);
    const double dang = max_abs_diff(rebuilt, out.rotation, 9);
    std::snprintf(detail, sizeof detail, "φ=%.6f° ω=%.6f° κ=%.6f°，max|ΔR|=%.1e",
                  out.phi_deg, out.omega_deg, out.kappa_deg, dang);
    say(dang < 1e-12, "三个角拼回 R", detail);

    std::printf("\n3 含噪声\n");
    {
        // 每坐标加 5 mm 的确定噪声（不用随机数，结果可复现）
        std::vector<double> nx(gx), ny(gy), nz(gz);
        for (int i = 0; i < 6; ++i) {
            nx[i] += 5.0 * std::sin(1.7 * i + 0.3);
            ny[i] += 5.0 * std::cos(2.3 * i + 1.1);
            nz[i] += 5.0 * std::sin(3.1 * i + 2.0);
        }
        pgm_absolute_orientation_input noisy = in;
        noisy.ground_x = nx.data();
        noisy.ground_y = ny.data();
        noisy.ground_z = nz.data();

        pgm_absolute_orientation_output out2{};
        out2.residual_xyz    = residual_xyz.data();
        out2.residual_length = residual_length.data();
        say_rc(pgm_absolute_orientation(&noisy, &out2), PGM_OK, "含噪声仍收敛");

        std::snprintf(detail, sizeof detail, "σ0 = %.3f m，rms = %.3f m", out2.sigma0, out2.rms);
        say(out2.sigma0 > 2.0 && out2.sigma0 < 12.0, "σ0 与噪声同量级", detail);

        std::snprintf(detail, sizeof detail, "max|v| = %.3f m", out2.max_abs_residual);
        say(out2.max_abs_residual < 15.0, "最大不符值合理", detail);

        // 逐点缓冲区是否真被写出来（相对定向踩过 memset 把它们清掉的坑）
        say(residual_length[0] > 0.0 || residual_length[5] > 0.0, "逐点残差已写出",
            (std::snprintf(detail, sizeof detail, "v[0]=%.3f v[5]=%.3f",
                           residual_length[0], residual_length[5]), detail));
    }

    std::printf("\n4 留点检核（前 4 点解，预测后 2 点）\n");
    {
        pgm_absolute_orientation_input partial = in;
        partial.count = 4;
        pgm_absolute_orientation_output out3{};
        say_rc(pgm_absolute_orientation(&partial, &out3), PGM_OK, "四点解（多余观测 5）");

        double worst = 0.0;
        for (int i = 4; i < 6; ++i) {
            const Vec3 g = rotation_apply(out3.rotation, kModel[i]);
            const double px = out3.scale * g.v[0] + out3.translation[0];
            const double py = out3.scale * g.v[1] + out3.translation[1];
            const double pz = out3.scale * g.v[2] + out3.translation[2];
            const double dx = px - gx[i], dy = py - gy[i], dz = pz - gz[i];
            const double d = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (d > worst) worst = d;
        }
        std::snprintf(detail, sizeof detail, "检查点最大偏差 %.2e m", worst);
        say(worst < 1e-6, "被留出的两个点预测正确", detail);
    }

    std::printf("\n5 数据镜像（地面坐标 X/Y 互换）\n");
    {
        std::vector<double> sx(gx), sy(gy);
        for (int i = 0; i < 6; ++i) { sx[i] = gy[i]; sy[i] = gx[i]; }
        pgm_absolute_orientation_input mirrored = in;
        mirrored.ground_x = sx.data();
        mirrored.ground_y = sy.data();

        pgm_absolute_orientation_output out4{};
        say_rc(pgm_absolute_orientation(&mirrored, &out4), PGM_OK, "镜像数据仍给出最优真旋转");
        std::snprintf(detail, sizeof detail, "mirror_suspected = %d，rms = %.1f m",
                      out4.mirror_suspected, out4.rms);
        say(out4.mirror_suspected == 1, "报出镜像嫌疑", detail);
        say(out4.rms > 100.0, "残差爆炸（贴合不上）",
            (std::snprintf(detail, sizeof detail, "rms = %.1f m", out4.rms), detail));
    }

    std::printf("\n6 控制点共面（Z 全为 0，野外常态）\n");
    {
        std::vector<double> cmz(6), cgz(6), cmx(6), cmy(6), cgx(6), cgy(6);
        for (int i = 0; i < 6; ++i) {
            cmx[i] = kModel[i].v[0];
            cmy[i] = kModel[i].v[1];
            cmz[i] = 0.0;
            const Vec3 g = rotation_apply(r_true, Vec3{ { cmx[i], cmy[i], 0.0 } });
            cgx[i] = kScale * g.v[0] + kT[0];
            cgy[i] = kScale * g.v[1] + kT[1];
            cgz[i] = kScale * g.v[2] + kT[2];   // 地面是一个倾斜平面
        }
        pgm_absolute_orientation_input planar = in;
        planar.model_x = cmx.data(); planar.model_y = cmy.data(); planar.model_z = cmz.data();
        planar.ground_x = cgx.data(); planar.ground_y = cgy.data(); planar.ground_z = cgz.data();

        pgm_absolute_orientation_output out5{};
        say_rc(pgm_absolute_orientation(&planar, &out5), PGM_OK, "共面控制点可解");
        const double d5 = max_abs_diff(out5.rotation, r_true, 9);
        std::snprintf(detail, sizeof detail, "σ3 = %.1e，max|ΔR| = %.1e",
                      out5.singular[2], d5);
        say(d5 < 1e-9, "共面时旋转仍正确", detail);
    }

    std::printf("\n7 控制点共线\n");
    {
        // 六个点全落在一条直线上
        std::vector<double> lx, ly, lz, tgx, tgy, tgz;
        for (int i = 0; i < 6; ++i) {
            const double t = i * 100.0;
            lx.push_back(t); ly.push_back(2.0 * t); lz.push_back(3.0 * t);
            const Vec3 g = rotation_apply(r_true, Vec3{ { t, 2.0 * t, 3.0 * t } });
            tgx.push_back(kScale * g.v[0] + kT[0]);
            tgy.push_back(kScale * g.v[1] + kT[1]);
            tgz.push_back(kScale * g.v[2] + kT[2]);
        }
        pgm_absolute_orientation_input collinear = in;
        collinear.model_x = lx.data(); collinear.model_y = ly.data(); collinear.model_z = lz.data();
        collinear.ground_x = tgx.data(); collinear.ground_y = tgy.data(); collinear.ground_z = tgz.data();

        pgm_absolute_orientation_output out6{};
        say_rc(pgm_absolute_orientation(&collinear, &out6), PGM_ERR_DEGENERATE, "共线被拒");
        const char* msg = pgm_last_error();
        say(msg != nullptr && msg[0] != '\0', "给了可读的失败原因", msg != nullptr ? msg : "(null)");
    }

    std::printf("\n8 输入错误\n");
    {
        pgm_absolute_orientation_output o{};
        say_rc(pgm_absolute_orientation(nullptr, &o), PGM_ERR_NULL_ARGUMENT, "输入指针为空");

        pgm_absolute_orientation_input few = in;
        few.count = 2;
        say_rc(pgm_absolute_orientation(&few, &o), PGM_ERR_TOO_FEW_POINTS, "只有 2 个控制点");

        std::vector<double> badx(gx);
        badx[2] = std::nan("");
        pgm_absolute_orientation_input nan_in = in;
        nan_in.ground_x = badx.data();
        say_rc(pgm_absolute_orientation(&nan_in, &o), PGM_ERR_INVALID_ARGUMENT, "坐标含 NaN");

        pgm_absolute_orientation_input nulls = in;
        nulls.ground_z = nullptr;
        say_rc(pgm_absolute_orientation(&nulls, &o), PGM_ERR_NULL_ARGUMENT, "地面坐标数组为空");
    }

    std::printf("\n%s\n", g_failures == 0 ? "ABSOLUTE-ORIENTATION PASS"
                                          : "ABSOLUTE-ORIENTATION FAIL");
    return g_failures == 0 ? 0 : 1;
}
