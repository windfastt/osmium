/*
 * Fuzzes the interface's argument checks (never shipped): arbitrary parameter structs (every field and the size from
 * the input), null pointers, lengths and capacities. Every call must return a status without crashing, reading or
 * writing out of bounds or leaking. A length beyond its buffer is passed only above OSN_MAX_INPUT, which must be
 * refused before anything is read; a capacity is never larger than its buffer.
 */
#include "fuzz_common.h"

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size);

static void fill(void* dst, size_t n, osn_fuzz_reader* r)
{
    uint8_t* d = (uint8_t*)dst;
    size_t i;
    for (i = 0; i < n; i++) d[i] = (uint8_t)osn_fuzz_u8(r);
}

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    osn_fuzz_reader r = { data, size };
    uint32_t which = osn_fuzz_u8(&r);
    uint32_t flags = osn_fuzz_u8(&r);
    union { osn_lzma_params l; osn_zstd_params z; uint8_t raw[64]; } p;
    osn_stats st;
    uint64_t bound = 0, cap;
    size_t src_len, dst_cap, written = 99;
    uint8_t* out;
    const uint8_t* src;
    int32_t status;
    int sane_size;

    memset(&p, 0, sizeof p);
    fill(&p, sizeof p, &r);
    sane_size = flags & 1;
    if (sane_size) p.l.size = (which & 1) ? (uint32_t)sizeof(osn_zstd_params) : (uint32_t)sizeof(osn_lzma_params);
    if (p.l.size == sizeof(osn_lzma_params) && !(which & 1)) p.l.dict_size &= (1u << 22) - 1;   /* keep memory small */
    if ((which & 1) && p.z.level > 19) p.z.level = 19;                                           /* and time short */
    cap = osn_fuzz_cap(osn_fuzz_u8(&r)) & (((uint64_t)1 << 30) - 1);
    if (cap == 0) cap = flags & 0x80 ? 0 : 4096;
    memset(&st, 0, sizeof st);
    st.size = (flags & 2) ? (uint32_t)sizeof st : osn_fuzz_u8(&r);

    if (r.n > (64u << 10)) r.n = 64u << 10;
    src = (flags & 4) ? NULL : r.p;
    src_len = r.n;
    if (flags & 8) src_len = (size_t)OSN_MAX_INPUT + 1 + osn_fuzz_u8(&r);   /* refused before reading */
    if (osn_zstd_bound(r.n, &bound) != OSN_OK || osn_lzma_bound(r.n, &bound) != OSN_OK) OSN_FUZZ_FAIL("bound");
    dst_cap = (flags & 16) ? 0 : (size_t)(bound * (osn_fuzz_u8(&r) + 1) / 128);
    out = osn_fuzz_out(dst_cap);

    if (which & 1)
        status = osn_zstd_encode(src, src_len, (flags & 32) ? NULL : out, dst_cap, (flags & 64) ? NULL : &written,
                                 (which & 2) ? NULL : &p.z, NULL, NULL, cap, (which & 4) ? NULL : &st);
    else
        status = osn_lzma_encode(src, src_len, (flags & 32) ? NULL : out, dst_cap, (flags & 64) ? NULL : &written,
                                 (which & 2) ? NULL : &p.l, NULL, NULL, cap, (which & 4) ? NULL : &st);

    if (status < OSN_OK || status > OSN_E_INTERNAL) OSN_FUZZ_FAIL("status %d out of range", (int)status);
    if ((src == NULL && src_len != 0) || (flags & 32 && dst_cap != 0) || (flags & 64) || (which & 2) || cap == 0
        || src_len > OSN_MAX_INPUT)
        if (status != OSN_E_PARAM) OSN_FUZZ_FAIL("invalid arguments gave status %d", (int)status);
    {
        size_t i;
        for (i = dst_cap; i < dst_cap + OSN_FUZZ_GUARD; i++)
            if (out[i] != 0xA5) OSN_FUZZ_FAIL("wrote past dst_cap %zu (status %d)", dst_cap, (int)status);
    }
    /* A refused call may leave dst_len untouched; any other must set it. */
    if (status != OSN_E_PARAM && !(flags & 64))
    {
        if (status != OSN_OK && written != 0) OSN_FUZZ_FAIL("dst_len %zu with status %d", written, (int)status);
        if (status == OSN_OK && written > dst_cap) OSN_FUZZ_FAIL("dst_len %zu above dst_cap %zu", written, dst_cap);
    }
    if (!(which & 4) && status != OSN_E_PARAM) osn_fuzz_check_stats(&st, cap, status);
    if (status == OSN_E_INTERNAL) OSN_FUZZ_FAIL("upstream failure %d on checked arguments", (int)st.upstream_code);
    free(out);
    return 0;
}
