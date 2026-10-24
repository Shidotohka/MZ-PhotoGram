#pragma once

// 库内部的三维线性代数与教材旋转矩阵。只给 native/src 下的实现文件用，不进 ABI。
//
// 后方交会与前方交会共用这一份。R 的约定如果各写一份，迟早有一边写歪，而写歪了
// 程序照样出数 —— 上次外方位解错就是栽在这类"两份约定"上。约定固定为：
//
//   · R 的行对应 U/V/W，列对应 dX/dY/dZ，即
//             | a1 b1 c1 |
//             | a2 b2 c2 |
//             | a3 b3 c3 |
//   · 分解顺序 R = R_kappa * R_omega * R_phi，三个初等阵取教材形式
//     （它们与通用右手旋转阵差一个符号，正是这个约定使得展开后等于 a1..c3）。
//   · R 把地面向量转到像空间：u = R * d。像空间 z 轴指向摄影者一侧，
//     故垂直摄影时 W = Z - Zs < 0。
//
// 反过来，像点 (x, y, -f) 转成地面方向要乘 R 的转置，见 mat_vec_t 与
// forward_intersection.cpp 里的说明。

#include <cmath>

namespace pgm {

// ---------------------------------------------------------------- 3x3

struct Mat3
{
    double m[9];  // 行主序
};

inline void mat_mul(const Mat3& a, const Mat3& b, Mat3& out)
{
    for (int i = 0; i < 3; ++i) {
        for (int j = 0; j < 3; ++j) {
            out.m[i * 3 + j] = a.m[i * 3 + 0] * b.m[0 * 3 + j] +
                               a.m[i * 3 + 1] * b.m[1 * 3 + j] +
                               a.m[i * 3 + 2] * b.m[2 * 3 + j];
        }
    }
}

inline void mat_vec(const Mat3& a, const double v[3], double out[3])
{
    for (int i = 0; i < 3; ++i) {
        out[i] = a.m[i * 3 + 0] * v[0] + a.m[i * 3 + 1] * v[1] + a.m[i * 3 + 2] * v[2];
    }
}

// out = a^T * v。
inline void mat_vec_t(const Mat3& a, const double v[3], double out[3])
{
    for (int j = 0; j < 3; ++j) {
        out[j] = a.m[0 * 3 + j] * v[0] + a.m[1 * 3 + j] * v[1] + a.m[2 * 3 + j] * v[2];
    }
}

inline double dot3(const double a[3], const double b[3])
{
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}

// ---------------------------------------------------------------- 旋转

inline Mat3 rot_phi(double a)
{
    const double c = std::cos(a), s = std::sin(a);
    return Mat3{ { c, 0.0, s, 0.0, 1.0, 0.0, -s, 0.0, c } };
}

inline Mat3 rot_omega(double a)
{
    const double c = std::cos(a), s = std::sin(a);
    return Mat3{ { 1.0, 0.0, 0.0, 0.0, c, s, 0.0, -s, c } };
}

inline Mat3 rot_kappa(double a)
{
    const double c = std::cos(a), s = std::sin(a);
    return Mat3{ { c, s, 0.0, -s, c, 0.0, 0.0, 0.0, 1.0 } };
}

struct Rotation
{
    Mat3 phi, omega, kappa;  // 三个初等阵
    Mat3 total;              // R = kappa * omega * phi
};

inline Rotation make_rotation(double phi, double omega, double kappa)
{
    Rotation rs;
    rs.phi = rot_phi(phi);
    rs.omega = rot_omega(omega);
    rs.kappa = rot_kappa(kappa);
    Mat3 tmp;
    mat_mul(rs.omega, rs.phi, tmp);
    mat_mul(rs.kappa, tmp, rs.total);
    return rs;
}

// ---------------------------------------------------------------- 输入检查

inline bool finite_all(const double* v, int n)
{
    for (int i = 0; i < n; ++i) {
        if (!std::isfinite(v[i])) return false;
    }
    return true;
}

}  // namespace pgm
