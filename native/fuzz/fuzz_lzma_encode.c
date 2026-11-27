/*
 * Fuzzes osn_lzma_encode (never shipped). The first bytes choose the parameters (dictionary 64 KiB to 16 MiB, lc, lp,
 * pb, fb, bt2/bt4, one or two threads), the output capacity (anything from 0 to the bound), the memory cap and an
 * allocation to fail; the rest is the input. On OSN_OK the output must fit the bound and decode with the SDK's
 * LzmaDec to the input; on any status nothing may be written past the capacity, and nothing may stay allocated.
 */
#include "fuzz_common.h"
#include "LzmaDec.h"

static void* dec_alloc(ISzAllocPtr p, size_t size) { (void)p; return malloc(size); }
static void dec_free(ISzAllocPtr p, void* a) { (void)p; free(a); }
static const ISzAlloc g_dec_alloc = { dec_alloc, dec_free };

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size);

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    osn_fuzz_reader r = { data, size };
    osn_lzma_params p;
    osn_stats st;
    uint64_t bound = 0, cap;
    size_t dst_cap, written = 0;
    uint32_t cap_sel, fail_at;
    uint8_t* out;
    int32_t status;

    memset(&p, 0, sizeof p);
    p.size = sizeof p;
    p.dict_size = (uint32_t)1 << (16 + osn_fuzz_u8(&r) % 9);
    p.lc = osn_fuzz_u8(&r) % 9;
    p.lp = osn_fuzz_u8(&r) % 5;
    p.pb = osn_fuzz_u8(&r) % 5;
    p.fb = 5 + osn_fuzz_u16(&r) % 269;
    p.match_finder = osn_fuzz_u8(&r) % 2;
    p.threads = 1 + osn_fuzz_u8(&r) % 2;
    cap_sel = osn_fuzz_u8(&r);
    cap = osn_fuzz_cap(osn_fuzz_u8(&r));
    fail_at = osn_fuzz_u8(&r) % 32;   /* 0 = no allocation fails */
    if (r.n > OSN_FUZZ_MAX_INPUT) r.n = OSN_FUZZ_MAX_INPUT;

    if (osn_lzma_bound(r.n, &bound) != OSN_OK) OSN_FUZZ_FAIL("no bound for %zu bytes", r.n);
    dst_cap = cap_sel >= 128 ? (size_t)bound : (size_t)(bound * cap_sel / 128);
    out = osn_fuzz_out(dst_cap);
    memset(&st, 0, sizeof st);
    st.size = sizeof st;
    osn_fuzz_fail_allocation(fail_at);
    status = osn_lzma_encode(r.n ? r.p : NULL, r.n, out, dst_cap, &written, &p, NULL, NULL, cap, &st);
    osn_fuzz_fail_allocation(0);

    osn_fuzz_check_guard(out, dst_cap, written, status);
    osn_fuzz_check_stats(&st, cap, status);
    switch (status)
    {
    case OSN_OK:
    {
        uint8_t props[5];
        uint8_t* back = (uint8_t*)malloc(r.n ? r.n : 1);
        SizeT dl = r.n, sl = written;
        ELzmaStatus ls;
        SRes res;
        if (written > bound) OSN_FUZZ_FAIL("output %zu above the bound %llu", written, (unsigned long long)bound);
        props[0] = (uint8_t)((p.pb * 5 + p.lp) * 9 + p.lc);
        props[1] = (uint8_t)p.dict_size; props[2] = (uint8_t)(p.dict_size >> 8);
        props[3] = (uint8_t)(p.dict_size >> 16); props[4] = (uint8_t)(p.dict_size >> 24);
        res = LzmaDecode(back, &dl, out, &sl, props, 5, LZMA_FINISH_END, &ls, &g_dec_alloc);
        if (res != SZ_OK || dl != r.n || (r.n && memcmp(back, r.p, r.n) != 0)) OSN_FUZZ_FAIL("round trip failed (res %d)", res);
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
