/*
 * Fuzzes osn_zstd_encode (never shipped). The first bytes choose the level (1 to 19, up to 22 on inputs under
 * 64 KiB), window (10 to 24), long-distance matching and its minimum, strategy, chunk size, output capacity, memory
 * cap and an allocation to fail; the rest is the input. On OSN_OK the frame must fit the bound and decode with
 * ZSTD_decompress to the input; on any status nothing may be written past the capacity or stay allocated.
 */
#include "fuzz_common.h"
#include "zstd.h"

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size);

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    static const uint32_t CHUNKS[] = { 0, 64u << 10, 128u << 10, 1u << 20 };
    osn_fuzz_reader r = { data, size };
    osn_zstd_params p;
    osn_stats st;
    uint64_t bound = 0, cap;
    size_t dst_cap, written = 0;
    uint32_t level_sel, cap_sel, fail_at;
    uint8_t* out;
    int32_t status;

    memset(&p, 0, sizeof p);
    p.size = sizeof p;
    level_sel = osn_fuzz_u8(&r);
    p.window_log = 10 + osn_fuzz_u8(&r) % 15;
    p.ldm = osn_fuzz_u8(&r) % 2;
    {
        uint32_t on = osn_fuzz_u8(&r) & 1, mm = osn_fuzz_u16(&r);   /* always read: 11 settings bytes */
        p.ldm_min_match = p.ldm && on ? 4 + mm % 4093 : 0;
    }
    p.strategy = osn_fuzz_u8(&r) % 10;
    p.chunk_size = CHUNKS[osn_fuzz_u8(&r) % 4];
    cap_sel = osn_fuzz_u8(&r);
    cap = osn_fuzz_cap(osn_fuzz_u8(&r));
    fail_at = osn_fuzz_u8(&r) % 16;
    if (r.n > OSN_FUZZ_MAX_INPUT) r.n = OSN_FUZZ_MAX_INPUT;
    p.level = 1 + level_sel % (r.n < (64u << 10) ? 22 : 19);

    if (osn_zstd_bound(r.n, &bound) != OSN_OK) OSN_FUZZ_FAIL("no bound for %zu bytes", r.n);
    dst_cap = cap_sel >= 128 ? (size_t)bound : (size_t)(bound * cap_sel / 128);
    out = osn_fuzz_out(dst_cap);
    memset(&st, 0, sizeof st);
    st.size = sizeof st;
    osn_fuzz_fail_allocation(fail_at);
    status = osn_zstd_encode(r.n ? r.p : NULL, r.n, out, dst_cap, &written, &p, NULL, NULL, cap, &st);
    osn_fuzz_fail_allocation(0);

    osn_fuzz_check_guard(out, dst_cap, written, status);
    osn_fuzz_check_stats(&st, cap, status);
    switch (status)
    {
    case OSN_OK:
    {
        uint8_t* back = (uint8_t*)malloc(r.n ? r.n : 1);
        size_t n;
        if (written > bound) OSN_FUZZ_FAIL("output %zu above the bound %llu", written, (unsigned long long)bound);
        n = ZSTD_decompress(back, r.n, out, written);
        if (ZSTD_isError(n) || n != r.n || (r.n && memcmp(back, r.p, r.n) != 0))
            OSN_FUZZ_FAIL("round trip failed (%s)", ZSTD_isError(n) ? ZSTD_getErrorName(n) : "length");
        free(back);
        break;
    }
    case OSN_E_OUTPUT_FULL:
        if (dst_cap >= bound) OSN_FUZZ_FAIL("OSN_E_OUTPUT_FULL with a buffer of the bound");
        break;
    case OSN_E_MEMCAP:
    case OSN_E_NOMEM:
        break;
    default:
        OSN_FUZZ_FAIL("unexpected status %d (upstream %d)", (int)status, (int)st.upstream_code);
    }
    free(out);
    return 0;
}
