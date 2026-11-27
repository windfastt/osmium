#!/usr/bin/env bash
# Checks the Linux build of the library; any failure ends with a non-zero status.
#   native/check_binary.sh <libosmium_native-x64.so> [<build folder with the object files>]
# - NEEDED only libc.so.6 (libm.so.6 tolerated); no RPATH or RUNPATH; no text relocations
# - BIND_NOW, a GNU_RELRO segment and a non-executable GNU_STACK
# - exactly the osn_* functions of osmium_native.map defined in the dynamic symbol table
# - no decoder in the library or its objects: no ZSTD_decompress*, ZSTD_createDCtx*, ZSTD_DCtx*, ZSTD_decodeFrame*
#   or LzmaDec_* symbol; the FSE/HUF table readers that survived --gc-sections are listed for review
set -euo pipefail

lib=$1
objs=${2:-}
fail=0
bad() { echo "check_binary: FAIL: $*" >&2; fail=1; }
ok() { echo "check_binary: ok: $*"; }

[ -f "$lib" ] || { echo "check_binary: no such file: $lib" >&2; exit 1; }

machine=$(readelf -h "$lib" | sed -n 's/^ *Machine: *//p')
[ "$machine" = "Advanced Micro Devices X86-64" ] && ok "machine $machine" || bad "machine is '$machine', expected x86-64"

dyn=$(readelf -dW "$lib")
needed=$(echo "$dyn" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p' | sort | tr '\n' ' ')
for n in $needed; do
  case "$n" in
    libc.so.6) ;;
    libm.so.6) echo "check_binary: note: libm.so.6 is needed" ;;
    *) bad "unexpected NEEDED $n" ;;
  esac
done
ok "NEEDED: $needed"
echo "$dyn" | grep -Eq '\((RPATH|RUNPATH)\)' && bad "RPATH or RUNPATH present" || ok "no RPATH/RUNPATH"
echo "$dyn" | grep -q '(TEXTREL)' && bad "text relocations present" || ok "no TEXTREL"
echo "$dyn" | grep -Eq '\(FLAGS\).*BIND_NOW|\(FLAGS_1\).*NOW' && ok "BIND_NOW" || bad "BIND_NOW missing"

segs=$(readelf -lW "$lib")
echo "$segs" | grep -q 'GNU_RELRO' && ok "GNU_RELRO" || bad "GNU_RELRO missing"
stack=$(echo "$segs" | awk '$1 == "GNU_STACK" { print $NF == "E" || $(NF-1) ~ /E/ ? "exec" : "noexec" }')
[ "$stack" = noexec ] && ok "GNU_STACK not executable" || bad "GNU_STACK is executable or missing ($stack)"

expected=$(sed -n 's/^ *\(osn_[a-z0-9_]*\);$/\1/p' "$(dirname "$0")/osmium_native.map" | sort | tr '\n' ' ')
exported=$(nm -D --defined-only "$lib" | awk '{ print $NF }' | sort | tr '\n' ' ')
[ "$exported" = "$expected" ] && ok "exports: $exported" || bad "exports are '$exported', expected '$expected'"

decoder='^(ZSTD_decompress|ZSTD_createDCtx|ZSTD_DCtx|ZSTD_decodeFrame|ZSTD_decodeLiteralsBlock|ZSTD_decodeSeqHeaders|LzmaDec_|LzmaDecode)'
found=$(nm "$lib" 2>/dev/null | awk '{ print $NF }' | grep -E "$decoder" || true)
[ -z "$found" ] && ok "no decoder symbol in the library" || bad "decoder symbols in the library: $found"
if [ -n "$objs" ]; then
  core=$(find "$objs/CMakeFiles/osn_core.dir" -name '*.o' | sort)
  found=$(for o in $core; do nm "$o" | awk '{ print $NF }'; done | grep -E "$decoder" || true)
  [ -z "$found" ] && ok "no decoder symbol in $(echo "$core" | wc -l) object files" || bad "decoder symbols in the objects: $found"
fi
readers=$(nm "$lib" 2>/dev/null | awk '{ print $NF }' | grep -E '^(FSE_.*[Dd]ecompress|FSE_readNCount|FSE_buildDTable|HUF_readStats|HUF_readCTable|HUF_readDTable)' | sort -u | tr '\n' ' ' || true)
echo "check_binary: table readers kept by the linker (for review): ${readers:-none}"

exit $fail
