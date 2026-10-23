#pragma once

// 库内部的公共声明，不对外暴露。

namespace pgm {

// 记录最近一次失败原因（线程局部）。
void set_error(const char* message);

// 三元运算辅助：判断两个 double 是否足够接近。
inline bool nearly_equal(double a, double b, double tol)
{
    const double d = a - b;
    return (d < 0 ? -d : d) <= tol;
}

}  // namespace pgm
