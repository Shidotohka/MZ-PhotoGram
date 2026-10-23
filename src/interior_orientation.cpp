// 六参数仿射内定向。算法见 Docs/算法笔记.md 第 5 章。

#include "photogrammetry.h"
#include "internal.hpp"

#include <cmath>
#include <cstring>
#include <new>

namespace {

constexpr double kPi = 3.14159265358979323846;

// 框标近似共线的判据：归一化相关系数。与影像尺寸无关。
constexpr double kCollinearRho = 0.9999;

double deg(double rad) { return rad * 180.0 / kPi; }

}  // namespace

extern "C" {

int32_t pgm_fiducial_orientation(const pgm_fiducial_input* in, pgm_fiducial_output* out)
{
    pgm::set_error("");

    if (in == nullptr || out == nullptr) {
        pgm::set_error("输入或输出指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }

    // 残差缓冲区由调用方预分配，memset 前先存下指针，否则会被清零
    double* const residual_x = out->residual_x_mm;
    double* const residual_y = out->residual_y_mm;

    std::memset(out, 0, sizeof(*out));
    out->residual_x_mm = residual_x;
    out->residual_y_mm = residual_y;

    const int n = in->count;
    if (n < 4) {
        pgm::set_error("内定向至少需要 4 个框标；3 个时多余观测为 0，残差恒为 0，无法检核");
        return PGM_ERR_TOO_FEW_POINTS;
    }
    if (in->fiducial_x_mm == nullptr || in->fiducial_y_mm == nullptr ||
        in->image_i == nullptr || in->image_j == nullptr) {
        pgm::set_error("框标坐标或像元坐标数组为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (in->image_width <= 0 || in->image_height <= 0) {
        pgm::set_error("影像宽高必须为正");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    for (int k = 0; k < n; ++k) {
        if (!std::isfinite(in->fiducial_x_mm[k]) || !std::isfinite(in->fiducial_y_mm[k]) ||
            !std::isfinite(in->image_i[k]) || !std::isfinite(in->image_j[k])) {
            pgm::set_error("框标输入中含非有限数值");
            return PGM_ERR_INVALID_ARGUMENT;
        }
    }

    try {
        // ---- 重心化：I、J 可达数千，不重心化 sum(I^2) ~ 1e7，法方程病态
        double i_mean = 0.0, j_mean = 0.0, x_mean = 0.0, y_mean = 0.0;
        for (int k = 0; k < n; ++k) {
            i_mean += in->image_i[k];
            j_mean += in->image_j[k];
            x_mean += in->fiducial_x_mm[k];
            y_mean += in->fiducial_y_mm[k];
        }
        i_mean /= n; j_mean /= n; x_mean /= n; y_mean /= n;

        double sxx = 0.0, syy = 0.0, sxy = 0.0;
        double sx_x = 0.0, sy_x = 0.0, sx_y = 0.0, sy_y = 0.0;
        for (int k = 0; k < n; ++k) {
            const double di = in->image_i[k] - i_mean;
            const double dj = in->image_j[k] - j_mean;
            const double dx = in->fiducial_x_mm[k] - x_mean;
            const double dy = in->fiducial_y_mm[k] - y_mean;

            sxx += di * di;
            syy += dj * dj;
            sxy += di * dj;

            sx_x += di * dx;
            sy_x += dj * dx;
            sx_y += di * dy;
            sy_y += dj * dy;
        }

        // ---- 几何构型检查：框标全在一排时无法确定两个方向的比例
        if (sxx <= 0.0 || syy <= 0.0) {
            pgm::set_error("框标在像幅某一方向上没有分布，无法确定像元尺寸");
            return PGM_ERR_DEGENERATE;
        }
        const double rho = sxy / std::sqrt(sxx * syy);
        if (std::fabs(rho) >= kCollinearRho) {
            pgm::set_error("框标近似共线，正规方程退化；请让取点覆盖像幅四角与四边");
            return PGM_ERR_DEGENERATE;
        }

        const double det = sxx * syy - sxy * sxy;
        if (!(det > 0.0)) {
            pgm::set_error("正规方程行列式非正，解算退化");
            return PGM_ERR_DEGENERATE;
        }

        // ---- 解两个 3 参数问题（重心化后 3x3 退化为 1 + 2x2）
        const double a1 = (syy * sx_x - sxy * sy_x) / det;
        const double a2 = (sxx * sy_x - sxy * sx_x) / det;
        const double b1 = (syy * sx_y - sxy * sy_y) / det;
        const double b2 = (sxx * sy_y - sxy * sx_y) / det;

        const double a0 = x_mean - a1 * i_mean - a2 * j_mean;
        const double b0 = y_mean - b1 * i_mean - b2 * j_mean;

        // ---- 残差 v = 计算值 - 观测值
        double sum_v2 = 0.0;
        double max_abs = 0.0;
        for (int k = 0; k < n; ++k) {
            const double fx = a0 + a1 * in->image_i[k] + a2 * in->image_j[k];
            const double fy = b0 + b1 * in->image_i[k] + b2 * in->image_j[k];
            const double vx = fx - in->fiducial_x_mm[k];
            const double vy = fy - in->fiducial_y_mm[k];

            sum_v2 += vx * vx + vy * vy;
            if (std::fabs(vx) > max_abs) max_abs = std::fabs(vx);
            if (std::fabs(vy) > max_abs) max_abs = std::fabs(vy);

            if (out->residual_x_mm != nullptr) out->residual_x_mm[k] = vx;
            if (out->residual_y_mm != nullptr) out->residual_y_mm[k] = vy;
        }

        const int redundancy = 2 * n - 6;
        const double sigma0 = (redundancy > 0) ? std::sqrt(sum_v2 / redundancy) : 0.0;

        // ---- 反算内参
        const double s_x = std::sqrt(a1 * a1 + b1 * b1);   // 第一列的模
        const double s_y = std::sqrt(a2 * a2 + b2 * b2);   // 第二列的模
        const double delta = 0.5 * (s_x + s_y);

        const double det2 = a1 * b2 - a2 * b1;
        if (!(std::fabs(det2) > 0.0)) {
            pgm::set_error("仿射矩阵退化，无法反算像主点");
            return PGM_ERR_DEGENERATE;
        }
        // 解 a0 + a1*Ip + a2*Jp = 0, b0 + b1*Ip + b2*Jp = 0
        const double principal_i = (a2 * b0 - a0 * b2) / det2;
        const double principal_j = (a0 * b1 - a1 * b0) / det2;

        out->a0 = a0; out->a1 = a1; out->a2 = a2;
        out->b0 = b0; out->b1 = b1; out->b2 = b2;

        out->s_x = s_x;
        out->s_y = s_y;
        out->pixel_size_mm = delta;
        out->principal_i = principal_i;
        out->principal_j = principal_j;
        out->x0_mm = delta * (principal_i - in->image_width * 0.5);
        out->y0_mm = -delta * (principal_j - in->image_height * 0.5);
        out->theta_i_deg = deg(std::atan2(b1, a1));
        out->theta_j_deg = deg(std::atan2(b2, a2));

        out->sigma0_mm = sigma0;
        out->max_abs_residual_mm = max_abs;
        out->used_count = n;
        out->redundancy = redundancy;

        // ---- b2 必须为负：像元 y 向下、像平面 y 向上
        if (b2 >= 0.0) {
            pgm::set_error("仿射系数 b2 >= 0，y 轴符号异常；通常是像元坐标与像平面坐标的 y 翻转遗漏");
            return PGM_ERR_AXIS_SIGN;
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

}  // extern "C"
