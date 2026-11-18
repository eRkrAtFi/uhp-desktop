/* Hand-builds the flattened device tree (DTB) this machine's own guest
 * Linux needs - a straight, node-for-node port of boot/make_dtb.py into C,
 * so the binary can generate it internally instead of every caller (this
 * file's own CLI, or QemuManager) having to shell out to Python or
 * reimplement the tree from scratch. See make_dtb.py's own comments for why
 * each node and each specific compatible/reg/interrupts value is what it
 * is - this is a translation, not an independent redesign, and the two are
 * expected to keep producing the same tree as long as both exist. */
#include "emu.h"
#include <stdlib.h>
#include <string.h>

typedef struct {
    uint8_t *buf;
    size_t len, cap;
} ByteBuf;

static void bb_reserve(ByteBuf *b, size_t extra)
{
    if (b->len + extra <= b->cap) return;
    size_t newcap = b->cap ? b->cap * 2 : 256;
    while (newcap < b->len + extra) newcap *= 2;
    b->buf = realloc(b->buf, newcap);
    b->cap = newcap;
}

static void bb_bytes(ByteBuf *b, const void *data, size_t n)
{
    if (!n) return;
    bb_reserve(b, n);
    memcpy(b->buf + b->len, data, n);
    b->len += n;
}

static void bb_u32(ByteBuf *b, uint32_t v)
{
    uint8_t be[4] = { (uint8_t)(v >> 24), (uint8_t)(v >> 16), (uint8_t)(v >> 8), (uint8_t)v };
    bb_bytes(b, be, 4);
}

static void bb_pad4(ByteBuf *b)
{
    static const uint8_t zero[3] = { 0, 0, 0 };
    size_t pad = (4 - (b->len % 4)) % 4;
    if (pad) bb_bytes(b, zero, pad);
}

#define FDT_BEGIN_NODE 0x1u
#define FDT_END_NODE   0x2u
#define FDT_PROP       0x3u
#define FDT_END        0x9u
#define MAX_DTB_STRINGS 128

typedef struct {
    ByteBuf strukt;
    ByteBuf strings;
    char *names[MAX_DTB_STRINGS];
    uint32_t name_offsets[MAX_DTB_STRINGS];
    int n_names;
} Dtb;

/* Linear scan for string-table dedup: the node/property set here is small
 * and fixed, so this costs nothing worth a hash map over. */
static uint32_t dtb_str_off(Dtb *d, const char *name)
{
    for (int i = 0; i < d->n_names; i++)
        if (!strcmp(d->names[i], name)) return d->name_offsets[i];
    uint32_t off = (uint32_t)d->strings.len;
    bb_bytes(&d->strings, name, strlen(name) + 1);
    d->names[d->n_names] = strdup(name);
    d->name_offsets[d->n_names] = off;
    d->n_names++;
    return off;
}

static void dtb_begin_node(Dtb *d, const char *name)
{
    bb_u32(&d->strukt, FDT_BEGIN_NODE);
    bb_bytes(&d->strukt, name, strlen(name) + 1);
    bb_pad4(&d->strukt);
}

static void dtb_end_node(Dtb *d) { bb_u32(&d->strukt, FDT_END_NODE); }

static void dtb_prop_raw(Dtb *d, const char *name, const void *data, size_t len)
{
    bb_u32(&d->strukt, FDT_PROP);
    bb_u32(&d->strukt, (uint32_t)len);
    bb_u32(&d->strukt, dtb_str_off(d, name));
    bb_bytes(&d->strukt, data, len);
    bb_pad4(&d->strukt);
}

static void dtb_prop_u32(Dtb *d, const char *name, uint32_t v)
{
    uint8_t be[4] = { (uint8_t)(v >> 24), (uint8_t)(v >> 16), (uint8_t)(v >> 8), (uint8_t)v };
    dtb_prop_raw(d, name, be, 4);
}

/* u32 lists ("reg" on a 1-cell-address node, "interrupts-extended" pairs,
 * etc.) - values given high-to-low in argument order, matching every call
 * site's own prop_u32(name, v1, v2, ...) shape in make_dtb.py. */
static void dtb_prop_u32n(Dtb *d, const char *name, const uint32_t *vals, int n)
{
    uint8_t be[64];
    for (int i = 0; i < n; i++) {
        be[i * 4 + 0] = (uint8_t)(vals[i] >> 24);
        be[i * 4 + 1] = (uint8_t)(vals[i] >> 16);
        be[i * 4 + 2] = (uint8_t)(vals[i] >> 8);
        be[i * 4 + 3] = (uint8_t)vals[i];
    }
    dtb_prop_raw(d, name, be, (size_t)n * 4);
}

static void dtb_prop_u64(Dtb *d, const char *name, uint64_t v)
{
    uint8_t be[8];
    for (int i = 0; i < 8; i++) be[i] = (uint8_t)(v >> (56 - i * 8));
    dtb_prop_raw(d, name, be, 8);
}

static void dtb_prop_u64_2(Dtb *d, const char *name, uint64_t a, uint64_t b)
{
    uint8_t be[16];
    for (int i = 0; i < 8; i++) be[i] = (uint8_t)(a >> (56 - i * 8));
    for (int i = 0; i < 8; i++) be[8 + i] = (uint8_t)(b >> (56 - i * 8));
    dtb_prop_raw(d, name, be, 16);
}

static void dtb_prop_str(Dtb *d, const char *name, const char *value)
{
    dtb_prop_raw(d, name, value, strlen(value) + 1);
}

/* A "compatible" list of several NUL-terminated strings back to back
 * (e.g. "sifive,clint0\0riscv,clint0\0") - the multi-string form several
 * nodes below need, same as make_dtb.py's own prop_raw(b"a\0b\0") calls. */
static void dtb_prop_strlist(Dtb *d, const char *name, const char *const *values, int n)
{
    ByteBuf tmp = { 0 };
    for (int i = 0; i < n; i++) bb_bytes(&tmp, values[i], strlen(values[i]) + 1);
    dtb_prop_raw(d, name, tmp.buf, tmp.len);
    free(tmp.buf);
}

static void dtb_prop_empty(Dtb *d, const char *name) { dtb_prop_raw(d, name, NULL, 0); }

/* A fixed, non-secret seed for Linux's add_bootloader_randomness() - see
 * make_dtb.py's own "rng-seed" comment for why this exists at all (unblocks
 * random_init()'s boot-time wait for entropy on a machine with no hardware
 * RNG and no real timing jitter) and why it is a hardcoded constant rather
 * than freshly generated every run (reproducibility for `make compare`). */
static const uint8_t rng_seed[64] = {
    0xd1,0xb1,0xb5,0x3c,0x6b,0x6f,0x6a,0x0e,0x8e,0x9f,0x9a,0x2e,0x3c,0x4d,0x5e,0x6f,
    0x70,0x81,0x92,0x0a,0x1b,0x2c,0x3d,0x4e,0x5f,0x60,0x71,0x82,0x93,0xa4,0xb5,0xc6,
    0xd7,0xe8,0xf9,0x0a,0x1b,0x2c,0x3d,0x4e,0x5f,0x60,0x71,0x82,0x93,0xa4,0xb5,0xc6,
    0xd7,0xe8,0xf9,0x00,0x11,0x22,0x33,0x44,0x55,0x66,0x77,0x88,0x99,0xaa,0xbb,0xcc,
};

uint8_t *build_dtb(uint64_t mem_base, uint64_t mem_size,
                    int has_initrd, uint64_t initrd_start, uint64_t initrd_end,
                    const char *bootargs, uint64_t *out_len)
{
    Dtb d;
    memset(&d, 0, sizeof(d));

    dtb_begin_node(&d, "");
    dtb_prop_u32(&d, "#address-cells", 2);
    dtb_prop_u32(&d, "#size-cells", 2);
    dtb_prop_str(&d, "compatible", "riscv-emu,virt");
    dtb_prop_str(&d, "model", "riscv-emu,virt");

    dtb_begin_node(&d, "chosen");
    dtb_prop_str(&d, "bootargs", bootargs);
    if (has_initrd) {
        dtb_prop_u64(&d, "linux,initrd-start", initrd_start);
        dtb_prop_u64(&d, "linux,initrd-end", initrd_end);
    }
    dtb_prop_raw(&d, "rng-seed", rng_seed, sizeof(rng_seed));
    dtb_end_node(&d);

    {
        char name[32];
        snprintf(name, sizeof(name), "memory@%llx", (unsigned long long)mem_base);
        dtb_begin_node(&d, name);
    }
    dtb_prop_str(&d, "device_type", "memory");
    dtb_prop_u64_2(&d, "reg", mem_base, mem_size);
    dtb_end_node(&d);

    dtb_begin_node(&d, "cpus");
    dtb_prop_u32(&d, "#address-cells", 1);
    dtb_prop_u32(&d, "#size-cells", 0);
    /* Matches real qemu-system-riscv64 -M virt's own CLINT (10 MHz) - see
     * make_dtb.py's own remarks on why an "honest" low value breaks the
     * kernel's clocksource math outright. */
    dtb_prop_u32(&d, "timebase-frequency", 10000000);

    dtb_begin_node(&d, "cpu@0");
    dtb_prop_str(&d, "device_type", "cpu");
    dtb_prop_u32(&d, "reg", 0);
    dtb_prop_str(&d, "status", "okay");
    dtb_prop_str(&d, "compatible", "riscv");
    dtb_prop_str(&d, "riscv,isa", "rv64imafdc_sstc");
    dtb_prop_str(&d, "mmu-type", "riscv,sv39");

    dtb_begin_node(&d, "interrupt-controller");
    dtb_prop_u32(&d, "#interrupt-cells", 1);
    dtb_prop_empty(&d, "interrupt-controller");
    dtb_prop_str(&d, "compatible", "riscv,cpu-intc");
    dtb_prop_u32(&d, "phandle", 1);
    dtb_end_node(&d);

    dtb_end_node(&d); /* cpu@0 */
    dtb_end_node(&d); /* cpus */

    dtb_begin_node(&d, "clint@2000000");
    {
        const char *compat[] = { "sifive,clint0", "riscv,clint0" };
        dtb_prop_strlist(&d, "compatible", compat, 2);
    }
    dtb_prop_u64_2(&d, "reg", 0x2000000, 0x10000);
    { uint32_t v[4] = { 1, 3, 1, 7 }; dtb_prop_u32n(&d, "interrupts-extended", v, 4); }
    dtb_end_node(&d);

    dtb_begin_node(&d, "plic@c000000");
    {
        const char *compat[] = { "sifive,plic-1.0.0", "riscv,plic0" };
        dtb_prop_strlist(&d, "compatible", compat, 2);
    }
    dtb_prop_u32(&d, "#interrupt-cells", 1);
    dtb_prop_empty(&d, "interrupt-controller");
    dtb_prop_u32(&d, "riscv,ndev", 31);
    dtb_prop_u64_2(&d, "reg", 0xc000000, 0x400000);
    { uint32_t v[4] = { 1, 11, 1, 9 }; dtb_prop_u32n(&d, "interrupts-extended", v, 4); }
    dtb_prop_u32(&d, "phandle", 2);
    dtb_end_node(&d);

    dtb_begin_node(&d, "serial@10000000");
    dtb_prop_str(&d, "compatible", "ns16550a");
    dtb_prop_u64_2(&d, "reg", 0x10000000, 0x100);
    dtb_prop_u32(&d, "clock-frequency", 3686400);
    dtb_prop_u32(&d, "interrupt-parent", 2);
    dtb_prop_u32(&d, "interrupts", 10);
    dtb_end_node(&d);

    dtb_begin_node(&d, "test@100000");
    dtb_prop_u32(&d, "phandle", 3);
    dtb_prop_u64_2(&d, "reg", 0x100000, 0x1000);
    {
        const char *compat[] = { "sifive,test1", "sifive,test0", "syscon" };
        dtb_prop_strlist(&d, "compatible", compat, 3);
    }
    dtb_end_node(&d);

    dtb_begin_node(&d, "poweroff");
    { const char *compat[] = { "syscon-poweroff" }; dtb_prop_strlist(&d, "compatible", compat, 1); }
    dtb_prop_u32(&d, "regmap", 3);
    dtb_prop_u32(&d, "offset", 0);
    dtb_prop_u32(&d, "value", 0x5555);
    dtb_end_node(&d);

    dtb_begin_node(&d, "reboot");
    { const char *compat[] = { "syscon-reboot" }; dtb_prop_strlist(&d, "compatible", compat, 1); }
    dtb_prop_u32(&d, "regmap", 3);
    dtb_prop_u32(&d, "offset", 0);
    dtb_prop_u32(&d, "value", 0x7777);
    dtb_end_node(&d);

    dtb_begin_node(&d, "rtc@101000");
    dtb_prop_u64_2(&d, "reg", 0x101000, 0x1000);
    dtb_prop_u32(&d, "interrupt-parent", 2);
    dtb_prop_u32(&d, "interrupts", 11);
    { const char *compat[] = { "google,goldfish-rtc" }; dtb_prop_strlist(&d, "compatible", compat, 1); }
    dtb_end_node(&d);

    dtb_begin_node(&d, "virtio_mmio@10008000");
    dtb_prop_str(&d, "compatible", "virtio,mmio");
    dtb_prop_u64_2(&d, "reg", 0x10008000, 0x1000);
    dtb_prop_u32(&d, "interrupt-parent", 2);
    dtb_prop_u32(&d, "interrupts", 8);
    dtb_end_node(&d);

    dtb_begin_node(&d, "virtio_mmio@10007000");
    dtb_prop_str(&d, "compatible", "virtio,mmio");
    dtb_prop_u64_2(&d, "reg", 0x10007000, 0x1000);
    dtb_prop_u32(&d, "interrupt-parent", 2);
    dtb_prop_u32(&d, "interrupts", 7);
    dtb_end_node(&d);

    dtb_end_node(&d); /* root */
    bb_u32(&d.strukt, FDT_END);

    {
        const uint32_t off_mem_rsvmap = 40; /* header is 10 big-endian u32 = 40 bytes */
        uint8_t rsvmap[16] = { 0 };
        uint32_t off_dt_struct = off_mem_rsvmap + (uint32_t)sizeof(rsvmap);
        uint32_t off_dt_strings = off_dt_struct + (uint32_t)d.strukt.len;
        uint32_t totalsize = off_dt_strings + (uint32_t)d.strings.len;

        uint8_t *result = malloc(totalsize);
        ByteBuf header = { 0 };
        bb_u32(&header, 0xD00DFEEDu);
        bb_u32(&header, totalsize);
        bb_u32(&header, off_dt_struct);
        bb_u32(&header, off_dt_strings);
        bb_u32(&header, off_mem_rsvmap);
        bb_u32(&header, 17u);  /* version */
        bb_u32(&header, 16u);  /* last_comp_version */
        bb_u32(&header, 0u);   /* boot_cpuid_phys */
        bb_u32(&header, (uint32_t)d.strings.len);
        bb_u32(&header, (uint32_t)d.strukt.len);

        size_t pos = 0;
        memcpy(result + pos, header.buf, header.len); pos += header.len;
        memcpy(result + pos, rsvmap, sizeof(rsvmap)); pos += sizeof(rsvmap);
        memcpy(result + pos, d.strukt.buf, d.strukt.len); pos += d.strukt.len;
        memcpy(result + pos, d.strings.buf, d.strings.len);

        free(header.buf);
        free(d.strukt.buf);
        free(d.strings.buf);
        for (int i = 0; i < d.n_names; i++) free(d.names[i]);

        *out_len = totalsize;
        return result;
    }
}
