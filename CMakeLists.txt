cmake_minimum_required(VERSION 3.20)
# FH2DotNet.dll, the bridge that runs .NET mods, built alongside the platform
# it ships with (the platform submodule). The managed side is built by dotnet;
# scripts/build.sh builds both and stages dist/.
project(fh2_dotnet VERSION 0.9.0 LANGUAGES C CXX)

add_subdirectory(platform)   # fh2mod_platform (d3dcompiler_47.dll, DWrite.dll) and fh2mod_api

add_library(fh2dotnet SHARED src/dotnet.cpp)
target_link_libraries(fh2dotnet PRIVATE fh2mod_api)
target_compile_features(fh2dotnet PRIVATE cxx_std_17)
target_compile_definitions(fh2dotnet PRIVATE FH2DOTNET_VERSION="${PROJECT_VERSION}" _WIN32_WINNT=0x0A00 WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(fh2dotnet PROPERTIES OUTPUT_NAME FH2DotNet PREFIX "")
if(MSVC)
    target_compile_options(fh2dotnet PRIVATE /W4 /utf-8 /EHsc)
else()
    target_compile_options(fh2dotnet PRIVATE -Wall -Wextra -Wno-missing-field-initializers)
    # No libc++/libunwind DLLs to ship.
    target_link_options(fh2dotnet PRIVATE -static "$<$<CONFIG:Release>:-s>")
endif()
target_link_libraries(fh2dotnet PRIVATE advapi32)

# FH2Console.exe, the console window FH2DotNet.dll opens (fh2-mods\dotnet\).
add_executable(fh2console src/console.cpp)
target_compile_features(fh2console PRIVATE cxx_std_17)
target_compile_definitions(fh2console PRIVATE _WIN32_WINNT=0x0A00 WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(fh2console PROPERTIES OUTPUT_NAME FH2Console)
if(MSVC)
    target_compile_options(fh2console PRIVATE /W4 /utf-8 /EHsc)
else()
    target_compile_options(fh2console PRIVATE -Wall -Wextra)
    target_link_options(fh2console PRIVATE -municode -static "$<$<CONFIG:Release>:-s>")
endif()

# tests/run_host_test.sh: a stand-in platform that loads FH2DotNet.dll and AOT mods without the game.
option(FH2DOTNET_TESTS "Build fake_platform.exe for tests/run_host_test.sh" OFF)
if(FH2DOTNET_TESTS)
    add_executable(fake_platform tests/fake_platform.cpp)
    target_link_libraries(fake_platform PRIVATE fh2mod_api)
    target_compile_features(fake_platform PRIVATE cxx_std_17)
    target_compile_definitions(fake_platform PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    if(NOT MSVC)
        target_link_options(fake_platform PRIVATE -municode -static)
    endif()
endif()
