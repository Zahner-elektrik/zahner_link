cmake_minimum_required (VERSION 3.27)

project(TestProject LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

list(APPEND CMAKE_PREFIX_PATH "C:/Users/max/Downloads/lib-for-install") # path to the downloaded library
find_package(zahner_link COMPONENTS zahner_link REQUIRED)

if(MSVC)
    add_compile_options(/bigobj /utf-8)
endif()

include_directories(${PROJECT_SOURCE_DIR})
add_executable(TestProject main.cpp)

target_link_libraries(TestProject PUBLIC zahner_link::zahner_link)