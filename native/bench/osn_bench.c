/*
 * osn_bench: compares builds of osmium_native in one process (never shipped).
 *
 *   osn_bench [--rounds N] [--lzma DICT_MIB | --zstd LEVEL] INPUT LIBRARY...
 *
 * Loads every LIBRARY by its full path, reads INPUT, and encodes it once per library per round, in an order that
 * rotates every round, on this one thread. Around each call it reads this thread's CPU cycles (Windows:
 * QueryThreadCycleTime) and CPU time (Windows: GetThreadTimes; elsewhere CLOCK_THREAD_CPUTIME_ID), so builds that
 * differ by a few percent (for example with and without /Qspectre, or MSVC against clang-cl) can be compared on a
 * shared machine where wall time cannot resolve that. Every library must produce the same bytes as the first one.
 * Prints one line per library: the median, minimum and maximum of cycles and CPU seconds, and the output size.
 */
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "osmium_native.h"

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <dlfcn.h>
#  include <time.h>
#endif

typedef int32_t (*lzma_encode_fn)(const uint8_t*, size_t, uint8_t*, size_t, size_t*, const osn_lzma_params*,
                                  osn_progress, void*, uint64_t, osn_stats*);
typedef int32_t (*zstd_encode_fn)(const uint8_t*, size_t, uint8_t*, size_t, size_t*, const osn_zstd_params*,
                                  osn_progress, void*, uint64_t, osn_stats*);
typedef int32_t (*bound_fn)(uint64_t, uint64_t*);
typedef const char* (*text_fn)(void);

typedef struct library {
    const char* path;
    lzma_encode_fn lzma;
    zstd_encode_fn zstd;
    bound_fn lzma_bound, zstd_bound;
    text_fn generation, versions;
    double* cycles;
    double* seconds;
    size_t out_len;
} library;

static void* symbol(void* module, const char* name) {
#if defined(_WIN32)
    return (void*)GetProcAddress((HMODULE)module, name);
#else
    return dlsym(module, name);
#endif
}

static int open_library(library* lib) {
    void* module;
#if defined(_WIN32)
    wchar_t wide[4096];
    if (MultiByteToWideChar(CP_UTF8, 0, lib->path, -1, wide, 4096) == 0) return 0;
    module = (void*)LoadLibraryExW(wide, NULL, LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
#else
    module = dlopen(lib->path, RTLD_NOW | RTLD_LOCAL);
#endif
    if (module == NULL) return 0;
    lib->lzma = (lzma_encode_fn)symbol(module, "osn_lzma_encode");
    lib->zstd = (zstd_encode_fn)symbol(module, "osn_zstd_encode");
    lib->lzma_bound = (bound_fn)symbol(module, "osn_lzma_bound");
    lib->zstd_bound = (bound_fn)symbol(module, "osn_zstd_bound");
    lib->generation = (text_fn)symbol(module, "osn_generation");
    lib->versions = (text_fn)symbol(module, "osn_versions");
    return lib->lzma && lib->zstd && lib->lzma_bound && lib->zstd_bound && lib->generation && lib->versions;
}

static void thread_counters(double* cycles, double* seconds) {
#if defined(_WIN32)
    ULONG64 c = 0;
    FILETIME created, exited, kernel, user;
    QueryThreadCycleTime(GetCurrentThread(), &c);
    GetThreadTimes(GetCurrentThread(), &created, &exited, &kernel, &user);
    ULARGE_INTEGER k, u;
    k.LowPart = kernel.dwLowDateTime; k.HighPart = kernel.dwHighDateTime;
    u.LowPart = user.dwLowDateTime; u.HighPart = user.dwHighDateTime;
    *cycles = (double)c;
    *seconds = (double)(k.QuadPart + u.QuadPart) / 1e7;
#else
    struct timespec t;
    clock_gettime(CLOCK_THREAD_CPUTIME_ID, &t);
    *seconds = (double)t.tv_sec + (double)t.tv_nsec / 1e9;
    *cycles = 0;   /* no portable per-thread cycle counter; CPU time stands in */
#endif
}

static int compare(const void* a, const void* b) {
    double x = *(const double*)a, y = *(const double*)b;
    return x < y ? -1 : x > y;
}

static double median(double* v, int n, double* lo, double* hi) {
    double* s = malloc((size_t)n * sizeof(double));
    if (s == NULL) exit(3);
    memcpy(s, v, (size_t)n * sizeof(double));
    qsort(s, (size_t)n, sizeof(double), compare);
    double m = n % 2 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    *lo = s[0];
    *hi = s[n - 1];
    free(s);
    return m;
}

static int usage(void) {
    fprintf(stderr, "usage: osn_bench [--rounds N] [--lzma DICT_MIB | --zstd LEVEL] INPUT LIBRARY...\n");
    return 2;
}

int main(int argc, char** argv) {
    int rounds = 5, zstd_level = 0;
    uint32_t dict_mib = 32;
    int i = 1;
    for (; i < argc && strncmp(argv[i], "--", 2) == 0; i++) {
        if (i + 1 >= argc) return usage();
        if (strcmp(argv[i], "--rounds") == 0) rounds = atoi(argv[++i]);
        else if (strcmp(argv[i], "--lzma") == 0) dict_mib = (uint32_t)atoi(argv[++i]);
        else if (strcmp(argv[i], "--zstd") == 0) zstd_level = atoi(argv[++i]);
        else return usage();
    }
    if (argc - i < 2 || rounds < 1 || rounds > 1000 || dict_mib < 1 || dict_mib > 512 || zstd_level < 0 || zstd_level > 22)
        return usage();
    const char* input = argv[i++];
    int n = argc - i;

    FILE* f = fopen(input, "rb");
    if (f == NULL) { fprintf(stderr, "cannot open %s\n", input); return 1; }
    if (fseek(f, 0, SEEK_END) != 0) return 1;
    long len = ftell(f);
    if (len <= 0 || (uint64_t)len > OSN_MAX_INPUT) { fprintf(stderr, "%s: unusable size\n", input); return 1; }
    rewind(f);
    uint8_t* src = malloc((size_t)len);
    if (src == NULL || fread(src, 1, (size_t)len, f) != (size_t)len) { fprintf(stderr, "cannot read %s\n", input); return 1; }
    fclose(f);

    library* libs = calloc((size_t)n, sizeof(library));
    if (libs == NULL) return 3;
    for (int k = 0; k < n; k++) {
        libs[k].path = argv[i + k];
        if (!open_library(&libs[k])) { fprintf(stderr, "cannot load %s\n", libs[k].path); return 1; }
        libs[k].cycles = calloc((size_t)rounds, sizeof(double));
        libs[k].seconds = calloc((size_t)rounds, sizeof(double));
        if (libs[k].cycles == NULL || libs[k].seconds == NULL) return 3;
    }

    uint64_t bound = 0;
    if ((zstd_level ? libs[0].zstd_bound : libs[0].lzma_bound)((uint64_t)len, &bound) != OSN_OK) return 1;
    uint8_t* first = malloc((size_t)bound);
    uint8_t* dst = malloc((size_t)bound);
    if (first == NULL || dst == NULL) return 3;
    size_t first_len = 0;

    osn_lzma_params lp = { sizeof(osn_lzma_params), dict_mib << 20, 3, 0, 2, 273, 1, 1, 0 };
    osn_zstd_params zp = { sizeof(osn_zstd_params), (uint32_t)zstd_level, 24, 0, 0, 0, 0, 0 };
    printf("# input %s, %ld bytes; %s; %d rounds\n", input, len,
           zstd_level ? "zstd" : "lzma bt4 fb273 lc3 lp0 pb2, one thread", rounds);
    if (zstd_level) printf("# zstd level %d, window log 24\n", zstd_level);
    else printf("# lzma dictionary %u MiB\n", dict_mib);

    for (int r = 0; r < rounds; r++) {
        for (int j = 0; j < n; j++) {
            library* lib = &libs[(j + r) % n];
            size_t out = 0;
            osn_stats st = { sizeof(osn_stats), 0, 0, 0, 0, 0, 0 };
            double c0, s0, c1, s1;
            thread_counters(&c0, &s0);
            int32_t rc = zstd_level
                ? lib->zstd(src, (size_t)len, dst, (size_t)bound, &out, &zp, NULL, NULL, UINT64_MAX, &st)
                : lib->lzma(src, (size_t)len, dst, (size_t)bound, &out, &lp, NULL, NULL, UINT64_MAX, &st);
            thread_counters(&c1, &s1);
            if (rc != OSN_OK) { fprintf(stderr, "%s: status %d\n", lib->path, rc); return 1; }
            lib->cycles[r] = c1 - c0;
            lib->seconds[r] = s1 - s0;
            lib->out_len = out;
            if (first_len == 0) { memcpy(first, dst, out); first_len = out; }
            else if (out != first_len || memcmp(first, dst, out) != 0) {
                fprintf(stderr, "%s produced other bytes than the first library\n", lib->path);
                return 1;
            }
        }
    }
    printf("library\tgeneration\tversions\tcycles_median\tcycles_min\tcycles_max\tcpu_s_median\tcpu_s_min\tcpu_s_max\tout_bytes\n");
    for (int k = 0; k < n; k++) {
        double clo, chi, slo, shi;
        double cm = median(libs[k].cycles, rounds, &clo, &chi);
        double sm = median(libs[k].seconds, rounds, &slo, &shi);
        printf("%s\t%s\t%s\t%.0f\t%.0f\t%.0f\t%.3f\t%.3f\t%.3f\t%zu\n", libs[k].path, libs[k].generation(),
               libs[k].versions(), cm, clo, chi, sm, slo, shi, libs[k].out_len);
    }
    return 0;
}
