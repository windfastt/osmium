/*
 * Self-test of osmium_native (never shipped). Linked with the library's own objects and with the upstream decoders,
 * which exist only in test programs.
 *
 *   osn_selftest                 every check, one thread
 *   osn_selftest --parallel N    also N threads encoding every input at once, LZMA with the helper thread, each
 *                                result compared with the one-thread reference (run under the thread sanitizer)
 *   osn_selftest --bound-large   also the LZMA bound on 64 MiB of random bytes
 * Exit status 0 when every check passed.
 */
#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#else
#  include <pthread.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "osmium_native.h"
#include "osn_generation.h"
#include "LzmaDec.h"
#define ZSTD_STATIC_LINKING_ONLY
#include "zstd.h"

static int g_failures;
static int g_checks;

#define CHECK(cond, ...) do { g_checks++; if (!(cond)) { g_failures++; \
    fprintf(stderr, "FAIL line %d: ", __LINE__); fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); } } while (0)

/* ------------------------------------------------------------------------------------------------ inputs */

static uint64_t g_rng;
static uint64_t next_rand(void)
{
    g_rng ^= g_rng >> 12; g_rng ^= g_rng << 25; g_rng ^= g_rng >> 27;
    return g_rng * 2685821657736338717ull;
}

static const char* const WORDS[] = { "the", "archive", "block", "of", "compression", "window", "match", "and",
    "literal", "Osmium", "data", "encoder", "a", "to", "is", "file", "folder", "level", "dictionary", "stream" };

typedef struct input { const char* name; uint8_t* data; size_t len; } input;

static uint8_t* make(size_t len, int kind, uint64_t seed)
{
    uint8_t* b = (uint8_t*)malloc(len ? len : 1);
    size_t i = 0;
    g_rng = seed * 0x9E3779B97F4A7C15ull + 1;
    while (i < len)
    {
        int k = kind;
        if (kind == 3) k = (int)((i >> 12) % 4);   /* mixed: 4 KiB pieces of each */
        if (k == 0)
        {   /* text */
            const char* w = WORDS[next_rand() % (sizeof WORDS / sizeof WORDS[0])];
            while (*w && i < len) b[i++] = (uint8_t)*w++;
            if (i < len) b[i++] = (next_rand() % 13 == 0) ? '\n' : ' ';
        }
        else if (k == 1)
        {   /* 32-byte records */
            uint32_t n = (uint32_t)(i / 32);
            uint8_t rec[32];
            size_t j;
            memset(rec, 0, sizeof rec);
            memcpy(rec, &n, 4);
            rec[4] = (uint8_t)(n * 7); rec[8] = (uint8_t)(n % 3); rec[16] = 0x42;
            for (j = 0; j < 32 && i < len; j++) { b[i] = rec[i % 32]; i++; }
        }
        else if (k == 2)
            b[i++] = (uint8_t)next_rand();   /* incompressible */
        else
            b[i++] = 0;
    }
    return b;
}

/* ------------------------------------------------------------------------------------------------ helpers */

static void* dec_alloc(ISzAllocPtr p, size_t size) { (void)p; return malloc(size); }
static void dec_free(ISzAllocPtr p, void* a) { (void)p; free(a); }
static const ISzAlloc g_dec_alloc = { dec_alloc, dec_free };

static osn_lzma_params lzma_params(uint32_t dict, uint32_t lc, uint32_t lp, uint32_t pb, uint32_t fb, uint32_t mf,
                                   uint32_t threads)
{
    osn_lzma_params p;
    memset(&p, 0, sizeof p);
    p.size = sizeof p; p.dict_size = dict; p.lc = lc; p.lp = lp; p.pb = pb; p.fb = fb; p.match_finder = mf;
    p.threads = threads;
    return p;
}

static osn_zstd_params zstd_params(uint32_t level, uint32_t wlog, uint32_t ldm, uint32_t ldmmin, uint32_t strategy)
{
    osn_zstd_params p;
    memset(&p, 0, sizeof p);
    p.size = sizeof p; p.level = level; p.window_log = wlog; p.ldm = ldm; p.ldm_min_match = ldmmin;
    p.strategy = strategy;
    return p;
}

static osn_stats new_stats(void) { osn_stats s; memset(&s, 0, sizeof s); s.size = sizeof s; return s; }

#define BIG_CAP ((uint64_t)8 << 30)
#define GUARD 64

/* Encodes into a buffer of the bound plus guard bytes and checks the guard. Returns a malloc'd copy or NULL. */
static uint8_t* lzma_enc(const input* in, const osn_lzma_params* p, size_t* out_len, osn_stats* st)
{
    uint64_t bound = 0;
    uint8_t* out;
    int32_t r;
    size_t i;
    CHECK(osn_lzma_bound(in->len, &bound) == OSN_OK, "lzma bound of %zu", in->len);
    out = (uint8_t*)malloc((size_t)bound + GUARD);
    memset(out, 0xA5, (size_t)bound + GUARD);
    r = osn_lzma_encode(in->data, in->len, out, (size_t)bound, out_len, p, NULL, NULL, BIG_CAP, st);
    CHECK(r == OSN_OK, "lzma encode %s: status %d", in->name, r);
    for (i = (size_t)bound; i < (size_t)bound + GUARD; i++)
        if (out[i] != 0xA5) { CHECK(0, "lzma wrote past the bound (%s)", in->name); break; }
    CHECK(*out_len <= bound, "lzma output %zu above bound %llu", *out_len, (unsigned long long)bound);
    if (r != OSN_OK) { free(out); return NULL; }
    return out;
}

static void lzma_check_decode(const input* in, const osn_lzma_params* p, const uint8_t* enc, size_t enc_len)
{
    uint8_t props[5];
    uint8_t* back = (uint8_t*)malloc(in->len ? in->len : 1);
    SizeT dl = in->len, sl = enc_len;
    ELzmaStatus status;
    SRes res;
    props[0] = (uint8_t)((p->pb * 5 + p->lp) * 9 + p->lc);
    props[1] = (uint8_t)p->dict_size; props[2] = (uint8_t)(p->dict_size >> 8);
    props[3] = (uint8_t)(p->dict_size >> 16); props[4] = (uint8_t)(p->dict_size >> 24);
    res = LzmaDecode(back, &dl, enc, &sl, props, 5, LZMA_FINISH_END, &status, &g_dec_alloc);
    CHECK(res == SZ_OK && dl == in->len && memcmp(back, in->data, in->len) == 0,
          "LzmaDec round trip of %s (res %d, %zu of %zu bytes)", in->name, res, (size_t)dl, in->len);
    CHECK(status == LZMA_STATUS_MAYBE_FINISHED_WITHOUT_MARK || status == LZMA_STATUS_FINISHED_WITH_MARK,
          "LzmaDec status %d for %s", (int)status, in->name);
    free(back);
}

static uint8_t* zstd_enc(const input* in, const osn_zstd_params* p, size_t* out_len, osn_stats* st)
{
    uint64_t bound = 0;
    uint8_t* out;
    int32_t r;
    size_t i;
    CHECK(osn_zstd_bound(in->len, &bound) == OSN_OK, "zstd bound of %zu", in->len);
    out = (uint8_t*)malloc((size_t)bound + GUARD);
    memset(out, 0x5A, (size_t)bound + GUARD);
    r = osn_zstd_encode(in->data, in->len, out, (size_t)bound, out_len, p, NULL, NULL, BIG_CAP, st);
    CHECK(r == OSN_OK, "zstd encode %s level %u: status %d", in->name, p->level, r);
    for (i = (size_t)bound; i < (size_t)bound + GUARD; i++)
        if (out[i] != 0x5A) { CHECK(0, "zstd wrote past the bound (%s)", in->name); break; }
    if (r != OSN_OK) { free(out); return NULL; }
    return out;
}

static void zstd_check_decode(const input* in, const uint8_t* enc, size_t enc_len)
{
    uint8_t* back = (uint8_t*)malloc(in->len ? in->len : 1);
    size_t n = ZSTD_decompress(back, in->len, enc, enc_len);
    unsigned long long cs = ZSTD_getFrameContentSize(enc, enc_len);
    CHECK(!ZSTD_isError(n) && n == in->len && memcmp(back, in->data, in->len) == 0,
          "ZSTD_decompress round trip of %s (%s)", in->name, ZSTD_isError(n) ? ZSTD_getErrorName(n) : "size");
    CHECK(cs == (unsigned long long)in->len, "frame content size of %s is %llu", in->name, cs);
    free(back);
}

/* ------------------------------------------------------------------------------------------------ checks */

static input g_inputs[9];
static size_t g_ninputs;

static void add_input(const char* name, size_t len, int kind, uint64_t seed)
{
    g_inputs[g_ninputs].name = name;
    g_inputs[g_ninputs].len = len;
    g_inputs[g_ninputs].data = make(len, kind, seed);
    g_ninputs++;
}

static void check_identity(void)
{
    CHECK(osn_abi() == OSN_ABI, "abi %d", osn_abi());
    CHECK(strcmp(osn_generation(), OSN_GENERATION) == 0, "generation %s", osn_generation());
    CHECK(strcmp(osn_versions(), OSN_EXPECT_VERSIONS) == 0, "versions '%s'", osn_versions());
}

static void check_bounds(void)
{
    uint64_t b = 1;
    CHECK(osn_lzma_bound(0, &b) == OSN_OK && b == 128, "lzma bound(0) = %llu", (unsigned long long)b);
    CHECK(osn_lzma_bound(1, &b) == OSN_OK && b == 129, "lzma bound(1)");
    CHECK(osn_lzma_bound(3, &b) == OSN_OK && b == 132, "lzma bound(3)");
    CHECK(osn_lzma_bound(OSN_MAX_INPUT, &b) == OSN_OK && b == OSN_MAX_INPUT + OSN_MAX_INPUT / 3 + 128, "lzma bound(max)");
    CHECK(osn_lzma_bound(OSN_MAX_INPUT + 1, &b) == OSN_E_PARAM && b == 0, "lzma bound above max");
    CHECK(osn_lzma_bound(UINT64_MAX, &b) == OSN_E_PARAM, "lzma bound(UINT64_MAX)");
    CHECK(osn_lzma_bound(1, NULL) == OSN_E_PARAM, "lzma bound NULL");
    CHECK(osn_zstd_bound(0, &b) == OSN_OK && b == ZSTD_compressBound(0), "zstd bound(0)");
    CHECK(osn_zstd_bound(1 << 20, &b) == OSN_OK && b == ZSTD_compressBound(1 << 20), "zstd bound(1 MiB)");
    CHECK(osn_zstd_bound(OSN_MAX_INPUT + 1, &b) == OSN_E_PARAM, "zstd bound above max");
    CHECK(osn_zstd_bound(UINT64_MAX, &b) == OSN_E_PARAM, "zstd bound(UINT64_MAX)");
}

static int32_t lzma_try(osn_lzma_params p)
{
    uint8_t src[16] = "abcabcabcabcabc", dst[512];
    size_t n;
    return osn_lzma_encode(src, sizeof src, dst, sizeof dst, &n, &p, NULL, NULL, BIG_CAP, NULL);
}

static int32_t zstd_try(osn_zstd_params p)
{
    uint8_t src[16] = "abcabcabcabcabc", dst[512];
    size_t n;
    return osn_zstd_encode(src, sizeof src, dst, sizeof dst, &n, &p, NULL, NULL, BIG_CAP, NULL);
}

static void check_params(void)
{
    osn_lzma_params l = lzma_params(1 << 16, 3, 0, 2, 32, 1, 1), x;
    osn_zstd_params z = zstd_params(3, 10, 0, 0, 0), y;
    uint8_t src[4] = { 1, 2, 3, 4 }, dst[256];
    size_t n = 7;
    osn_stats st = new_stats();

    CHECK(lzma_try(l) == OSN_OK, "lzma base params");
#define L_EDGE(field, bad, good) do { x = l; x.field = (bad); CHECK(lzma_try(x) == OSN_E_PARAM, "lzma " #field "=" #bad); \
                                      x.field = (good); CHECK(lzma_try(x) == OSN_OK, "lzma " #field "=" #good); } while (0)
    L_EDGE(dict_size, (1u << 16) - 1, 1u << 16);
    /* A 512 MiB dictionary allocates its whole 4 GiB match finder, even for 16 bytes: accepted means anything but
     * OSN_E_PARAM (a system without 4 GiB to spare answers OSN_E_NOMEM). */
    x = l; x.dict_size = (1u << 29) + 1; CHECK(lzma_try(x) == OSN_E_PARAM, "lzma dict_size above the maximum");
    x.dict_size = 1u << 29; CHECK(lzma_try(x) != OSN_E_PARAM, "lzma dict_size at the maximum");
    L_EDGE(lc, 9, 8);
    L_EDGE(lp, 5, 4);
    L_EDGE(pb, 5, 4);
    L_EDGE(fb, 4, 5);
    L_EDGE(fb, 274, 273);
    L_EDGE(match_finder, 2, 0);
    L_EDGE(threads, 0, 1);
    L_EDGE(threads, 3, 2);
    L_EDGE(flags, 2, 0);
    L_EDGE(size, sizeof(osn_lzma_params) - 4, sizeof(osn_lzma_params));
#undef L_EDGE

    CHECK(zstd_try(z) == OSN_OK, "zstd base params");
#define Z_EDGE(field, bad, good) do { y = z; y.field = (bad); CHECK(zstd_try(y) == OSN_E_PARAM, "zstd " #field "=" #bad); \
                                      y.field = (good); CHECK(zstd_try(y) == OSN_OK, "zstd " #field "=" #good); } while (0)
    Z_EDGE(level, 0, 1);
    Z_EDGE(level, 23, 22);
    Z_EDGE(window_log, 9, 10);
    Z_EDGE(window_log, 32, 31);
    Z_EDGE(ldm, 2, 1);
    Z_EDGE(strategy, 10, 9);
    Z_EDGE(chunk_size, (64u << 10) - 1, 64u << 10);
    Z_EDGE(chunk_size, 32u << 20, 16u << 20);
    Z_EDGE(chunk_size, 3u << 16, 1u << 17);
    Z_EDGE(flags, 1, 0);
    Z_EDGE(size, sizeof(osn_zstd_params) + 4, sizeof(osn_zstd_params));
#undef Z_EDGE
    y = z; y.ldm_min_match = 64; CHECK(zstd_try(y) == OSN_E_PARAM, "zstd ldm_min_match without ldm");
    y.ldm = 1; CHECK(zstd_try(y) == OSN_OK, "zstd ldm_min_match 64 with ldm");
    y.ldm_min_match = 3; CHECK(zstd_try(y) == OSN_E_PARAM, "zstd ldm_min_match 3");
    y.ldm_min_match = 4; CHECK(zstd_try(y) == OSN_OK, "zstd ldm_min_match 4");
    y.ldm_min_match = 4096; CHECK(zstd_try(y) == OSN_OK, "zstd ldm_min_match 4096");
    y.ldm_min_match = 4097; CHECK(zstd_try(y) == OSN_E_PARAM, "zstd ldm_min_match 4097");

    /* Pointers and lengths. */
    CHECK(osn_lzma_encode(NULL, 4, dst, sizeof dst, &n, &l, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM, "lzma NULL src");
    CHECK(osn_lzma_encode(src, 4, NULL, 16, &n, &l, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM, "lzma NULL dst");
    CHECK(osn_lzma_encode(src, 4, dst, sizeof dst, NULL, &l, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM, "lzma NULL dst_len");
    CHECK(osn_lzma_encode(src, 4, dst, sizeof dst, &n, NULL, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM, "lzma NULL params");
    CHECK(osn_lzma_encode(src, 4, dst, sizeof dst, &n, &l, NULL, NULL, 0, NULL) == OSN_E_PARAM, "lzma mem_cap 0");
    CHECK(osn_lzma_encode(src, (size_t)OSN_MAX_INPUT + 1, dst, sizeof dst, &n, &l, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM,
          "lzma src_len above max");
    st.size = 8;
    CHECK(osn_lzma_encode(src, 4, dst, sizeof dst, &n, &l, NULL, NULL, BIG_CAP, &st) == OSN_E_PARAM, "lzma stats size");
    CHECK(osn_zstd_encode(src, 4, dst, sizeof dst, &n, &z, NULL, NULL, BIG_CAP, &st) == OSN_E_PARAM, "zstd stats size");
    CHECK(osn_zstd_encode(NULL, 4, dst, sizeof dst, &n, &z, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM, "zstd NULL src");
    CHECK(osn_zstd_encode(src, 4, NULL, 16, &n, &z, NULL, NULL, BIG_CAP, NULL) == OSN_E_PARAM, "zstd NULL dst");
    CHECK(osn_zstd_encode(src, 4, dst, sizeof dst, &n, &z, NULL, NULL, 0, NULL) == OSN_E_PARAM, "zstd mem_cap 0");
    n = 7;
    CHECK(osn_lzma_encode(NULL, 0, NULL, 0, &n, &l, NULL, NULL, BIG_CAP, NULL) == OSN_E_OUTPUT_FULL && n == 0,
          "lzma empty input into no output");
    CHECK(osn_zstd_encode(NULL, 0, NULL, 0, &n, &z, NULL, NULL, BIG_CAP, NULL) == OSN_E_OUTPUT_FULL && n == 0,
          "zstd empty input into no output");
}

static void check_zstd_is_single_threaded(void)
{
    ZSTD_CCtx* c = ZSTD_createCCtx();
    size_t r = ZSTD_CCtx_setParameter(c, ZSTD_c_nbWorkers, 1);
    CHECK(ZSTD_isError(r), "the compiled Zstandard accepted nbWorkers=1");
    ZSTD_freeCCtx(c);
}

static void check_round_trips(void)
{
    static const uint32_t LC[][3] = { { 0, 0, 0 }, { 3, 0, 2 }, { 4, 2, 2 }, { 8, 4, 4 } };
    static const uint32_t LEVELS[] = { 1, 3, 9, 19, 22 };
    size_t i, j, k;
    for (i = 0; i < g_ninputs; i++)
    {
        const input* in = &g_inputs[i];
        for (j = 0; j < sizeof LC / sizeof LC[0]; j++)
            for (k = 0; k < 2; k++)
            {
                osn_lzma_params p1 = lzma_params(1 << 20, LC[j][0], LC[j][1], LC[j][2], k ? 273 : 32, (uint32_t)k, 1);
                osn_lzma_params p2 = p1;
                size_t len1 = 0, len2 = 0;
                osn_stats s1 = new_stats(), s2 = new_stats();
                uint8_t *e1, *e2;
                p2.threads = 2;
                e1 = lzma_enc(in, &p1, &len1, &s1);
                e2 = lzma_enc(in, &p2, &len2, &s2);
                if (e1 && e2)
                {
                    CHECK(len1 == len2 && memcmp(e1, e2, len1) == 0, "lzma 1 and 2 threads differ (%s)", in->name);
                    CHECK(s1.threads_used == 1 && s2.threads_used == 2 && s2.thread_retries == 0, "lzma threads used");
                    CHECK(s1.live_bytes_after == 0 && s2.live_bytes_after == 0, "lzma leaked memory");
                    lzma_check_decode(in, &p1, e1, len1);
                }
                free(e1); free(e2);
            }
        for (j = 0; j < sizeof LEVELS / sizeof LEVELS[0]; j++)
        {
            uint8_t* ref = NULL;
            size_t ref_len = 0;
            /* The default, every allowed size from the smallest to more than the input: the output must not change. */
            static const uint32_t CHUNKS[] = { 0, 64u << 10, 128u << 10, 1u << 20, 4u << 20, 16u << 20 };
            for (k = 0; k < sizeof CHUNKS / sizeof CHUNKS[0]; k++)
            {
                osn_zstd_params p = zstd_params(LEVELS[j], 20, LEVELS[j] >= 19, 0, 0);
                size_t n = 0;
                osn_stats s = new_stats();
                uint8_t* e;
                p.chunk_size = CHUNKS[k];
                e = zstd_enc(in, &p, &n, &s);
                if (!e) continue;
                CHECK(s.live_bytes_after == 0, "zstd leaked memory");
                if (ref == NULL) { ref = e; ref_len = n; zstd_check_decode(in, e, n); continue; }
                CHECK(n == ref_len && memcmp(e, ref, n) == 0, "zstd level %u differs at chunk %u (%s)",
                      LEVELS[j], CHUNKS[k], in->name);
                free(e);
            }
            free(ref);
        }
    }
}

static void check_bound_on_random(int large)
{
    static const size_t SIZES[] = { 0, 1, 2, 3, 15, 16, 17, 255, 256, 4095, 4096, 65535, 65536, 65537, 1 << 20,
                                    (1 << 20) + 17, 4 << 20 };
    static const uint32_t LC[][2] = { { 8, 4 }, { 0, 0 } };
    size_t i, j;
    for (i = 0; i < sizeof SIZES / sizeof SIZES[0] + (large ? 1 : 0); i++)
    {
        input in;
        in.name = "random";
        in.len = i < sizeof SIZES / sizeof SIZES[0] ? SIZES[i] : (size_t)64 << 20;
        in.data = make(in.len, 2, 1000 + i);
        for (j = 0; j < 2; j++)
        {
            osn_lzma_params p = lzma_params(1 << 22, LC[j][0], LC[j][1], 2, 273, 1, 1);
            size_t n = 0;
            uint8_t* e = lzma_enc(&in, &p, &n, NULL);
            free(e);
        }
        {
            osn_zstd_params zp = zstd_params(19, 22, 0, 0, 0);
            size_t n = 0;
            uint8_t* e = zstd_enc(&in, &zp, &n, NULL);
            free(e);
        }
        free(in.data);
    }
}

static void check_output_full(void)
{
    const input* in = &g_inputs[4];   /* 256 KiB of text */
    osn_lzma_params lp = lzma_params(1 << 20, 3, 0, 2, 64, 1, 1);
    osn_zstd_params zp = zstd_params(9, 20, 0, 0, 0);
    size_t full = 0, n = 0, cap, i;
    uint8_t* e = lzma_enc(in, &lp, &full, NULL);
    uint8_t* buf = (uint8_t*)malloc(full + GUARD);
    int t;
    for (t = 0; t < 2; t++)
    {
        lp.threads = (uint32_t)(t + 1);
        for (cap = 0; cap < full; cap = cap == 0 ? 1 : cap * 3 + 7)
        {
            memset(buf, 0xC3, full + GUARD);
            CHECK(osn_lzma_encode(in->data, in->len, buf, cap, &n, &lp, NULL, NULL, BIG_CAP, NULL) == OSN_E_OUTPUT_FULL
                  && n == 0, "lzma into %zu of %zu bytes", cap, full);
            for (i = cap; i < full + GUARD; i++) if (buf[i] != 0xC3) { CHECK(0, "lzma wrote past dst_cap %zu", cap); break; }
        }
        memset(buf, 0xC3, full + GUARD);
        CHECK(osn_lzma_encode(in->data, in->len, buf, full - 1, &n, &lp, NULL, NULL, BIG_CAP, NULL) == OSN_E_OUTPUT_FULL,
              "lzma into one byte less");
        CHECK(buf[full - 1] == 0xC3, "lzma wrote the last byte past dst_cap");
    }
    free(e); free(buf);
    e = zstd_enc(in, &zp, &full, NULL);
    buf = (uint8_t*)malloc(full + GUARD);
    for (cap = 0; cap < full; cap = cap == 0 ? 1 : cap * 3 + 7)
    {
        memset(buf, 0xC3, full + GUARD);
        CHECK(osn_zstd_encode(in->data, in->len, buf, cap, &n, &zp, NULL, NULL, BIG_CAP, NULL) == OSN_E_OUTPUT_FULL && n == 0,
              "zstd into %zu of %zu bytes", cap, full);
        for (i = cap; i < full + GUARD; i++) if (buf[i] != 0xC3) { CHECK(0, "zstd wrote past dst_cap %zu", cap); break; }
    }
    memset(buf, 0xC3, full + GUARD);
    CHECK(osn_zstd_encode(in->data, in->len, buf, full - 1, &n, &zp, NULL, NULL, BIG_CAP, NULL) == OSN_E_OUTPUT_FULL,
          "zstd into one byte less");
    CHECK(buf[full - 1] == 0xC3, "zstd wrote the last byte past dst_cap");
    free(e); free(buf);
}

static void check_memory_cap(void)
{
    const input* in = &g_inputs[6];   /* 1 MiB + 17 mixed */
    osn_lzma_params lp = lzma_params(1 << 20, 3, 0, 2, 273, 1, 1);
    osn_zstd_params zp = zstd_params(19, 20, 0, 0, 0);
    uint64_t bound;
    uint8_t* out;
    size_t n;
    uint64_t caps[] = { 1, 4096, 1 << 20, 4 << 20 };
    size_t i;
    int t;
    osn_lzma_bound(in->len, &bound);
    out = (uint8_t*)malloc((size_t)bound);
    for (t = 1; t <= 2; t++)
        for (i = 0; i < sizeof caps / sizeof caps[0]; i++)
        {
            osn_stats s = new_stats();
            lp.threads = (uint32_t)t;
            CHECK(osn_lzma_encode(in->data, in->len, out, (size_t)bound, &n, &lp, NULL, NULL, caps[i], &s) == OSN_E_MEMCAP,
                  "lzma with mem_cap %llu", (unsigned long long)caps[i]);
            CHECK(s.live_bytes_after == 0 && s.peak_bytes <= caps[i], "lzma leaked or passed the cap");
        }
    {
        osn_stats s = new_stats();
        CHECK(osn_lzma_encode(in->data, in->len, out, (size_t)bound, &n, &lp, NULL, NULL, BIG_CAP, &s) == OSN_OK, "lzma uncapped");
        printf("  lzma dict 1 MiB bt4 on 1 MiB: peak %llu bytes in %llu allocations\n",
               (unsigned long long)s.peak_bytes, (unsigned long long)s.allocations);
        CHECK(osn_lzma_encode(in->data, in->len, out, (size_t)bound, &n, &lp, NULL, NULL, s.peak_bytes, NULL) == OSN_OK,
              "lzma with mem_cap equal to its peak");
        CHECK(osn_lzma_encode(in->data, in->len, out, (size_t)bound, &n, &lp, NULL, NULL, s.peak_bytes - 1, NULL) == OSN_E_MEMCAP,
              "lzma with mem_cap one byte below its peak");
    }
    free(out);
    osn_zstd_bound(in->len, &bound);
    out = (uint8_t*)malloc((size_t)bound);
    for (i = 0; i < sizeof caps / sizeof caps[0]; i++)
    {
        osn_stats s = new_stats();
        CHECK(osn_zstd_encode(in->data, in->len, out, (size_t)bound, &n, &zp, NULL, NULL, caps[i], &s) == OSN_E_MEMCAP,
              "zstd with mem_cap %llu", (unsigned long long)caps[i]);
        CHECK(s.live_bytes_after == 0 && s.peak_bytes <= caps[i], "zstd leaked or passed the cap");
    }
    {
        osn_stats s = new_stats();
        CHECK(osn_zstd_encode(in->data, in->len, out, (size_t)bound, &n, &zp, NULL, NULL, BIG_CAP, &s) == OSN_OK, "zstd uncapped");
        printf("  zstd 19 wlog 20 on 1 MiB: peak %llu bytes in %llu allocations\n",
               (unsigned long long)s.peak_bytes, (unsigned long long)s.allocations);
        CHECK(osn_zstd_encode(in->data, in->len, out, (size_t)bound, &n, &zp, NULL, NULL, s.peak_bytes, NULL) == OSN_OK,
              "zstd with mem_cap equal to its peak");
        CHECK(osn_zstd_encode(in->data, in->len, out, (size_t)bound, &n, &zp, NULL, NULL, s.peak_bytes - 1, NULL) == OSN_E_MEMCAP,
              "zstd with mem_cap one byte below its peak");
    }
    free(out);
}

typedef struct progress_state { uint64_t last; uint64_t calls; uint64_t stop_after; int monotonic; } progress_state;

static int32_t on_progress(void* user, uint64_t done)
{
    progress_state* s = (progress_state*)user;
    if (done < s->last) s->monotonic = 0;
    s->last = done;
    s->calls++;
    return s->stop_after != 0 && s->calls >= s->stop_after;
}

static void check_progress(void)
{
    const input* in = &g_inputs[7];   /* 2 MiB text */
    osn_lzma_params lp = lzma_params(1 << 21, 3, 0, 2, 32, 1, 1);
    osn_zstd_params zp = zstd_params(3, 21, 0, 0, 0);
    uint64_t bound;
    uint8_t* out;
    size_t n;
    int t;
    osn_lzma_bound(in->len, &bound);
    out = (uint8_t*)malloc((size_t)bound);
    for (t = 1; t <= 2; t++)
    {
        progress_state s = { 0, 0, 0, 1 };
        osn_stats st = new_stats();
        lp.threads = (uint32_t)t;
        CHECK(osn_lzma_encode(in->data, in->len, out, (size_t)bound, &n, &lp, on_progress, &s, BIG_CAP, NULL) == OSN_OK,
              "lzma with progress");
        CHECK(s.monotonic && s.calls >= 4 && s.last <= in->len, "lzma progress: %llu calls, last %llu",
              (unsigned long long)s.calls, (unsigned long long)s.last);
        s.last = 0; s.calls = 0; s.stop_after = 2;
        CHECK(osn_lzma_encode(in->data, in->len, out, (size_t)bound, &n, &lp, on_progress, &s, BIG_CAP, &st) == OSN_E_ABORTED
              && n == 0, "lzma stopped by progress");
        CHECK(st.live_bytes_after == 0, "lzma stopped: leaked");
    }
    free(out);
    osn_zstd_bound(in->len, &bound);
    out = (uint8_t*)malloc((size_t)bound);
    {
        progress_state s = { 0, 0, 0, 1 };
        osn_stats st = new_stats();
        CHECK(osn_zstd_encode(in->data, in->len, out, (size_t)bound, &n, &zp, on_progress, &s, BIG_CAP, NULL) == OSN_OK,
              "zstd with progress");
        CHECK(s.monotonic && s.calls == 1 && s.last == (1u << 20), "zstd progress: %llu calls, last %llu",
              (unsigned long long)s.calls, (unsigned long long)s.last);
        s.last = 0; s.calls = 0; s.stop_after = 1;
        CHECK(osn_zstd_encode(in->data, in->len, out, (size_t)bound, &n, &zp, on_progress, &s, BIG_CAP, &st) == OSN_E_ABORTED
              && n == 0, "zstd stopped by progress");
        CHECK(st.live_bytes_after == 0, "zstd stopped: leaked");
    }
    free(out);
}

static void check_thread_retry(void)
{
    const input* in = &g_inputs[7];
    osn_lzma_params p = lzma_params(1 << 21, 3, 0, 2, 64, 1, 2);
    osn_lzma_params q;
    osn_stats s1 = new_stats(), s2 = new_stats();
    size_t len1 = 0, len2 = 0;
    uint8_t *e1, *e2;
    q = p; q.flags = OSN_LZMA_FLAG_TEST_THREAD_ERROR;
    e1 = lzma_enc(in, &p, &len1, &s1);
    e2 = lzma_enc(in, &q, &len2, &s2);
    if (e1 && e2)
        CHECK(len1 == len2 && memcmp(e1, e2, len1) == 0 && s2.thread_retries == 1 && s2.threads_used == 1,
              "helper thread error: retries %u, threads %u", s2.thread_retries, s2.threads_used);
    free(e1); free(e2);
}

/* ------------------------------------------------------------------------------------------------ parallel */

typedef struct reference { uint8_t* lzma; size_t lzma_len; uint8_t* zstd; size_t zstd_len; } reference;
static reference g_refs[9];
static int g_repeat = 1;

#if defined(_WIN32)
static volatile LONG g_parallel_failures;
#  define ADD_FAILURE() InterlockedIncrement(&g_parallel_failures)
#else
static int g_parallel_failures;
static pthread_mutex_t g_fail_lock = PTHREAD_MUTEX_INITIALIZER;
#  define ADD_FAILURE() do { pthread_mutex_lock(&g_fail_lock); g_parallel_failures++; pthread_mutex_unlock(&g_fail_lock); } while (0)
#endif

static void parallel_work(void)
{
    int r;
    size_t i;
    for (r = 0; r < g_repeat; r++)
        for (i = 0; i < g_ninputs; i++)
        {
            const input* in = &g_inputs[i];
            osn_lzma_params lp = lzma_params(1 << 21, 3, 0, 2, 64, 1, 2);
            osn_zstd_params zp = zstd_params(9, 21, 0, 0, 0);
            uint64_t bl, bz;
            uint8_t *a, *b;
            size_t na = 0, nb = 0;
            osn_lzma_bound(in->len, &bl);
            osn_zstd_bound(in->len, &bz);
            a = (uint8_t*)malloc((size_t)bl);
            b = (uint8_t*)malloc((size_t)bz);
            if (osn_lzma_encode(in->data, in->len, a, (size_t)bl, &na, &lp, NULL, NULL, BIG_CAP, NULL) != OSN_OK
                || na != g_refs[i].lzma_len || memcmp(a, g_refs[i].lzma, na) != 0) ADD_FAILURE();
            if (osn_zstd_encode(in->data, in->len, b, (size_t)bz, &nb, &zp, NULL, NULL, BIG_CAP, NULL) != OSN_OK
                || nb != g_refs[i].zstd_len || memcmp(b, g_refs[i].zstd, nb) != 0) ADD_FAILURE();
            free(a); free(b);
        }
}

#if defined(_WIN32)
static DWORD WINAPI parallel_thread(LPVOID arg) { (void)arg; parallel_work(); return 0; }
#else
static void* parallel_thread(void* arg) { (void)arg; parallel_work(); return NULL; }
#endif

static void check_parallel(int threads)
{
    size_t i;
    int t;
    for (i = 0; i < g_ninputs; i++)
    {
        osn_lzma_params lp = lzma_params(1 << 21, 3, 0, 2, 64, 1, 1);   /* reference: one thread */
        osn_zstd_params zp = zstd_params(9, 21, 0, 0, 0);
        g_refs[i].lzma = lzma_enc(&g_inputs[i], &lp, &g_refs[i].lzma_len, NULL);
        g_refs[i].zstd = zstd_enc(&g_inputs[i], &zp, &g_refs[i].zstd_len, NULL);
    }
    {
#if defined(_WIN32)
        HANDLE h[64];
        for (t = 0; t < threads; t++) h[t] = CreateThread(NULL, 0, parallel_thread, NULL, 0, NULL);
        for (t = 0; t < threads; t++) { WaitForSingleObject(h[t], INFINITE); CloseHandle(h[t]); }
#else
        pthread_t h[64];
        for (t = 0; t < threads; t++) pthread_create(&h[t], NULL, parallel_thread, NULL);
        for (t = 0; t < threads; t++) pthread_join(h[t], NULL);
#endif
    }
    CHECK(g_parallel_failures == 0, "%d parallel encodes (helper thread on) differ from the reference",
          (int)g_parallel_failures);
    for (i = 0; i < g_ninputs; i++) { free(g_refs[i].lzma); free(g_refs[i].zstd); }
}

int main(int argc, char** argv)
{
    int parallel = 0, large = 0, only_parallel = 0, i;
    for (i = 1; i < argc; i++)
    {
        if (strcmp(argv[i], "--parallel") == 0 && i + 1 < argc) parallel = atoi(argv[++i]);
        else if (strcmp(argv[i], "--repeat") == 0 && i + 1 < argc) g_repeat = atoi(argv[++i]);
        else if (strcmp(argv[i], "--bound-large") == 0) large = 1;
        else if (strcmp(argv[i], "--only-parallel") == 0) only_parallel = 1;
        else
        {
            fprintf(stderr, "usage: osn_selftest [--parallel N [--repeat N] [--only-parallel]] [--bound-large]\n");
            return 2;
        }
    }
    if (only_parallel && parallel == 0) return 2;
    if (parallel < 0 || parallel > 64 || g_repeat < 1) return 2;

    add_input("empty", 0, 0, 1);
    add_input("one byte", 1, 0, 2);
    add_input("text 4 KiB", 4 << 10, 0, 3);
    add_input("records 64 KiB + 1", (64 << 10) + 1, 1, 4);
    add_input("text 256 KiB", 256 << 10, 0, 5);
    add_input("random 256 KiB", 256 << 10, 2, 6);
    add_input("mixed 1 MiB + 17", (1 << 20) + 17, 3, 7);
    add_input("text 2 MiB", 2 << 20, 0, 8);
    add_input("mixed 2 MiB", 2 << 20, 3, 9);

    printf("osmium_native self-test: %s, abi %d, %s\n", osn_generation(), osn_abi(), osn_versions());
    check_identity();
    if (!only_parallel)
    {
        check_bounds();
        check_params();
        check_zstd_is_single_threaded();
        check_round_trips();
        check_bound_on_random(large);
        check_output_full();
        check_memory_cap();
        check_progress();
        check_thread_retry();
    }
    if (parallel > 0) check_parallel(parallel);

    for (i = 0; i < (int)g_ninputs; i++) free(g_inputs[i].data);
    printf("%d checks, %d failed\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
