# osmium_native: native LZMA and Zstandard encoders. Every source file is listed by name; nothing of the upstream
# trees is globbed, configured or run. Build it with build.sh (Linux) or build.ps1 (Windows), which first fetch and
# check the sources (fetch_sources.py) and afterwards check the binary.
cmake_minimum_required(VERSION 3.20)
cmake_policy(SET CMP0091 NEW)   # CMAKE_MSVC_RUNTIME_LIBRARY
project(osmium_native LANGUAGES C)

set(OSN_SRC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../artifacts/native-src" CACHE PATH "Folder with the checked upstream sources")
set(OSN_ARCH "" CACHE STRING "Target architecture in the file name: x64 or arm64")
option(OSN_BUILD_TESTS "Build the self-test and the test-only marker library" OFF)
option(OSN_ANALYZE "MSVC: compile the interface layer with /analyze (findings fail the build)" OFF)
option(OSN_FUZZ "Build the libFuzzer harnesses (clang; with OSN_SANITIZE)" OFF)
option(OSN_FUZZ_REPLAY "Build the fuzz harnesses with a file-replay main instead, for any compiler" OFF)
set(OSN_SANITIZE "" CACHE STRING "Linux test builds only: -fsanitize= list, e.g. thread or address,undefined")
option(OSN_BENCH_NO_SPECTRE "MSVC, measurements only: compile without /Qspectre to measure its cost (never shipped)" OFF)
set(OSN_VERSION_DEFINE "0.0.0" CACHE STRING "Osmium version for the Windows version resource")

if(NOT OSN_ARCH)
  if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(AMD64|x86_64|amd64)$")
    set(OSN_ARCH x64)
  elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(ARM64|aarch64|arm64)$")
    set(OSN_ARCH arm64)
  else()
    message(FATAL_ERROR "Unknown architecture ${CMAKE_SYSTEM_PROCESSOR}; pass -DOSN_ARCH=x64 or arm64")
  endif()
endif()
if(NOT OSN_ARCH MATCHES "^(x64|arm64)$")
  message(FATAL_ERROR "OSN_ARCH must be x64 or arm64")
endif()

# ------------------------------------------------------------------------------ generation.txt
file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/generation.txt" OSN_GEN_LINES REGEX "^[a-z-]+=")
foreach(line IN LISTS OSN_GEN_LINES)
  string(REGEX MATCH "^([a-z-]+)=(.*)$" _ "${line}")
  set("OSN_GEN_${CMAKE_MATCH_1}" "${CMAKE_MATCH_2}")
endforeach()
if(NOT OSN_GEN_generation MATCHES "^[a-z]+-[0-9]+$" OR NOT OSN_GEN_abi MATCHES "^[0-9]+$"
   OR NOT OSN_GEN_lzma-sdk MATCHES "^([0-9]+)\\.([0-9]+)$")
  message(FATAL_ERROR "generation.txt is malformed")
endif()
math(EXPR OSN_LZMA_MAJOR "${CMAKE_MATCH_1}")
math(EXPR OSN_LZMA_MINOR "${CMAKE_MATCH_2}")
if(NOT OSN_GEN_zstd MATCHES "^([0-9]+)\\.([0-9]+)\\.([0-9]+)$")
  message(FATAL_ERROR "generation.txt: malformed zstd version")
endif()
math(EXPR OSN_ZSTD_NUMBER "${CMAKE_MATCH_1} * 10000 + ${CMAKE_MATCH_2} * 100 + ${CMAKE_MATCH_3}")
file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/generated/osn_generation.h" CONTENT
"/* Generated from generation.txt by CMakeLists.txt. */
#define OSN_GENERATION \"${OSN_GEN_generation}\"
#define OSN_ABI ${OSN_GEN_abi}
#define OSN_EXPECT_LZMA_MAJOR ${OSN_LZMA_MAJOR}
#define OSN_EXPECT_LZMA_MINOR ${OSN_LZMA_MINOR}
#define OSN_EXPECT_ZSTD_NUMBER ${OSN_ZSTD_NUMBER}
#define OSN_EXPECT_VERSIONS \"lzma-sdk ${OSN_GEN_lzma-sdk}; zstd ${OSN_GEN_zstd}\"
" @ONLY)

# ------------------------------------------------------------------------------ sources
set(LZ "${OSN_SRC_DIR}/lzma-sdk-${OSN_GEN_lzma-sdk}/C")
set(ZS "${OSN_SRC_DIR}/zstd-${OSN_GEN_zstd}/zstd-${OSN_GEN_zstd}/lib")
if(NOT EXISTS "${LZ}/LzmaEnc.c" OR NOT EXISTS "${ZS}/compress/zstd_compress.c")
  message(FATAL_ERROR "Upstream sources not found in ${OSN_SRC_DIR}: run python3 -I native/fetch_sources.py first")
endif()

set(OSN_SHIM_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/src/osmium_native.c")
set(OSN_LZMA_SOURCES
  "${LZ}/LzmaEnc.c" "${LZ}/LzFind.c" "${LZ}/LzFindMt.c" "${LZ}/LzFindOpt.c" "${LZ}/Threads.c" "${LZ}/CpuArch.c"
  "${LZ}/Alloc.c")
# lib/common/threading.c is left out: without ZSTD_MULTITHREAD it compiles to nothing (threading.h is still used).
set(OSN_ZSTD_SOURCES
  "${ZS}/common/debug.c" "${ZS}/common/entropy_common.c" "${ZS}/common/error_private.c"
  "${ZS}/common/fse_decompress.c" "${ZS}/common/pool.c" "${ZS}/common/xxhash.c" "${ZS}/common/zstd_common.c"
  "${ZS}/compress/fse_compress.c" "${ZS}/compress/hist.c" "${ZS}/compress/huf_compress.c"
  "${ZS}/compress/zstd_compress.c" "${ZS}/compress/zstd_compress_literals.c"
  "${ZS}/compress/zstd_compress_sequences.c" "${ZS}/compress/zstd_compress_superblock.c"
  "${ZS}/compress/zstd_double_fast.c" "${ZS}/compress/zstd_fast.c" "${ZS}/compress/zstd_lazy.c"
  "${ZS}/compress/zstd_ldm.c" "${ZS}/compress/zstd_opt.c" "${ZS}/compress/zstd_preSplit.c"
  "${ZS}/compress/zstdmt_compress.c")
set(OSN_TEST_DECODER_SOURCES   # test programs only: the decoders that check round trips
  "${LZ}/LzmaDec.c"
  "${ZS}/decompress/huf_decompress.c" "${ZS}/decompress/zstd_ddict.c" "${ZS}/decompress/zstd_decompress.c"
  "${ZS}/decompress/zstd_decompress_block.c")

# Zstandard: public API not exported (its headers mark it visibility("default") for gcc and clang), no tracing, no
# assembly, single-threaded (ZSTD_MULTITHREAD must stay undefined: it is tested with #ifdef). LZMA SDK: threads on.
set(OSN_DEFINES NDEBUG ZSTDLIB_VISIBLE= ZSTDERRORLIB_VISIBLE= ZSTDLIB_HIDDEN= ZSTD_TRACE=0 ZSTD_DISABLE_ASM)

# ------------------------------------------------------------------------------ flags
set(OSN_ROOT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/..")
get_filename_component(OSN_ROOT_DIR "${OSN_ROOT_DIR}" ABSOLUTE)

if(MSVC)
  include(CheckCCompilerFlag)
  enable_language(RC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")       # static CRT: the DLL imports only KERNEL32.dll
  set(CMAKE_C_FLAGS_RELEASE "")
  set(OSN_CLANG_CL OFF)
  if(CMAKE_C_COMPILER_ID STREQUAL "Clang")
    set(OSN_CLANG_CL ON)
  endif()
  # /Z7 keeps debug information in each object (no shared PDB between parallel compiles); the linker's /DEBUG:FULL
  # writes the full PDB, which is a CI artifact and never shipped.
  set(OSN_C_FLAGS /O2 /GS /guard:cf /Z7 /utf-8 /Brepro /Gy /Gw)
  if(OSN_CLANG_CL)
    # clang-cl has no /Qspectre and no /guard:signret; ARM64 return-address signing comes from branch protection.
    if(OSN_ARCH STREQUAL "arm64")
      list(APPEND OSN_C_FLAGS /clang:-mbranch-protection=standard)
    endif()
    list(APPEND OSN_C_FLAGS "/clang:-ffile-prefix-map=${OSN_ROOT_DIR}=." /clang:-std=c11)
  else()
    list(APPEND OSN_C_FLAGS /std:c11)
    if(NOT OSN_BENCH_NO_SPECTRE)
      list(APPEND OSN_C_FLAGS /Qspectre)
    endif()
    check_c_compiler_flag("/pathmap:${OSN_ROOT_DIR}=." OSN_HAS_PATHMAP)
    if(OSN_HAS_PATHMAP)
      list(APPEND OSN_C_FLAGS "/pathmap:${OSN_ROOT_DIR}=.")
    endif()
    check_c_compiler_flag(/experimental:deterministic OSN_HAS_DETERMINISTIC)
    if(OSN_HAS_DETERMINISTIC)
      list(APPEND OSN_C_FLAGS /experimental:deterministic)
    endif()
    if(OSN_ARCH STREQUAL "x64")
      list(APPEND OSN_C_FLAGS /guard:ehcont)
    else()
      list(APPEND OSN_C_FLAGS /guard:signret)
    endif()
  endif()
  set(OSN_UPSTREAM_WARN /W3)
  set(OSN_SHIM_WARN /W4 /WX /sdl)
  if(OSN_ANALYZE)
    # Findings in the interface layer are errors (/WX). The system headers it includes by angle brackets (the
    # Windows SDK and the C runtime) are not analysed and give no warnings, so only this project's code is judged.
    list(APPEND OSN_SHIM_WARN /analyze /external:anglebrackets /external:W0 /analyze:external-)
  endif()
  set(OSN_LINK_FLAGS /GUARD:CF /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /Brepro /DEBUG:FULL
      "/PDBALTPATH:osmium_native-${OSN_ARCH}.pdb" /OPT:REF /OPT:ICF /INCREMENTAL:NO /DEPENDENTLOADFLAG:0x800
      "/MAP:${CMAKE_CURRENT_BINARY_DIR}/osmium_native-${OSN_ARCH}.map")
  if(OSN_ARCH STREQUAL "x64")
    list(APPEND OSN_LINK_FLAGS /CETCOMPAT /guard:ehcont)
  endif()
else()
  set(CMAKE_C_FLAGS_RELEASE "")
  set(OSN_C_FLAGS -O2 -fPIC -fvisibility=hidden -fstack-protector-strong -fstack-clash-protection
      "-ffile-prefix-map=${OSN_ROOT_DIR}=." -ffunction-sections -fdata-sections)
  if(OSN_ARCH STREQUAL "x64")
    list(APPEND OSN_C_FLAGS -fcf-protection=full)
  endif()
  set(OSN_LZMA_SAN_EXCEPTIONS)
  if(OSN_SANITIZE)
    list(APPEND OSN_C_FLAGS -g -fno-omit-frame-pointer "-fsanitize=${OSN_SANITIZE}")
    if(OSN_SANITIZE MATCHES "undefined")
      # Every finding is fatal. Two idioms of the LZMA SDK's files only are left unchecked: it reads unaligned 16-
      # and 32-bit values on purpose where the CPU allows it (CpuArch.h: GetUi16/GetUi32 are plain loads when
      # MY_CPU_LE_UNALIGN is defined, as on x64 and ARM64), and it multiplies signed offsets by shifting
      # (LzFindOpt.c: (diff + ...) << 1 with a negative diff). Zstandard and the interface layer keep every check.
      list(APPEND OSN_C_FLAGS -fno-sanitize-recover=undefined)
      set(OSN_LZMA_SAN_EXCEPTIONS -fno-sanitize=alignment,shift-base)
    endif()
  else()
    list(APPEND OSN_C_FLAGS -D_FORTIFY_SOURCE=3)
  endif()
  # Upstream code is written for the compilers' default GNU dialect (it uses POSIX declarations); the interface
  # layer is strict C11.
  set(OSN_UPSTREAM_WARN -std=gnu11 -Wall -Wextra)
  set(OSN_TEST_STD -std=gnu11)
  set(OSN_SHIM_WARN -std=c11 -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion -Wshadow -Wstrict-prototypes
      -Wmissing-prototypes -Wcast-qual -Wformat=2 -Werror)
  set(OSN_LINK_FLAGS -Wl,-z,relro,-z,now,-z,noexecstack -Wl,--build-id=sha1 -Wl,--gc-sections -Wl,--no-undefined
      "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/osmium_native.map")
  # Sanitizer link flags, for the test programs only (sanitizer builds link no library, see below).
  set(OSN_SAN_LINK)
  if(OSN_SANITIZE)
    list(APPEND OSN_SAN_LINK "-fsanitize=${OSN_SANITIZE}")
    if(OSN_SANITIZE MATCHES "undefined")
      list(APPEND OSN_SAN_LINK -fno-sanitize-recover=undefined)
    endif()
  endif()
endif()

# Sanitizer and fuzz builds produce test programs only, each linked statically with the library's objects: clang
# links no sanitizer runtime into a shared object (the runtime comes from the executable), so the library's
# -Wl,--no-undefined could not hold, and nothing would load a sanitized library. No sanitized library is built.
if(OSN_SANITIZE OR OSN_FUZZ OR OSN_FUZZ_REPLAY)
  set(OSN_LINK_LIBRARY OFF)
else()
  set(OSN_LINK_LIBRARY ON)
endif()

set(OSN_INCLUDES "${CMAKE_CURRENT_SOURCE_DIR}/include" "${CMAKE_CURRENT_BINARY_DIR}/generated" "${LZ}" "${ZS}"
    "${ZS}/common")

# The flags as used, for build-info.txt.
string(JOIN " " _cf ${OSN_C_FLAGS})
string(JOIN " " _uw ${OSN_UPSTREAM_WARN})
string(JOIN " " _sw ${OSN_SHIM_WARN})
string(JOIN " " _lf ${OSN_LINK_FLAGS})
string(JOIN " " _df ${OSN_DEFINES})
file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/osn_flags.txt" CONTENT
"compiler=${CMAKE_C_COMPILER_ID} ${CMAKE_C_COMPILER_VERSION}
c_flags=${_cf}
upstream_flags=${_uw}
interface_flags=${_sw}
defines=${_df}
link_flags=${_lf}
" @ONLY)

# ------------------------------------------------------------------------------ the library
add_library(osn_core OBJECT ${OSN_SHIM_SOURCES} ${OSN_LZMA_SOURCES} ${OSN_ZSTD_SOURCES})
target_include_directories(osn_core PRIVATE ${OSN_INCLUDES})
target_compile_definitions(osn_core PRIVATE ${OSN_DEFINES})
target_compile_options(osn_core PRIVATE ${OSN_C_FLAGS})
set_source_files_properties(${OSN_ZSTD_SOURCES} PROPERTIES COMPILE_OPTIONS "${OSN_UPSTREAM_WARN}")
set_source_files_properties(${OSN_LZMA_SOURCES} PROPERTIES COMPILE_OPTIONS "${OSN_UPSTREAM_WARN};${OSN_LZMA_SAN_EXCEPTIONS}")
set_source_files_properties(${OSN_SHIM_SOURCES} PROPERTIES COMPILE_OPTIONS "${OSN_SHIM_WARN}")
set_target_properties(osn_core PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(NOT OSN_LINK_LIBRARY)
  set_target_properties(osn_core PROPERTIES EXCLUDE_FROM_ALL ON)   # built only for the self-test that links it
endif()

set(OSN_LIB_EXTRA)
if(WIN32)
  list(APPEND OSN_LIB_EXTRA "${CMAKE_CURRENT_SOURCE_DIR}/osmium_native.def")
  string(REGEX MATCH "^([0-9]+)\\.([0-9]+)\\.([0-9]+)" _ "${OSN_VERSION_DEFINE}")
  set(OSN_VER_COMMA "${CMAKE_MATCH_1},${CMAKE_MATCH_2},${CMAKE_MATCH_3},0")
  if(NOT CMAKE_MATCH_1 MATCHES "^[0-9]+$")
    set(OSN_VER_COMMA "0,0,0,0")
  endif()
  configure_file("${CMAKE_CURRENT_SOURCE_DIR}/src/osmium_native.rc.in" "${CMAKE_CURRENT_BINARY_DIR}/osmium_native.rc" @ONLY)
  list(APPEND OSN_LIB_EXTRA "${CMAKE_CURRENT_BINARY_DIR}/osmium_native.rc")
endif()
if(OSN_LINK_LIBRARY)
  add_library(osmium_native SHARED $<TARGET_OBJECTS:osn_core> ${OSN_LIB_EXTRA})
  set_target_properties(osmium_native PROPERTIES OUTPUT_NAME "osmium_native-${OSN_ARCH}" LINKER_LANGUAGE C)
  target_link_options(osmium_native PRIVATE ${OSN_LINK_FLAGS})
  if(NOT WIN32)
    set_target_properties(osmium_native PROPERTIES LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/osmium_native.map")
  endif()
endif()

# ------------------------------------------------------------------------------ tests (never shipped)
if(OSN_BUILD_TESTS)
  add_library(osn_testdec STATIC ${OSN_TEST_DECODER_SOURCES})
  target_include_directories(osn_testdec PRIVATE ${OSN_INCLUDES})
  target_compile_definitions(osn_testdec PRIVATE ${OSN_DEFINES})
  target_compile_options(osn_testdec PRIVATE ${OSN_C_FLAGS} ${OSN_UPSTREAM_WARN})
  set_source_files_properties("${LZ}/LzmaDec.c" PROPERTIES COMPILE_OPTIONS "${OSN_LZMA_SAN_EXCEPTIONS}")

  # The self-test links the very objects of the library (so it can also ask the compiled Zstandard what it accepts)
  # and the test decoders.
  add_executable(osn_selftest tests/selftest.c $<TARGET_OBJECTS:osn_core>)
  target_include_directories(osn_selftest PRIVATE ${OSN_INCLUDES})
  target_compile_definitions(osn_selftest PRIVATE ${OSN_DEFINES})
  target_compile_options(osn_selftest PRIVATE ${OSN_C_FLAGS} ${OSN_TEST_STD})
  target_link_libraries(osn_selftest PRIVATE osn_testdec)
  if(NOT WIN32)
    target_link_libraries(osn_selftest PRIVATE pthread)
    target_link_options(osn_selftest PRIVATE ${OSN_SAN_LINK})
  endif()

  # A library with the right exports whose start-up code writes a file named by OSMIUM_MARKER_FILE: the loader
  # tests place it where a planted copy could be and check that its code never runs.
  # Compares builds of the library in one process by per-thread CPU cycles (native-bench.yml): /Qspectre against
  # none, MSVC against clang-cl. It loads each library by the full path it is given.
  add_executable(osn_bench bench/osn_bench.c)
  target_include_directories(osn_bench PRIVATE ${OSN_INCLUDES})
  target_compile_options(osn_bench PRIVATE ${OSN_C_FLAGS} ${OSN_TEST_STD})
  if(NOT WIN32)
    target_link_libraries(osn_bench PRIVATE ${CMAKE_DL_LIBS})
    target_link_options(osn_bench PRIVATE ${OSN_SAN_LINK})
  endif()

  if(OSN_LINK_LIBRARY)
    add_library(osn_marker SHARED tests/marker.c)
    set_target_properties(osn_marker PROPERTIES OUTPUT_NAME "osmium_native_marker-${OSN_ARCH}")
    target_include_directories(osn_marker PRIVATE ${OSN_INCLUDES})
    target_compile_options(osn_marker PRIVATE ${OSN_C_FLAGS} ${OSN_TEST_STD})
    if(WIN32)
      target_sources(osn_marker PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/osmium_native.def")
    endif()
  endif()
endif()

# ------------------------------------------------------------------------------ fuzz harnesses (never shipped)
# The harnesses link the library's own objects, built with OSN_FUZZING (which adds the "fail the N-th allocation"
# switch), and the upstream decoders that check round trips. OSN_FUZZ builds them for libFuzzer (clang);
# OSN_FUZZ_REPLAY builds them with fuzz/replay.c, which runs them over files with any compiler and sanitizer.
if(OSN_FUZZ OR OSN_FUZZ_REPLAY)
  set(OSN_FUZZ_C_FLAGS ${OSN_C_FLAGS})
  set(OSN_FUZZ_COMPILE)
  set(OSN_FUZZ_LINK ${OSN_SAN_LINK})
  if(MSVC)
    # Windows x64 (the msvc-asan job): AddressSanitizer and libFuzzer from the compiler, whose objects name their
    # runtime libraries for the linker. The shipped build's hardening flags are left out; this build is a test
    # program only.
    if(NOT OSN_FUZZ OR OSN_FUZZ_REPLAY OR OSN_CLANG_CL)
      message(FATAL_ERROR "With MSVC only OSN_FUZZ (libFuzzer) is built, with cl")
    endif()
    set(OSN_FUZZ_C_FLAGS /O2 /Z7 /utf-8 /fsanitize=address /fsanitize=fuzzer)
  elseif(OSN_FUZZ)
    set(OSN_FUZZ_COMPILE -fsanitize=fuzzer-no-link)
    list(APPEND OSN_FUZZ_LINK -fsanitize=fuzzer)
  endif()
  add_library(osn_fuzzcore OBJECT ${OSN_SHIM_SOURCES} ${OSN_LZMA_SOURCES} ${OSN_ZSTD_SOURCES})
  target_include_directories(osn_fuzzcore PRIVATE ${OSN_INCLUDES})
  target_compile_definitions(osn_fuzzcore PRIVATE ${OSN_DEFINES} OSN_FUZZING)
  target_compile_options(osn_fuzzcore PRIVATE ${OSN_FUZZ_C_FLAGS} ${OSN_FUZZ_COMPILE})
  add_library(osn_fuzzdec STATIC ${OSN_TEST_DECODER_SOURCES})
  target_include_directories(osn_fuzzdec PRIVATE ${OSN_INCLUDES})
  target_compile_definitions(osn_fuzzdec PRIVATE ${OSN_DEFINES})
  target_compile_options(osn_fuzzdec PRIVATE ${OSN_FUZZ_C_FLAGS} ${OSN_UPSTREAM_WARN})
  foreach(h fuzz_lzma_encode fuzz_zstd_encode fuzz_shim_args)
    if(OSN_FUZZ)
      set(t ${h})
      add_executable(${t} fuzz/${h}.c $<TARGET_OBJECTS:osn_fuzzcore>)
    else()
      set(t replay_${h})
      add_executable(${t} fuzz/${h}.c fuzz/replay.c $<TARGET_OBJECTS:osn_fuzzcore>)
    endif()
    target_include_directories(${t} PRIVATE ${OSN_INCLUDES})
    target_compile_definitions(${t} PRIVATE ${OSN_DEFINES})
    target_compile_options(${t} PRIVATE ${OSN_FUZZ_C_FLAGS} ${OSN_TEST_STD} ${OSN_FUZZ_COMPILE})
    target_link_libraries(${t} PRIVATE osn_fuzzdec)
    if(NOT WIN32)
      target_link_libraries(${t} PRIVATE pthread)
    endif()
    target_link_options(${t} PRIVATE ${OSN_FUZZ_LINK})
  endforeach()
endif()
