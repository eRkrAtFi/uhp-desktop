/* Host-served MMIO devices, Phase 1: just enough for a bare-metal test
 * program to print to a console and tell the host pass/fail/reset. This is
 * the same role emu86's bios.c plays for 8086 interrupts - the guest talks
 * to a handful of fixed addresses, and we answer from the host side instead
 * of modelling real hardware we don't need yet.
 */
#include "emu.h"
#include <stdlib.h>
#include <time.h>

int device_covers(uint64_t addr, uint64_t base, uint64_t size)
{
    return addr >= base && addr < base + size;
}

/* A 16550-compatible UART, modelling only what a polling console driver
 * reads before it decides it's safe to write or has something to read:
 * THR/RBR (offset 0) and LSR (offset 5), where bit 5 (THRE) must read as 1
 * or the guest spins forever waiting to transmit, and bit 0 (data ready)
 * reflects whether console_getchar() actually has a byte - driven by the
 * host stdin reader thread (see console_input_start in main.c), not
 * modelled as always-empty anymore. */
uint64_t uart_read(Machine *m, uint64_t addr, int width)
{
    uint64_t off = addr - UART_BASE;
    (void)width;
    if (off == 0) {
        int c = console_getchar(m);
        if (getenv("RISCV_EMU_SBIDBG"))
            fprintf(stderr, "[%llu] uart RBR read -> %d ('%c') (ring r=%u w=%u)\n",
                    (unsigned long long)m->cpu.insns, c, (c >= 32 && c < 127) ? c : '?',
                    m->stdin_ring.read_pos, m->stdin_ring.write_pos);
        return c < 0 ? 0 : (uint64_t)c;
    }
    if (off == 5) return 0x60 | (console_input_ready(m) ? 1 : 0); /* THRE | TEMT [| DR] */
    return 0;
}

void uart_write(Machine *m, uint64_t addr, int width, uint64_t val)
{
    uint64_t off = addr - UART_BASE;
    (void)width;
    if (off == 0) {
        int ch = (int)(val & 0xff);
        putchar(ch);
        fflush(stdout);
        if (getenv("RISCV_EMU_SBIDBG"))
            fprintf(stderr, "[%llu] uart THR write <- %d ('%c')\n",
                    (unsigned long long)m->cpu.insns, ch, (ch >= 32 && ch < 127) ? ch : '?');
    }
    if (off == 1) m->cpu.uart_ier = (uint8_t)val;
    /* Other registers (FCR, LCR, ...) are accepted and ignored; no guest
     * code we run yet depends on their state surviving a read. */
}

/* QEMU virt's "sifive_test" syscon device: a guest write of a recognized
 * 32-bit pattern ends the machine. We mirror the same encoding so the exact
 * same test image works, unmodified, under both this emulator and real
 * qemu-system-riscv64 -M virt. */
void testdev_write(Machine *m, uint64_t addr, int width, uint64_t val)
{
    (void)addr;
    (void)width;
    uint32_t v = (uint32_t)val;
    if (v == FINISHER_PASS) {
        m->cpu.halted = 1;
        m->cpu.exit_code = 0;
    } else if ((v & 0xffff) == 0x3333) {
        m->cpu.halted = 1;
        m->cpu.exit_code = (int)(v >> 16);
    } else if (v == FINISHER_RESET) {
        cpu_reset(m);
    }
}

/* google,goldfish-rtc: real wall-clock time, not simulated/deterministic
 * mtime - added specifically because its absence meant this kernel's
 * system clock stayed at whatever default it boots with (no goldfish-rtc
 * driver message, unlike real QEMU's own "setting system clock to ..."
 * one, ever appeared), which an Alpine guest's own TLS certificate
 * validation then correctly rejected as "not yet valid" (any CA cert's
 * own notBefore date being later than the clock's default). TIME_LOW
 * latches both halves of the current time on read, matching real
 * goldfish-rtc hardware exactly: TIME_HIGH must return what TIME_LOW
 * latched, not a freshly re-sampled clock, or the two halves can
 * observably disagree if a read happens to land across a wall-clock
 * tick between them - deliberately not "just read the clock twice". Only
 * reads are implemented (no alarm/interrupt support) since nothing this
 * project boots sets an RTC alarm.
 *
 * Real time, by design, is the one deliberate source of non-determinism
 * this emulator has - every *existing* differential test is bare-metal
 * code that never reads this device at all, so it stays invisible to
 * `make compare`. But a full Linux boot reads it unconditionally, at
 * boot, to set its own system clock - the very message that confirmed
 * this device works at all - and prints that read's result straight to
 * the console log, so any *future* byte-for-byte boot-log comparison
 * (exactly the technique that confirmed both performance optimizations
 * above changed nothing observable) would see a different timestamp
 * every run once this existed, not because anything regressed. `boot_
 * test.sh` itself is unaffected (it only pattern-matches for the shell
 * prompt appearing), but RISCV_EMU_RTC_FIXED exists so a *new* exact-
 * comparison test doesn't have to give up determinism to use this
 * device at all: set to a decimal Unix timestamp, it pins what every
 * read returns instead of sampling the host clock, restoring the same
 * repeatable-boot-log technique for any test that wants one. */
uint64_t rtc_read(Machine *m, uint64_t addr, int width)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - RTC_BASE;
    (void)width;
    if (off == 0x00) { /* TIME_LOW: latch now, return the low half */
        const char *fixed = getenv("RISCV_EMU_RTC_FIXED");
        uint64_t now = fixed ? strtoull(fixed, NULL, 10) : (uint64_t)time(NULL);
        c->rtc_latched_ns = now * 1000000000ull;
        return (uint32_t)c->rtc_latched_ns;
    }
    if (off == 0x04) return (uint32_t)(c->rtc_latched_ns >> 32); /* TIME_HIGH */
    return 0;
}

/* SiFive CLINT: the only interrupt source modelled so far, and the only one
 * real firmware strictly needs before it can hand S-mode a working timer -
 * everything else (PLIC-routed external interrupts) is a later refinement.
 * Guest code accesses these as one aligned 32- or 64-bit access; narrower
 * accesses are not something any real firmware or kernel issues against
 * CLINT, so they are not specially handled here. */
uint64_t clint_read(Machine *m, uint64_t addr, int width)
{
    uint64_t off = addr - CLINT_BASE;
    (void)width;
    if (off == CLINT_MSIP_OFF) return m->cpu.msip;
    if (off == CLINT_MTIMECMP_OFF) return m->cpu.mtimecmp;
    if (off == CLINT_MTIME_OFF) return m->cpu.mtime;
    return 0;
}

void clint_write(Machine *m, uint64_t addr, int width, uint64_t val)
{
    uint64_t off = addr - CLINT_BASE;
    (void)width;
    if (getenv("RISCV_EMU_SBIDBG"))
        fprintf(stderr, "[%llu] clint_write off=%llx val=%llx (mtime=%llu)\n",
                (unsigned long long)m->cpu.insns, (unsigned long long)off,
                (unsigned long long)val, (unsigned long long)m->cpu.mtime);
    if (off == CLINT_MSIP_OFF) m->cpu.msip = (uint32_t)(val & 1);
    else if (off == CLINT_MTIMECMP_OFF) m->cpu.mtimecmp = val;
    else if (off == CLINT_MTIME_OFF) m->cpu.mtime = val;
}

/* PLIC: see PLIC_BASE's own remarks in emu.h. Two real interrupt sources:
 * the UART (receive-data-ready, "pending" derived live from
 * console_input_ready() rather than latched, matching how a real
 * level-triggered UART-ready line behaves - it stays asserted for as long
 * as there is an unread byte, full stop, not until some separate ack; PLUS
 * transmit-holding-register-empty, gated on the guest's own IER bit for it
 * - see below) and virtio-blk's used-buffer notification (virtio_isr, set
 * once a request finishes - see virtio_blk_process_queue below - and
 * cleared by the guest's own InterruptACK write, not derived live from
 * anything since there is no underlying "still pending" condition to
 * re-derive once the guest has actually drained the used ring).
 *
 * The THRE half of the UART condition was missing for a long time and is
 * worth recording why it matters: this model's uart_write transmits a byte
 * the instant the guest writes THR (putchar+fflush, no actual "in flight"
 * delay), so THR is architecturally always empty - a real 8250 driver
 * (Linux's serial8250) relies on a THRE *interrupt* to drain the rest of
 * whatever it has queued past the first byte any single write() call kicks
 * off synchronously, not on polling. Without this, every byte past that
 * first synchronous one sat in the kernel's own tty buffer forever unless
 * some *other* UART interrupt happened to fire too - which an RX byte
 * arriving does, because real hardware shares one IRQ line between RX and
 * TX conditions and the driver's single ISR checks and services both.
 * That incidental coupling is exactly what made this look like a timing-
 * dependent race in earlier testing: console output after the first couple
 * of lines would only ever appear once *something* was typed, and exactly
 * how much of the backlog drained before the guest's own IER re-disabled
 * THRE (once its buffer emptied) depended on precise instruction timing -
 * a real non-determinism, but in when buffered TX got serviced, not in the
 * stdin ring buffer that earlier investigation (see README) suspected. */
#define UART_IER_RDI 0x01
#define UART_IER_THRI 0x02
static int plic_irq_live(Machine *m, int irq)
{
    CPU *c = &m->cpu;
    /* RX half gated on IER.RDI (bit 0) && LSR.DR, matching a real 16550
     * - previously ungated (console_input_ready(m) alone), which read as
     * "RX IRQ live" even with RDI disabled. Harmless in practice so far
     * (the guest's own serial8250 driver sets RDI whenever it has the
     * port open wanting input, the same assumption the THRE half next to
     * it already relies on), but still the wrong condition to report,
     * and now that StdinRing never drops/overwrites an unread byte (see
     * its own comment in emu.h/main.c), lingering RX-ready data the
     * guest has deliberately masked off is exactly the kind of state
     * this needs to not misreport as a pending interrupt. */
    if (irq == UART_IRQ)
        return ((c->uart_ier & UART_IER_RDI) && console_input_ready(m)) || (c->uart_ier & UART_IER_THRI);
    if (irq == VIRTIO_IRQ) return c->virtio_isr != 0;
    if (irq == VIRTIO_NET_IRQ) return c->net_isr != 0;
    return 0;
}

/* plic_irq_live() only ever recognizes these three IRQ numbers (anything
 * else falls through its own "return 0" and can never be live) - this is
 * called from check_interrupt on *every* instruction (for the SEIP
 * check), so scanning the other ~29 nonexistent IRQ numbers in
 * PLIC_MAX_IRQ's full 1..31 range on every single step was pure waste,
 * confirmed a measurable chunk of check_interrupt's own cost by a
 * dedicated profiling run (see README's performance section). Mathematically
 * identical result to the old full scan - skipping irqs that can only
 * ever read as "not live" cannot change which one wins the priority
 * comparison below - just without doing the work to prove it 29 extra
 * times a step. plic_read's own pending-bitmap/claim-register MMIO reads
 * keep the full 1..31 scan: real device registers, not a per-instruction
 * hot path, not worth the same churn for no measurable benefit. */
static const int plic_real_irqs[] = { VIRTIO_NET_IRQ, VIRTIO_IRQ, UART_IRQ };

uint32_t plic_claim_peek(Machine *m, int ctx)
{
    CPU *c = &m->cpu;
    uint32_t best = 0, best_prio = 0;
    for (size_t i = 0; i < sizeof(plic_real_irqs) / sizeof(plic_real_irqs[0]); i++) {
        int irq = plic_real_irqs[i];
        if (!plic_irq_live(m, irq)) continue;
        if (c->plic_claimed & (1u << irq)) continue;             /* claimed, in service */
        if (!((c->plic_enable[ctx] >> irq) & 1)) continue;
        uint32_t prio = c->plic_priority[irq];
        if (prio == 0 || prio <= c->plic_threshold[ctx]) continue; /* 0 = "never interrupt" */
        if (prio > best_prio) { best_prio = prio; best = (uint32_t)irq; }
    }
    return best;
}

uint64_t plic_read(Machine *m, uint64_t addr, int width)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - PLIC_BASE;
    (void)width;

    if (off >= 4 && off < 4u * PLIC_MAX_IRQ) return c->plic_priority[off / 4];
    if (off == 0x1000) {                                          /* pending bitmap, word 0 */
        uint32_t v = 0;
        for (int irq = 1; irq < PLIC_MAX_IRQ; irq++)
            if (plic_irq_live(m, irq) && !(c->plic_claimed & (1u << irq))) v |= 1u << irq;
        return v;
    }
    if (off >= 0x2000 && off < 0x2000 + PLIC_NUM_CONTEXTS * 0x80u) {
        uint64_t rel = off - 0x2000;
        int ctx = (int)(rel / 0x80);
        if (rel % 0x80 == 0) return c->plic_enable[ctx];
        return 0;
    }
    if (off >= 0x200000 && off < 0x200000 + PLIC_NUM_CONTEXTS * 0x1000u) {
        uint64_t rel = off - 0x200000;
        int ctx = (int)(rel / 0x1000);
        uint64_t reg = rel % 0x1000;
        if (reg == 0) return c->plic_threshold[ctx];
        if (reg == 4) {
            uint32_t irq = plic_claim_peek(m, ctx);
            if (irq) c->plic_claimed |= (1u << irq);              /* claim: suppress re-delivery */
            if (getenv("RISCV_EMU_SBIDBG"))
                fprintf(stderr, "[%llu] plic claim ctx=%d -> irq=%u (threshold=%u prio=%u live=%d)\n",
                        (unsigned long long)m->cpu.insns, ctx, irq, c->plic_threshold[ctx],
                        c->plic_priority[UART_IRQ], plic_irq_live(m, UART_IRQ));
            return irq;
        }
    }
    return 0;
}

void plic_write(Machine *m, uint64_t addr, int width, uint64_t val)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - PLIC_BASE;
    (void)width;

    if (off >= 4 && off < 4u * PLIC_MAX_IRQ) {
        c->plic_priority[off / 4] = (uint32_t)val;
        if (getenv("RISCV_EMU_SBIDBG"))
            fprintf(stderr, "[%llu] plic_priority[%llu] = %x\n",
                    (unsigned long long)m->cpu.insns, (unsigned long long)(off / 4), (unsigned)val);
        return;
    }
    if (off >= 0x2000 && off < 0x2000 + PLIC_NUM_CONTEXTS * 0x80u) {
        uint64_t rel = off - 0x2000;
        int ctx = (int)(rel / 0x80);
        if (rel % 0x80 == 0) {
            c->plic_enable[ctx] = (uint32_t)val;
            if (getenv("RISCV_EMU_SBIDBG"))
                fprintf(stderr, "[%llu] plic_enable[%d] = %x\n",
                        (unsigned long long)m->cpu.insns, ctx, (unsigned)val);
        }
        return;
    }
    if (off >= 0x200000 && off < 0x200000 + PLIC_NUM_CONTEXTS * 0x1000u) {
        uint64_t rel = off - 0x200000;
        int ctx = (int)(rel / 0x1000);
        uint64_t reg = rel % 0x1000;
        if (reg == 0) c->plic_threshold[ctx] = (uint32_t)val;
        else if (reg == 4) c->plic_claimed &= ~(1u << (val & (PLIC_MAX_IRQ - 1))); /* complete */
    }
}

/* --- VirtIO-MMIO + virtio-blk (Phase 6) ------------------------------------
 * See VIRTIO_BASE's own remarks in emu.h for the address/IRQ choice. This
 * is the "modern" (version 2) interface only - no legacy guest-page-size
 * register, no legacy alignment field; Linux's virtio_mmio driver supports
 * both, and modern is simpler to implement correctly. We advertise exactly
 * one feature bit (VIRTIO_F_VERSION_1) and accept whatever status
 * transitions the driver makes without validating them - there is nothing
 * to negotiate down to, since the only feature on offer is the one every
 * modern driver already requires anyway. */
#define VIRTIO_MAGIC            0x74726976u  /* "virt", little-endian */
#define VIRTIO_VERSION          2u
#define VIRTIO_DEVID_BLK        2u
#define VIRTIO_VENDOR           0x52495343u  /* "RISC" - cosmetic, nothing reads it */
#define VIRTIO_F_VERSION_1_BIT  32           /* the only feature bit we offer */

static void virtio_blk_process_queue(Machine *m);

uint64_t virtio_read(Machine *m, uint64_t addr, int width)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - VIRTIO_BASE;
    (void)width;

    switch (off) {
    case 0x000: return VIRTIO_MAGIC;
    case 0x004: return VIRTIO_VERSION;
    case 0x008: return m->disk_file ? VIRTIO_DEVID_BLK : 0;   /* 0 = "no device in this slot" */
    case 0x00c: return VIRTIO_VENDOR;
    case 0x010:                                                /* DeviceFeatures, word selected by sel */
        return c->virtio_dev_features_sel == 1 ? (1u << (VIRTIO_F_VERSION_1_BIT - 32)) : 0;
    case 0x034: return VIRTQ_MAX_SIZE;                         /* QueueNumMax */
    case 0x044: return c->virtio_q[0].ready;                   /* QueueReady */
    case 0x060: return c->virtio_isr;                          /* InterruptStatus */
    case 0x070: return c->virtio_status;                       /* Status */
    case 0x0fc: return 0;                                      /* ConfigGeneration: config never changes */
    default:
        /* struct virtio_blk_config's capacity (le64, in 512-byte sectors)
         * at config-space offset 0x100 - shifting by the byte offset into
         * the field and letting phys_read8/16/32/64's own truncation pick
         * out the right slice works for a byte-wise config read (what
         * virtio_mmio's config->get() actually does) and for a direct
         * 32/64-bit read at the field's base alike, with one formula. */
        if (off >= 0x100 && off < 0x108)
            return (m->disk_size / 512) >> (8 * (off - 0x100));
        return 0;   /* rest of virtio_blk_config: not populated, zero is a valid "unsupported" default */
    }
}

void virtio_write(Machine *m, uint64_t addr, int width, uint64_t val)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - VIRTIO_BASE;
    (void)width;
    VirtQueue *q = &c->virtio_q[0];

    switch (off) {
    case 0x014: c->virtio_dev_features_sel = (uint32_t)val; return;
    case 0x020: return;  /* DriverFeatures: nothing to validate against our one-bit offer */
    case 0x024: c->virtio_drv_features_sel = (uint32_t)val; return;
    case 0x030: c->virtio_queue_sel = (uint32_t)val; return;   /* only queue 0 exists */
    case 0x038: if (c->virtio_queue_sel == 0) q->num = (uint32_t)val; return;
    case 0x044: if (c->virtio_queue_sel == 0) q->ready = (uint32_t)val; return;
    case 0x050:                                                /* QueueNotify: new buffers on this queue */
        if (val == 0) virtio_blk_process_queue(m);
        return;
    case 0x064: c->virtio_isr &= ~(uint32_t)val; return;       /* InterruptACK */
    case 0x070:
        c->virtio_status = (uint32_t)val;
        if (val == 0) { *q = (VirtQueue){0}; c->virtio_isr = 0; }  /* guest-initiated reset */
        return;
    case 0x080: if (c->virtio_queue_sel == 0) q->desc_addr   = (q->desc_addr   & 0xFFFFFFFF00000000ull) | val; return;
    case 0x084: if (c->virtio_queue_sel == 0) q->desc_addr   = (q->desc_addr   & 0xFFFFFFFFull) | (val << 32); return;
    case 0x090: if (c->virtio_queue_sel == 0) q->driver_addr = (q->driver_addr & 0xFFFFFFFF00000000ull) | val; return;
    case 0x094: if (c->virtio_queue_sel == 0) q->driver_addr = (q->driver_addr & 0xFFFFFFFFull) | (val << 32); return;
    case 0x0a0: if (c->virtio_queue_sel == 0) q->device_addr = (q->device_addr & 0xFFFFFFFF00000000ull) | val; return;
    case 0x0a4: if (c->virtio_queue_sel == 0) q->device_addr = (q->device_addr & 0xFFFFFFFFull) | (val << 32); return;
    default: return;   /* config space is read-only from the guest's side for virtio-blk */
    }
}

/* Appends one entry to the used ring and advances its index - the device's
 * side of "here is a completed request", per the split-virtqueue layout
 * (struct virtq_used: le16 flags; le16 idx; struct{le32 id,le32 len} ring[]). */
void virtq_push_used(Machine *m, VirtQueue *q, uint32_t desc_id, uint32_t len)
{
    uint64_t idx_addr = q->device_addr + 2;
    uint16_t used_idx = (uint16_t)phys_read16(m, idx_addr);
    uint64_t elem_addr = q->device_addr + 4 + (uint64_t)(used_idx % q->num) * 8;
    phys_write32(m, elem_addr, desc_id);
    phys_write32(m, elem_addr + 4, len);
    phys_write16(m, idx_addr, (uint16_t)(used_idx + 1));
}

/* VIRTIO_BLK_T_* request types this device understands; anything else gets
 * VIRTIO_BLK_S_UNSUPP (2) in the status byte, same as real virtio-blk. */
enum { VIRTIO_BLK_T_IN = 0, VIRTIO_BLK_T_OUT = 1 };

/* Processes every newly-available request on the one virtqueue virtio-blk
 * uses. A request is a descriptor chain of exactly three device-visible
 * buffers: a read-only 16-byte header (type, reserved, sector), a data
 * buffer (device-writable for a read, device-readable for a write), and a
 * device-writable 1-byte status - this is the one layout virtio-blk ever
 * uses without VIRTIO_BLK_F_* feature bits we don't advertise (no
 * discard/flush/scsi-passthrough support), so the chain is walked
 * positionally (1st/2nd/3rd descriptor) rather than by inspecting flags to
 * classify each one. */
static void virtio_blk_process_queue(Machine *m)
{
    CPU *c = &m->cpu;
    VirtQueue *q = &c->virtio_q[0];
    if (!q->ready || q->num == 0 || !m->disk_file) return;

    uint16_t avail_idx = (uint16_t)phys_read16(m, q->driver_addr + 2);
    while (q->last_avail_idx != avail_idx) {
        uint16_t slot = q->last_avail_idx % (uint16_t)q->num;
        uint16_t head = (uint16_t)phys_read16(m, q->driver_addr + 4 + (uint64_t)slot * 2);
        q->last_avail_idx++;

        uint64_t header_addr = 0, data_addr = 0, status_addr = 0;
        uint32_t data_len = 0;
        int seen = 0;
        uint16_t idx = head;
        for (;;) {
            uint64_t d = q->desc_addr + (uint64_t)idx * 16;
            uint64_t daddr  = phys_read64(m, d);
            uint32_t dlen   = phys_read32(m, d + 8);
            uint16_t dflags = (uint16_t)phys_read16(m, d + 12);
            uint16_t dnext  = (uint16_t)phys_read16(m, d + 14);
            if (seen == 0) header_addr = daddr;
            else if (seen == 1) { data_addr = daddr; data_len = dlen; }
            else status_addr = daddr;
            seen++;
            if (!(dflags & 1) || seen > 64) break;  /* no NEXT flag, or a malformed/looping chain */
            idx = dnext;
        }
        if (seen < 3) continue;  /* not a well-formed virtio-blk request; nothing sane to answer */

        uint32_t type = (uint32_t)phys_read32(m, header_addr);
        uint64_t sector = phys_read64(m, header_addr + 8);
        uint8_t status = 0;      /* VIRTIO_BLK_S_OK */
        uint32_t written = 0;    /* bytes written into the data buffer, for a read */

        if (type == VIRTIO_BLK_T_IN) {
            uint8_t *buf = malloc(data_len ? data_len : 1);
            if (buf) {
                fseek(m->disk_file, (long)(sector * 512), SEEK_SET);
                size_t got = fread(buf, 1, data_len, m->disk_file);
                uint32_t i;
                for (i = 0; i < got; i++) phys_write8(m, data_addr + i, buf[i]);
                for (; i < data_len; i++) phys_write8(m, data_addr + i, 0);  /* short/missing read: zero-fill */
                free(buf);
                written = data_len;
            } else status = 1;  /* VIRTIO_BLK_S_IOERR */
        } else if (type == VIRTIO_BLK_T_OUT) {
            uint8_t *buf = malloc(data_len ? data_len : 1);
            if (buf) {
                uint32_t i;
                for (i = 0; i < data_len; i++) buf[i] = phys_read8(m, data_addr + i);
                fseek(m->disk_file, (long)(sector * 512), SEEK_SET);
                fwrite(buf, 1, data_len, m->disk_file);
                fflush(m->disk_file);
                free(buf);
            } else status = 1;
        } else {
            status = 2;  /* VIRTIO_BLK_S_UNSUPP */
        }

        phys_write8(m, status_addr, status);
        virtq_push_used(m, q, head, written + 1);  /* +1: the status byte itself counts too */
    }

    c->virtio_isr |= 1;  /* "a used buffer is ready" - cleared by the guest's own InterruptACK write */
}

/* --- VirtIO-MMIO + virtio-net (Phase 9) ------------------------------------
 * The second virtio-mmio slot - see VIRTIO_NET_BASE's own remarks in emu.h
 * for the address/IRQ choice. Register layout mirrors virtio_read/write
 * above almost exactly (same "modern" v2 interface, same feature-
 * negotiation mechanics), just against net_q[]/net_status/etc instead of
 * virtio_q[]/virtio_status/etc, and with two virtqueues selectable via
 * QueueSel instead of one. The actual packet handling (net_transmit,
 * net_poll) lives in net.c, not here - this file stays the same "just the
 * registers" layer virtio_read/write already establish, same as
 * plic_read/write vs. check_interrupt's own use of plic_claim_peek. */
#define VIRTIO_DEVID_NET     1u
#define VIRTIO_NET_F_MAC_BIT 5    /* within dev_features_sel's word 0 - "config space has a real MAC" */

uint64_t virtio_net_read(Machine *m, uint64_t addr, int width)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - VIRTIO_NET_BASE;
    (void)width;

    switch (off) {
    case 0x000: return VIRTIO_MAGIC;
    case 0x004: return VIRTIO_VERSION;
    case 0x008: return m->net ? VIRTIO_DEVID_NET : 0;          /* 0 = "no device in this slot" */
    case 0x00c: return VIRTIO_VENDOR;
    case 0x010:                                                 /* DeviceFeatures, word selected by sel */
        if (c->net_dev_features_sel == 0) return 1u << VIRTIO_NET_F_MAC_BIT;
        if (c->net_dev_features_sel == 1) return 1u << (VIRTIO_F_VERSION_1_BIT - 32);
        return 0;
    case 0x034: return VIRTQ_MAX_SIZE;                          /* QueueNumMax */
    case 0x044: return c->net_q[c->net_queue_sel & 1].ready;    /* QueueReady */
    case 0x060: return c->net_isr;                              /* InterruptStatus */
    case 0x070: return c->net_status;                           /* Status */
    case 0x0fc: return 0;                                       /* ConfigGeneration: config never changes */
    default:
        /* struct virtio_net_config: u8 mac[6] at offset 0x100, then a le16
         * `status` field this device never advertises VIRTIO_NET_F_STATUS
         * for (so nothing reads it; Linux's virtio_net driver just assumes
         * the link is always up without that bit, which happens to be
         * exactly true here). */
        if (off >= 0x100 && off < 0x106) return net_guest_mac[off - 0x100];
        return 0;
    }
}

void virtio_net_write(Machine *m, uint64_t addr, int width, uint64_t val)
{
    CPU *c = &m->cpu;
    uint64_t off = addr - VIRTIO_NET_BASE;
    (void)width;
    VirtQueue *q = &c->net_q[c->net_queue_sel & 1];

    switch (off) {
    case 0x014: c->net_dev_features_sel = (uint32_t)val; return;
    case 0x020: return;  /* DriverFeatures: nothing to validate against our two offered bits */
    case 0x024: c->net_drv_features_sel = (uint32_t)val; return;
    case 0x030: c->net_queue_sel = (uint32_t)val; return;       /* 0 = receiveq, 1 = transmitq */
    case 0x038: if (c->net_queue_sel < 2) q->num = (uint32_t)val; return;
    case 0x044: if (c->net_queue_sel < 2) q->ready = (uint32_t)val; return;
    case 0x050:                                                 /* QueueNotify: queue c->net_queue_sel moved */
        if (val == 1) net_transmit(m);  /* transmitq (index 1): the guest just handed us a frame */
        return;                          /* receiveq (index 0): nothing to do now - see net.c's own remarks */
    case 0x064: c->net_isr &= ~(uint32_t)val; return;           /* InterruptACK */
    case 0x070:
        c->net_status = (uint32_t)val;
        if (val == 0) { c->net_q[0] = (VirtQueue){0}; c->net_q[1] = (VirtQueue){0}; c->net_isr = 0; }
        return;
    case 0x080: if (c->net_queue_sel < 2) q->desc_addr   = (q->desc_addr   & 0xFFFFFFFF00000000ull) | val; return;
    case 0x084: if (c->net_queue_sel < 2) q->desc_addr   = (q->desc_addr   & 0xFFFFFFFFull) | (val << 32); return;
    case 0x090: if (c->net_queue_sel < 2) q->driver_addr = (q->driver_addr & 0xFFFFFFFF00000000ull) | val; return;
    case 0x094: if (c->net_queue_sel < 2) q->driver_addr = (q->driver_addr & 0xFFFFFFFFull) | (val << 32); return;
    case 0x0a0: if (c->net_queue_sel < 2) q->device_addr = (q->device_addr & 0xFFFFFFFF00000000ull) | val; return;
    case 0x0a4: if (c->net_queue_sel < 2) q->device_addr = (q->device_addr & 0xFFFFFFFFull) | (val << 32); return;
    default: return;   /* config space is read-only from the guest's side for virtio-net too */
    }
}
