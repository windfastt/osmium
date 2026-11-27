/*
 * Shared helpers of the libFuzzer harnesses (never shipped). The harnesses link the library's own objects, built with
 * OSN_FUZZING (which adds osn_fuzz_fail_allocation), and the upstream decoders, which exist only in test programs.
 */
#ifndef OSN_FUZZ_COMMON_H
#define OSN_FUZZ_COMMON_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "osmium_native.h"

void osn_fuzz_fail_allocation(uint64_t n);   /* src/osmium_native.c, OSN_FUZZING builds only */

#define OSN_FUZZ_GUARD 64
#define OSN_FUZZ_MAX_INPUT ((size_t)1 << 20)

/* Reads harness settings from the front of the fuzz input. */
typedef struct osn_fuzz_reader { const uint8_t* p; size_t n; } osn_fuzz_reader;

static uint32_t osn_fuzz_u8(osn_fuzz_reader* r)
{
    if (r->n == 0) return 0;
    r->n--;
    return *r->p++;
}

static uint32_t osn_fuzz_u16(osn_fuzz_reader* r) { uint32_t a = osn_fuzz_u8(r); return a | (osn_fuzz_u8(r) << 8); }

#define OSN_FUZZ_FAIL(...) do { fprintf(stderr, "osmium_native fuzz: " __VA_ARGS__); fputc('\n', stderr); abort(); } while (0)

/* A memory cap from one byte: from 4 KiB up to "no practical limit" in powers of two. */
static uint64_t osn_fuzz_cap(uint32_t b)
{
    uint32_t s = b % 24;   /* 0..23 */
    return s == 23 ? ((uint64_t)1 << 40) : ((uint64_t)4096 << s);
}

/* Output buffer of cap bytes followed by guard bytes. */
static uint8_t* osn_fuzz_out(size_t cap)
{
    uint8_t* b = (uint8_t*)malloc(cap + OSN_FUZZ_GUARD);
    if (b == NULL) abort();
    memset(b, 0xA5, cap + OSN_FUZZ_GUARD);
    return b;
}

static void osn_fuzz_check_guard(const uint8_t* b, size_t cap, size_t written, int32_t status)
{
    size_t i;
    for (i = cap; i < cap + OSN_FUZZ_GUARD; i++)
        if (b[i] != 0xA5) OSN_FUZZ_FAIL("wrote past dst_cap %zu (status %d)", cap, (int)status);
    if (status != OSN_OK && written != 0) OSN_FUZZ_FAIL("dst_len %zu with status %d", written, (int)status);
    if (status == OSN_OK && written > cap) OSN_FUZZ_FAIL("dst_len %zu above dst_cap %zu", written, cap);
}

static void osn_fuzz_check_stats(const osn_stats* s, uint64_t cap, int32_t status)
{
    if (s->live_bytes_after != 0) OSN_FUZZ_FAIL("%llu bytes still allocated after the call (status %d)", (unsigned long long)s->live_bytes_after, (int)status);
    if (s->peak_bytes > cap) OSN_FUZZ_FAIL("peak %llu above mem_cap %llu", (unsigned long long)s->peak_bytes, (unsigned long long)cap);
}

#endif
