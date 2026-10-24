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

enable_testing()
add_test(NAME interior_orientation
         COMMAND pgm_test ${CMAKE_CURRENT_SOURCE_DIR}/../Data/sim)
add_test(NAME resection COMMAND pgm_resection_test)
add_test(NAME forward_intersection COMMAND pgm_intersection_test)
add_test(NAME relative_orientation COMMAND pgm_relative_orientation_test)
