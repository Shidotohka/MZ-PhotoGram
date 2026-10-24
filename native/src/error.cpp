#include "photogrammetry.h"
#include "internal.hpp"

#include <string>

namespace pgm {
namespace {

// 线程局部：多线程调用时各自的错误信息互不覆盖。
thread_local std::string g_last_error;

}  // namespace

void set_error(const char* message)
{
    g_last_error = (message != nullptr) ? message : "";
}

}  // namespace pgm

extern "C" {

const char* pgm_version(void)
{
    return "1.1.0";
}

const char* pgm_last_error(void)
{
    return pgm::g_last_error.c_str();
}

}  // extern "C"
