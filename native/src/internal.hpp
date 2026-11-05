#pragma once

// 库内部的公共声明，不对外暴露。

namespace pgm {

// 记录最近一次失败原因（线程局部）。
void set_error(const char* message);

}  // namespace pgm
