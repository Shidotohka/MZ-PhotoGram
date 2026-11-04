cmake_minimum_required(VERSION 3.20)
project(photogrammetry LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
    add_compile_options(/W4 /permissive- /utf-8 /EHsc)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS)
else()
    add_compile_options(-Wall -Wextra)
endif()

# DLL 与测试程序输出到同一目录，测试 exe 才能找到 DLL
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)

# ---------------------------------------------------------------- 算法 DLL

add_library(photogrammetry SHARED
    src/error.cpp
    src/interior_orientation.cpp
    src/resection.cpp
    src/forward_intersection.cpp
    src/relative_orientation.cpp
    src/epipolar.cpp
)

target_include_directories(photogrammetry PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_definitions(photogrammetry PRIVATE PGM_BUILDING_DLL)

# ---------------------------------------------------------------- 测试程序

add_executable(pgm_test tests/test_interior_orientation.cpp)
target_link_libraries(pgm_test PRIVATE photogrammetry)

add_executable(pgm_resection_test tests/test_resection.cpp)
target_link_libraries(pgm_resection_test PRIVATE photogrammetry)

add_executable(pgm_intersection_test tests/test_forward_intersection.cpp)
target_link_libraries(pgm_intersection_test PRIVATE photogrammetry)

add_executable(pgm_relative_orientation_test tests/test_relative_orientation.cpp)
target_link_libraries(pgm_relative_orientation_test PRIVATE photogrammetry)

add_executable(pgm_epipolar_test tests/test_epipolar.cpp)
target_link_libraries(pgm_epipolar_test PRIVATE photogrammetry)

enable_testing()

# ---------------------------------------------------------------- CV 匹配（第二个 DLL）
#
# 只有这一个目标链接 OpenCV。找不到就跳过，其余算法照常构建——
# 「主 DLL 零依赖」这条不是口号，见 Docs/界面设计规范.md ADR-22。
# 配置时带上 vcpkg 的工具链：
#   cmake -S native -B native/build -DCMAKE_TOOLCHAIN_FILE=C:/vcpkg/scripts/buildsystems/vcpkg.cmake
find_package(OpenCV CONFIG QUIET)

if(OpenCV_FOUND)
    message(STATUS "找到 OpenCV ${OpenCV_VERSION}：构建 photogrammetry_cv")

    add_library(photogrammetry_cv SHARED src/cv_match.cpp)
    target_include_directories(photogrammetry_cv PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
    target_compile_definitions(photogrammetry_cv PRIVATE PGM_CV_BUILDING_DLL)
    target_link_libraries(photogrammetry_cv PRIVATE photogrammetry ${OpenCV_LIBS})

    add_executable(pgm_cv_match_test tests/test_cv_match.cpp)
    target_link_libraries(pgm_cv_match_test PRIVATE photogrammetry_cv photogrammetry ${OpenCV_LIBS})

    # 运行时需要 opencv_*.dll 与 exe 并排；vcpkg 的运行时都在 installed/<triplet>/bin
    if(DEFINED VCPKG_INSTALLED_DIR)
        file(GLOB _opencv_runtime_dlls "${VCPKG_INSTALLED_DIR}/x64-windows/bin/opencv*.dll")
        foreach(_dll ${_opencv_runtime_dlls})
            add_custom_command(TARGET photogrammetry_cv POST_BUILD
                COMMAND ${CMAKE_COMMAND} -E copy_if_different
                        "${_dll}" "$<TARGET_FILE_DIR:photogrammetry_cv>")
        endforeach()
    endif()

    add_test(NAME cv_match COMMAND pgm_cv_match_test
             ${CMAKE_CURRENT_SOURCE_DIR}/../Data/real/2_Hammer/HammerIndex/01-156_50mic.jpg
             ${CMAKE_CURRENT_SOURCE_DIR}/../Data/real/2_Hammer/HammerIndex/01-155_50mic.jpg
             0.04503)
else()
    message(STATUS "未找到 OpenCV：跳过 photogrammetry_cv（匹配模块不可用，其余照常）")
endif()

add_test(NAME interior_orientation
         COMMAND pgm_test ${CMAKE_CURRENT_SOURCE_DIR}/../Data/sim)
add_test(NAME resection COMMAND pgm_resection_test)
add_test(NAME forward_intersection COMMAND pgm_intersection_test)
add_test(NAME relative_orientation COMMAND pgm_relative_orientation_test)
add_test(NAME epipolar COMMAND pgm_epipolar_test)
