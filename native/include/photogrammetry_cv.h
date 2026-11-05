/*
 * photogrammetry_cv.h —— 计算机视觉匹配的 C ABI（OpenCV）
 *
 * 这是「第二个 DLL」：只有它链接 OpenCV，photogrammetry.dll 保持零第三方依赖
 * （理由见 Docs/界面设计规范.md ADR-22）。它调用同一目录下的 photogrammetry.dll
 * 做核线过滤，所以两个 DLL 必须并排放在一起。
 *
 * 约定与 photogrammetry.h 相同：固定布局结构体、调用方持有缓冲区、返回 0 为成功、
 * C++ 异常不跨越 ABI 边界。
 */
#ifndef PHOTOMETRY_CV_H_INCLUDED
#define PHOTOMETRY_CV_H_INCLUDED

#include <stdint.h>

#include "photogrammetry.h"

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  ifdef PGM_CV_BUILDING_DLL
#    define PGM_CV_API __declspec(dllexport)
#  else
#    define PGM_CV_API __declspec(dllimport)
#  endif
#else
#  define PGM_CV_API
#endif

/*
 * 一次匹配的输入：两幅 8 位灰度影像 + 相机的像元尺寸/主点 + （可选）核线几何。
 *
 * 影像缓冲由调用方持有，本函数不保留引用。step 是每行的字节数（一般等于 width，
 * 但 WPF 的位图可能按 4 字节对齐，所以必须显式给）。
 *
 * 像元 -> 像平面（mm）按
 *     x_mm = (i - cx_px) * pixel_size_mm
 *     y_mm = (cy_px - j) * pixel_size_mm        （y 向上，符合像平面约定）
 * 换算。左右片一般同相机，主点取同一组；分成两个字段是为了以后换相机时不用改 ABI。
 *
 * use_epipolar = 1 时用 epipolar 里的 (b, R, f) 做核线过滤：先按描述子匹配，再把
 * |点到核线距离| > epipolar_tolerance_mm 的候选丢掉。pixel_size_mm <= 0 时不做过滤
 * （坐标换算不出来）。
 */
typedef struct pgm_cv_match_input
{
    const uint8_t* left_pixels;
    int32_t        left_width, left_height, left_step;

    const uint8_t* right_pixels;
    int32_t        right_width, right_height, right_step;

    double pixel_size_mm;
    double cx_left_px,  cy_left_px;
    double cx_right_px, cy_right_px;

    int32_t            use_epipolar;
    pgm_epipolar_input epipolar;     /* focal_length_mm 也在这里 */

    double ratio;                    /* Lowe 比值检验阈值，<= 0 取 0.8 */
    double epipolar_tolerance_mm;    /* <= 0 取 0.10 */
} pgm_cv_match_input;

/*
 * 匹配：SIFT 特征 -> 最近邻 + 比值检验 -> （可选）核线过滤 -> RANSAC 复核基础矩阵。
 *
 * 输出是像元坐标（i 为列、j 为行，可为小数），按描述子距离从优到劣排列，最多 capacity 组。
 *   out_score         描述子距离（越小越好），可为 NULL
 *   out_epipolar_mm   |点到核线距离|，mm；没做核线过滤时写 0，可为 NULL
 *   out_count         实际输出组数
 *   out_inliers       RANSAC 判为内点的组数（输出里只保留内点）
 *   out_detected      两片检出的特征数取小者，供调用方判断"图太平坦"
 *
 * 检不出特征或一对都配不上时返回 PGM_OK 且 *out_count = 0——那是数据的问题，
 * 不是调用错误。参数非法返回 PGM_ERR_INVALID_ARGUMENT。
 */
PGM_CV_API int32_t pgm_cv_match(const pgm_cv_match_input* in,
                                int32_t capacity,
                                double* out_left_i,  double* out_left_j,
                                double* out_right_i, double* out_right_j,
                                double* out_score,
                                double* out_epipolar_mm,
                                int32_t* out_count,
                                int32_t* out_inliers,
                                int32_t* out_detected);

/* 形如 "pgm-cv 1.0 / OpenCV 4.12.0"。给自检打印用，确认加载的是哪份 OpenCV。 */
PGM_CV_API const char* pgm_cv_version(void);

/* 最近一次调用失败的原因（UTF-8）。线程局部，返回的指针在下一次调用前有效。 */
PGM_CV_API const char* pgm_cv_last_error(void);

#ifdef __cplusplus
}
#endif

#endif /* PHOTOMETRY_CV_H_INCLUDED */
