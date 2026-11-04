// 核线几何：由相对定向的 (b, R) 求两片的同名核线、核点与核面转角。
//
// 约定（与 relative_orientation.cpp 的出口一致，混用会整体差一个转置）：
//   · b 是基线单位方向，表达在辅助系 = 左片像空间系（X 右、Y 上、Z 指向摄影者）；
//   · R 是「辅助系 -> 右片像空间」，行主序，就是 pgm_relative_orientation 的 rotation；
//   · 像点是 (x, y, -f)，f 为正。
//
// 推导只要一行：共面条件 (b × d1)·(R p2) = 0，把括号展开成
//
//     p2 · [ R (b × d1) ] = 0,
//
// 于是法向量 m = R n（n = b × d1）给出右核线，而 n 自己就是左核线——同一平面切两片。
// 核线在像平面上的显式方程是 m_u x + m_v y = f m_w，永远是**一次**方程，不是近似。

#include "photogrammetry.h"

#include "geometry.hpp"
#include "internal.hpp"

#include <algorithm>
#include <cmath>

namespace {

using pgm::Mat3;

constexpr double kTiny = 1e-12;
constexpr double kPi   = 3.14159265358979323846;

void cross3(const double a[3], const double b[3], double out[3])
{
    out[0] = a[1] * b[2] - a[2] * b[1];
    out[1] = a[2] * b[0] - a[0] * b[2];
    out[2] = a[0] * b[1] - a[1] * b[0];
}

// 平面法向量 n（辅助系）切像平面 z = -f 得到的直线，按 a^2 + b^2 = 1 归一化。
// 归一化的好处：点到线距离直接就是 |a x + b y + c|，各次调用的系数也能互相比。
// 返回 false 表示平面与像平面平行（交线在无穷远），或者整条线退化。
bool line_of_plane(const double n[3], double f, pgm_line2* line)
{
    const double norm = std::hypot(n[0], n[1]);
    if (!(norm > kTiny)) return false;

    line->a = n[0] / norm;
    line->b = n[1] / norm;
    line->c = -f * n[2] / norm;
    return true;
}

// 与 b 正交的一组基：u1 由某根坐标轴投影得到，u2 = b × u1。
// 挑轴时避开与 b 最平行的那根，保证 u1 不会因为投影长度趋零而丢精度。
void ortho_basis(const double b[3], double u1[3], double u2[3])
{
    double ref[3] = { 1.0, 0.0, 0.0 };
    if (std::fabs(b[0]) > 0.9) {
        ref[0] = 0.0;
        ref[1] = 1.0;
    }

    const double proj = pgm::dot3(ref, b);
    u1[0] = ref[0] - proj * b[0];
    u1[1] = ref[1] - proj * b[1];
    u1[2] = ref[2] - proj * b[2];

    const double norm = std::sqrt(pgm::dot3(u1, u1));
    u1[0] /= norm;
    u1[1] /= norm;
    u1[2] /= norm;

    cross3(b, u1, u2);
}

// 把核面转角折到 [0, pi)：n 与 -n 是同一个平面，转角必须唯一。
double fold_theta(double t)
{
    while (t < 0.0) t += kPi;
    while (t >= kPi) t -= kPi;
    return t;
}

// 双线性采样：i 是列、j 是行，可以是小数。落在像幅外返回 false（不钳边，钳边会造出假纹理）。
bool sample_bilinear(const uint8_t* px, int32_t w, int32_t h, int32_t step,
                     double i, double j, uint8_t* out)
{
    if (!(i >= 0.0) || !(j >= 0.0) ||
        i > static_cast<double>(w - 1) || j > static_cast<double>(h - 1)) {
        return false;
    }

    const int32_t i0 = static_cast<int32_t>(std::floor(i));
    const int32_t j0 = static_cast<int32_t>(std::floor(j));
    const int32_t i1 = std::min(i0 + 1, w - 1);
    const int32_t j1 = std::min(j0 + 1, h - 1);
    const double fx = i - static_cast<double>(i0);
    const double fy = j - static_cast<double>(j0);

    auto at = [px, step](int32_t ii, int32_t jj) {
        return static_cast<double>(px[static_cast<size_t>(jj) * static_cast<size_t>(step) +
                                      static_cast<size_t>(ii)]);
    };

    const double top = at(i0, j0) * (1.0 - fx) + at(i1, j0) * fx;
    const double bot = at(i0, j1) * (1.0 - fx) + at(i1, j1) * fx;
    *out = static_cast<uint8_t>(std::lround(top * (1.0 - fy) + bot * fy));
    return true;
}

double theta_of_normal(const double n[3], const double b[3])
{
    double u1[3], u2[3];
    ortho_basis(b, u1, u2);
    return fold_theta(std::atan2(pgm::dot3(n, u2), pgm::dot3(n, u1)));
}

struct Context
{
    Mat3   r;      // 辅助系 -> 右片像空间
    double b[3];
    double f;
};

int32_t prepare(const pgm_epipolar_input* in, Context* ctx)
{
    if (in == nullptr) {
        pgm::set_error("输入指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (!std::isfinite(in->focal_length_mm) || !(in->focal_length_mm > 0.0)) {
        pgm::set_error("主距必须为正的有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    const double bv[3] = { in->bx, in->by, in->bz };
    if (!pgm::finite_all(bv, 3) || !pgm::finite_all(in->rotation, 9)) {
        pgm::set_error("核线输入含非有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    ctx->b[0] = in->bx;
    ctx->b[1] = in->by;
    ctx->b[2] = in->bz;
    const double nb = std::sqrt(pgm::dot3(ctx->b, ctx->b));
    if (!(nb > kTiny)) {
        pgm::set_error("基线方向为零：核线无从谈起");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    ctx->b[0] /= nb;
    ctx->b[1] /= nb;
    ctx->b[2] /= nb;

    for (int i = 0; i < 9; ++i) ctx->r.m[i] = in->rotation[i];
    ctx->f = in->focal_length_mm;
    return PGM_OK;
}

// 左点 -> 核面法向量 n = b × d1。
bool normal_from_left_point(const Context& ctx, double x, double y, double n[3])
{
    const double d1[3] = { x, y, -ctx.f };
    cross3(ctx.b, d1, n);
    return std::hypot(n[0], n[1]) > kTiny || std::fabs(n[2]) > kTiny;
}

}  // namespace

extern "C" {

int32_t pgm_epipolar_rotation(double phi_rad, double omega_rad, double kappa_rad,
                              double* out_rotation9)
{
    if (out_rotation9 == nullptr) {
        pgm::set_error("输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    const double angles[3] = { phi_rad, omega_rad, kappa_rad };
    if (!pgm::finite_all(angles, 3)) {
        pgm::set_error("角度含非有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    const pgm::Rotation rs = pgm::make_rotation(phi_rad, omega_rad, kappa_rad);
    for (int i = 0; i < 9; ++i) out_rotation9[i] = rs.total.m[i];
    return PGM_OK;
}

int32_t pgm_epipolar_lines(const pgm_epipolar_input* in,
                           double x1_mm, double y1_mm,
                           pgm_line2* out_left, pgm_line2* out_right)
{
    Context ctx;
    const int32_t rc = prepare(in, &ctx);
    if (rc != PGM_OK) return rc;
    if (out_left == nullptr && out_right == nullptr) {
        pgm::set_error("左右核线的输出指针不能都为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (!std::isfinite(x1_mm) || !std::isfinite(y1_mm)) {
        pgm::set_error("像点坐标含非有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    double n[3];
    if (!normal_from_left_point(ctx, x1_mm, y1_mm, n)) {
        pgm::set_error("像点与基线重合：核面不确定");
        return PGM_ERR_DEGENERATE;
    }

    pgm_line2 left{}, right{};
    if (!line_of_plane(n, ctx.f, &left)) {
        pgm::set_error("核面与像平面平行：左核线在无穷远");
        return PGM_ERR_DEGENERATE;
    }

    double m[3];
    pgm::mat_vec(ctx.r, n, m);
    if (!line_of_plane(m, ctx.f, &right)) {
        pgm::set_error("核面与右像平面平行：右核线在无穷远");
        return PGM_ERR_DEGENERATE;
    }

    if (out_left) *out_left = left;
    if (out_right) *out_right = right;
    return PGM_OK;
}

int32_t pgm_epipolar_epipoles(const pgm_epipolar_input* in,
                              double*   out_e1_x, double* out_e1_y, int32_t* out_e1_at_infinity,
                              double*   out_e2_x, double* out_e2_y, int32_t* out_e2_at_infinity)
{
    Context ctx;
    const int32_t rc = prepare(in, &ctx);
    if (rc != PGM_OK) return rc;
    if (out_e1_x == nullptr || out_e1_y == nullptr || out_e2_x == nullptr || out_e2_y == nullptr) {
        pgm::set_error("核点坐标的输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    // 左核点：基线方向本身就是辅助系里的向量，直接与 z = -f 求交。
    int32_t e1_inf = 0;
    double e1x = 0.0, e1y = 0.0;
    if (std::fabs(ctx.b[2]) > kTiny) {
        e1x = -ctx.f * ctx.b[0] / ctx.b[2];
        e1y = -ctx.f * ctx.b[1] / ctx.b[2];
    } else {
        e1_inf = 1;
    }

    // 右核点：把基线转到右片像空间，v = R b。
    // （注意方向：出口的 rotation 是「辅助系 -> 像」，所以是乘 R，不是乘 R^T。
    //   文档里写成 R^T b 是因为那份推导用的是「像 -> 辅助系」的 R，两者互为转置。）
    double v[3];
    pgm::mat_vec(ctx.r, ctx.b, v);
    int32_t e2_inf = 0;
    double e2x = 0.0, e2y = 0.0;
    if (std::fabs(v[2]) > kTiny) {
        e2x = -ctx.f * v[0] / v[2];
        e2y = -ctx.f * v[1] / v[2];
    } else {
        e2_inf = 1;
    }

    *out_e1_x = e1x;
    *out_e1_y = e1y;
    *out_e2_x = e2x;
    *out_e2_y = e2y;
    if (out_e1_at_infinity) *out_e1_at_infinity = e1_inf;
    if (out_e2_at_infinity) *out_e2_at_infinity = e2_inf;
    return PGM_OK;
}

int32_t pgm_epipolar_distance(const pgm_epipolar_input* in,
                              double x1_mm, double y1_mm,
                              double x2_mm, double y2_mm,
                              double* out_distance_mm)
{
    if (out_distance_mm == nullptr) {
        pgm::set_error("输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    pgm_line2 right{};
    const int32_t rc = pgm_epipolar_lines(in, x1_mm, y1_mm, nullptr, &right);
    if (rc != PGM_OK) return rc;

    if (!std::isfinite(x2_mm) || !std::isfinite(y2_mm)) {
        pgm::set_error("像点坐标含非有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    // 系数已归一化，a x + b y + c 就是有符号垂直距离，符号由法向方向决定。
    *out_distance_mm = right.a * x2_mm + right.b * y2_mm + right.c;
    return PGM_OK;
}

int32_t pgm_epipolar_row(const pgm_epipolar_input* in, int32_t side,
                         double x_mm, double y_mm, double* out_theta_rad)
{
    Context ctx;
    const int32_t rc = prepare(in, &ctx);
    if (rc != PGM_OK) return rc;
    if (out_theta_rad == nullptr) {
        pgm::set_error("输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (side != 0 && side != 1) {
        pgm::set_error("side 只能是 0（左片）或 1（右片）");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    if (!std::isfinite(x_mm) || !std::isfinite(y_mm)) {
        pgm::set_error("像点坐标含非有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    double n[3];
    if (side == 0) {
        if (!normal_from_left_point(ctx, x_mm, y_mm, n)) {
            pgm::set_error("像点与基线重合：核面不确定");
            return PGM_ERR_DEGENERATE;
        }
    } else {
        // 右片上的点先转回辅助系：d2 = R^T p2。
        const double p2[3] = { x_mm, y_mm, -ctx.f };
        double d2[3];
        pgm::mat_vec_t(ctx.r, p2, d2);
        cross3(ctx.b, d2, n);
        if (!(std::hypot(n[0], n[1]) > kTiny) && !(std::fabs(n[2]) > kTiny)) {
            pgm::set_error("像点与基线重合：核面不确定");
            return PGM_ERR_DEGENERATE;
        }
    }

    *out_theta_rad = theta_of_normal(n, ctx.b);
    return PGM_OK;
}

int32_t pgm_epipolar_lines_at(const pgm_epipolar_input* in, double theta_rad,
                              pgm_line2* out_left, pgm_line2* out_right)
{
    Context ctx;
    const int32_t rc = prepare(in, &ctx);
    if (rc != PGM_OK) return rc;
    if (out_left == nullptr && out_right == nullptr) {
        pgm::set_error("左右核线的输出指针不能都为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (!std::isfinite(theta_rad)) {
        pgm::set_error("核面转角含非有限值");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    double u1[3], u2[3];
    ortho_basis(ctx.b, u1, u2);

    const double c = std::cos(theta_rad);
    const double s = std::sin(theta_rad);
    double n[3];
    for (int i = 0; i < 3; ++i) n[i] = c * u1[i] + s * u2[i];

    pgm_line2 left{}, right{};
    if (!line_of_plane(n, ctx.f, &left)) {
        pgm::set_error("核面与像平面平行：左核线在无穷远");
        return PGM_ERR_DEGENERATE;
    }

    double m[3];
    pgm::mat_vec(ctx.r, n, m);
    if (!line_of_plane(m, ctx.f, &right)) {
        pgm::set_error("核面与右像平面平行：右核线在无穷远");
        return PGM_ERR_DEGENERATE;
    }

    if (out_left) *out_left = left;
    if (out_right) *out_right = right;
    return PGM_OK;
}

int32_t pgm_epipolar_rectify(const pgm_epipolar_rectify_input*  in,
                             const pgm_epipolar_rectify_output* out)
{
    if (in == nullptr || out == nullptr) {
        pgm::set_error("输入或输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (out->left_image == nullptr || out->right_image == nullptr) {
        pgm::set_error("核线影像的输出缓冲为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (in->rows <= 0 || in->cols <= 0 || !(in->step_mm > 0.0) || !(in->pixel_size_mm > 0.0) ||
        !std::isfinite(in->step_mm) || !std::isfinite(in->pixel_size_mm)) {
        pgm::set_error("行数/列数/采样间隔/像元尺寸必须为正");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    if (in->left_pixels == nullptr || in->right_pixels == nullptr ||
        in->left_width <= 0 || in->left_height <= 0 || in->left_step < in->left_width ||
        in->right_width <= 0 || in->right_height <= 0 || in->right_step < in->right_width) {
        pgm::set_error("源影像缓冲为空或尺寸不合法");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    Context ctx;
    const int32_t rc = prepare(&in->epipolar, &ctx);
    if (rc != PGM_OK) return rc;

    double u1[3], u2[3];
    ortho_basis(ctx.b, u1, u2);

    // 直线 a x + b y + c = 0 的垂足与方向向量（(a,b) 已归一化，垂足就是 -c*(a,b)）。
    auto foot_and_dir = [](const pgm_line2& l, double* fx, double* fy, double* dx, double* dy) {
        *fx = -l.c * l.a;
        *fy = -l.c * l.b;
        *dx = -l.b;
        *dy = l.a;
    };

    for (int32_t row = 0; row < in->rows; ++row) {
        const double theta = kPi * (static_cast<double>(row) + 0.5) / static_cast<double>(in->rows);
        const double c = std::cos(theta);
        const double s = std::sin(theta);

        double n[3];
        for (int i = 0; i < 3; ++i) n[i] = c * u1[i] + s * u2[i];

        double m[3];
        pgm::mat_vec(ctx.r, n, m);

        pgm_line2 left_line{}, right_line{};
        if (!line_of_plane(n, ctx.f, &left_line) || !line_of_plane(m, ctx.f, &right_line)) {
            pgm::set_error("核面与像平面平行：这一行没有核线");
            return PGM_ERR_DEGENERATE;
        }

        double lfx, lfy, ldx, ldy, rfx, rfy, rdx, rdy;
        foot_and_dir(left_line, &lfx, &lfy, &ldx, &ldy);
        foot_and_dir(right_line, &rfx, &rfy, &rdx, &rdy);

        // 两幅图的列方向必须同向，否则右图整片左右镜像、视差符号反过来。
        if (ldx * rdx + ldy * rdy < 0.0) {
            rdx = -rdx;
            rdy = -rdy;
        }

        if (out->left_lines) out->left_lines[row] = left_line;
        if (out->right_lines) out->right_lines[row] = right_line;

        uint8_t* lrow = out->left_image + static_cast<size_t>(row) * static_cast<size_t>(out->left_step);
        uint8_t* rrow = out->right_image + static_cast<size_t>(row) * static_cast<size_t>(out->right_step);
        uint8_t* lmask = out->left_mask
            ? out->left_mask + static_cast<size_t>(row) * static_cast<size_t>(in->cols) : nullptr;
        uint8_t* rmask = out->right_mask
            ? out->right_mask + static_cast<size_t>(row) * static_cast<size_t>(in->cols) : nullptr;

        const double half = 0.5 * (static_cast<double>(in->cols) - 1.0);

        for (int32_t k = 0; k < in->cols; ++k) {
            const double t = (static_cast<double>(k) - half) * in->step_mm;

            const double lx_mm = lfx + t * ldx;
            const double ly_mm = lfy + t * ldy;
            const double li_px = in->cx_left_px + lx_mm / in->pixel_size_mm;
            const double lj_px = in->cy_left_px - ly_mm / in->pixel_size_mm;

            uint8_t lv = 0;
            const bool lok = sample_bilinear(in->left_pixels, in->left_width, in->left_height,
                                             in->left_step, li_px, lj_px, &lv);
            lrow[k] = lok ? lv : 0;
            if (lmask) lmask[k] = lok ? 1 : 0;

            const double rx_mm = rfx + t * rdx;
            const double ry_mm = rfy + t * rdy;
            const double ri_px = in->cx_right_px + rx_mm / in->pixel_size_mm;
            const double rj_px = in->cy_right_px - ry_mm / in->pixel_size_mm;

            uint8_t rv = 0;
            const bool rok = sample_bilinear(in->right_pixels, in->right_width, in->right_height,
                                             in->right_step, ri_px, rj_px, &rv);
            rrow[k] = rok ? rv : 0;
            if (rmask) rmask[k] = rok ? 1 : 0;
        }
    }

    return PGM_OK;
}

}  // extern "C"
