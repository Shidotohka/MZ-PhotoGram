// 特征匹配：SIFT + 比值检验 + 核线过滤 + RANSAC。
//
// 这是唯一链接 OpenCV 的编译单元。为什么用库、为什么单独一个 DLL、为什么核线不交给库，
// 见 Docs/界面设计规范.md ADR-22。这里的几何约束（核线）仍然调 photogrammetry.dll，
// 不在 OpenCV 里另写一份。
//
// 流程（每一步都能单独关掉，方便定位是哪一步出的问题）：
//   1. SIFT 检特征 + 描述子（两片各一份）
//   2. 最近邻匹配 + Lowe 比值检验：丢掉"第一候选和第二候选一样好"的配对——那是重复纹理
//   3. 核线过滤：|点到左点所定核线的距离| > 容差就丢，这一步把二维候选压成一维
//   4. RANSAC 复核基础矩阵：只留内点。核线用的是定向结果（b, R），这一步用的是
//      "两片所有匹配点能否被同一个对极几何解释"，两者独立，互相兜底
//
// 注意第 4 步的尺度：RANSAC 阈值按像元给（3 px），不按 mm。

#include "photogrammetry_cv.h"

#include <opencv2/calib3d.hpp>
#include <opencv2/core.hpp>
#include <opencv2/features2d.hpp>
#include <opencv2/flann.hpp>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace {

thread_local std::string g_error;

// 排查用：设 PGM_CV_DEBUG=1 时把每一步剩多少对打到 stderr。
// 只影响诊断，不改变任何结果——"匹配太少"到底少在哪一步，得看这个漏斗。
bool funnel_on()
{
    static const bool on = std::getenv("PGM_CV_DEBUG") != nullptr;
    return on;
}

void funnel(const char* stage, int n)
{
    if (funnel_on()) std::fprintf(stderr, "[funnel] %-28s %d\n", stage, n);
}

// 调参用：PGM_CV_SIFT_MAX 覆盖每片特征数上限（0 = 不限），PGM_CV_MATCHER=bf|flann 强制选匹配器。
// 只在排查时用；不设就跟原来完全一样。
//
// 默认值 40000 是量出来的，不是拍的（Hammer 01-156 x 02-165，5120²，漏斗见 Docs/算法笔记.md §10.9）：
//   上限      最终同名点     q_rms      说明
//    8000        154       0.026 mm   旧默认，只剩 1 点 / 3277 px，太抠
//   20000        377       0.027 mm
//   40000        776       0.027 mm   取这个：点多 5 倍，精度不变
//   80000       1379       0.027 mm   再翻倍仍可用，但配对耗时跟着涨
//  不限        3376         -        SIFT 出 26 万个特征，配对不可接受地慢
// 特征上限只决定"参与配对的有多少"，不影响检测耗时；耗时主要花在配对。
int sift_cap()
{
    const char* s = std::getenv("PGM_CV_SIFT_MAX");
    if (s == nullptr) return 40000;
    return std::atoi(s);
}

bool force_bf()
{
    const char* s = std::getenv("PGM_CV_MATCHER");
    if (s == nullptr) return false;
    const std::string v(s);
    return v == "bf" || v == "BF";
}

bool force_flann()
{
    const char* s = std::getenv("PGM_CV_MATCHER");
    if (s == nullptr) return false;
    const std::string v(s);
    return v == "flann" || v == "FLANN";
}

void set_error(const char* msg)
{
    g_error = msg ? msg : "";
}

struct Match
{
    double li, lj, ri, rj;   // 像元坐标
    double score;            // 描述子距离
    double epi_mm;           // |点到核线距离|
};

bool valid_image(const uint8_t* px, int32_t w, int32_t h, int32_t step)
{
    return px != nullptr && w > 0 && h > 0 && step >= w;
}

}  // namespace

extern "C" {

int32_t pgm_cv_match(const pgm_cv_match_input* in,
                     int32_t capacity,
                     double* out_left_i,  double* out_left_j,
                     double* out_right_i, double* out_right_j,
                     double* out_score,
                     double* out_epipolar_mm,
                     int32_t* out_count,
                     int32_t* out_inliers,
                     int32_t* out_detected)
{
    if (in == nullptr) {
        set_error("输入指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    if (out_count == nullptr) {
        set_error("输出计数器指针为空");
        return PGM_ERR_NULL_ARGUMENT;
    }
    *out_count = 0;
    if (out_inliers) *out_inliers = 0;
    if (out_detected) *out_detected = 0;

    if (capacity <= 0 || out_left_i == nullptr || out_left_j == nullptr ||
        out_right_i == nullptr || out_right_j == nullptr) {
        set_error("容量必须为正，四个坐标数组不能为空");
        return PGM_ERR_INVALID_ARGUMENT;
    }
    if (!valid_image(in->left_pixels, in->left_width, in->left_height, in->left_step) ||
        !valid_image(in->right_pixels, in->right_width, in->right_height, in->right_step)) {
        set_error("影像缓冲指针为空，或宽高/行字节数不合法");
        return PGM_ERR_INVALID_ARGUMENT;
    }

    const double ratio = (in->ratio > 0.0 && in->ratio < 1.0) ? in->ratio : 0.8;
    const double tol_mm = in->epipolar_tolerance_mm > 0.0 ? in->epipolar_tolerance_mm : 0.10;

    try {
        // step 原样交给 OpenCV：WPF 的位图常按 4 字节对齐，不能假设 step == width。
        const cv::Mat left(in->left_height, in->left_width, CV_8U,
                           const_cast<uint8_t*>(in->left_pixels), static_cast<size_t>(in->left_step));
        const cv::Mat right(in->right_height, in->right_width, CV_8U,
                            const_cast<uint8_t*>(in->right_pixels), static_cast<size_t>(in->right_step));

        std::vector<cv::KeyPoint> kp_left, kp_right;
        cv::Mat desc_left, desc_right;

        // SIFT：尺度不变、带方向，对航片的旋转与尺度差异比对 ORB 稳。
        // 特征数封顶只影响"参与配对的点数"，检测本身仍是全图扫一遍（封顶是检测后截断）。
        // 5120² 原片不封顶能出 26 万特征，配对时间不可接受；40000 是量出来的折中。
        cv::Ptr<cv::SIFT> sift = cv::SIFT::create(sift_cap());
        sift->detectAndCompute(left, cv::noArray(), kp_left, desc_left);
        sift->detectAndCompute(right, cv::noArray(), kp_right, desc_right);

        if (out_detected) {
            *out_detected = static_cast<int32_t>(std::min(kp_left.size(), kp_right.size()));
        }
        funnel("SIFT 左", static_cast<int>(kp_left.size()));
        funnel("SIFT 右", static_cast<int>(kp_right.size()));
        if (kp_left.empty() || kp_right.empty() || desc_left.empty() || desc_right.empty()) {
            return PGM_OK;   // 图太平坦：不是调用错误
        }

        // 特征少时暴力最近邻最准；上万条时用 FLANN 的 KD 树，否则时间全耗在算距离上。
        cv::Ptr<cv::DescriptorMatcher> matcher;
        if (force_bf() || (!force_flann() && desc_left.rows <= 3000)) {
            matcher = cv::makePtr<cv::BFMatcher>(cv::NORM_L2);
        } else {
            matcher = cv::makePtr<cv::FlannBasedMatcher>(
                cv::makePtr<cv::flann::KDTreeIndexParams>(4),
                cv::makePtr<cv::flann::SearchParams>(64));
        }

        std::vector<std::vector<cv::DMatch>> knn;
        matcher->knnMatch(desc_left, desc_right, knn, 2);
        funnel("最近邻候选", static_cast<int>(knn.size()));

        // 反向最近邻：右片每个特征最像左片的哪一个。
        // 只留"互为最佳"的配对——单向比值检验挡不住重复纹理（农田、屋脊），
        // 一个左点能被好几个右点抢，反向查一次就露馅。
        std::vector<std::vector<cv::DMatch>> back;
        matcher->knnMatch(desc_right, desc_left, back, 1);
        std::vector<int> reverse(static_cast<size_t>(kp_right.size()), -1);
        for (const auto& v : back) {
            if (!v.empty()) reverse[static_cast<size_t>(v[0].queryIdx)] = v[0].trainIdx;
        }

        std::vector<Match> kept;
        kept.reserve(knn.size());
        int n_ratio = 0, n_mutual = 0, n_epi_dropped = 0;

        for (const auto& pair : knn) {
            if (pair.size() < 2) continue;
            if (!(pair[0].distance < ratio * pair[1].distance)) continue;   // 比值检验
            ++n_ratio;
            if (reverse[static_cast<size_t>(pair[0].trainIdx)] != pair[0].queryIdx) continue;  // 互为最佳
            ++n_mutual;

            const cv::KeyPoint& kl = kp_left[static_cast<size_t>(pair[0].queryIdx)];
            const cv::KeyPoint& kr = kp_right[static_cast<size_t>(pair[0].trainIdx)];

            Match m{ kl.pt.x, kl.pt.y, kr.pt.x, kr.pt.y, pair[0].distance, 0.0 };

            if (in->use_epipolar && in->pixel_size_mm > 0.0) {
                const double lx = (m.li - in->cx_left_px) * in->pixel_size_mm;
                const double ly = (in->cy_left_px - m.lj) * in->pixel_size_mm;
                const double rx = (m.ri - in->cx_right_px) * in->pixel_size_mm;
                const double ry = (in->cy_right_px - m.rj) * in->pixel_size_mm;

                double d = 0.0;
                const int32_t rc = pgm_epipolar_distance(&in->epipolar, lx, ly, rx, ry, &d);
                if (rc == PGM_OK) {
                    m.epi_mm = std::fabs(d);
                    if (m.epi_mm > tol_mm) { ++n_epi_dropped; continue; }    // 几何上不可能，直接丢
                }
                // rc != OK（核面在无穷远等）时不过滤，留给 RANSAC 与后面的相对定向去判
            }

            kept.push_back(m);
        }

        funnel("过比值检验 0.8", n_ratio);
        funnel("过互为最佳", n_mutual);
        funnel("核线过滤丢掉", n_epi_dropped);
        funnel("给 RANSAC 的", static_cast<int>(kept.size()));

        // RANSAC：按像元坐标复核。用 3 px 阈值（手工刺点也就这个量级），
        // 只保留能被同一个对极几何解释的配对。
        if (kept.size() >= 8) {
            std::vector<cv::Point2f> p1, p2;
            p1.reserve(kept.size());
            p2.reserve(kept.size());
            for (const auto& m : kept) {
                p1.emplace_back(static_cast<float>(m.li), static_cast<float>(m.lj));
                p2.emplace_back(static_cast<float>(m.ri), static_cast<float>(m.rj));
            }

            cv::Mat mask;
            const cv::Mat F = cv::findFundamentalMat(p1, p2, cv::FM_RANSAC, 3.0, 0.999, mask);
            if (!F.empty() && mask.rows == static_cast<int>(kept.size())) {
                int32_t inliers = 0;
                std::vector<Match> filtered;
                filtered.reserve(kept.size());
                for (size_t k = 0; k < kept.size(); ++k) {
                    if (mask.at<uint8_t>(static_cast<int>(k), 0) == 0) continue;
                    filtered.push_back(kept[k]);
                    ++inliers;
                }
                if (out_inliers) *out_inliers = inliers;
                funnel("RANSAC 内点", inliers);
                kept.swap(filtered);
            }
        }

        std::sort(kept.begin(), kept.end(),
                  [](const Match& a, const Match& b) { return a.score < b.score; });

        const int32_t n = static_cast<int32_t>(std::min<size_t>(kept.size(), static_cast<size_t>(capacity)));
        for (int32_t k = 0; k < n; ++k) {
            out_left_i[k] = kept[k].li;
            out_left_j[k] = kept[k].lj;
            out_right_i[k] = kept[k].ri;
            out_right_j[k] = kept[k].rj;
            if (out_score) out_score[k] = kept[k].score;
            if (out_epipolar_mm) out_epipolar_mm[k] = kept[k].epi_mm;
        }
        *out_count = n;
        return PGM_OK;
    } catch (const cv::Exception& e) {
        set_error(e.what());
        return PGM_ERR_INTERNAL;
    } catch (...) {
        set_error("匹配过程中出现未预期异常");
        return PGM_ERR_INTERNAL;
    }
}

const char* pgm_cv_version(void)
{
    static const std::string kVersion = std::string("pgm-cv 1.0 / OpenCV ") + CV_VERSION;
    return kVersion.c_str();
}

const char* pgm_cv_last_error(void)
{
    return g_error.c_str();
}

}  // extern "C"
