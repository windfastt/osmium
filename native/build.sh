#!/usr/bin/env bash
# Builds libosmium_native-x64.so into artifacts/native/linux-x64/ with build-info.txt beside it.
# The Linux library is for tests and contributors only; Osmium ships no Linux build.
#
#   native/build.sh [--compiler gcc|clang] [--from DIR] [--tests] [--sanitize LIST]
#
#   --compiler   C compiler (default gcc)
#   --from DIR   take the upstream archives from DIR instead of downloading them
#   --tests      also build and run the self-test, and build the test-only marker library into
#                artifacts/native-test/linux-x64/ (never into artifacts/native/)
#   --sanitize   build only the self-test, with -fsanitize=LIST (thread, address,undefined), and run it;
#                produces no library
#   --fuzz       build only the libFuzzer harnesses (clang, address and undefined-behaviour sanitizers) into
#                artifacts/native-fuzz/, and write the seed inputs there
#   --fuzz-replay  build the harnesses with a file-replay main instead (any compiler, the same sanitizers) and run
#                them over the seeds and over artifacts/native-fuzz/corpus/<harness>/ if it exists
# Uses at most OSN_JOBS parallel compiles (default 2).
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
compiler=gcc
from=
tests=0
sanitize=
fuzz=
while [ $# -gt 0 ]; do
  case "$1" in
    --compiler) compiler=$2; shift 2 ;;
    --from) from=$2; shift 2 ;;
    --tests) tests=1; shift ;;
    --sanitize) sanitize=$2; shift 2 ;;
    --fuzz) fuzz=libfuzzer; compiler=clang; shift ;;
    --fuzz-replay) fuzz=replay; shift ;;
    *) echo "usage: native/build.sh [--compiler gcc|clang] [--from DIR] [--tests] [--sanitize LIST] [--fuzz|--fuzz-replay]" >&2; exit 2 ;;
  esac
done
jobs=${OSN_JOBS:-2}
rid=linux-x64

echo "== sources"
if [ -n "$from" ]; then python3 -I "$root/native/fetch_sources.py" --from "$from"; else python3 -I "$root/native/fetch_sources.py"; fi
python3 -I "$root/native/check_sources.py"

if [ -n "$fuzz" ]; then
  build="$root/artifacts/native-build/$rid-fuzz-$fuzz-$compiler"
  out="$root/artifacts/native-fuzz"
  rm -rf "$build"
  flag=$([ "$fuzz" = libfuzzer ] && echo OSN_FUZZ || echo OSN_FUZZ_REPLAY)
  cmake -S "$root/native" -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER="$compiler" \
    -DOSN_ARCH=x64 -D$flag=ON -DOSN_SANITIZE=address,undefined > /dev/null
  ninja -C "$build" -j "$jobs"
  mkdir -p "$out"
  python3 -I "$root/native/fuzz/make_seeds.py" "$out/seeds"
  export ASAN_OPTIONS="detect_leaks=1 halt_on_error=1 ${ASAN_OPTIONS:-}"
  export UBSAN_OPTIONS="print_stacktrace=1 halt_on_error=1 ${UBSAN_OPTIONS:-}"
  for h in fuzz_lzma_encode fuzz_zstd_encode fuzz_shim_args; do
    if [ "$fuzz" = libfuzzer ]; then
      cp "$build/$h" "$out/"
    else
      echo "== replay $h"
      dirs="$out/seeds/$h"
      [ -d "$out/corpus/$h" ] && dirs="$dirs $out/corpus/$h"
      # shellcheck disable=SC2086
      "$build/replay_$h" $dirs
    fi
  done
  exit 0
fi

if [ -n "$sanitize" ]; then
  build="$root/artifacts/native-build/$rid-$compiler-${sanitize//,/-}"
  rm -rf "$build"
  cmake -S "$root/native" -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER="$compiler" \
    -DOSN_ARCH=x64 -DOSN_BUILD_TESTS=ON -DOSN_SANITIZE="$sanitize" > /dev/null
  ninja -C "$build" -j "$jobs" osn_selftest
  echo "== self-test with -fsanitize=$sanitize"
  export TSAN_OPTIONS="suppressions=$root/native/tests/tsan.supp halt_on_error=1 exitcode=66 ${TSAN_OPTIONS:-}"
  export ASAN_OPTIONS="detect_leaks=1 halt_on_error=1 ${ASAN_OPTIONS:-}"
  export UBSAN_OPTIONS="print_stacktrace=1 halt_on_error=1 ${UBSAN_OPTIONS:-}"
  if [ "$sanitize" = thread ]; then
    "$build/osn_selftest" --parallel "${OSN_PARALLEL:-4}" --repeat "${OSN_REPEAT:-2}" --only-parallel
  else
    "$build/osn_selftest"
  fi
  exit 0
fi

# A fixed build folder, so two builds of one commit see the same paths (they are also mapped to "." in the output).
build="$root/artifacts/native-build/$rid"
rm -rf "$build"
echo "== configure ($compiler)"
cmake -S "$root/native" -B "$build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER="$compiler" \
  -DOSN_ARCH=x64 -DOSN_BUILD_TESTS="$([ $tests = 1 ] && echo ON || echo OFF)"
echo "== build"
ninja -C "$build" -j "$jobs" 2>&1 | tee "$build/build.log"
warnings=$(grep -c 'warning:' "$build/build.log" || true)
echo "compiler warnings: $warnings"

lib="$build/libosmium_native-x64.so"
echo "== binary checks"
"$root/native/check_binary.sh" "$lib" "$build"

out="$root/artifacts/native/$rid"
rm -rf "$out"
mkdir -p "$out"
cp "$lib" "$out/"
gen() { sed -n "s/^$1=//p" "$root/native/generation.txt"; }
first() { "$@" 2>&1 | head -n 1; }
sevenzip=$(command -v 7z || command -v 7za || command -v 7zz || true)
{
  echo "target=$rid"
  echo "file=libosmium_native-x64.so"
  echo "sha256=$(sha256sum "$out/libosmium_native-x64.so" | cut -d' ' -f1)"
  echo "size=$(stat -c %s "$out/libosmium_native-x64.so")"
  echo "generation=$(gen generation)"
  echo "abi=$(gen abi)"
  echo "versions=lzma-sdk $(gen lzma-sdk); zstd $(gen zstd)"
  echo "sources_lock_sha256=$(sha256sum "$root/native/sources.lock" | cut -d' ' -f1)"
  echo "sources_files_sha256=$(sha256sum "$root/native/sources.files" | cut -d' ' -f1)"
  echo "compiler_version=$(first "$compiler" --version)"
  echo "cmake=$(first cmake --version)"
  echo "ninja=$(ninja --version)"
  echo "python=$(python3 --version 2>&1)"
  echo "7zip=$([ -n "$sevenzip" ] && "$sevenzip" | sed -n '/[^[:space:]]/{p;q}' || echo none)"
  echo "os=$(uname -srm)"
  echo "image=${ImageOS:-local} ${ImageVersion:-}"
  echo "warnings=$warnings"
  cat "$build/osn_flags.txt"
} > "$out/build-info.txt"
cat "$out/build-info.txt"

if [ $tests = 1 ]; then
  test_out="$root/artifacts/native-test/$rid"
  rm -rf "$test_out"
  mkdir -p "$test_out"
  cp "$build/libosmium_native_marker-x64.so" "$build/osn_selftest" "$build/osn_bench" "$test_out/"
  echo "== self-test"
  "$build/osn_selftest"
fi
