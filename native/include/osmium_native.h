/*
 * osmium_native: native LZMA and Zstandard encoders for Osmium (MIT).
 *
 * A flat C interface with fixed-width types. Every function returns a status (OSN_OK or an OSN_E_* code) except the
 * three that return constants. Every parameter is checked before upstream code sees it; nothing is ever written
 * past dst_cap; all memory goes through a per-call allocator that stops at mem_cap. The library holds no state
 * between calls apart from one CPU-feature initialisation done once; every function may run on many threads at once.
 *
 * Changing any struct, constant or behaviour here is a new ABI number and a new encoder generation (generation.txt).
 */
#ifndef OSMIUM_NATIVE_H
#define OSMIUM_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  define OSN_API              /* exported through osmium_native.def */
#elif defined(__GNUC__)
#  define OSN_API __attribute__((visibility("default")))
#else
#  define OSN_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes. */
#define OSN_OK              0
#define OSN_E_PARAM         1  /* an argument is outside the documented range; nothing was done */
#define OSN_E_OUTPUT_FULL   2  /* the output did not fit in dst_cap bytes (nothing written past dst_cap) */
#define OSN_E_NOMEM         3  /* the system refused an allocation */
#define OSN_E_MEMCAP        4  /* an allocation would have passed mem_cap */
#define OSN_E_ABORTED       5  /* the progress callback asked to stop */
#define OSN_E_INTERNAL      6  /* upstream code failed; osn_stats.upstream_code has its code */

/* Limits shared by both encoders. */
#define OSN_MAX_INPUT       ((uint64_t)1 << 31)   /* largest src_len accepted (2 GiB) */

/* LZMA (raw stream: no header, no end marker; the decoder knows the parameters and the output length). */
#define OSN_LZMA_DICT_MIN   ((uint32_t)1 << 16)
#define OSN_LZMA_DICT_MAX   ((uint32_t)1 << 29)
#define OSN_LZMA_FLAG_TEST_THREAD_ERROR  1u  /* tests only: act as if the helper thread could not start */

typedef struct osn_lzma_params {
    uint32_t size;          /* sizeof(osn_lzma_params) */
    uint32_t dict_size;     /* the effective dictionary, OSN_LZMA_DICT_MIN..OSN_LZMA_DICT_MAX */
    uint32_t lc;            /* literal context bits, 0..8 */
    uint32_t lp;            /* literal position bits, 0..4 */
    uint32_t pb;            /* position bits, 0..4 */
    uint32_t fb;            /* fast bytes, 5..273 */
    uint32_t match_finder;  /* 0 = bt2, 1 = bt4 */
    uint32_t threads;       /* 1, or 2 for the match finder's helper thread (the output is the same) */
    uint32_t flags;         /* 0, or OSN_LZMA_FLAG_* */
} osn_lzma_params;

/* Zstandard (one frame with the content size, no checksum, no dictionary). */
typedef struct osn_zstd_params {
    uint32_t size;          /* sizeof(osn_zstd_params) */
    uint32_t level;         /* 1..22 */
    uint32_t window_log;    /* the effective window, 10..31 */
    uint32_t ldm;           /* long-distance matching: 0 = as the level decides, 1 = on */
    uint32_t ldm_min_match; /* 0 = default, or 4..4096; must be 0 when ldm is 0 */
    uint32_t strategy;      /* 0 = as the level decides, or 1..9 */
    uint32_t chunk_size;    /* 0 = default (1 MiB; 128 KiB at levels 20 and above), or a power of two from 64 KiB to
                               16 MiB; the input is fed in pieces of this size, with a progress call between them.
                               The output does not depend on it. */
    uint32_t flags;         /* 0 */
} osn_zstd_params;

typedef struct osn_stats {
    uint32_t size;              /* sizeof(osn_stats), set by the caller */
    int32_t  upstream_code;     /* with OSN_E_INTERNAL: the LZMA SDK's SRes or Zstandard's ZSTD_ErrorCode */
    uint64_t peak_bytes;        /* most memory allocated at one time during the call */
    uint64_t allocations;       /* number of allocations */
    uint64_t live_bytes_after;  /* memory still allocated when the call returned (0 unless something leaked) */
    uint32_t threads_used;      /* LZMA: 1 or 2 */
    uint32_t thread_retries;    /* LZMA: the helper thread could not start, so the block was encoded with one */
} osn_stats;

/* Called with the number of input bytes consumed so far; return non-zero to stop (the call then returns
 * OSN_E_ABORTED). Called on the thread that called the encoder. May be NULL. */
typedef int32_t (*osn_progress)(void* user, uint64_t input_done);

OSN_API int32_t     osn_abi(void);
OSN_API const char* osn_generation(void);   /* e.g. "native-1" */
OSN_API const char* osn_versions(void);     /* e.g. "lzma-sdk 26.06; zstd 1.5.7" */

/* Largest output of an n-byte input: LZMA n + n/3 + 128, Zstandard ZSTD_compressBound(n). OSN_E_PARAM when n is
 * above OSN_MAX_INPUT or bound is NULL. */
OSN_API int32_t osn_lzma_bound(uint64_t n, uint64_t* bound);
OSN_API int32_t osn_zstd_bound(uint64_t n, uint64_t* bound);

/* Encode src[0..src_len) into dst[0..dst_cap). On OSN_OK, *dst_len is the output length; otherwise it is 0.
 * src may be NULL only when src_len is 0, dst only when dst_cap is 0. mem_cap must be at least 1. stats may be NULL;
 * when given, its size field must be sizeof(osn_stats). */
OSN_API int32_t osn_lzma_encode(const uint8_t* src, size_t src_len, uint8_t* dst, size_t dst_cap, size_t* dst_len,
                                const osn_lzma_params* p, osn_progress cb, void* user, uint64_t mem_cap,
                                osn_stats* stats);
OSN_API int32_t osn_zstd_encode(const uint8_t* src, size_t src_len, uint8_t* dst, size_t dst_cap, size_t* dst_len,
                                const osn_zstd_params* p, osn_progress cb, void* user, uint64_t mem_cap,
                                osn_stats* stats);

#ifdef __cplusplus
}
#endif

#endif /* OSMIUM_NATIVE_H */
