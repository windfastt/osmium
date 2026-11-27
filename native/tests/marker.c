/*
 * Test-only "planted" library (never shipped): it has every export of osmium_native and reports the right ABI,
 * generation and versions, but its start-up code (DllMain on Windows, a constructor on Linux) writes the file named
 * by the environment variable OSMIUM_MARKER_FILE. The loader tests put it where a planted copy could be found and
 * check that the file never appears: Osmium must refuse it before any of its code runs.
 */
#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif
#include <stdio.h>
#include <stdlib.h>

#include "osmium_native.h"
#include "osn_generation.h"

static void write_marker(void)
{
    const char* path = getenv("OSMIUM_MARKER_FILE");
    FILE* f;
    if (path == NULL || path[0] == '\0') return;
    f = fopen(path, "wb");
    if (f != NULL)
    {
        fputs("planted library code ran\n", f);
        fclose(f);
    }
}

#if defined(_WIN32)
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) write_marker();
    return TRUE;
}
#else
__attribute__((constructor)) static void marker_start(void) { write_marker(); }
#endif

int32_t osn_abi(void) { return OSN_ABI; }
const char* osn_generation(void) { return OSN_GENERATION; }
const char* osn_versions(void) { return OSN_EXPECT_VERSIONS; }
int32_t osn_lzma_bound(uint64_t n, uint64_t* bound) { if (bound) *bound = n + n / 3 + 128; return OSN_OK; }
int32_t osn_zstd_bound(uint64_t n, uint64_t* bound) { if (bound) *bound = n + n / 128 + 1024; return OSN_OK; }

int32_t osn_lzma_encode(const uint8_t* src, size_t src_len, uint8_t* dst, size_t dst_cap, size_t* dst_len,
                        const osn_lzma_params* p, osn_progress cb, void* user, uint64_t mem_cap, osn_stats* stats)
{
    (void)src; (void)src_len; (void)dst; (void)dst_cap; (void)p; (void)cb; (void)user; (void)mem_cap; (void)stats;
    write_marker();
    if (dst_len) *dst_len = 0;
    return OSN_E_INTERNAL;
}

int32_t osn_zstd_encode(const uint8_t* src, size_t src_len, uint8_t* dst, size_t dst_cap, size_t* dst_len,
                        const osn_zstd_params* p, osn_progress cb, void* user, uint64_t mem_cap, osn_stats* stats)
{
    (void)src; (void)src_len; (void)dst; (void)dst_cap; (void)p; (void)cb; (void)user; (void)mem_cap; (void)stats;
    write_marker();
    if (dst_len) *dst_len = 0;
    return OSN_E_INTERNAL;
}
