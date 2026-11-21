cmake_minimum_required(VERSION 3.24)

project(StalkerUsvfsOverlayPoc LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}")

set(USVFS_SOURCE_DIR "" CACHE PATH "Path to the ModOrganizer2/usvfs source tree")
set(USVFS_BINARY_DIR "" CACHE PATH "Path to the built ModOrganizer2/usvfs source tree")

if(NOT USVFS_SOURCE_DIR)
  message(FATAL_ERROR "USVFS_SOURCE_DIR is required")
endif()

if(NOT USVFS_BINARY_DIR)
  message(FATAL_ERROR "USVFS_BINARY_DIR is required")
endif()

set(USVFS_INCLUDE_DIR "${USVFS_SOURCE_DIR}/include/usvfs")
set(USVFS_LIB_DIR "${USVFS_BINARY_DIR}/lib")
set(USVFS_BIN_DIR "${USVFS_BINARY_DIR}/bin")

add_executable(usvfs_overlay_child child.cpp)
add_executable(usvfs_overlay_child_x86 child.cpp)
add_executable(usvfs_overlay_launcher_x86 launcher.cpp)
add_library(usvfs_smoke_base SHARED smoke_base.cpp)
add_library(usvfs_smoke_mod SHARED smoke_mod.cpp)
target_link_libraries(usvfs_smoke_mod PRIVATE usvfs_smoke_base)
add_executable(usvfs_layered_launcher_x86 launcher.cpp)
target_compile_definitions(usvfs_layered_launcher_x86 PRIVATE USVFS_SMOKE_DLLS)
target_link_libraries(usvfs_layered_launcher_x86 PRIVATE usvfs_smoke_mod)
add_executable(usvfs_layered_child_x86 child.cpp)
target_compile_definitions(usvfs_layered_child_x86 PRIVATE USVFS_SMOKE_DLLS)
target_link_libraries(usvfs_layered_child_x86 PRIVATE usvfs_smoke_mod)
add_library(usvfs_smoke_override SHARED smoke_base.cpp)
target_compile_definitions(usvfs_smoke_override PRIVATE USVFS_SMOKE_OVERRIDE)

add_executable(usvfs_overlay_host host.cpp)
target_include_directories(usvfs_overlay_host PRIVATE "${USVFS_INCLUDE_DIR}")
target_link_libraries(usvfs_overlay_host PRIVATE "${USVFS_LIB_DIR}/usvfs_x64.lib")

add_custom_command(TARGET usvfs_overlay_host POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${USVFS_LIB_DIR}/usvfs_x64.dll"
    "$<TARGET_FILE_DIR:usvfs_overlay_host>/usvfs_x64.dll"
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${USVFS_BIN_DIR}/usvfs_proxy_x64.exe"
    "$<TARGET_FILE_DIR:usvfs_overlay_host>/usvfs_proxy_x64.exe")
