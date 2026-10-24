cmake_minimum_required(VERSION 3.24)

project(StalkerModLauncherUsvfsX86Host LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}")

set(USVFS_SOURCE_DIR "" CACHE PATH "Path to the ModOrganizer2/usvfs source tree")

if(NOT USVFS_SOURCE_DIR)
  message(FATAL_ERROR "USVFS_SOURCE_DIR is required")
endif()

add_executable(StalkerModLauncher.UsvfsX86Host main.cpp)
target_include_directories(
  StalkerModLauncher.UsvfsX86Host
  PRIVATE "${USVFS_SOURCE_DIR}/include/usvfs")
target_link_libraries(
  StalkerModLauncher.UsvfsX86Host
  PRIVATE "${USVFS_SOURCE_DIR}/lib/usvfs_x86.lib")

include(CTest)
if(BUILD_TESTING)
  add_executable(process_list_tests process_list_tests.cpp)
  target_include_directories(process_list_tests PRIVATE "${USVFS_SOURCE_DIR}/include/usvfs")
  target_link_libraries(process_list_tests PRIVATE "${USVFS_SOURCE_DIR}/lib/usvfs_x86.lib")
  add_test(NAME process_list_tests COMMAND process_list_tests)
  set_tests_properties(process_list_tests PROPERTIES
    ENVIRONMENT_MODIFICATION "PATH=path_list_prepend:${USVFS_SOURCE_DIR}/lib")
endif()

