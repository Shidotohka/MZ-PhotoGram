#pragma once

// 库内部的线性代数：对称矩阵特征分解、伪逆求解、N^-1 的对角线。
//
// 后方交会与相对定向共用这一份。相对定向的法方程是 5x5，后方交会是 6x6，病态
// 的来源不同（后方交会怕控制点共面，相对定向怕基线方向与旋转耦合），但要的是
// 同一件事：解一个对称正定（或近乎奇异）的法方程，并且**判定它是否秩亏**。
//
// 判秩用特征值比 λmin/λmax，而不是行列式：行列式随矩阵阶数和单位一起变，5x5 与
// 6x6 之间没法比；特征值比是无量纲的。相对定向尤其需要它 —— 列相关系数实测
// 0.9924（见 Docs/相对定向算法.md 第七节），按行列式判会一路绿灯到解出垃圾。
//
// 这里只管矩阵，不认识任何摄影测量量，所以与 geometry.hpp 一样留在库内部，
// 不进 ABI。

#include <cmath>
#include <vector>

namespace pgm {

// λmin / λmax 的下界，低于它按秩亏处理。
constexpr double kRankTolerance = 1e-12;

// ---------------------------------------------------------------- 对称矩阵特征分解

// 循环 Jacobi。a 为 dim*dim 行主序，长度 dim*dim，就地修改。
// values[j] = 特征值；vectors[j*dim + i] = 第 j 个特征向量的第 i 个分量。
inline void jacobi_eigen(double* a, int dim, double* values, double* vectors)
{
    const int nn = dim * dim;
    for (int i = 0; i < nn; ++i) vectors[i] = 0.0;
    for (int i = 0; i < dim; ++i) vectors[i * dim + i] = 1.0;

    for (int sweep = 0; sweep < 64; ++sweep) {
        double off = 0.0;
        double max_diag = 0.0;
        for (int p = 0; p < dim; ++p) {
            const double d = std::fabs(a[p * dim + p]);
            if (d > max_diag) max_diag = d;
            for (int q = p + 1; q < dim; ++q) off += a[p * dim + q] * a[p * dim + q];
        }
        if (max_diag <= 0.0) break;
        if (off <= 1e-28 * max_diag * max_diag) break;

        for (int p = 0; p < dim - 1; ++p) {
            for (int q = p + 1; q < dim; ++q) {
                const double apq = a[p * dim + q];
                if (apq == 0.0) continue;

                const double theta = (a[q * dim + q] - a[p * dim + p]) / (2.0 * apq);
                const double sign = (theta >= 0.0) ? 1.0 : -1.0;
                const double t = sign / (std::fabs(theta) + std::sqrt(theta * theta + 1.0));
                const double c = 1.0 / std::sqrt(t * t + 1.0);
                const double s = t * c;

                for (int k = 0; k < dim; ++k) {          // A <- A * G
                    const double kp = a[k * dim + p];
                    const double kq = a[k * dim + q];
                    a[k * dim + p] = c * kp - s * kq;
                    a[k * dim + q] = s * kp + c * kq;
                }
                for (int k = 0; k < dim; ++k) {          // A <- G^T * A
                    const double pk = a[p * dim + k];
                    const double qk = a[q * dim + k];
                    a[p * dim + k] = c * pk - s * qk;
                    a[q * dim + k] = s * pk + c * qk;
                }
                for (int k = 0; k < dim; ++k) {          // V <- V * G
                    const double kp = vectors[k * dim + p];
                    const double kq = vectors[k * dim + q];
                    vectors[k * dim + p] = c * kp - s * kq;
                    vectors[k * dim + q] = s * kp + c * kq;
                }
            }
        }
    }

    for (int j = 0; j < dim; ++j) values[j] = a[j * dim + j];
}

// 对称矩阵特征分解 + 秩检查。返回 false 表示数值上秩亏。
inline bool symmetric_eigen(const double* mat, int dim, std::vector<double>& values,
                            std::vector<double>& vectors, double* condition)
{
    std::vector<double> a(mat, mat + static_cast<size_t>(dim) * dim);
    values.assign(dim, 0.0);
    vectors.assign(static_cast<size_t>(dim) * dim, 0.0);
    jacobi_eigen(a.data(), dim, values.data(), vectors.data());

    double lmax = values[0], lmin = values[0];
    for (int j = 1; j < dim; ++j) {
        if (values[j] > lmax) lmax = values[j];
        if (values[j] < lmin) lmin = values[j];
    }
    if (condition != nullptr) *condition = (lmin > 0.0) ? lmax / lmin : 1e300;
    if (!(lmax > 0.0)) return false;
    return lmin > kRankTolerance * lmax;
}

// x = N^-1 b，复用已算好的特征分解。N = V*Λ*V^T，V[i][j] = vectors[i*dim + j]。
inline void solve_from_eigen(const std::vector<double>& values, const std::vector<double>& vectors,
                             int dim, const double* b, double* x)
{
    // vb = Λ^-1 * V^T * b
    std::vector<double> vb(static_cast<size_t>(dim), 0.0);
    for (int j = 0; j < dim; ++j) {
        double s = 0.0;
        for (int i = 0; i < dim; ++i) s += vectors[i * dim + j] * b[i];
        vb[j] = s / values[j];
    }
    // x = V * vb
    for (int i = 0; i < dim; ++i) {
        double s = 0.0;
        for (int j = 0; j < dim; ++j) s += vectors[i * dim + j] * vb[j];
        x[i] = s;
    }
}

// (N^-1) 的对角线，用于精度评定。
inline void diag_inverse_from_eigen(const std::vector<double>& values,
                                    const std::vector<double>& vectors, int dim, double* diag)
{
    for (int i = 0; i < dim; ++i) {
        double s = 0.0;
        for (int j = 0; j < dim; ++j) {
            const double v = vectors[i * dim + j];
            s += v * v / values[j];
        }
        diag[i] = s;
    }
}

}  // namespace pgm
