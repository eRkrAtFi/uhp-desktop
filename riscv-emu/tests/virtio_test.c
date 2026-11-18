/* Phase 6 differential test: a bare-metal virtio-blk round trip (one
 * VIRTIO_BLK_T_OUT, then one VIRTIO_BLK_T_IN of the same sector into a
 * different buffer, checked byte-for-byte), run under riscv-emu and under
 * real qemu-system-riscv64 -M virt -device virtio-blk-device against the
 * same backing disk image - see Makefile's virtio-compare target.
 *
 * Polls the used ring rather than assuming synchronous completion: this
 * emulator happens to finish a request inside the QueueNotify write
 * itself, but real QEMU's virtio-blk backend does not promise that, and a
 * test that only passes by accident on one side would not be a real
 * differential proof.
 */
#include <stdint.h>

#define UART_BASE    0x10000000ULL
#define TESTDEV_BASE 0x00100000ULL
#define VIRTIO_BASE  0x10008000ULL  /* must match src/emu.h's VIRTIO_BASE */

#define FINISHER_PASS 0x5555u
#define FINISHER_FAIL(code) (0x3333u | ((uint32_t)(code) << 16))

static volatile uint8_t *const uart = (volatile uint8_t *)UART_BASE;
static volatile uint32_t *const testdev = (volatile uint32_t *)TESTDEV_BASE;

static void putc_(char c) { *uart = (uint8_t)c; }
static void puts_(const char *s) { while (*s) putc_(*s++); }

static void fail(uint32_t code)
{
    puts_("FAIL\n");
    *testdev = FINISHER_FAIL(code);
    for (;;) {}
}

#define VREG(off) (*(volatile uint32_t *)(VIRTIO_BASE + (off)))

typedef struct __attribute__((packed)) {
    uint64_t addr;
    uint32_t len;
    uint16_t flags;
    uint16_t next;
} VqDesc;

#define QSIZE 4

typedef struct __attribute__((packed)) {
    uint16_t flags;
    uint16_t idx;
    uint16_t ring[QSIZE];
} VqAvail;

typedef struct __attribute__((packed)) {
    uint32_t id;
    uint32_t len;
} VqUsedElem;

typedef struct __attribute__((packed)) {
    uint16_t flags;
    uint16_t idx;
    VqUsedElem ring[QSIZE];
} VqUsed;

__attribute__((aligned(16))) static VqDesc desc[QSIZE];
__attribute__((aligned(2)))  static VqAvail avail;
__attribute__((aligned(4)))  static VqUsed  used;

__attribute__((aligned(8))) static uint8_t req_header[16];
__attribute__((aligned(8))) static uint8_t wdata[512];
__attribute__((aligned(8))) static uint8_t rdata[512];
static volatile uint8_t wstatus, rstatus;

static uint16_t last_used_idx;

/* Submits the 3-descriptor chain already built at desc[0..2] (header, data,
 * status) as a new avail-ring entry and notifies the device. */
static void submit(void)
{
    avail.ring[avail.idx % QSIZE] = 0; /* head is always descriptor 0: the
                                         * three slots are rebuilt fresh for
                                         * each request rather than
                                         * allocated from a free list - one
                                         * outstanding request at a time is
                                         * all this test ever needs. */
    avail.idx++;
    VREG(0x050) = 0; /* QueueNotify, queue 0 */
}

static int wait_used(void)
{
    uint32_t spins = 0;
    while (used.idx == last_used_idx) {
        if (++spins > 100000000u) return 0; /* the backend never finished this request */
    }
    last_used_idx = used.idx;
    return 1;
}

void cmain(void)
{
    puts_("virtio-blk test starting\n");

    if (VREG(0x000) != 0x74726976u) fail(1); /* MagicValue: "virt" */
    if (VREG(0x004) != 2) fail(2);            /* Version: modern (2) only */
    if (VREG(0x008) != 2) fail(3);            /* DeviceID: block device */

    VREG(0x070) = 0;                 /* reset */
    VREG(0x070) = 1;                 /* ACKNOWLEDGE */
    VREG(0x070) = 1 | 2;             /* + DRIVER */
    VREG(0x070) = 1 | 2 | 8;         /* + FEATURES_OK (no feature bits selected - none needed) */
    if (!(VREG(0x070) & 8)) fail(4);

    VREG(0x030) = 0;                 /* QueueSel = 0 (the only queue) */
    if (VREG(0x034) < QSIZE) fail(5); /* QueueNumMax must fit our chosen size */
    VREG(0x038) = QSIZE;             /* QueueNum */
    VREG(0x080) = (uint32_t)(uint64_t)&desc;
    VREG(0x084) = (uint32_t)((uint64_t)&desc >> 32);
    VREG(0x090) = (uint32_t)(uint64_t)&avail;
    VREG(0x094) = (uint32_t)((uint64_t)&avail >> 32);
    VREG(0x0a0) = (uint32_t)(uint64_t)&used;
    VREG(0x0a4) = (uint32_t)((uint64_t)&used >> 32);
    VREG(0x044) = 1;                 /* QueueReady */

    VREG(0x070) = 1 | 2 | 8 | 4;     /* + DRIVER_OK: setup complete */

    for (int i = 0; i < 512; i++) wdata[i] = (uint8_t)(i ^ 0xA5);

    /* --- VIRTIO_BLK_T_OUT: write the pattern to sector 1 --- */
    *(uint32_t *)(req_header + 0) = 1;  /* type = OUT */
    *(uint32_t *)(req_header + 4) = 0;  /* reserved */
    *(uint64_t *)(req_header + 8) = 1;  /* sector 1 */

    desc[0].addr = (uint64_t)&req_header; desc[0].len = 16;  desc[0].flags = 1;     desc[0].next = 1;
    desc[1].addr = (uint64_t)&wdata;      desc[1].len = 512; desc[1].flags = 1;     desc[1].next = 2;
    desc[2].addr = (uint64_t)&wstatus;    desc[2].len = 1;   desc[2].flags = 2;     desc[2].next = 0;

    submit();
    if (!wait_used()) fail(6);
    if (wstatus != 0) fail(7);
    puts_("write ok\n");

    /* --- VIRTIO_BLK_T_IN: read sector 1 back into a different buffer --- */
    for (int i = 0; i < 512; i++) rdata[i] = 0;
    *(uint32_t *)(req_header + 0) = 0;  /* type = IN */
    *(uint32_t *)(req_header + 4) = 0;
    *(uint64_t *)(req_header + 8) = 1;

    desc[0].addr = (uint64_t)&req_header; desc[0].len = 16;  desc[0].flags = 1;     desc[0].next = 1;
    desc[1].addr = (uint64_t)&rdata;      desc[1].len = 512; desc[1].flags = 1 | 2; desc[1].next = 2;
    desc[2].addr = (uint64_t)&rstatus;    desc[2].len = 1;   desc[2].flags = 2;     desc[2].next = 0;

    submit();
    if (!wait_used()) fail(8);
    if (rstatus != 0) fail(9);
    puts_("read ok\n");

    for (int i = 0; i < 512; i++) {
        if (rdata[i] != wdata[i]) fail(10);
    }

    puts_("virtio-blk roundtrip OK\n");
    *testdev = FINISHER_PASS;
    for (;;) {}
}
