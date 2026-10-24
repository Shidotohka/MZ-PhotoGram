// 内定向 DLL 的验收程序。
//
// 读 Data/sim 的合成数据，调 photogrammetry.dll，和答案对表。
// 用法： pgm_test [数据目录]      默认 Data/sim
// 返回： 0 全部通过，1 有失败项

#ifdef _WIN32
#  define NOMINMAX
#  include <windows.h>
#endif

#include "photogrammetry.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <random>
#include <sstream>
#include <string>
#include <vector>

namespace {

constexpr int kWidth = 5120;
constexpr int kHeight = 5120;

std::vector<std::vector<std::string>> read_csv(const std::string& path)
{
    std::vector<std::vector<std::string>> rows;
    std::ifstream file(path);
    if (!file) return rows;

    std::string line;
    bool header = true;
    while (std::getline(file, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        if (header) { header = false; continue; }

        std::vector<std::string> cells;
        std::stringstream ss(line);
        std::string cell;
        while (std::getline(ss, cell, ',')) cells.push_back(cell);
        rows.push_back(cells);
    }
    return rows;
}

double to_double(const std::string& s)
{
    return std::strtod(s.c_str(), nullptr);
}

struct Sample {
    std::vector<double> fx, fy, i, j;
};

// 用给定位移噪声生成观测
Sample perturb(const Sample& base, double sigma_px, std::mt19937& rng)
{
    Sample out = base;
    if (sigma_px <= 0.0) return out;

    std::normal_distribution<double> noise(0.0, sigma_px);
    for (size_t k = 0; k < out.i.size(); ++k) {
        out.i[k] += noise(rng);
        out.j[k] += noise(rng);
    }
    return out;
}

int run_case(const char* title,
             const Sample& s,
             const std::map<std::string, double>& expected,
             double sigma_px,
             bool expect_ok)
{
    std::vector<double> rx(s.i.size(), 0.0), ry(s.i.size(), 0.0);

    pgm_fiducial_input in{};
    in.count = static_cast<int32_t>(s.i.size());
    in.fiducial_x_mm = s.fx.data();
    in.fiducial_y_mm = s.fy.data();
    in.image_i = s.i.data();
    in.image_j = s.j.data();
    in.image_width = kWidth;
    in.image_height = kHeight;

    pgm_fiducial_output out{};
    out.residual_x_mm = rx.data();
    out.residual_y_mm = ry.data();

    const int32_t rc = pgm_fiducial_orientation(&in, &out);

    if (!expect_ok) {
        const bool pass = (rc != PGM_OK);
        std::printf("[%s] %s  (rc=%d %s)\n", pass ? "PASS" : "FAIL", title, rc,
                    rc != PGM_OK ? pgm_last_error() : "预期被拒绝但没有");
        return pass ? 0 : 1;
    }

    if (rc != PGM_OK) {
        std::printf("[FAIL] %s  rc=%d %s\n", title, rc, pgm_last_error());
        return 1;
    }

    const double exp_delta = expected.at("pixel_size_delta");
    const double exp_x0 = expected.at("x0");
    const double exp_y0 = expected.at("y0");
    const double exp_pi = expected.at("principal_I");
    const double exp_pj = expected.at("principal_J");

    const double d_delta_pct = (out.pixel_size_mm - exp_delta) / exp_delta * 100.0;
    const double d_pi = out.principal_i - exp_pi;
    const double d_pj = out.principal_j - exp_pj;

    bool pass;
    if (sigma_px <= 0.0) {
        pass = std::fabs(out.pixel_size_mm - exp_delta) < 1e-8 &&
               std::fabs(out.x0_mm - exp_x0) < 1e-8 &&
               std::fabs(out.y0_mm - exp_y0) < 1e-8;
    } else {
        pass = std::fabs(d_delta_pct) < 0.5 &&
               std::fabs(d_pi) < 3.0 && std::fabs(d_pj) < 3.0 &&
               out.b2 < 0.0;
    }

    std::printf("[%s] %s\n", pass ? "PASS" : "FAIL", title);
    std::printf("       delta=%.9f (%+.4f%%)  s_x=%.9f  s_y=%.9f  b2=%.9f\n",
                out.pixel_size_mm, d_delta_pct, out.s_x, out.s_y, out.b2);
    std::printf("       x0=%+.9f  y0=%+.9f  principal=(%.3f, %.3f)  d=(%+.3f, %+.3f) px\n",
                out.x0_mm, out.y0_mm, out.principal_i, out.principal_j, d_pi, d_pj);
    std::printf("       theta_i=%.4f  theta_j=%.4f  sigma0=%.6f mm  redundancy=%d\n",
                out.theta_i_deg, out.theta_j_deg, out.sigma0_mm, out.redundancy);

    return pass ? 0 : 1;
}

}  // namespace

int main(int argc, char** argv)
{
#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif

    const std::string data_dir = (argc > 1) ? argv[1] : "Data/sim";
    const std::string mm_path = data_dir + "/m2/fiducials_image_mm.csv";
    const std::string px_path = data_dir + "/m2/answer_fiducials_px.csv";
    const std::string ans_path = data_dir + "/m2/answer_intrinsics.csv";

    const auto mm_rows = read_csv(mm_path);
    const auto px_rows = read_csv(px_path);
    const auto ans_rows = read_csv(ans_path);
    if (mm_rows.empty() || px_rows.empty() || ans_rows.empty()) {
        std::printf("找不到测试数据：%s\n请先运行 python Data/sim/make_sim.py\n", data_dir.c_str());
        return 1;
    }

    std::map<int, std::pair<double, double>> fiducial_mm;
    for (const auto& r : mm_rows) {
        if (r.size() < 3) continue;
        fiducial_mm[std::atoi(r[0].c_str())] = {to_double(r[1]), to_double(r[2])};
    }

    Sample truth;
    for (const auto& r : px_rows) {
        if (r.size() < 3) continue;
        const int id = std::atoi(r[0].c_str());
        const auto it = fiducial_mm.find(id);
        if (it == fiducial_mm.end()) continue;
        truth.fx.push_back(it->second.first);
        truth.fy.push_back(it->second.second);
        truth.i.push_back(to_double(r[1]));
        truth.j.push_back(to_double(r[2]));
    }

    std::map<std::string, double> expected;
    for (const auto& r : ans_rows) {
        if (r.size() < 2) continue;
        expected[r[0]] = to_double(r[1]);
    }

    std::printf("=== pgm_fiducial_orientation 自检 ===\n");
    std::printf("版本 %s   数据 %s   框标 %zu 个\n", pgm_version(), data_dir.c_str(), truth.i.size());
    std::printf("真值 delta=%.9f  x0=%+.9f  y0=%+.9f  主点=(%.3f, %.3f)\n\n",
                expected["pixel_size_delta"], expected["x0"], expected["y0"],
                expected["principal_I"], expected["principal_J"]);

    int failures = 0;
    std::mt19937 rng(20241121);

    failures += run_case("理想取点 sigma=0", truth, expected, 0.0, true);
    failures += run_case("手工取点 sigma=0.5px", perturb(truth, 0.5, rng), expected, 0.5, true);
    failures += run_case("手工取点 sigma=1.5px", perturb(truth, 1.5, rng), expected, 1.5, true);

    // 退化：3 个框标
    {
        Sample three = truth;
        three.fx.resize(3); three.fy.resize(3); three.i.resize(3); three.j.resize(3);
        failures += run_case("仅 3 个框标应被拒绝", three, expected, 0.0, false);
    }

    // 退化：共线
    {
        Sample line;
        for (int k = 0; k < 4; ++k) {
            line.fx.push_back(truth.fx[k]);
            line.fy.push_back(truth.fy[k]);
            line.i.push_back(500.0 + k * 300.0);
            line.j.push_back(1000.0);
        }
        failures += run_case("框标共线应被拒绝", line, expected, 0.0, false);
    }

    // 轴符号：把像元 j 取反，b2 应当变正
    {
        Sample flipped = truth;
        for (auto& v : flipped.j) v = -v;
        failures += run_case("像元 j 取反应触发 b2 符号错误", flipped, expected, 0.0, false);
    }

    std::printf("\n%s\n", failures == 0 ? "DLL SELFTEST PASS" : "DLL SELFTEST FAIL");
    return failures == 0 ? 0 : 1;
}
