// 计算机视觉匹配的验收程序（第二个 DLL）。
//
// 用真实像对（Hammer 的 01-156 / 02-165 缩略图）跑完整链路：
//   1 读图与特征      —— SIFT 能检出多少
//   2 只靠描述子匹配  —— 比值检验 + RANSAC 之后还剩多少
//   3 相对定向        —— 拿第 2 步的点解 (b, R)，看 q_rms 是否落在手工点的量级
//   4 核线约束后重匹配 —— 用第 3 步的 (b, R) 过滤，再比一次数量与核线残差
//
// 用法： pgm_cv_match_test [左图] [右图]
// 返回： 0 全部通过，1 有失败项

#include "photogrammetry.h"
#include "photogrammetry_cv.h"

#include <opencv2/calib3d.hpp>
#include <opencv2/core.hpp>
#include <opencv2/imgcodecs.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace {

// Hammer 那两片缩略图：5120 px 的原片缩到 512 px，像元 0.04503 mm x 10
constexpr double kPixelMm = 0.4503;
constexpr double kFocalMm = 152.72;
constexpr int kCapacity = 2000;

int g_failures = 0;

void say(bool ok, const char* what, const char* detail)
{
    std::printf("  [%s] %-40s %s\n", ok ? "PASS" : "FAIL", what, detail);
    if (!ok) ++g_failures;
}

struct Matches
{
    int32_t count = 0;
    int32_t inliers = 0;
    int32_t detected = 0;
    std::vector<double> li, lj, ri, rj, score, epi;
};

bool run_match(const cv::Mat& left, const cv::Mat& right,
               const pgm_cv_match_input& base, Matches* out)
{
    pgm_cv_match_input in = base;
    in.left_pixels = left.data;
    in.left_width = left.cols;
    in.left_height = left.rows;
    in.left_step = static_cast<int32_t>(left.step);
    in.right_pixels = right.data;
    in.right_width = right.cols;
    in.right_height = right.rows;
    in.right_step = static_cast<int32_t>(right.step);

    out->li.assign(kCapacity, 0.0);
    out->lj.assign(kCapacity, 0.0);
    out->ri.assign(kCapacity, 0.0);
    out->rj.assign(kCapacity, 0.0);
    out->score.assign(kCapacity, 0.0);
    out->epi.assign(kCapacity, 0.0);

    const int32_t rc = pgm_cv_match(&in, kCapacity,
                                    out->li.data(), out->lj.data(),
                                    out->ri.data(), out->rj.data(),
                                    out->score.data(), out->epi.data(),
                                    &out->count, &out->inliers, &out->detected);
    return rc == PGM_OK;
}

double median(std::vector<double> v)
{
    if (v.empty()) return 0.0;
    std::sort(v.begin(), v.end());
    return v[v.size() / 2];
}

}  // namespace

int main(int argc, char** argv)
{
    const std::string left_path = argc > 1 ? argv[1]
        : "Data/real/2_Hammer/HammerIndex/01-156_50mic.jpg";
    const std::string right_path = argc > 2 ? argv[2]
        : "Data/real/2_Hammer/HammerIndex/01-155_50mic.jpg";
    const double pixel_mm = argc > 3 ? std::atof(argv[3]) : 0.04503;

    std::printf("计算机视觉匹配验收（OpenCV）\n");
    std::printf("版本：%s\n", pgm_cv_version());
    std::printf("左：%s\n右：%s\n\n", left_path.c_str(), right_path.c_str());

    const cv::Mat left = cv::imread(left_path, cv::IMREAD_GRAYSCALE);
    const cv::Mat right = cv::imread(right_path, cv::IMREAD_GRAYSCALE);
    if (left.empty() || right.empty()) {
        std::printf("  [FAIL] 读不到影像：%s\n", pgm_cv_last_error());
        return 1;
    }

    std::printf("== 1 读图与特征 ==\n");
    {
        char detail[160];
        std::snprintf(detail, sizeof detail, "左 %dx%d，右 %dx%d（灰度）",
                      left.cols, left.rows, right.cols, right.rows);
        say(left.cols > 100 && right.cols > 100, "两幅影像都已载入", detail);
    }

    pgm_cv_match_input base{};
    base.pixel_size_mm = pixel_mm;
    base.cx_left_px = 0.5 * (left.cols - 1);
    base.cy_left_px = 0.5 * (left.rows - 1);
    base.cx_right_px = 0.5 * (right.cols - 1);
    base.cy_right_px = 0.5 * (right.rows - 1);
    base.ratio = 0.8;
    base.epipolar_tolerance_mm = 0.10;
    base.use_epipolar = 0;
    base.epipolar.focal_length_mm = kFocalMm;

    // ---------------------------------------------------------------- 2
    std::printf("== 2 只靠描述子匹配（比值检验 + RANSAC） ==\n");
    Matches plain;
    if (!run_match(left, right, base, &plain)) {
        std::printf("  [FAIL] 匹配返回错误：%s\n", pgm_cv_last_error());
        return 1;
    }

    {
        char detail[160];
        std::snprintf(detail, sizeof detail, "检出 %d，配上 %d 对，RANSAC 内点 %d",
                      plain.detected, plain.count, plain.inliers);
        say(plain.count >= 30, "匹配到足够多的同名点", detail);
    }

    // 构型诊断：两片之间大概差多少旋转。航摄片被旋转 90° 扫进来是常见的，
    // 这种像对能匹配，但相对定向的初值（平均视差方向）会差 90°，解不出来。
    if (plain.count >= 4) {
        std::vector<cv::Point2f> p1, p2;
        for (int32_t k = 0; k < plain.count; ++k) {
            p1.emplace_back(static_cast<float>(plain.li[k]), static_cast<float>(plain.lj[k]));
            p2.emplace_back(static_cast<float>(plain.ri[k]), static_cast<float>(plain.rj[k]));
        }
        const cv::Mat affine = cv::estimateAffinePartial2D(p1, p2, cv::noArray(), cv::RANSAC);
        if (!affine.empty()) {
            const double a = affine.at<double>(0, 0);
            const double b = affine.at<double>(0, 1);
            const double angle = std::atan2(b, a) * 180.0 / 3.14159265358979323846;
            const double scale = std::sqrt(a * a + b * b);
            std::printf("        旋转诊断：约 %.1f°，尺度 %.3f（90° 说明两片扫进来时转了向）\n",
                        angle, scale);
        }
    }

    // ---------------------------------------------------------------- 3
    std::printf("== 3 用第 2 步的点做相对定向 ==\n");
    double bx = 0.0, by = 0.0, bz = 0.0;
    double rotation[9] = { 0 };
    bool oriented = false;
    {
        std::vector<double> lx(plain.li.size()), ly(plain.lj.size());
        std::vector<double> rx(plain.ri.size()), ry(plain.rj.size());
        for (int32_t k = 0; k < plain.count; ++k) {
            lx[k] = (plain.li[k] - base.cx_left_px) * pixel_mm;
            ly[k] = (base.cy_left_px - plain.lj[k]) * pixel_mm;
            rx[k] = (plain.ri[k] - base.cx_right_px) * pixel_mm;
            ry[k] = (base.cy_right_px - plain.rj[k]) * pixel_mm;
        }

        std::vector<double> q(static_cast<size_t>(plain.count), 0.0);
        std::vector<int32_t> flags(static_cast<size_t>(plain.count), 0);

        pgm_relative_orientation_input ri{};
        ri.count = plain.count;
        ri.left_x_mm = lx.data();
        ri.left_y_mm = ly.data();
        ri.right_x_mm = rx.data();
        ri.right_y_mm = ry.data();
        ri.focal_length_mm = kFocalMm;
        ri.image_sigma_mm = 0.0;
        ri.max_iterations = 50;
        ri.tolerance = 1e-10;

        pgm_relative_orientation_output ro{};
        ro.residual_q_mm = q.data();
        ro.used_flags = flags.data();

        const int32_t rc = pgm_relative_orientation(&ri, &ro);
        oriented = rc == PGM_OK;
        if (oriented) {
            bx = ro.b_x; by = ro.b_y; bz = ro.b_z;
            for (int i = 0; i < 9; ++i) rotation[i] = ro.rotation[i];
        }

        char detail[200];
        if (oriented) {
            std::snprintf(detail, sizeof detail,
                          "%.0f 轮　参与 %d/%d　q_rms %.4f mm　σ0 %.4f mm　冻结 b%c",
                          static_cast<double>(ro.iterations), ro.used_count, plain.count,
                          ro.q_rms_mm, ro.sigma0_mm, "uvw"[ro.gauge]);
        } else {
            std::snprintf(detail, sizeof detail, "rc = %d：%s", rc, pgm_last_error());
        }
        say(oriented && ro.q_rms_mm < 0.10, "自动匹配的点能定向（q_rms < 0.10 mm）", detail);

        // 残差分布：判断是"点普遍不准"还是"少数点错"
        if (oriented) {
            std::vector<double> absq;
            for (size_t k = 0; k < q.size(); ++k) absq.push_back(std::fabs(q[k]));
            int big = 0;
            for (double v : absq) if (v > 0.30) ++big;
            std::printf("        q 中位数 %.4f mm，最大 %.4f mm，|q|>0.3 mm 的有 %d 个\n",
                        median(absq), absq.empty() ? 0.0 : *std::max_element(absq.begin(), absq.end()), big);

            // 视差分布：航向像对上 dx 大、dy 小；旁向像对反过来
            std::vector<double> dx, dy;
            for (int32_t k = 0; k < plain.count; ++k) {
                dx.push_back(plain.ri[k] - plain.li[k]);
                dy.push_back(plain.rj[k] - plain.lj[k]);
            }
            std::snprintf(detail, sizeof detail,
                          "dx 中位 %.1f px，dy 中位 %.1f px，dy 范围 %.1f~%.1f px",
                          median(dx), median(dy),
                          dy.empty() ? 0.0 : *std::min_element(dy.begin(), dy.end()),
                          dy.empty() ? 0.0 : *std::max_element(dy.begin(), dy.end()));
            std::printf("        %s\n", detail);
        }
    }

    // ---------------------------------------------------------------- 4
    std::printf("== 4 核线约束后重匹配 ==\n");
    if (oriented) {
        pgm_cv_match_input with_epi = base;
        with_epi.use_epipolar = 1;
        with_epi.epipolar.bx = bx;
        with_epi.epipolar.by = by;
        with_epi.epipolar.bz = bz;
        for (int i = 0; i < 9; ++i) with_epi.epipolar.rotation[i] = rotation[i];

        Matches constrained;
        if (run_match(left, right, with_epi, &constrained)) {
            std::vector<double> epi;
            for (int32_t k = 0; k < constrained.count; ++k) epi.push_back(constrained.epi[k]);

            char detail[200];
            std::snprintf(detail, sizeof detail, "%d 对（原 %d 对），|核线距离| 中位数 %.4f mm，最大 %.3f mm",
                          constrained.count, plain.count, median(epi), epi.empty() ? 0.0 : epi.back());
            say(constrained.count >= 20 && median(epi) < 0.05,
                "核线过滤后仍有足够匹配、残差小", detail);
        } else {
            say(false, "核线约束匹配", pgm_cv_last_error());
        }
    } else {
        say(false, "核线约束匹配", "第 3 步没定向成功，跳过");
    }

    std::printf("\n");
    if (g_failures == 0) {
        std::printf("CV MATCH PASS\n");
        return 0;
    }
    std::printf("CV MATCH FAIL（%d 项）\n", g_failures);
    return 1;
}
