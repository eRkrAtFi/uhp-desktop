/* RV64IM interpreter with CSRs, traps and M/S/U privilege levels (Phase 2 -
 * see emu.h for the phase map). Still flat physical addressing throughout:
 * Sv39 paging is Phase 3, so a privilege change affects which CSRs and
 * instructions are legal, not yet which addresses are reachable.
 */
#include "emu.h"
#include <string.h>
#include <stdlib.h>
#include <math.h>
#include <stdio.h>

/* QueryPerformanceCounter for real-time mtime/WFI sleep (see below) - the
 * same kind of host-specific dependency main.c's own known-debt note
 * already flags for net.c/main.c (Winsock, the stdin reader thread): this
 * adds cpu.c to that same list, not a new category of problem. Directed
 * explicitly rather than deferred to the host-abstraction-layer rewrite
 * that debt is waiting on - that rewrite was never meant to block a real
 * guest-timing bug fix, only to eventually collect everything like this
 * into one place. */
#ifdef _WIN32
#include <windows.h>
#include <mmsystem.h>
#endif

/* --- physical memory + MMIO dispatch -------------------------------------
 * Every access goes through here so RAM and devices share one address
 * space, the same way real hardware's memory bus works. */

static int in_ram(Machine *m, uint64_t addr, uint64_t size)
{
    return addr >= RAM_BASE && addr + size <= RAM_BASE + m->mem_size;
}

uint8_t phys_read8(Machine *m, uint64_t addr)
{
    if (in_ram(m, addr, 1)) return m->mem[addr - RAM_BASE];
    if (device_covers(addr, UART_BASE, UART_SIZE)) return (uint8_t)uart_read(m, addr, 1);
    if (device_covers(addr, RTC_BASE, RTC_SIZE)) return (uint8_t)rtc_read(m, addr, 1);
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) return (uint8_t)clint_read(m, addr, 1);
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) return (uint8_t)plic_read(m, addr, 1);
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) return (uint8_t)virtio_read(m, addr, 1);
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) return (uint8_t)virtio_net_read(m, addr, 1);
    return 0;
}

uint16_t phys_read16(Machine *m, uint64_t addr)
{
    if (in_ram(m, addr, 2)) {
        uint16_t v;
        memcpy(&v, &m->mem[addr - RAM_BASE], 2);
        return v;
    }
    if (device_covers(addr, UART_BASE, UART_SIZE)) return (uint16_t)uart_read(m, addr, 2);
    if (device_covers(addr, RTC_BASE, RTC_SIZE)) return (uint16_t)rtc_read(m, addr, 2);
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) return (uint16_t)clint_read(m, addr, 2);
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) return (uint16_t)plic_read(m, addr, 2);
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) return (uint16_t)virtio_read(m, addr, 2);
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) return (uint16_t)virtio_net_read(m, addr, 2);
    return 0;
}

uint32_t phys_read32(Machine *m, uint64_t addr)
{
    if (in_ram(m, addr, 4)) {
        uint32_t v;
        memcpy(&v, &m->mem[addr - RAM_BASE], 4);
        return v;
    }
    if (device_covers(addr, UART_BASE, UART_SIZE)) return (uint32_t)uart_read(m, addr, 4);
    if (device_covers(addr, RTC_BASE, RTC_SIZE)) return (uint32_t)rtc_read(m, addr, 4);
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) return (uint32_t)clint_read(m, addr, 4);
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) return (uint32_t)plic_read(m, addr, 4);
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) return (uint32_t)virtio_read(m, addr, 4);
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) return (uint32_t)virtio_net_read(m, addr, 4);
    return 0;
}

uint64_t phys_read64(Machine *m, uint64_t addr)
{
    if (in_ram(m, addr, 8)) {
        uint64_t v;
        memcpy(&v, &m->mem[addr - RAM_BASE], 8);
        return v;
    }
    if (device_covers(addr, UART_BASE, UART_SIZE)) return uart_read(m, addr, 8);
    if (device_covers(addr, RTC_BASE, RTC_SIZE)) return rtc_read(m, addr, 8);
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) return clint_read(m, addr, 8);
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) return plic_read(m, addr, 8);
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) return virtio_read(m, addr, 8);
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) return virtio_net_read(m, addr, 8);
    return 0;
}

void phys_write8(Machine *m, uint64_t addr, uint8_t val)
{
    if (in_ram(m, addr, 1)) { m->mem[addr - RAM_BASE] = val; return; }
    if (device_covers(addr, UART_BASE, UART_SIZE)) { uart_write(m, addr, 1, val); return; }
    if (device_covers(addr, TESTDEV_BASE, TESTDEV_SIZE)) { testdev_write(m, addr, 1, val); return; }
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) { clint_write(m, addr, 1, val); return; }
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) { plic_write(m, addr, 1, val); return; }
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) { virtio_write(m, addr, 1, val); return; }
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) { virtio_net_write(m, addr, 1, val); return; }
}

void phys_write16(Machine *m, uint64_t addr, uint16_t val)
{
    if (in_ram(m, addr, 2)) { memcpy(&m->mem[addr - RAM_BASE], &val, 2); return; }
    if (device_covers(addr, UART_BASE, UART_SIZE)) { uart_write(m, addr, 2, val); return; }
    if (device_covers(addr, TESTDEV_BASE, TESTDEV_SIZE)) { testdev_write(m, addr, 2, val); return; }
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) { clint_write(m, addr, 2, val); return; }
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) { plic_write(m, addr, 2, val); return; }
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) { virtio_write(m, addr, 2, val); return; }
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) { virtio_net_write(m, addr, 2, val); return; }
}

void phys_write32(Machine *m, uint64_t addr, uint32_t val)
{
    if (in_ram(m, addr, 4)) { memcpy(&m->mem[addr - RAM_BASE], &val, 4); return; }
    if (device_covers(addr, UART_BASE, UART_SIZE)) { uart_write(m, addr, 4, val); return; }
    if (device_covers(addr, TESTDEV_BASE, TESTDEV_SIZE)) { testdev_write(m, addr, 4, val); return; }
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) { clint_write(m, addr, 4, val); return; }
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) { plic_write(m, addr, 4, val); return; }
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) { virtio_write(m, addr, 4, val); return; }
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) { virtio_net_write(m, addr, 4, val); return; }
}

void phys_write64(Machine *m, uint64_t addr, uint64_t val)
{
    if (in_ram(m, addr, 8)) { memcpy(&m->mem[addr - RAM_BASE], &val, 8); return; }
    if (device_covers(addr, UART_BASE, UART_SIZE)) { uart_write(m, addr, 8, val); return; }
    if (device_covers(addr, TESTDEV_BASE, TESTDEV_SIZE)) { testdev_write(m, addr, 8, val); return; }
    if (device_covers(addr, CLINT_BASE, CLINT_SIZE)) { clint_write(m, addr, 8, val); return; }
    if (device_covers(addr, PLIC_BASE, PLIC_SIZE)) { plic_write(m, addr, 8, val); return; }
    if (device_covers(addr, VIRTIO_BASE, VIRTIO_SIZE)) { virtio_write(m, addr, 8, val); return; }
    if (device_covers(addr, VIRTIO_NET_BASE, VIRTIO_NET_SIZE)) { virtio_net_write(m, addr, 8, val); return; }
}

/* --- reset / trace -------------------------------------------------------- */
void cpu_reset(Machine *m)
{
    memset(&m->cpu, 0, sizeof(m->cpu));
    m->cpu.pc = RAM_BASE; /* where -kernel/-bios none places a raw image on virt */
    m->cpu.priv = PRIV_M; /* real hardware also resets straight into M-mode */
    /* Unlike mtimecmp (every existing boot path already disarms it itself
     * before relying on it), stimecmp is new and nothing older knows to -
     * defaulting it to 0 from memset would make mtime >= stimecmp true
     * almost immediately after reset, spuriously asserting STIP long
     * before any guest ever touches Sstc at all (confirmed: this broke
     * Phase 4's own CLINT test, which never mentions Sstc, the first time
     * this CSR was added). */
    m->cpu.stimecmp = UINT64_MAX;
}

void cpu_dump(const CPU *c, FILE *out)
{
    static const char *name[32] = {
        "zero","ra","sp","gp","tp","t0","t1","t2",
        "s0","s1","a0","a1","a2","a3","a4","a5",
        "a6","a7","s2","s3","s4","s5","s6","s7",
        "s8","s9","s10","s11","t3","t4","t5","t6",
    };
    static const char *priv_name[4] = { "U", "S", "<reserved>", "M" };
    fprintf(out, "pc=%016llx priv=%s\n", (unsigned long long)c->pc, priv_name[c->priv & 3]);
    for (int i = 0; i < 32; i += 4) {
        fprintf(out, "%-3s=%016llx %-3s=%016llx %-3s=%016llx %-3s=%016llx\n",
                name[i], (unsigned long long)c->x[i],
                name[i + 1], (unsigned long long)c->x[i + 1],
                name[i + 2], (unsigned long long)c->x[i + 2],
                name[i + 3], (unsigned long long)c->x[i + 3]);
    }
    fprintf(out, "mstatus=%016llx mtvec=%016llx mepc=%016llx mcause=%016llx mtval=%016llx\n",
            (unsigned long long)c->mstatus, (unsigned long long)c->mtvec,
            (unsigned long long)c->mepc, (unsigned long long)c->mcause,
            (unsigned long long)c->mtval);
    fprintf(out, "stvec  =%016llx sepc =%016llx scause=%016llx stval =%016llx satp =%016llx\n",
            (unsigned long long)c->stvec, (unsigned long long)c->sepc,
            (unsigned long long)c->scause, (unsigned long long)c->stval,
            (unsigned long long)c->satp);
}

static void unimplemented(Machine *m, uint32_t insn)
{
    fprintf(stderr, "riscv-emu: unimplemented instruction %08x at pc=%016llx\n",
            insn, (unsigned long long)m->cpu.pc);
    m->cpu.halted = 1;
    m->cpu.exit_code = 2;
}

/* --- field extraction -----------------------------------------------------
 * Names match the RISC-V manual's own field names so the decoder reads like
 * the spec tables it implements. */
static inline uint32_t f_opcode(uint32_t i) { return i & 0x7f; }
static inline uint32_t f_rd(uint32_t i)     { return (i >> 7) & 0x1f; }
static inline uint32_t f_funct3(uint32_t i) { return (i >> 12) & 0x7; }
static inline uint32_t f_rs1(uint32_t i)    { return (i >> 15) & 0x1f; }
static inline uint32_t f_rs2(uint32_t i)    { return (i >> 20) & 0x1f; }
static inline uint32_t f_funct7(uint32_t i) { return (i >> 25) & 0x7f; }
static inline uint32_t f_csr(uint32_t i)    { return (i >> 20) & 0xfff; }
static inline uint32_t f_rs3(uint32_t i)    { return (i >> 27) & 0x1f; }   /* R4-type (FMADD family) */
static inline uint32_t f_fmt2(uint32_t i)   { return (i >> 25) & 0x3; }    /* R4-type fmt: 0=S, 1=D */

static inline int64_t imm_i(uint32_t i) { return (int32_t)i >> 20; }
static inline int64_t imm_s(uint32_t i)
{
    uint32_t v = ((i >> 7) & 0x1f) | (((i >> 25) & 0x7f) << 5);
    return (int32_t)(v << 20) >> 20;
}
static inline int64_t imm_b(uint32_t i)
{
    uint32_t v = (((i >> 8) & 0xf) << 1) | (((i >> 25) & 0x3f) << 5) |
                 (((i >> 7) & 0x1) << 11) | (((i >> 31) & 0x1) << 12);
    return (int32_t)(v << 19) >> 19;
}
static inline int64_t imm_u(uint32_t i) { return (int64_t)(int32_t)(i & 0xfffff000u); }
static inline int64_t imm_j(uint32_t i)
{
    uint32_t v = (((i >> 21) & 0x3ff) << 1) | (((i >> 20) & 0x1) << 11) |
                 (((i >> 12) & 0xff) << 12) | (((i >> 31) & 0x1) << 20);
    return (int32_t)(v << 11) >> 11;
}

/* x0 is hardwired to zero: read as zero, writes discarded. Centralizing
 * that here means every instruction below can just "write rd" uniformly. */
static inline uint64_t xr(CPU *c, uint32_t r) { return r == 0 ? 0 : c->x[r]; }
static inline void xw(CPU *c, uint32_t r, uint64_t v) { if (r != 0) c->x[r] = v; }

/* --- F/D register file -----------------------------------------------------
 * f0 has no hardwired-zero case the way x0 does - every one of the 32
 * registers is a plain read/write value. A 32-bit (single-precision) value
 * stored in this 64-bit file is NaN-boxed (upper 32 bits all 1) per spec;
 * f_gets() enforces the spec's "not properly boxed -> treat as canonical
 * NaN" rule on read rather than trusting the upper bits, and f_sets() always
 * writes a correctly-boxed value. Any instruction that writes an f register
 * (or fcsr) sets mstatus.FS to Dirty(3) - real hardware's own rule for
 * software to detect "this context now holds live FP state worth saving". */
static inline void fs_dirty(CPU *c);
static inline double f_getd(CPU *c, uint32_t r) { double d; memcpy(&d, &c->f[r], sizeof d); return d; }
/* Every arithmetic result that is NaN must be *the* canonical NaN (sign
 * clear, only the quiet bit set) per spec - never whatever sign/payload
 * the host's own FPU happened to produce (confirmed to actually differ
 * here: this host's SSE2 gives a NaN with the sign bit set for 0.0/0.0 or
 * +inf+(-inf), and propagates an input NaN's own payload instead of
 * discarding it, where real QEMU's softfloat gives the plain canonical
 * pattern in both cases - see tests/selftest.S test R and this project's
 * own README). f_setd()/f_sets() are used only for genuine *computed*
 * results (FADD/FSUB/FMUL/FDIV/FSQRT/FMADD-family, FCVT.S.D/D.S, FMIN/
 * FMAX) - never for a raw bit move (FLD/FLW, FMV.{W,D}.X, FSGNJ*), which
 * must preserve an input NaN's exact bits and so write `c->f[]` directly
 * instead - so canonicalizing unconditionally here, rather than at each
 * of those call sites individually, is both correct and the one place
 * that can't be forgotten for a future opcode using the same setters. */
static inline void   f_setd(CPU *c, uint32_t r, double d)
{
    if (isnan(d)) { uint64_t nan = 0x7FF8000000000000ull; memcpy(&d, &nan, sizeof d); }
    memcpy(&c->f[r], &d, sizeof d);
    fs_dirty(c);
}
static inline float  f_gets(CPU *c, uint32_t r)
{
    if ((c->f[r] >> 32) != 0xFFFFFFFFull) {
        uint32_t nan = 0x7fc00000u; /* canonical quiet NaN */
        float f; memcpy(&f, &nan, sizeof f); return f;
    }
    uint32_t lo = (uint32_t)c->f[r];
    float f; memcpy(&f, &lo, sizeof f); return f;
}
static inline void f_sets(CPU *c, uint32_t r, float f)
{
    if (isnan(f)) { uint32_t nan = 0x7fc00000u; memcpy(&f, &nan, sizeof f); }
    uint32_t bits; memcpy(&bits, &f, sizeof bits);
    c->f[r] = 0xFFFFFFFF00000000ull | bits;
    fs_dirty(c);
}

/* RISC-V FMIN/FMAX.S/D: IEEE 754-2008 minNum/maxNum semantics - a NaN
 * operand is ignored in favor of the other operand; two NaNs produce the
 * canonical NaN, not whatever the host happens to propagate (f_setd/
 * f_sets above already canonicalize that case, so returning either input
 * NaN here is fine). minNum/maxNum leaves +0 vs -0 "numerically equal"
 * and the choice between them unspecified, but the RISC-V ISA manual
 * pins it down explicitly - FMIN(-0,+0) = -0, FMAX(-0,+0) = +0 - and this
 * host's own libm `fmin`/`fmax` do not reliably honor that (observed:
 * `fmin(-0.0, +0.0) == +0.0` here), so that exact case is handled before
 * ever calling into them rather than trusted to match. */
static inline double rv_fmin_d(double a, double b)
{
    if (isnan(a)) return b;
    if (isnan(b)) return a;
    if (a == 0.0 && b == 0.0) return (signbit(a) || signbit(b)) ? -0.0 : 0.0;
    return fmin(a, b);
}
static inline double rv_fmax_d(double a, double b)
{
    if (isnan(a)) return b;
    if (isnan(b)) return a;
    if (a == 0.0 && b == 0.0) return (!signbit(a) || !signbit(b)) ? 0.0 : -0.0;
    return fmax(a, b);
}
static inline float rv_fmin_s(float a, float b)
{
    if (isnan(a)) return b;
    if (isnan(b)) return a;
    if (a == 0.0f && b == 0.0f) return (signbit(a) || signbit(b)) ? -0.0f : 0.0f;
    return fminf(a, b);
}
static inline float rv_fmax_s(float a, float b)
{
    if (isnan(a)) return b;
    if (isnan(b)) return a;
    if (a == 0.0f && b == 0.0f) return (!signbit(a) || !signbit(b)) ? 0.0f : -0.0f;
    return fmaxf(a, b);
}

/* FCLASS.S/D: the 10-bit classification mask from Table 11.5 of the spec.
 * Built from the raw bit pattern directly rather than host classification
 * macros, since "signaling vs quiet NaN" (the mantissa's MSB) is an RISC-V
 * convention isnan()/fpclassify() know nothing about. */
static uint64_t fclassify_d(double d)
{
    uint64_t bits; memcpy(&bits, &d, sizeof bits);
    int sign = (int)(bits >> 63);
    uint64_t exp = (bits >> 52) & 0x7FFull;
    uint64_t mant = bits & 0xFFFFFFFFFFFFFull;
    if (exp == 0x7FF) {
        if (mant == 0) return sign ? (1ull << 0) : (1ull << 7);              /* +-inf */
        return ((mant >> 51) & 1) ? (1ull << 9) : (1ull << 8);               /* qNaN : sNaN */
    }
    if (exp == 0) {
        if (mant == 0) return sign ? (1ull << 3) : (1ull << 4);              /* +-0 */
        return sign ? (1ull << 2) : (1ull << 5);                             /* +-subnormal */
    }
    return sign ? (1ull << 1) : (1ull << 6);                                  /* +-normal */
}
static uint64_t fclassify_s(float f)
{
    uint32_t bits; memcpy(&bits, &f, sizeof bits);
    int sign = (int)(bits >> 31);
    uint32_t exp = (bits >> 23) & 0xFFu;
    uint32_t mant = bits & 0x7FFFFFu;
    if (exp == 0xFF) {
        if (mant == 0) return sign ? (1ull << 0) : (1ull << 7);
        return ((mant >> 22) & 1) ? (1ull << 9) : (1ull << 8);
    }
    if (exp == 0) {
        if (mant == 0) return sign ? (1ull << 3) : (1ull << 4);
        return sign ? (1ull << 2) : (1ull << 5);
    }
    return sign ? (1ull << 1) : (1ull << 6);
}

/* --- C extension: expand a 16-bit compressed instruction into the exact
 * 32-bit encoding it is defined as an alias for, so the existing decoder
 * above runs it completely unchanged - every RVC instruction is specified
 * that way, so there is no separate semantics to get right here, only the
 * bit-scramble each compressed format uses to pack a narrower immediate
 * or a 3-bit "rs1'/rs2'/rd'" register field (always x8-x15) into 16 bits.
 * Returns 0 (itself an illegal instruction - opcode 0 matches nothing
 * below) for anything genuinely reserved. C.FLD/C.FSD/C.FLDSP/C.FSDSP
 * expand to the equivalent 32-bit FLD/FSD, same as every other compressed
 * load/store here - whether that expansion is actually legal to execute
 * (mstatus.FS != Off) is decided downstream, same as any other FP opcode. */
static uint32_t expand_c(uint16_t ci)
{
    uint32_t op = ci & 3, f3 = (ci >> 13) & 7;
    uint32_t rdp = ((ci >> 2) & 7) + 8;   /* rd'/rs2' in CL/CS/CA forms: x8-x15 */
    uint32_t rs1p = ((ci >> 7) & 7) + 8;  /* rs1' in CL/CS/CB forms: x8-x15 */
    uint32_t rd5 = (ci >> 7) & 0x1f;      /* full 5-bit rd/rs1 field (Q1/Q2) */
    uint32_t rs2_5 = (ci >> 2) & 0x1f;    /* full 5-bit rs2 field (Q2) */
    /* The 6-bit sign-extended immediate shared by ADDI/ADDIW/LI/ANDI/SLLI
     * family compressed forms: {ci[12], ci[6:2]}. */
    int32_t imm6 = (int32_t)(((ci >> 7) & 0x20) | ((ci >> 2) & 0x1f));
    imm6 = (imm6 << 26) >> 26; /* sign-extend from bit 5 */

    if (op == 0) {
        switch (f3) {
        case 0: { /* C.ADDI4SPN rd', nzuimm(sp) -> addi rd', x2, nzuimm */
            uint32_t nz = (((ci >> 7) & 0xf) << 6) | (((ci >> 11) & 3) << 4) |
                          (((ci >> 5) & 1) << 3) | (((ci >> 6) & 1) << 2);
            if (nz == 0) return 0;
            return (nz << 20) | (2u << 15) | (0u << 12) | (rdp << 7) | 0x13;
        }
        case 2: { /* C.LW rd', uimm(rs1') -> lw rd', uimm(rs1') */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 6) & 1) << 2) | (((ci >> 5) & 1) << 6);
            return (u << 20) | (rs1p << 15) | (2u << 12) | (rdp << 7) | 0x03;
        }
        case 3: { /* C.LD rd', uimm(rs1') -> ld rd', uimm(rs1') */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 5) & 3) << 6);
            return (u << 20) | (rs1p << 15) | (3u << 12) | (rdp << 7) | 0x03;
        }
        case 6: { /* C.SW rs1', rs2', uimm -> sw rs2', uimm(rs1') */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 6) & 1) << 2) | (((ci >> 5) & 1) << 6);
            return (((u >> 5) & 0x7f) << 25) | (rdp << 20) | (rs1p << 15) | (2u << 12) |
                   ((u & 0x1f) << 7) | 0x23;
        }
        case 7: { /* C.SD rs1', rs2', uimm -> sd rs2', uimm(rs1') */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 5) & 3) << 6);
            return (((u >> 5) & 0x7f) << 25) | (rdp << 20) | (rs1p << 15) | (3u << 12) |
                   ((u & 0x1f) << 7) | 0x23;
        }
        case 1: { /* C.FLD rd', uimm(rs1') -> fld rd', uimm(rs1') - same immediate as C.LD */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 5) & 3) << 6);
            return (u << 20) | (rs1p << 15) | (3u << 12) | (rdp << 7) | 0x07;
        }
        case 5: { /* C.FSD rs1', rs2', uimm -> fsd rs2', uimm(rs1') - same immediate as C.SD */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 5) & 3) << 6);
            return (((u >> 5) & 0x7f) << 25) | (rdp << 20) | (rs1p << 15) | (3u << 12) |
                   ((u & 0x1f) << 7) | 0x27;
        }
        default: return 0; /* funct3==4: genuinely reserved */
        }
    }

    if (op == 1) {
        switch (f3) {
        case 0: /* C.NOP / C.ADDI rd, rd, imm6 -> addi rd, rd, imm6 */
            return ((uint32_t)imm6 << 20) | (rd5 << 15) | (0u << 12) | (rd5 << 7) | 0x13;
        case 1: /* C.ADDIW rd, rd, imm6 -> addiw rd, rd, imm6 (RV64; rd=0 reserved) */
            if (rd5 == 0) return 0;
            return ((uint32_t)imm6 << 20) | (rd5 << 15) | (0u << 12) | (rd5 << 7) | 0x1b;
        case 2: /* C.LI rd, imm6 -> addi rd, x0, imm6 */
            return ((uint32_t)imm6 << 20) | (0u << 15) | (0u << 12) | (rd5 << 7) | 0x13;
        case 3:
            if (rd5 == 2) { /* C.ADDI16SP -> addi x2, x2, nzimm (10-bit, x16) */
                int32_t nz = (int32_t)((((ci >> 12) & 1) << 9) | (((ci >> 6) & 1) << 4) |
                                        (((ci >> 5) & 1) << 6) | (((ci >> 3) & 3) << 7) |
                                        (((ci >> 2) & 1) << 5));
                nz = (nz << 22) >> 22; /* sign-extend from bit 9 */
                if (nz == 0) return 0;
                return ((uint32_t)nz << 20) | (2u << 15) | (0u << 12) | (2u << 7) | 0x13;
            } else { /* C.LUI rd, nzimm -> lui rd, nzimm (rd != 0, 2) */
                if (rd5 == 0 || imm6 == 0) return 0;
                return (((uint32_t)imm6 & 0xfffff) << 12) | (rd5 << 7) | 0x37;
            }
        case 4: {
            uint32_t hi2 = (ci >> 10) & 3;
            if (hi2 == 0) { /* C.SRLI rd', shamt -> srli rd', rd', shamt */
                uint32_t sh = (((ci >> 12) & 1) << 5) | ((ci >> 2) & 0x1f);
                return (sh << 20) | (rs1p << 15) | (5u << 12) | (rs1p << 7) | 0x13;
            } else if (hi2 == 1) { /* C.SRAI */
                uint32_t sh = (((ci >> 12) & 1) << 5) | ((ci >> 2) & 0x1f);
                return (0x20u << 25) | (sh << 20) | (rs1p << 15) | (5u << 12) | (rs1p << 7) | 0x13;
            } else if (hi2 == 2) { /* C.ANDI rd', rd', imm6 */
                return ((uint32_t)imm6 << 20) | (rs1p << 15) | (7u << 12) | (rs1p << 7) | 0x13;
            } else { /* hi2 == 3: register-register forms on rd'/rs2' */
                uint32_t rs2p = ((ci >> 2) & 7) + 8;
                uint32_t lo2 = (ci >> 5) & 3;
                uint32_t bit12 = (ci >> 12) & 1;
                if (bit12 == 0) {
                    static const uint32_t f3w[4] = { 0, 4, 6, 7 }; /* SUB XOR OR AND */
                    uint32_t funct7 = (lo2 == 0) ? 0x20u : 0;
                    return (funct7 << 25) | (rs2p << 20) | (rs1p << 15) | (f3w[lo2] << 12) |
                           (rs1p << 7) | 0x33;
                } else {
                    if (lo2 == 0) /* C.SUBW rd',rd',rs2' -> subw */
                        return (0x20u << 25) | (rs2p << 20) | (rs1p << 15) | (0u << 12) |
                               (rs1p << 7) | 0x3b;
                    if (lo2 == 1) /* C.ADDW rd',rd',rs2' -> addw */
                        return (rs2p << 20) | (rs1p << 15) | (0u << 12) | (rs1p << 7) | 0x3b;
                    return 0; /* reserved */
                }
            }
        }
        case 5: { /* C.J imm -> jal x0, imm */
            uint32_t raw = (((ci >> 12) & 1) << 11) | (((ci >> 11) & 1) << 4) |
                            (((ci >> 9) & 3) << 8) | (((ci >> 8) & 1) << 10) |
                            (((ci >> 7) & 1) << 6) | (((ci >> 6) & 1) << 7) |
                            (((ci >> 3) & 7) << 1) | (((ci >> 2) & 1) << 5);
            int32_t off = ((int32_t)(raw << 20)) >> 20; /* sign-extend from bit 11 */
            uint32_t u = (uint32_t)off;
            return (((u >> 20) & 1) << 31) | (((u >> 1) & 0x3ff) << 21) |
                   (((u >> 11) & 1) << 20) | (((u >> 12) & 0xff) << 12) | (0u << 7) | 0x6f;
        }
        case 6: case 7: { /* C.BEQZ/C.BNEZ rs1', imm -> beq/bne rs1', x0, imm */
            uint32_t raw = (((ci >> 12) & 1) << 8) | (((ci >> 10) & 3) << 3) |
                            (((ci >> 5) & 3) << 6) | (((ci >> 3) & 3) << 1) |
                            (((ci >> 2) & 1) << 5);
            int32_t off = ((int32_t)(raw << 23)) >> 23; /* sign-extend from bit 8 */
            uint32_t u = (uint32_t)off;
            uint32_t bf3 = (f3 == 6) ? 0u : 1u; /* BEQ=0, BNE=1 */
            return (((u >> 12) & 1) << 31) | (((u >> 5) & 0x3f) << 25) | (0u << 20) |
                   (rs1p << 15) | (bf3 << 12) | (((u >> 1) & 0xf) << 8) | (((u >> 11) & 1) << 7) | 0x63;
        }
        }
    }

    if (op == 2) {
        switch (f3) {
        case 0: { /* C.SLLI rd, rd, shamt -> slli */
            uint32_t sh = (((ci >> 12) & 1) << 5) | ((ci >> 2) & 0x1f);
            if (rd5 == 0) return 0;
            return (sh << 20) | (rd5 << 15) | (1u << 12) | (rd5 << 7) | 0x13;
        }
        case 2: { /* C.LWSP rd, uimm(sp) -> lw rd, uimm(x2) */
            if (rd5 == 0) return 0;
            uint32_t u = (((ci >> 12) & 1) << 5) | (((ci >> 4) & 7) << 2) | (((ci >> 2) & 3) << 6);
            return (u << 20) | (2u << 15) | (2u << 12) | (rd5 << 7) | 0x03;
        }
        case 3: { /* C.LDSP rd, uimm(sp) -> ld rd, uimm(x2) (RV64) */
            if (rd5 == 0) return 0;
            uint32_t u = (((ci >> 12) & 1) << 5) | (((ci >> 5) & 3) << 3) | (((ci >> 2) & 7) << 6);
            return (u << 20) | (2u << 15) | (3u << 12) | (rd5 << 7) | 0x03;
        }
        case 4: {
            uint32_t bit12 = (ci >> 12) & 1;
            if (bit12 == 0) {
                if (rs2_5 == 0) { /* C.JR rd -> jalr x0, 0(rd) */
                    if (rd5 == 0) return 0;
                    return (0u << 20) | (rd5 << 15) | (0u << 12) | (0u << 7) | 0x67;
                } else { /* C.MV rd, rs2 -> add rd, x0, rs2 */
                    return (rs2_5 << 20) | (0u << 15) | (0u << 12) | (rd5 << 7) | 0x33;
                }
            } else {
                if (rd5 == 0 && rs2_5 == 0) /* C.EBREAK */
                    return 0x00100073u;
                if (rs2_5 == 0) /* C.JALR rd -> jalr x1, 0(rd) */
                    return (0u << 20) | (rd5 << 15) | (0u << 12) | (1u << 7) | 0x67;
                /* C.ADD rd, rd, rs2 -> add rd, rd, rs2 */
                return (rs2_5 << 20) | (rd5 << 15) | (0u << 12) | (rd5 << 7) | 0x33;
            }
        }
        case 6: { /* C.SWSP rs2, uimm(sp) -> sw rs2, uimm(x2) */
            uint32_t u = (((ci >> 9) & 0xf) << 2) | (((ci >> 7) & 3) << 6);
            return (((u >> 5) & 0x7f) << 25) | (rs2_5 << 20) | (2u << 15) | (2u << 12) |
                   ((u & 0x1f) << 7) | 0x23;
        }
        case 7: { /* C.SDSP rs2, uimm(sp) -> sd rs2, uimm(x2) (RV64) */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 7) & 7) << 6);
            return (((u >> 5) & 0x7f) << 25) | (rs2_5 << 20) | (2u << 15) | (3u << 12) |
                   ((u & 0x1f) << 7) | 0x23;
        }
        case 1: { /* C.FLDSP rd, uimm(sp) -> fld rd, uimm(x2) - rd=0 is NOT reserved here
                   * (unlike C.LDSP/C.LWSP): f0 is an ordinary usable register, not a
                   * hardwired zero the way x0 is. */
            uint32_t u = (((ci >> 12) & 1) << 5) | (((ci >> 5) & 3) << 3) | (((ci >> 2) & 7) << 6);
            return (u << 20) | (2u << 15) | (3u << 12) | (rd5 << 7) | 0x07;
        }
        case 5: { /* C.FSDSP rs2, uimm(sp) -> fsd rs2, uimm(x2) */
            uint32_t u = (((ci >> 10) & 7) << 3) | (((ci >> 7) & 7) << 6);
            return (((u >> 5) & 0x7f) << 25) | (rs2_5 << 20) | (2u << 15) | (3u << 12) |
                   ((u & 0x1f) << 7) | 0x27;
        }
        default: return 0; /* unreachable: quadrant 2's funct3 space is now fully assigned */
        }
    }

    return 0;
}

/* --- CSRs and traps --------------------------------------------------------
 * mstatus bit positions (RV64). We model exactly the subset Phase 2 needs:
 * the global and previous interrupt-enable/privilege bits that trap entry
 * and MRET/SRET read and write. Everything else (FS/XS/VS, MPRV, SUM, MXR,
 * TVM/TW/TSR, UXL/SXL) is Phase 3+ territory - mostly moot without paging
 * or S-mode doing anything privilege-sensitive yet - and reads as the
 * all-zero reset value until then. */
#define MSTATUS_SIE   (1ull << 1)
#define MSTATUS_MIE   (1ull << 3)
#define MSTATUS_SPIE  (1ull << 5)
#define MSTATUS_MPIE  (1ull << 7)
#define MSTATUS_SPP   (1ull << 8)
#define MSTATUS_MPP_SHIFT 11
#define MSTATUS_MPP   (3ull << MSTATUS_MPP_SHIFT)
#define MSTATUS_SUM   (1ull << 18)  /* permit Supervisor access to User pages */
#define MSTATUS_MXR   (1ull << 19)  /* Make eXecutable Readable (for loads) */
/* FS: the FPU context state (0=Off,1=Initial,2=Clean,3=Dirty). Software
 * (the kernel) manages the Off<->enabled transition itself, via sstatus -
 * see do_csr's existing comment on trap_val and Linux's lazy-FPU-enable
 * handler: an FP opcode executed while FS==Off raises an ordinary illegal-
 * instruction trap (ILLEGAL_INSTRUCTION below checks this explicitly), which
 * the kernel's own trap handler recognizes and answers by setting FS to
 * Initial and retrying the faulting instruction - this emulator does not
 * special-case that sequence at all, it falls straight out of already-
 * correct illegal-instruction + CSR-write plumbing. Any instruction that
 * writes an f register or fcsr then advances FS straight to Dirty(3)
 * (fs_dirty, below) - real hardware's rule, skipping the Initial/Clean
 * distinction software would otherwise use to decide what needs saving;
 * nothing here ever reads FS for anything except "zero or not". */
#define MSTATUS_FS_SHIFT 13
#define MSTATUS_FS    (3ull << MSTATUS_FS_SHIFT)

/* The bits of mstatus that are also visible, under the name sstatus, to
 * S-mode. A write through sstatus touches only these bits of the one
 * underlying register; a read through sstatus exposes only these. */
#define SSTATUS_MASK (MSTATUS_SIE | MSTATUS_SPIE | MSTATUS_SPP | MSTATUS_SUM | MSTATUS_MXR | MSTATUS_FS)

static inline int fp_enabled(CPU *c) { return (int)((c->mstatus & MSTATUS_FS) >> MSTATUS_FS_SHIFT) != 0; }
static inline void fs_dirty(CPU *c) { c->mstatus |= MSTATUS_FS; }

/* sie/sip likewise expose only the S-level interrupt bits of mie/mip. */
#define SIE_MASK ((1ull << 1) | (1ull << 5) | (1ull << 9))
/* Of those, only SSIP (bit 1) is actually writable through sip - confirmed
 * against real qemu-system-riscv64, not assumed: STIP/SEIP are hardware-
 * (or M-mode-) driven pending signals that S-mode can see but not clear on
 * its own. Without Sstc, a hart's documented way to let S-mode manage its
 * own timer interrupt is the SBI "set timer" call specifically *because*
 * this write is not available directly - our first implementation allowed
 * it, which let a differential test hang against real QEMU in a way it
 * never would have against itself. */
#define SIP_WRITABLE_MASK (1ull << 1)

/* Standard cause codes (Privileged spec table) this phase actually raises. */
enum {
    CAUSE_INSTRUCTION_ACCESS_FAULT = 1,
    CAUSE_ILLEGAL_INSTRUCTION = 2,
    CAUSE_BREAKPOINT = 3,
    CAUSE_LOAD_ACCESS_FAULT = 5,
    CAUSE_STORE_ACCESS_FAULT = 7,
    CAUSE_ECALL_FROM_U = 8,
    CAUSE_ECALL_FROM_S = 9,
    CAUSE_ECALL_FROM_M = 11,
};

/* Physical Memory Protection. access: 0 = load, 1 = store, 2 = fetch.
 * M-mode bypasses PMP entirely here (true unless a matching entry is
 * locked, which no guest code in this phase sets - a documented
 * simplification). For S/U-mode, entries are checked in priority order
 * (lowest index wins, matching the spec); if nothing matches, the access
 * is denied - the real-hardware default with zero configured regions,
 * which is exactly why M-mode firmware always sets at least one region
 * up before ever dropping into S-mode. */
static int pmp_allows(CPU *c, uint64_t addr, int access)
{
    if (c->priv == PRIV_M) return 1;

    for (int i = 0; i < 16; i++) {
        uint8_t cfg = c->pmpcfg[i];
        int mode = (cfg >> 3) & 3; /* 0=OFF 1=TOR 2=NA4 3=NAPOT */
        uint64_t lo, hi;

        if (mode == 0) continue;
        if (mode == 1) { /* TOR: [previous pmpaddr, this pmpaddr) */
            lo = (i == 0) ? 0 : (c->pmpaddr[i - 1] << 2);
            hi = c->pmpaddr[i] << 2;
        } else if (mode == 2) { /* NA4: exactly 4 bytes */
            lo = c->pmpaddr[i] << 2;
            hi = lo + 4;
        } else { /* NAPOT: trailing 1s in pmpaddr encode size, then a 0 */
            uint64_t pa = c->pmpaddr[i];
            if (pa == UINT64_MAX) {
                lo = 0; hi = UINT64_MAX; /* the "whole address space" case */
            } else {
                int ones = 0;
                while (ones < 63 && ((pa >> ones) & 1)) ones++;
                uint64_t size = 1ull << (ones + 3);
                lo = (pa & ~((1ull << ones) - 1)) << 2;
                hi = lo + size;
            }
        }

        if (addr < lo || addr >= hi) continue;
        int r = cfg & 1, w = (cfg >> 1) & 1, x = (cfg >> 2) & 1;
        return access == 0 ? r : access == 1 ? w : x;
    }
    return 0;
}

/* Forward declarations: translate() (defined further down, with the rest
 * of Sv39 address translation) is needed here by raise_trap's own
 * RISCV_EMU_SBIDBG ppoll-decoding diagnostic, which must translate a user
 * virtual address before reading the pollfd struct it points to. */
enum { ACCESS_LOAD = 0, ACCESS_STORE = 1, ACCESS_FETCH = 2 };
static int translate(Machine *m, uint64_t va, int access, uint64_t *pa);

/* Enters a trap: saves the interrupted pc and the cause/tval pair into
 * whichever privilege level will handle it, updates that level's status
 * bits the way real hardware does, and redirects pc to its trap vector.
 * is_interrupt distinguishes asynchronous traps (cause's top bit set) from
 * synchronous exceptions - Phase 2 only raises exceptions itself, but
 * Phase 4's timer interrupts will reuse this unchanged. */
static void raise_trap(Machine *m, uint64_t cause, uint64_t tval, int is_interrupt)
{
    CPU *c = &m->cpu;
    uint64_t deleg_reg = is_interrupt ? c->mideleg : c->medeleg;
    int delegated = c->priv != PRIV_M && cause < 64 && ((deleg_reg >> cause) & 1);
    uint64_t full_cause = cause | (is_interrupt ? (1ull << 63) : 0);

    if (getenv("RISCV_EMU_SBIDBG")) {
        if (is_interrupt && cause == 7)
            fprintf(stderr, "[%llu] MTI delivered (mtime=%llu mtimecmp=%llu)\n",
                    (unsigned long long)c->insns, (unsigned long long)c->mtime, (unsigned long long)c->mtimecmp);
        else if (is_interrupt && cause == 5)
            fprintf(stderr, "[%llu] STI delivered\n", (unsigned long long)c->insns);
        else if (is_interrupt && cause == 9)
            fprintf(stderr, "[%llu] SEI check: priv=%d delegated=%d mideleg=%llx\n",
                    (unsigned long long)c->insns, c->priv, delegated, (unsigned long long)deleg_reg);
        else if (!is_interrupt && cause == 9)
            fprintf(stderr, "[%llu] ECALL-from-S: a7(eid)=%llx a6(fid)=%llx a0=%llx\n",
                    (unsigned long long)c->insns, (unsigned long long)c->x[17],
                    (unsigned long long)c->x[16], (unsigned long long)c->x[10]);
        else if (!is_interrupt && cause == 8)
            /* ECALL-from-U: a Linux syscall. a7 is the syscall number (the
             * RISC-V Linux syscall ABI's own convention, not an SBI one -
             * a7 just happens to be the argument register both conventions
             * put their "which function" selector in); a0-a2 are its first
             * three arguments. Logged at entry, not return - do_sret below
             * logs a0 on every SRET, which a reader can correlate back to
             * the syscall number printed just before it for a single-
             * threaded, non-reentrant trace like this one. */
            /* satp (not just its ASID field, which this kernel build never
             * bothers to allocate per-process - every process shows ASID 0)
             * still identifies which process this is: satp's PPN is that
             * process's own root page table, which is necessarily distinct
             * from every other live process's, so a change in the printed
             * satp value across ECALL lines means a context switch really
             * happened, and a repeated value means the same process made
             * two syscalls in a row - a cheap stand-in for a real PID/comm
             * without having to understand this kernel build's task_struct
             * layout. */
            fprintf(stderr, "[%llu] ECALL-from-U (syscall): a7=%llu a0=%llx a1=%llx a2=%llx pc=%016llx satp=%016llx\n",
                    (unsigned long long)c->insns, (unsigned long long)c->x[17],
                    (unsigned long long)c->x[10], (unsigned long long)c->x[11],
                    (unsigned long long)c->x[12], (unsigned long long)c->pc,
                    (unsigned long long)c->satp);
        if (!is_interrupt && cause == 8 && c->x[17] == 73 && c->x[11] >= 1) {
            /* ppoll(fds, nfds, ...): dump the fd/events of each pollfd (the
             * syscall ABI's struct pollfd: s32 fd; s16 events; s16 revents;
             * 8 bytes each) - identifies exactly what a blocking ppoll(...,
             * NULL) with no timeout is actually waiting on. */
            uint64_t fds = c->x[10], nfds = c->x[11];
            for (uint64_t i = 0; i < nfds && i < 8; i++) {
                uint64_t va = fds + i * 8, pa;
                /* fds is a user virtual address - translate it the same way
                 * a real load would, through whatever satp is currently
                 * active, rather than reading it as if it were physical. */
                if (!translate(m, va, ACCESS_LOAD, &pa)) continue;
                uint32_t fd = phys_read32(m, pa);
                uint16_t events = (uint16_t)phys_read16(m, pa + 4);
                fprintf(stderr, "[%llu]   pollfd[%llu]: fd=%d events=%x\n",
                        (unsigned long long)c->insns, (unsigned long long)i, (int)fd, events);
            }
        }
    }

    if (delegated) {
        c->sepc = c->pc;
        c->scause = full_cause;
        c->stval = tval;
        /* SPIE <- SIE; SIE <- 0; SPP <- the mode we were in (U or S only -
         * delegation never happens from M, so SPP never needs to record M). */
        uint64_t spie = (c->mstatus & MSTATUS_SIE) ? 1 : 0;
        c->mstatus = (c->mstatus & ~(MSTATUS_SPIE | MSTATUS_SIE | MSTATUS_SPP))
                   | (spie ? MSTATUS_SPIE : 0)
                   | (c->priv == PRIV_S ? MSTATUS_SPP : 0);
        c->priv = PRIV_S;
        c->pc = c->stvec & ~1ull; /* bit 0 selects vectored mode; Phase 2 only uses direct */
    } else {
        c->mepc = c->pc;
        c->mcause = full_cause;
        c->mtval = tval;
        uint64_t mpie = (c->mstatus & MSTATUS_MIE) ? 1 : 0;
        c->mstatus = (c->mstatus & ~(MSTATUS_MPIE | MSTATUS_MIE | MSTATUS_MPP))
                   | (mpie ? MSTATUS_MPIE : 0)
                   | ((uint64_t)c->priv << MSTATUS_MPP_SHIFT);
        c->priv = PRIV_M;
        c->pc = c->mtvec & ~1ull;
    }
}

static void illegal_instruction(Machine *m, uint32_t insn)
{
    raise_trap(m, CAUSE_ILLEGAL_INSTRUCTION, insn, 0);
}

static void do_mret(Machine *m)
{
    CPU *c = &m->cpu;
    if (c->priv != PRIV_M) { illegal_instruction(m, 0x30200073); return; }
    int mpp = (int)((c->mstatus & MSTATUS_MPP) >> MSTATUS_MPP_SHIFT);
    uint64_t mpie = (c->mstatus & MSTATUS_MPIE) ? 1 : 0;
    c->mstatus = (c->mstatus & ~(MSTATUS_MIE | MSTATUS_MPIE | MSTATUS_MPP))
               | (mpie ? MSTATUS_MIE : 0)
               | MSTATUS_MPIE                 /* MPIE is set to 1 on return */
               | ((uint64_t)PRIV_U << MSTATUS_MPP_SHIFT); /* MPP reset to U (no H-mode) */
    c->priv = mpp;
    c->pc = c->mepc;
}

static void do_sret(Machine *m)
{
    CPU *c = &m->cpu;
    if (c->priv == PRIV_U) { illegal_instruction(m, 0x10200073); return; }
    int spp = (c->mstatus & MSTATUS_SPP) ? PRIV_S : PRIV_U;
    uint64_t spie = (c->mstatus & MSTATUS_SPIE) ? 1 : 0;
    c->mstatus = (c->mstatus & ~(MSTATUS_SIE | MSTATUS_SPIE | MSTATUS_SPP))
               | (spie ? MSTATUS_SIE : 0)
               | MSTATUS_SPIE;
    if (getenv("RISCV_EMU_SBIDBG") && spp == PRIV_U)
        /* Returning to U-mode: if the trap being returned from was a
         * syscall (the common case), a0 here is that syscall's return
         * value per the Linux RISC-V calling convention - correlate with
         * the ECALL-from-U line logged just before it. */
        fprintf(stderr, "[%llu] SRET to U: a0=%llx sepc=%016llx\n",
                (unsigned long long)c->insns, (unsigned long long)c->x[10],
                (unsigned long long)c->sepc);
    c->priv = spp;
    c->pc = c->sepc;
}

/* --- Sv39 address translation --------------------------------------------
 * access: 0 = load, 1 = store, 2 = fetch - matches pmp_allows' convention,
 * and also selects which of the three page-fault causes to raise. (The
 * ACCESS_* enum itself is declared earlier, alongside raise_trap's forward
 * declaration of translate() - not redeclared here to avoid a duplicate
 * enum definition.) */

static uint64_t page_fault_cause(int access)
{
    return access == ACCESS_FETCH ? 12 : access == ACCESS_LOAD ? 13 : 15;
}

/* Walks the guest's own Sv39 page table for one access. Returns 1 and
 * writes *pa on success; returns 0 having already raised the appropriate
 * page fault (with the *virtual* address as tval, matching real hardware)
 * on failure - callers should bail out immediately, the same way they do
 * after pmp_allows() denies an access.
 *
 * M-mode and a satp in Bare mode both mean "no translation": identity. Real
 * hardware's mstatus.MPRV lets M-mode optionally translate on S/U's behalf;
 * no guest code in this phase uses it, so it is not modelled (M always
 * sees physical addresses directly here).
 *
 * A/D bits are auto-set on first access/write rather than trapping for
 * software to set them (Svadu-style) - the spec permits either, and
 * auto-set is far simpler for an interpreter with no separate TLB-fill
 * path to hook the trap into. */
/* TEMPORARY profiling instrumentation - remove once the performance work
 * this backs is done. rdtsc, not a timer/signal, specifically because
 * gprof's own -pg sampling timer produces zero samples on this MinGW/
 * Windows build (confirmed: an actual profiling run's gmon.out had an
 * empty flat profile AND an empty call graph), and a DIY SIGALRM-based
 * sampler would hit the same underlying signal-delivery gap. */
static int prof_enabled = -1;
static uint64_t prof_translate_cycles = 0, prof_checkint_cycles = 0, prof_cpustep_cycles = 0, prof_steps = 0;
static inline int prof_on(void) { if (prof_enabled < 0) prof_enabled = getenv("RISCV_EMU_PROF") != NULL; return prof_enabled; }
__attribute__((destructor))
static void prof_report(void)
{
    if (prof_enabled <= 0) return;
    fprintf(stderr, "[prof] steps=%llu total_cycles=%llu check_interrupt=%llu (%.1f%%) translate=%llu (%.1f%%) rest=%llu (%.1f%%)\n",
            (unsigned long long)prof_steps, (unsigned long long)prof_cpustep_cycles,
            (unsigned long long)prof_checkint_cycles, prof_cpustep_cycles ? 100.0 * prof_checkint_cycles / prof_cpustep_cycles : 0.0,
            (unsigned long long)prof_translate_cycles, prof_cpustep_cycles ? 100.0 * prof_translate_cycles / prof_cpustep_cycles : 0.0,
            (unsigned long long)(prof_cpustep_cycles - prof_checkint_cycles - prof_translate_cycles),
            prof_cpustep_cycles ? 100.0 * (prof_cpustep_cycles - prof_checkint_cycles - prof_translate_cycles) / prof_cpustep_cycles : 0.0);
}
static int translate_impl(Machine *m, uint64_t va, int access, uint64_t *pa);
static inline int translate(Machine *m, uint64_t va, int access, uint64_t *pa)
{
    if (!prof_on()) return translate_impl(m, va, access, pa);
    uint64_t t0 = __builtin_ia32_rdtsc();
    int r = translate_impl(m, va, access, pa);
    prof_translate_cycles += __builtin_ia32_rdtsc() - t0;
    return r;
}
static int translate_impl(Machine *m, uint64_t va, int access, uint64_t *pa)
{
    CPU *c = &m->cpu;
    uint64_t satp = c->satp;
    uint64_t mode = satp >> 60;

    if (c->priv == PRIV_M || mode == 0) { *pa = va; return 1; }
    if (mode != 8) { /* only Sv39 is implemented */
        raise_trap(m, page_fault_cause(access), va, 0);
        return 0;
    }

    /* Canonical check: bits [63:39] must all equal bit 38 (Sv39 addresses
     * are sign-extended from 39 bits, like x86-64's canonical addresses). */
    int64_t top = (int64_t)va >> 38;
    if (top != 0 && top != -1) {
        raise_trap(m, page_fault_cause(access), va, 0);
        return 0;
    }

    uint64_t vpn4k = va >> 12;
    uint32_t tidx = (uint32_t)(vpn4k & 255);
    if (c->tlb[tidx].valid && c->tlb[tidx].vpn == vpn4k && c->tlb[tidx].priv == c->priv) {
        int ok = access == ACCESS_FETCH ? c->tlb[tidx].x
               : access == ACCESS_STORE ? c->tlb[tidx].w
               : c->tlb[tidx].r;
        if (ok) {
            *pa = (c->tlb[tidx].ppn << 12) | (va & 0xfff);
            c->tlb_hits++;
            return 1;
        }
        /* Cached but the wrong permission for this access (e.g. mstatus.MXR
         * changed since this entry was filled): fall through to a real walk
         * rather than trusting a possibly-stale decision. */
    }
    c->tlb_misses++;

    uint64_t base = (satp & 0xFFFFFFFFFFFull) << 12; /* satp's PPN field, 44 bits */
    int level;
    for (level = 2; level >= 0; level--) {
        int shift = 12 + 9 * level;
        uint64_t vpn = (va >> shift) & 0x1FF;
        uint64_t pte_addr = base + vpn * 8;
        uint64_t pte = phys_read64(m, pte_addr); /* table walks are physical, not PMP-checked here */
        uint64_t v = pte & 1, r = (pte >> 1) & 1, w = (pte >> 2) & 1, x = (pte >> 3) & 1;

        if (!v || (!r && w)) { /* invalid, or the reserved R=0,W=1 encoding */
            raise_trap(m, page_fault_cause(access), va, 0);
            return 0;
        }

        if (!r && !x) { /* non-leaf: descend to the next level */
            base = ((pte >> 10) & 0xFFFFFFFFFFFull) << 12;
            continue;
        }

        /* Leaf PTE. */
        int u = (int)((pte >> 4) & 1);
        if (c->priv == PRIV_U) {
            if (!u) { raise_trap(m, page_fault_cause(access), va, 0); return 0; }
        } else if (u && !((c->mstatus >> 18) & 1)) { /* S-mode needs SUM for a U page */
            raise_trap(m, page_fault_cause(access), va, 0);
            return 0;
        }

        int mxr = (int)((c->mstatus >> 19) & 1);
        int ok = access == ACCESS_FETCH ? x
               : access == ACCESS_STORE ? w
               : (r || (x && mxr)); /* MXR: loads may also read an X-only page */
        if (!ok) { raise_trap(m, page_fault_cause(access), va, 0); return 0; }

        uint64_t ppn = (pte >> 10) & 0xFFFFFFFFFFFull;
        if (level > 0) { /* superpage: the low VPN bits of the PPN must be zero */
            uint64_t low_mask = (1ull << (9 * level)) - 1;
            if (ppn & low_mask) { raise_trap(m, page_fault_cause(access), va, 0); return 0; }
        }

        uint64_t new_pte = pte | (1ull << 6); /* A */
        if (access == ACCESS_STORE) new_pte |= (1ull << 7); /* D */
        if (new_pte != pte) phys_write64(m, pte_addr, new_pte);

        uint64_t page_off = va & ((1ull << shift) - 1);
        *pa = (ppn << 12) | page_off;

        c->tlb[tidx].valid = 1;
        c->tlb[tidx].vpn = vpn4k;
        c->tlb[tidx].ppn = (*pa) >> 12;
        c->tlb[tidx].priv = c->priv;
        c->tlb[tidx].r = r || (x && mxr);
        c->tlb[tidx].w = w;
        c->tlb[tidx].x = x;
        return 1;
    }

    raise_trap(m, page_fault_cause(access), va, 0); /* fell through level 0 without a leaf */
    return 0;
}

int cpu_translate_for_debug(Machine *m, uint64_t va, uint64_t *pa)
{
    CPU *c = &m->cpu;
    uint64_t saved_cause = c->scause, saved_tval = c->stval, saved_sepc = c->sepc;
    uint64_t saved_mcause = c->mcause, saved_mtval = c->mtval, saved_mepc = c->mepc;
    int saved_priv = c->priv;
    uint64_t saved_mstatus = c->mstatus, saved_pc = c->pc;
    int ok = translate(m, va, ACCESS_LOAD, pa);
    /* translate() raises a real trap (redirecting pc, priv, etc.) on a
     * fault - undo all of that, since this is a debug peek, not a real
     * memory access the guest asked for. */
    c->scause = saved_cause; c->stval = saved_tval; c->sepc = saved_sepc;
    c->mcause = saved_mcause; c->mtval = saved_mtval; c->mepc = saved_mepc;
    c->priv = saved_priv; c->mstatus = saved_mstatus; c->pc = saved_pc;
    return ok;
}

/* CSR address encoding (Privileged spec): bits[9:8] give the minimum
 * privilege required, bits[11:10] == 3 marks a read-only register. Checking
 * this generically, rather than per-CSR, is what real decode logic does too. */
static inline int csr_min_priv(uint32_t addr) { return (int)((addr >> 8) & 3); }
static inline int csr_is_readonly(uint32_t addr) { return ((addr >> 10) & 3) == 3; }

/* Returns 0 and leaves *out unset on an unimplemented-but-presumably-valid
 * CSR number (treated as illegal, same as real hardware does for anything
 * it doesn't implement); returns 1 with the value otherwise. */
static int csr_read(Machine *m, uint32_t addr, uint64_t *out)
{
    CPU *c = &m->cpu;
    if (addr >= 0x3B0 && addr <= 0x3BF) { *out = c->pmpaddr[addr - 0x3B0]; return 1; }
    switch (addr) {
    case 0x3A0: case 0x3A2: {                                            /* pmpcfg0 / pmpcfg2 */
        int base = (addr == 0x3A0) ? 0 : 8;
        uint64_t v = 0;
        for (int i = 0; i < 8; i++) v |= (uint64_t)c->pmpcfg[base + i] << (8 * i);
        *out = v;
        return 1;
    }
    /* fflags/frm/fcsr: real hardware traps these (same as any FP opcode)
     * while mstatus.FS == Off, since there is no live FP context to read -
     * csr_read returning 0 here makes do_csr raise the same illegal-
     * instruction trap it would for any other unimplemented CSR, which is
     * exactly the behavior wanted. */
    case 0x001: if (!fp_enabled(c)) return 0; *out = c->fcsr & 0x1Full; return 1;        /* fflags */
    case 0x002: if (!fp_enabled(c)) return 0; *out = (c->fcsr >> 5) & 0x7ull; return 1;  /* frm */
    case 0x003: if (!fp_enabled(c)) return 0; *out = c->fcsr & 0xFFull; return 1;        /* fcsr */
    case 0x100: *out = c->mstatus & SSTATUS_MASK; return 1;              /* sstatus */
    case 0x104: *out = c->mie & SIE_MASK; return 1;                      /* sie */
    case 0x105: *out = c->stvec; return 1;
    case 0x106: *out = 0; return 1;                                      /* scounteren: none enabled */
    case 0x140: *out = c->sscratch; return 1;
    case 0x141: *out = c->sepc; return 1;
    case 0x142: *out = c->scause; return 1;
    case 0x143: *out = c->stval; return 1;
    case 0x144: *out = c->mip & SIE_MASK; return 1;                      /* sip */
    case 0x14D: *out = c->stimecmp; return 1;                            /* stimecmp (Sstc) */
    case 0x180: *out = c->satp; return 1;                                /* inert until Phase 3 */

    case 0x300: *out = c->mstatus; return 1;
    case 0x301:                                                          /* misa: RV64 + IMAFDC -
                                     * A and C were already implemented without ever being added
                                     * here (a pre-existing under-report, harmless so far since
                                     * Linux's hwcap detection uses the DTB "riscv,isa" string, not
                                     * this CSR, per the boot log's own "Falling back to deprecated
                                     * riscv,isa"); F/D are added alongside their real implementation. */
                *out = (1ull << 63)
                     | (1ull << ('i' - 'a')) | (1ull << ('m' - 'a')) | (1ull << ('a' - 'a'))
                     | (1ull << ('f' - 'a')) | (1ull << ('d' - 'a')) | (1ull << ('c' - 'a'));
                return 1;
    case 0x302: *out = c->medeleg; return 1;
    case 0x303: *out = c->mideleg; return 1;
    case 0x304: *out = c->mie; return 1;
    case 0x305: *out = c->mtvec; return 1;
    case 0x306: *out = 0; return 1;                                      /* mcounteren */
    case 0x340: *out = c->mscratch; return 1;
    case 0x341: *out = c->mepc; return 1;
    case 0x342: *out = c->mcause; return 1;
    case 0x343: *out = c->mtval; return 1;
    case 0x344: *out = c->mip; return 1;
    case 0xF11: *out = 0; return 1;                                      /* mvendorid */
    case 0xF12: *out = 0; return 1;                                      /* marchid */
    case 0xF13: *out = 0; return 1;                                      /* mimpid */
    case 0xF14: *out = 0; return 1;                                      /* mhartid: single-hart */
    case 0xB00: case 0xC00: *out = c->insns; return 1;                   /* mcycle / cycle (proxy) */
    case 0xC01: *out = c->mtime; return 1;                               /* time: a real hardware
                                     * shadow of CLINT's mtime, not M-mode-gated like mip/mie are -
                                     * Linux's timer driver reads this directly via the "rdtime"
                                     * pseudo-instruction (csrrs rd, time, x0) from S-mode, with no
                                     * SBI call and no M-mode involvement at all. Phase 4 added the
                                     * SBI "set timer" call without this; without it, nothing ever
                                     * reads the clock Phase 4 built, which stalls the scheduler. */
    case 0xB02: case 0xC02: *out = c->insns; return 1;                   /* minstret / instret */
    default: return 0;
    }
}

static int csr_write(Machine *m, uint32_t addr, uint64_t val)
{
    CPU *c = &m->cpu;
    if (addr >= 0x3B0 && addr <= 0x3BF) {
        int i = (int)(addr - 0x3B0);
        if (!(c->pmpcfg[i] & 0x80)) c->pmpaddr[i] = val; /* locked entries ignore writes */
        return 1;
    }
    switch (addr) {
    case 0x3A0: case 0x3A2: {                                            /* pmpcfg0 / pmpcfg2 */
        int base = (addr == 0x3A0) ? 0 : 8;
        for (int i = 0; i < 8; i++) {
            if (c->pmpcfg[base + i] & 0x80) continue;    /* locked: ignore this byte's write */
            c->pmpcfg[base + i] = (uint8_t)(val >> (8 * i));
        }
        return 1;
    }
    case 0x001: if (!fp_enabled(c)) return 0; c->fcsr = (c->fcsr & ~0x1Full) | (val & 0x1Full); return 1;
    case 0x002: if (!fp_enabled(c)) return 0; c->fcsr = (c->fcsr & ~0xE0ull) | ((val & 0x7ull) << 5); return 1;
    case 0x003: if (!fp_enabled(c)) return 0; c->fcsr = val & 0xFFull; return 1;
    case 0x100: c->mstatus = (c->mstatus & ~SSTATUS_MASK) | (val & SSTATUS_MASK); return 1;
    case 0x104: c->mie = (c->mie & ~SIE_MASK) | (val & SIE_MASK); return 1;
    case 0x105: c->stvec = val; return 1;
    case 0x106: return 1;                                                /* scounteren: ignored */
    case 0x140: c->sscratch = val; return 1;
    case 0x141: c->sepc = val; return 1;
    case 0x142: c->scause = val; return 1;
    case 0x143: c->stval = val; return 1;
    case 0x144: c->mip = (c->mip & ~SIP_WRITABLE_MASK) | (val & SIP_WRITABLE_MASK); return 1;
    case 0x14D: /* stimecmp (Sstc) */
        c->stimecmp = val;
        /* STIP is a level signal driven by mtime >= stimecmp: writing a
         * comparator value the current time has not reached yet must drop
         * the bit immediately, not wait for next step's recompute (which
         * only ever sets it, never clears it - see check_interrupt). */
        if (c->mtime < c->stimecmp) c->mip &= ~(1ull << 5);
        return 1;
    case 0x180: { /* satp */
        /* Real hardware silently ignores a write that requests an
         * unsupported MODE - the whole write has no effect, not just the
         * mode field - which is how Linux's head.S probes for the best
         * paging mode it can use: it speculatively writes Sv57, then Sv48,
         * then Sv39, reading satp back after each to see whether the mode
         * actually "stuck"; only Sv39 (8) is implemented here, so anything
         * else must be rejected exactly that way, not accepted and left to
         * fault on the very next fetch with no way for the kernel to tell
         * the difference from a real translation failure. */
        uint64_t req_mode = val >> 60;
        if (req_mode == 0 || req_mode == 8) {
            c->satp = val;
            memset(c->tlb, 0, sizeof(c->tlb)); /* new address space or mode:
                every cached translation is for the old one */
        }
        return 1;
    }

    case 0x300: c->mstatus = val; return 1;
    case 0x301: return 1;                                                /* misa: fixed, writes ignored */
    case 0x302: c->medeleg = val; return 1;
    case 0x303: c->mideleg = val; return 1;
    case 0x304: c->mie = val; return 1;
    case 0x305: c->mtvec = val; return 1;
    case 0x306: return 1;                                                /* mcounteren: ignored */
    case 0x340: c->mscratch = val; return 1;
    case 0x341: c->mepc = val; return 1;
    case 0x342: c->mcause = val; return 1;
    case 0x343: c->mtval = val; return 1;
    case 0x344: c->mip = val; return 1;
    default: return 0;
    }
}

/* Executes one CSRR{W,S,C}[I]. funct3's low 2 bits select the operation
 * (01 write, 10 set, 11 clear); bit 2 selects the immediate (zimm, taken
 * from the rs1 field instead of a register) forms. Returns 0 if it raised
 * an illegal-instruction trap (the caller must return immediately without
 * touching pc - raise_trap already pointed it at the trap handler - the
 * same contract do_mret/do_sret's own callers already follow; this one
 * used to fall through to the normal `pc = next_pc` advance instead,
 * silently overwriting that redirect - see this project's own README for
 * how a differential test on S-mode CSR access against real QEMU found
 * it), 1 on ordinary completion. */
static int do_csr(Machine *m, uint32_t insn, uint32_t rd, uint32_t rs1, uint32_t funct3)
{
    CPU *c = &m->cpu;
    uint32_t addr = f_csr(insn);
    uint64_t old;

    if (csr_min_priv(addr) > c->priv || !csr_read(m, addr, &old)) {
        illegal_instruction(m, insn);
        return 0;
    }

    uint64_t operand = (funct3 & 4) ? rs1 : xr(c, rs1); /* zimm vs register */
    int writes = (funct3 & 3) == 1 || rs1 != 0; /* CSRRW always writes; S/C only if operand != 0 */
    uint64_t newval;
    switch (funct3 & 3) {
    case 1: newval = operand; break;                /* CSRRW[I] */
    case 2: newval = old | operand; break;           /* CSRRS[I] */
    case 3: newval = old & ~operand; break;          /* CSRRC[I] */
    default: illegal_instruction(m, insn); return 0;
    }

    if (writes) {
        if (csr_is_readonly(addr) || !csr_write(m, addr, newval)) {
            illegal_instruction(m, insn);
            return 0;
        }
    }
    /* CSRRW historically skips the read for rd=x0 to avoid an unwanted
     * side effect on a write-only-intended access; our reads have no side
     * effects, so always producing the value is simpler and equivalent. */
    xw(c, rd, old);
    return 1;
}

/* --- real-time mtime / WFI sleep -----------------------------------------
 * See emu.h's own remarks on Machine.deterministic_time/time_origin_qpc/
 * qpc_frequency for the why; this is the how. */
static int check_interrupt(Machine *m, uint64_t *cause); /* defined below; wfi_sleep_realtime calls it to reuse its mip/mie gating instead of duplicating it */
#ifdef _WIN32
void realtime_clock_init(Machine *m)
{
    LARGE_INTEGER freq, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&now);
    m->qpc_frequency = (uint64_t)freq.QuadPart;
    m->time_origin_qpc = (uint64_t)now.QuadPart;

    /* Without this, Sleep(1) in wfi_sleep_realtime below commonly actually
     * sleeps ~15.6ms on Windows (the default system timer tick), not 1ms -
     * confirmed the hard way: a first version of this code without the
     * call produced a real RCU stall warning in the guest kernel log
     * ("rcu: Unless rcu_sched kthread gets sufficient CPU time, OOM is
     * now expected behavior"), because every WFI wake was arriving over
     * 10x later than the 1ms the sleep loop asked for, starving the
     * guest's own scheduler tick whenever the CPU sat idle. Process-wide
     * for this process's whole lifetime - never paired with
     * timeEndPeriod, since nothing else here needs the coarser default
     * back before the process exits anyway (Windows reverts it then
     * regardless). */
    timeBeginPeriod(1);
}

/* Elapsed host time since realtime_clock_init's own reading, rescaled from
 * whatever this host's QueryPerformanceCounter frequency actually is to
 * the fixed 10MHz this machine's DTB advertises (dtb.c's
 * "timebase-frequency" property) - mtime is defined architecturally as a
 * free-running counter at that frequency, so guest software's own
 * ticks-to-seconds math (sleep(), ping's 1-second interval, the
 * clocksource driver backing gettimeofday/jiffies) only comes out at the
 * right real-world pace if this scaling is right. Double precision, not
 * exact integer scaling with __int128 or similar: a double's 52-bit
 * mantissa holds a 10MHz count exactly for over 28 years of continuous
 * uptime before it could even begin to lose a single tick of precision -
 * nowhere close to a concern for a guest that runs for minutes or hours. */
static uint64_t realtime_mtime(Machine *m)
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    uint64_t elapsed = (uint64_t)now.QuadPart - m->time_origin_qpc;
    return (uint64_t)((double)elapsed * 10000000.0 / (double)m->qpc_frequency);
}

/* Real time's whole point: stop burning a full host CPU core spinning
 * through the guest's own idle loop (WFI is architecturally a no-op this
 * emulator was already free to implement as "do nothing, keep going," so
 * the guest's own "wfi; loop" idle code re-executed it billions of times a
 * second with nothing to actually interrupt it) and sleep in small bounded
 * slices instead, waking on whichever comes first: a real interrupt
 * becoming pending+enabled (reusing check_interrupt itself - the same
 * mip/mie/mideleg/mstatus gating logic cpu_step_impl already relies on for
 * this, not a second, parallel copy of it that could drift from the real
 * one and get some edge case wrong), or new UART input. Network sockets
 * still get serviced on every slice via net_poll, exactly as the main loop
 * already does between instructions - WFI pausing the CPU core was never
 * meant to pause device I/O the guest is waiting on. The 1ms sleep
 * granularity bounds worst-case wake latency to about that much
 * regardless of how any of the above timing math is interpreted - small
 * enough to be imperceptible interactively, and nowhere near the cost
 * instruction-by-instruction spinning was paying. */
static void wfi_sleep_realtime(Machine *m)
{
    CPU *c = &m->cpu;
    uint64_t dummy_cause;
    for (;;) {
        if (m->net) net_poll(m);
        if (console_input_ready(m)) return;
        c->mtime = realtime_mtime(m);
        /* check_interrupt's own return value answers "would a trap
         * actually be taken right now" - it also gates on mstatus.MIE/
         * SIE and mideleg-based privilege targeting, which is exactly
         * right for cpu_step_impl's use of it (deciding whether to
         * raise_trap) but wrong for WFI's own wake condition: the spec
         * has WFI resume on any interrupt that is both pending (mip) and
         * locally enabled (mie), regardless of whether the global
         * mstatus.{M,S}IE bit happens to be clear at that exact moment -
         * global IE only decides whether a taken trap is deliverable,
         * not whether WFI is allowed to stop waiting. Getting this wrong
         * is a real deadlock, not a theoretical one: software is free to
         * execute wfi with its own global IE momentarily clear (e.g.
         * right before an atomic "enable and wait" sequence), fully
         * expecting to resume at the next instruction and re-enable it
         * itself - a WFI that insists on the global-IE-gated condition
         * instead can sleep forever right there, since nothing is left
         * running to ever flip that bit back on. Confirmed by reproducing
         * exactly this: a real guest kernel's idle hart parked here with
         * mstatus.SIE momentarily 0 while Sstc's STIP was already pending
         * and enabled in mie, triggering an RCU stall (the guest's
         * scheduler tick never arriving) until an unrelated external
         * event (console input) forced a wake through a different path
         * below - call check_interrupt purely for its side effect of
         * refreshing mip's live bits (MTIP/STIP/SEIP), and use the
         * simpler mip & mie directly for the actual wake decision. */
        (void)check_interrupt(m, &dummy_cause);
        if (c->mip & c->mie) return;
        Sleep(1);
    }
}
#else
void realtime_clock_init(Machine *m) { (void)m; }
static uint64_t realtime_mtime(Machine *m) { (void)m; return 0; }
static void wfi_sleep_realtime(Machine *m) { (void)m; }
#endif

/* Spec-mandated priority among simultaneously pending+enabled interrupts,
 * highest first. We only ever produce MSI(3)/MTI(7) from real hardware
 * (CLINT) plus SSI(1)/STI(5) when M-mode software injects them (mip is a
 * plain CSR M-mode can write any bit of, same as real hardware's SBI
 * firmware converting a CLINT timer tick into a supervisor-visible one).
 * MEI/SEI(11/9) stay permanently zero without a PLIC; listed anyway so
 * adding one later does not need this reordered. */
static int check_interrupt(Machine *m, uint64_t *cause)
{
    CPU *c = &m->cpu;
    static const int priority[] = { 11, 3, 7, 9, 1, 5 };

    if (c->mtime >= c->mtimecmp) c->mip |= (1ull << 7); else c->mip &= ~(1ull << 7);
    if (c->msip) c->mip |= (1ull << 3); else c->mip &= ~(1ull << 3);
    /* Sstc's STIP: unlike MTIP above, this only ever sets the bit here -
     * clearing it is stimecmp's own write side's job (csr_write case
     * 0x14D), since this bit is shared with the legacy MTI->STI software
     * latch that firmware's trap handler sets directly and expects to stay
     * set until SBI set_timer clears it; auto-clearing it here on every
     * step whenever stimecmp happens to be in the past would stomp on that
     * unrelated mechanism. */
    if (c->mtime >= c->stimecmp) c->mip |= (1ull << 5);

    /* SEIP (bit 9): driven by the PLIC's S-mode context (context 1) exactly
     * the way MTIP above is driven by CLINT - recomputed fresh every step,
     * not something software sets directly, since this is the hart's live
     * external-interrupt-line input, not a latch. */
    if (plic_claim_peek(m, 1) != 0) c->mip |= (1ull << 9); else c->mip &= ~(1ull << 9);

    uint64_t pending = c->mip & c->mie;
    if (!pending) return 0;

    for (size_t i = 0; i < sizeof(priority) / sizeof(priority[0]); i++) {
        int bit = priority[i];
        if (!((pending >> bit) & 1)) continue;

        int delegated = (c->mideleg >> bit) & 1;
        int target = delegated ? PRIV_S : PRIV_M;
        int enabled;
        if (c->priv > target) enabled = 0;            /* above the target level: masked entirely */
        else if (c->priv < target) enabled = 1;        /* below it: a higher-target interrupt always wins */
        else enabled = (target == PRIV_M) ? (c->mstatus & MSTATUS_MIE) != 0
                                          : (c->mstatus & MSTATUS_SIE) != 0;
        if (enabled) { *cause = (uint64_t)bit; return 1; }
    }
    return 0;
}

/* LOAD/STORE's own translate()+phys_read/writeN pattern assumed a
 * multi-byte access never needs more than one translate() call - true
 * for the address itself, but wrong for what comes after it: this
 * emulator's "physical memory" is one flat host array, so two
 * *physically* adjacent guest addresses are also host-adjacent, but two
 * *virtually* adjacent ones are not guaranteed to be physically adjacent
 * at all once a real page table is involved. An 8-byte access starting
 * within 7 bytes of a page boundary (not required to be aligned - RISC-V
 * permits unaligned ordinary loads/stores, only AMO/LR/SC require it, and
 * alignment to a page-size-dividing power of two provably can't cross a
 * page at all, which is why they're untouched here) would translate only
 * its first byte's address and then read/write the rest contiguously in
 * the host array - silently landing in whatever guest physical page
 * happens to sit next to the first one in *this emulator's own* layout,
 * not necessarily the one the second virtual page's own PTE actually
 * names. Confirmed to be a real, live bug, not a theoretical one: a
 * userspace `read()` buffer (wget downloading into a non-page-aligned
 * heap allocation - the "compiler-emitted code keeps naturally-aligned
 * accesses within one page" assumption this comment used to make holds
 * for a *compiler's own* stack/struct accesses, never for arbitrary
 * heap buffer alignment a userspace allocator hands out) produced
 * exactly this: silent corruption at a position that depends only on
 * the buffer's own page alignment - deterministic in *where*, different
 * in *what* each run (whatever guest-physical page happened to sit next
 * door, stale from whatever was there before) - that survived a TCP
 * checksum (computed correctly over what this NAT actually sent,
 * confirmed by capturing it directly) because the corruption happens
 * well after the kernel already validated and accepted the segment.
 * Falls back to a byte-at-a-time path - translating, PMP-checking, and
 * fault-checking every single byte independently - only when an access
 * actually straddles a page, which real compiler/libc code mostly
 * doesn't do, so the fast single-translate path above stays exactly as
 * cheap as it already was for the overwhelmingly common case. */
static int mem_load(Machine *m, uint64_t va, int access, int size, uint64_t *val)
{
    CPU *c = &m->cpu;
    uint64_t pa;
    if (((va & 0xFFF) + (uint64_t)size) <= 0x1000) {
        if (!translate(m, va, access, &pa)) return 0;
        if (!pmp_allows(c, pa, 0)) {
            raise_trap(m, CAUSE_LOAD_ACCESS_FAULT, va, 0);
            return 0;
        }
        switch (size) {
        case 1: *val = phys_read8(m, pa); break;
        case 2: *val = phys_read16(m, pa); break;
        case 4: *val = phys_read32(m, pa); break;
        default: *val = phys_read64(m, pa); break;
        }
        return 1;
    }
    /* Page-crossing path: every byte's own address is translated and PMP-
     * checked *before* any byte is actually read, not interleaved one at
     * a time - a fault on a later byte must leave no trace of the access
     * at all, matching the architectural guarantee that a faulting load
     * is a no-op (not just "rd stays unwritten," already true either way
     * here since *val is never touched before this function's very last
     * line, but a device region crossed into partway through - unlikely,
     * but not impossible once an access is already this unusual - could
     * have a real read side effect, e.g. draining a FIFO, that must not
     * happen for a byte the access never ends up legitimately completing). */
    uint64_t pas[8];
    for (int i = 0; i < size; i++) {
        if (!translate(m, va + (uint64_t)i, access, &pas[i])) return 0;
        if (!pmp_allows(c, pas[i], 0)) {
            raise_trap(m, CAUSE_LOAD_ACCESS_FAULT, va, 0);
            return 0;
        }
    }
    uint64_t tmp = 0;
    for (int i = 0; i < size; i++) tmp |= (uint64_t)phys_read8(m, pas[i]) << (8 * i);
    *val = tmp;
    return 1;
}
static int mem_store(Machine *m, uint64_t va, int access, int size, uint64_t val)
{
    CPU *c = &m->cpu;
    uint64_t pa;
    if (((va & 0xFFF) + (uint64_t)size) <= 0x1000) {
        if (!translate(m, va, access, &pa)) return 0;
        if (!pmp_allows(c, pa, 1)) {
            raise_trap(m, CAUSE_STORE_ACCESS_FAULT, va, 0);
            return 0;
        }
        switch (size) {
        case 1: phys_write8(m, pa, (uint8_t)val); break;
        case 2: phys_write16(m, pa, (uint16_t)val); break;
        case 4: phys_write32(m, pa, (uint32_t)val); break;
        default: phys_write64(m, pa, val); break;
        }
        return 1;
    }
    /* Same reasoning as mem_load's own crossing path, but more critical
     * here: a store that faults partway through must not have modified
     * memory *at all* - not even the bytes on the page that translated
     * fine - since nothing guarantees the faulting instruction is ever
     * retried verbatim (or retried at all) the way real hardware's own
     * precise-exception model assumes. Translate and PMP-check every
     * byte's address first; only once every one of them is confirmed
     * legal does any byte actually get written. */
    uint64_t pas[8];
    for (int i = 0; i < size; i++) {
        if (!translate(m, va + (uint64_t)i, access, &pas[i])) return 0;
        if (!pmp_allows(c, pas[i], 1)) {
            raise_trap(m, CAUSE_STORE_ACCESS_FAULT, va, 0);
            return 0;
        }
    }
    for (int i = 0; i < size; i++) phys_write8(m, pas[i], (uint8_t)(val >> (8 * i)));
    return 1;
}

static int cpu_step_impl(Machine *m);
int cpu_step(Machine *m)
{
    if (!prof_on()) return cpu_step_impl(m);
    uint64_t t0 = __builtin_ia32_rdtsc();
    int r = cpu_step_impl(m);
    prof_cpustep_cycles += __builtin_ia32_rdtsc() - t0;
    prof_steps++;
    return r;
}
static int cpu_step_impl(Machine *m)
{
    CPU *c = &m->cpu;
    uint64_t pc = c->pc;
    uint64_t fetch_pa;
    uint64_t int_cause;

    /* Deterministic mode: unchanged from Phase 4 - one tick per retired
     * instruction, exactly what every differential/MD5 test already
     * verified. Real-time mode (the default): refreshed from the host
     * clock every 4096 instructions, not every single one - cheap enough
     * at that cadence to be free, but a QueryPerformanceCounter() call per
     * *instruction* (tens of millions a second) would be real, measurable
     * overhead for no benefit, since nothing downstream needs mtime any
     * fresher than that (the coarsest thing driving a decision from it,
     * net_poll's own cadence just below, already uses this exact mask). */
    if (m->deterministic_time) {
        c->mtime++;
    } else if ((c->insns & 0xFFFull) == 0) {
        c->mtime = realtime_mtime(m);
    }
    {
        uint64_t ci0 = prof_on() ? __builtin_ia32_rdtsc() : 0;
        int pending = check_interrupt(m, &int_cause);
        if (prof_on()) prof_checkint_cycles += __builtin_ia32_rdtsc() - ci0;
        if (pending) {
            raise_trap(m, int_cause, 0, 1);
            return c->halted;
        }
    }

    /* With the C extension, instructions are only 2-byte aligned, not
     * 4-byte: a 32-bit instruction can start right before a page boundary
     * and have its second half on a different physical page, so the two
     * halfwords are translated and fault-checked independently rather than
     * fetched as a single 4-byte unit. */
    if (!translate(m, pc, ACCESS_FETCH, &fetch_pa)) return c->halted;
    if (!pmp_allows(c, fetch_pa, 2)) {
        raise_trap(m, CAUSE_INSTRUCTION_ACCESS_FAULT, pc, 0);
        return c->halted;
    }
    uint16_t lo16 = phys_read16(m, fetch_pa);

    uint32_t insn;
    uint64_t next_pc;
    if ((lo16 & 3) != 3) {
        insn = expand_c(lo16);
        next_pc = pc + 2;
    } else {
        /* pc+2's own translation only ever needs to actually run when it
         * can land on a *different* physical page than pc's own fetch
         * just resolved - true only in the last 2 bytes of a page
         * (offset 0xFFE/0xFFF), the rare case the comment above is about.
         * Every other time (measured: the overwhelming majority of 4-byte
         * fetches), pc+2 is provably on the same page translate() just
         * walked/TLB-hit for pc itself, so its physical address is just
         * fetch_pa+2 - deriving that directly skips a second full TLB
         * lookup (plus permission re-check) that a real differential
         * test confirmed could only ever recompute the identical answer,
         * never a different one, for this case specifically. PMP is
         * still re-checked for the derived address, not assumed, since a
         * PMP region's own granularity (down to 4 bytes via NAPOT) is not
         * guaranteed to match a 4KB page's, unlike the TLB's own per-page
         * permission cache above. */
        uint64_t hi_pa;
        if ((pc & 0xFFF) < 0xFFE) {
            hi_pa = fetch_pa + 2;
        } else if (!translate(m, pc + 2, ACCESS_FETCH, &hi_pa)) {
            return c->halted;
        }
        if (!pmp_allows(c, hi_pa, 2)) {
            raise_trap(m, CAUSE_INSTRUCTION_ACCESS_FAULT, pc + 2, 0);
            return c->halted;
        }
        uint16_t hi16 = phys_read16(m, hi_pa);
        insn = (uint32_t)lo16 | ((uint32_t)hi16 << 16);
        next_pc = pc + 4;
    }
    uint32_t opcode = f_opcode(insn);
    uint32_t rd = f_rd(insn), rs1 = f_rs1(insn), rs2 = f_rs2(insn);
    uint32_t funct3 = f_funct3(insn), funct7 = f_funct7(insn);
    /* mtval/stval for an illegal instruction must hold the actual faulting
     * bits - 16 bits, zero-extended, for a compressed one - not our
     * synthesized 32-bit expansion. Linux's own FPU-lazy-enable trap
     * handler decodes this value to tell a real SIGILL apart from a first
     * FP instruction it should silently allow, so getting it wrong here
     * would misfire that logic, not just cosmetically mislabel a trap. */
    uint32_t trap_val = (next_pc == pc + 2) ? (uint32_t)lo16 : insn;

    if (m->trace) {
        fprintf(stderr, "pc=%016llx insn=%08x\n", (unsigned long long)pc, insn);
    }
    c->insns++;

    switch (opcode) {
    case 0x37: /* LUI */
        xw(c, rd, (uint64_t)imm_u(insn));
        break;
    case 0x17: /* AUIPC */
        xw(c, rd, pc + (uint64_t)imm_u(insn));
        break;
    case 0x6F: /* JAL */
        xw(c, rd, next_pc);
        next_pc = pc + (uint64_t)imm_j(insn);
        break;
    case 0x67: /* JALR */
        if (funct3 == 0) {
            uint64_t target = (xr(c, rs1) + (uint64_t)imm_i(insn)) & ~1ull;
            xw(c, rd, next_pc);
            next_pc = target;
        } else {
            illegal_instruction(m, trap_val);
            return c->halted;
        }
        break;
    case 0x63: { /* BRANCH */
        uint64_t a = xr(c, rs1), b = xr(c, rs2);
        int take;
        switch (funct3) {
        case 0: take = a == b; break;                        /* BEQ */
        case 1: take = a != b; break;                         /* BNE */
        case 4: take = (int64_t)a < (int64_t)b; break;         /* BLT */
        case 5: take = (int64_t)a >= (int64_t)b; break;        /* BGE */
        case 6: take = a < b; break;                           /* BLTU */
        case 7: take = a >= b; break;                          /* BGEU */
        default: illegal_instruction(m, trap_val); return c->halted;
        }
        if (take) next_pc = pc + (uint64_t)imm_b(insn);
        break;
    }
    case 0x03: { /* LOAD */
        uint64_t addr = xr(c, rs1) + (uint64_t)imm_i(insn);
        int size = funct3 == 0 || funct3 == 4 ? 1 : funct3 == 1 || funct3 == 5 ? 2
                 : funct3 == 2 || funct3 == 6 ? 4 : 8;
        if (funct3 > 6) { illegal_instruction(m, trap_val); return c->halted; }
        uint64_t raw;
        if (!mem_load(m, addr, ACCESS_LOAD, size, &raw)) return c->halted;
        switch (funct3) {
        case 0: xw(c, rd, (uint64_t)(int64_t)(int8_t)raw); break;   /* LB */
        case 1: xw(c, rd, (uint64_t)(int64_t)(int16_t)raw); break;  /* LH */
        case 2: xw(c, rd, (uint64_t)(int64_t)(int32_t)raw); break;  /* LW */
        case 3: xw(c, rd, raw); break;                              /* LD */
        case 4: xw(c, rd, (uint8_t)raw); break;                     /* LBU */
        case 5: xw(c, rd, (uint16_t)raw); break;                    /* LHU */
        case 6: xw(c, rd, (uint32_t)raw); break;                    /* LWU */
        default: illegal_instruction(m, trap_val); return c->halted;
        }
        break;
    }
    case 0x23: { /* STORE */
        uint64_t addr = xr(c, rs1) + (uint64_t)imm_s(insn);
        uint64_t val = xr(c, rs2);
        if (funct3 > 3) { illegal_instruction(m, trap_val); return c->halted; }
        int size = funct3 == 0 ? 1 : funct3 == 1 ? 2 : funct3 == 2 ? 4 : 8;
        if (!mem_store(m, addr, ACCESS_STORE, size, val)) return c->halted;
        c->reservation_valid = 0; /* any store can invalidate an LR reservation;
                                    * over-invalidating is always architecturally
                                    * legal, just occasionally pessimistic */
        break;
    }
    case 0x13: { /* OP-IMM */
        uint64_t a = xr(c, rs1);
        int64_t imm = imm_i(insn);
        uint32_t shamt = (uint32_t)imm & 0x3f; /* RV64 shifts take 6 bits */
        switch (funct3) {
        case 0: xw(c, rd, a + (uint64_t)imm); break;                        /* ADDI */
        case 2: xw(c, rd, (int64_t)a < imm); break;                         /* SLTI */
        case 3: xw(c, rd, a < (uint64_t)imm); break;                        /* SLTIU */
        case 4: xw(c, rd, a ^ (uint64_t)imm); break;                        /* XORI */
        case 6: xw(c, rd, a | (uint64_t)imm); break;                        /* ORI */
        case 7: xw(c, rd, a & (uint64_t)imm); break;                        /* ANDI */
        case 1: xw(c, rd, a << shamt); break;                               /* SLLI */
        case 5:
            if (funct7 & 0x20) xw(c, rd, (uint64_t)((int64_t)a >> shamt));  /* SRAI */
            else xw(c, rd, a >> shamt);                                     /* SRLI */
            break;
        default: illegal_instruction(m, trap_val); return c->halted;
        }
        break;
    }
    case 0x1B: { /* OP-IMM-32: RV64 word-sized immediate ops */
        uint32_t a = (uint32_t)xr(c, rs1);
        int64_t imm = imm_i(insn);
        uint32_t shamt = (uint32_t)imm & 0x1f; /* word shifts take 5 bits */
        int32_t res;
        switch (funct3) {
        case 0: res = (int32_t)(a + (uint32_t)imm); break;                  /* ADDIW */
        case 1: res = (int32_t)(a << shamt); break;                         /* SLLIW */
        case 5:
            if (funct7 & 0x20) res = (int32_t)a >> shamt;                   /* SRAIW */
            else res = (int32_t)(a >> shamt);                               /* SRLIW */
            break;
        default: illegal_instruction(m, trap_val); return c->halted;
        }
        xw(c, rd, (uint64_t)(int64_t)res);
        break;
    }
    case 0x33: { /* OP: register-register, 64-bit width */
        uint64_t a = xr(c, rs1), b = xr(c, rs2);
        if (funct7 == 0x01) { /* M extension */
            __int128 wide;
            switch (funct3) {
            case 0: xw(c, rd, a * b); break;                                          /* MUL */
            case 1: wide = (__int128)(int64_t)a * (__int128)(int64_t)b;
                    xw(c, rd, (uint64_t)(wide >> 64)); break;                          /* MULH */
            case 2: wide = (__int128)(int64_t)a * (unsigned __int128)b;
                    xw(c, rd, (uint64_t)((unsigned __int128)wide >> 64)); break;       /* MULHSU */
            case 3: xw(c, rd, (uint64_t)(((unsigned __int128)a * b) >> 64)); break;    /* MULHU */
            case 4: xw(c, rd, b == 0 ? ~0ull
                     : (a == (1ull << 63) && (int64_t)b == -1) ? a
                     : (uint64_t)((int64_t)a / (int64_t)b)); break;                    /* DIV */
            case 5: xw(c, rd, b == 0 ? ~0ull : a / b); break;                          /* DIVU */
            case 6: xw(c, rd, b == 0 ? a
                     : (a == (1ull << 63) && (int64_t)b == -1) ? 0
                     : (uint64_t)((int64_t)a % (int64_t)b)); break;                    /* REM */
            case 7: xw(c, rd, b == 0 ? a : a % b); break;                              /* REMU */
            default: illegal_instruction(m, trap_val); return c->halted;
            }
            break;
        }
        switch (funct3) {
        case 0: xw(c, rd, (funct7 & 0x20) ? a - b : a + b); break;           /* SUB/ADD */
        case 1: xw(c, rd, a << (b & 0x3f)); break;                          /* SLL */
        case 2: xw(c, rd, (int64_t)a < (int64_t)b); break;                  /* SLT */
        case 3: xw(c, rd, a < b); break;                                    /* SLTU */
        case 4: xw(c, rd, a ^ b); break;                                    /* XOR */
        case 5:
            if (funct7 & 0x20) xw(c, rd, (uint64_t)((int64_t)a >> (b & 0x3f))); /* SRA */
            else xw(c, rd, a >> (b & 0x3f));                                   /* SRL */
            break;
        case 6: xw(c, rd, a | b); break;                                    /* OR */
        case 7: xw(c, rd, a & b); break;                                    /* AND */
        default: illegal_instruction(m, trap_val); return c->halted;
        }
        break;
    }
    case 0x3B: { /* OP-32: register-register, word width */
        uint32_t a = (uint32_t)xr(c, rs1), b = (uint32_t)xr(c, rs2);
        int32_t res;
        if (funct7 == 0x01) { /* M extension, word forms */
            int32_t sa = (int32_t)a, sb = (int32_t)b;
            switch (funct3) {
            case 0: res = sa * sb; break;                                              /* MULW */
            case 4: res = (b == 0) ? -1 : (sa == INT32_MIN && sb == -1) ? sa : sa / sb; break; /* DIVW */
            case 5: res = (int32_t)((b == 0) ? ~0u : a / b); break;                     /* DIVUW */
            case 6: res = (b == 0) ? sa : (sa == INT32_MIN && sb == -1) ? 0 : sa % sb; break; /* REMW */
            case 7: res = (int32_t)((b == 0) ? a : a % b); break;                       /* REMUW */
            default: illegal_instruction(m, trap_val); return c->halted;
            }
            xw(c, rd, (uint64_t)(int64_t)res);
            break;
        }
        switch (funct3) {
        case 0: res = (funct7 & 0x20) ? (int32_t)(a - b) : (int32_t)(a + b); break; /* SUBW/ADDW */
        case 1: res = (int32_t)(a << (b & 0x1f)); break;                           /* SLLW */
        case 5:
            if (funct7 & 0x20) res = (int32_t)a >> (b & 0x1f);                     /* SRAW */
            else res = (int32_t)(a >> (b & 0x1f));                                 /* SRLW */
            break;
        default: illegal_instruction(m, trap_val); return c->halted;
        }
        xw(c, rd, (uint64_t)(int64_t)res);
        break;
    }
    case 0x2F: { /* AMO: the A extension - LR/SC and the read-modify-write ops */
        uint32_t funct5 = funct7 >> 2; /* bits 31:27; bits 26:25 are aq/rl, ignored - a
                                         * single-hart sequential interpreter has no
                                         * concurrent access to order against */
        uint64_t addr = xr(c, rs1);
        int width64 = (funct3 == 3);
        uint64_t resv_mask = width64 ? ~7ull : ~3ull;
        uint64_t pa;

        if (funct3 != 2 && funct3 != 3) { illegal_instruction(m, trap_val); return c->halted; }

        if (funct5 == 0x02) { /* LR: read-only, establishes a reservation */
            if (!translate(m, addr, ACCESS_LOAD, &pa)) return c->halted;
            if (!pmp_allows(c, pa, 0)) { raise_trap(m, CAUSE_LOAD_ACCESS_FAULT, addr, 0); return c->halted; }
            xw(c, rd, width64 ? phys_read64(m, pa)
                              : (uint64_t)(int64_t)(int32_t)phys_read32(m, pa));
            c->reservation_valid = 1;
            c->reservation_addr = addr & resv_mask;
            break;
        }
        if (funct5 == 0x03) { /* SC: write-only, conditional on the reservation */
            int ok = c->reservation_valid && c->reservation_addr == (addr & resv_mask);
            if (ok) {
                if (!translate(m, addr, ACCESS_STORE, &pa)) return c->halted;
                if (!pmp_allows(c, pa, 1)) { raise_trap(m, CAUSE_STORE_ACCESS_FAULT, addr, 0); return c->halted; }
                if (width64) phys_write64(m, pa, xr(c, rs2));
                else phys_write32(m, pa, (uint32_t)xr(c, rs2));
            }
            c->reservation_valid = 0; /* any attempt clears it, successful or not */
            xw(c, rd, ok ? 0 : 1); /* 0 = succeeded, 1 = failed - per spec, not boolean-backwards */
            break;
        }

        /* A real AMO: read-modify-write, so it needs both permissions, and
         * (being a store) clears any outstanding reservation the same way
         * the plain STORE case below does. */
        if (!translate(m, addr, ACCESS_LOAD, &pa)) return c->halted;
        if (!pmp_allows(c, pa, 0)) { raise_trap(m, CAUSE_LOAD_ACCESS_FAULT, addr, 0); return c->halted; }
        if (!pmp_allows(c, pa, 1)) { raise_trap(m, CAUSE_STORE_ACCESS_FAULT, addr, 0); return c->halted; }
        c->reservation_valid = 0;

        uint64_t rs2v = xr(c, rs2);
        if (width64) {
            uint64_t old = phys_read64(m, pa), result;
            int64_t a = (int64_t)old, b = (int64_t)rs2v;
            switch (funct5) {
            case 0x01: result = rs2v; break;                      /* AMOSWAP */
            case 0x00: result = old + rs2v; break;                /* AMOADD */
            case 0x04: result = old ^ rs2v; break;                /* AMOXOR */
            case 0x0C: result = old & rs2v; break;                /* AMOAND */
            case 0x08: result = old | rs2v; break;                /* AMOOR */
            case 0x10: result = (uint64_t)(a < b ? a : b); break; /* AMOMIN */
            case 0x14: result = (uint64_t)(a > b ? a : b); break; /* AMOMAX */
            case 0x18: result = old < rs2v ? old : rs2v; break;   /* AMOMINU */
            case 0x1C: result = old > rs2v ? old : rs2v; break;   /* AMOMAXU */
            default: illegal_instruction(m, trap_val); return c->halted;
            }
            phys_write64(m, pa, result);
            xw(c, rd, old);
        } else {
            uint32_t old = phys_read32(m, pa), r2 = (uint32_t)rs2v, result;
            int32_t a = (int32_t)old, b = (int32_t)r2;
            switch (funct5) {
            case 0x01: result = r2; break;
            case 0x00: result = old + r2; break;
            case 0x04: result = old ^ r2; break;
            case 0x0C: result = old & r2; break;
            case 0x08: result = old | r2; break;
            case 0x10: result = (uint32_t)(a < b ? a : b); break;
            case 0x14: result = (uint32_t)(a > b ? a : b); break;
            case 0x18: result = old < r2 ? old : r2; break;
            case 0x1C: result = old > r2 ? old : r2; break;
            default: illegal_instruction(m, trap_val); return c->halted;
            }
            phys_write32(m, pa, result);
            xw(c, rd, (uint64_t)(int64_t)(int32_t)old); /* sign-extend, like LW */
        }
        break;
    }
    case 0x0F: /* MISC-MEM: FENCE, FENCE.I - no-ops without a pipeline/cache */
        break;
    case 0x73: /* SYSTEM: ECALL/EBREAK/SRET/MRET/WFI/SFENCE.VMA/CSR* */
        if (funct3 == 0 && funct7 == 0x09) {
            /* SFENCE.VMA rs1,rs2: flushes cached translations. We ignore
             * rs1/rs2 (a specific address or ASID) and always flush the
             * whole TLB - over-flushing is always architecturally correct,
             * just occasionally more work than a precise implementation
             * would do, and simplicity wins given how rarely this executes
             * relative to every fetch/load/store the TLB actually speeds up. */
            memset(c->tlb, 0, sizeof(c->tlb));
            break;
        }
        if (funct3 == 0) {
            uint32_t imm12 = f_csr(insn);
            switch (imm12) {
            case 0x000: /* ECALL: cause is 8+priv - U=0,S=1,M=3 map to the
                         * correct 8/9/11 (10 is reserved, for H-mode, which
                         * we don't implement) with no special-casing needed. */
                raise_trap(m, CAUSE_ECALL_FROM_U + (uint64_t)c->priv, 0, 0);
                return c->halted;
            case 0x001: /* EBREAK */
                raise_trap(m, CAUSE_BREAKPOINT, pc, 0);
                return c->halted;
            case 0x102: /* SRET */
                do_sret(m);
                return c->halted;
            case 0x105: /* WFI: spec permits a no-op, which is exactly what
                         * deterministic mode keeps (see emu.h's remarks -
                         * every existing differential/MD5 test already
                         * verified that timing). Real time's whole reason
                         * to exist is this case specifically: sleep
                         * instead of spinning through the guest's idle
                         * loop at full interpreter speed for no reason -
                         * see wfi_sleep_realtime's own comment. */
                if (!m->deterministic_time) wfi_sleep_realtime(m);
                break;
            case 0x302: /* MRET */
                do_mret(m);
                return c->halted;
            default:
                illegal_instruction(m, trap_val);
                return c->halted;
            }
        } else {
            if (!do_csr(m, insn, rd, rs1, funct3)) return c->halted;
        }
        break;
    case 0x07: { /* LOAD-FP: FLW/FLD */
        if (!fp_enabled(c)) { illegal_instruction(m, trap_val); return c->halted; }
        if (funct3 != 2 && funct3 != 3) { illegal_instruction(m, trap_val); return c->halted; }
        uint64_t addr = xr(c, rs1) + (uint64_t)imm_i(insn);
        uint64_t raw;
        if (!mem_load(m, addr, ACCESS_LOAD, funct3 == 2 ? 4 : 8, &raw)) return c->halted;
        if (funct3 == 2) c->f[rd] = 0xFFFFFFFF00000000ull | (uint32_t)raw; /* FLW */
        else c->f[rd] = raw;                                              /* FLD */
        fs_dirty(c);
        break;
    }
    case 0x27: { /* STORE-FP: FSW/FSD */
        if (!fp_enabled(c)) { illegal_instruction(m, trap_val); return c->halted; }
        if (funct3 != 2 && funct3 != 3) { illegal_instruction(m, trap_val); return c->halted; }
        uint64_t addr = xr(c, rs1) + (uint64_t)imm_s(insn);
        if (!mem_store(m, addr, ACCESS_STORE, funct3 == 2 ? 4 : 8, c->f[rs2])) return c->halted;
        c->reservation_valid = 0; /* same reasoning as the integer STORE case */
        break;
    }
    case 0x43: case 0x47: case 0x4B: case 0x4F: { /* FMADD/FMSUB/FNMSUB/FNMADD.S/D */
        if (!fp_enabled(c)) { illegal_instruction(m, trap_val); return c->halted; }
        uint32_t rs3 = f_rs3(insn);
        if (f_fmt2(insn) == 1) { /* D */
            double a = f_getd(c, rs1), b = f_getd(c, rs2), cc = f_getd(c, rs3), r;
            switch (opcode) {
            case 0x43: r = fma(a, b, cc); break;    /* FMADD.D:  (a*b)+c */
            case 0x47: r = fma(a, b, -cc); break;   /* FMSUB.D:  (a*b)-c */
            case 0x4B: r = fma(-a, b, cc); break;   /* FNMSUB.D: -(a*b)+c */
            default:   r = fma(-a, b, -cc); break;  /* FNMADD.D: -(a*b)-c */
            }
            f_setd(c, rd, r);
        } else { /* S */
            float a = f_gets(c, rs1), b = f_gets(c, rs2), cc = f_gets(c, rs3), r;
            switch (opcode) {
            case 0x43: r = fmaf(a, b, cc); break;
            case 0x47: r = fmaf(a, b, -cc); break;
            case 0x4B: r = fmaf(-a, b, cc); break;
            default:   r = fmaf(-a, b, -cc); break;
            }
            f_sets(c, rd, r);
        }
        break;
    }
    case 0x53: { /* OP-FP: everything else F/D (funct7's low bit is the fmt,
                  * 0=S/1=D, for every op below except the S<->D convert pair
                  * at 0x20/0x21, which use rs2 instead to name the *source*
                  * format since funct7 alone already names the destination). */
        if (!fp_enabled(c)) { illegal_instruction(m, trap_val); return c->halted; }
        switch (funct7) {
        case 0x00: case 0x01: /* FADD.S/D */
            if (funct7 & 1) f_setd(c, rd, f_getd(c, rs1) + f_getd(c, rs2));
            else f_sets(c, rd, f_gets(c, rs1) + f_gets(c, rs2));
            break;
        case 0x04: case 0x05: /* FSUB.S/D */
            if (funct7 & 1) f_setd(c, rd, f_getd(c, rs1) - f_getd(c, rs2));
            else f_sets(c, rd, f_gets(c, rs1) - f_gets(c, rs2));
            break;
        case 0x08: case 0x09: /* FMUL.S/D */
            if (funct7 & 1) f_setd(c, rd, f_getd(c, rs1) * f_getd(c, rs2));
            else f_sets(c, rd, f_gets(c, rs1) * f_gets(c, rs2));
            break;
        case 0x0C: case 0x0D: /* FDIV.S/D */
            if (funct7 & 1) f_setd(c, rd, f_getd(c, rs1) / f_getd(c, rs2));
            else f_sets(c, rd, f_gets(c, rs1) / f_gets(c, rs2));
            break;
        case 0x2C: case 0x2D: /* FSQRT.S/D (rs2 == 0) */
            if (funct7 & 1) f_setd(c, rd, sqrt(f_getd(c, rs1)));
            else f_sets(c, rd, sqrtf(f_gets(c, rs1)));
            break;
        case 0x10: case 0x11: { /* FSGNJ/FSGNJN/FSGNJX.S/D */
            if (funct7 & 1) {
                uint64_t a = c->f[rs1], b = c->f[rs2];
                uint64_t sign = b & (1ull << 63), mag = a & ~(1ull << 63), res;
                switch (funct3) {
                case 0: res = mag | sign; break;
                case 1: res = mag | (sign ^ (1ull << 63)); break;
                case 2: res = mag | ((a ^ b) & (1ull << 63)); break;
                default: illegal_instruction(m, trap_val); return c->halted;
                }
                c->f[rd] = res; fs_dirty(c);
            } else {
                uint32_t a = (uint32_t)c->f[rs1], b = (uint32_t)c->f[rs2];
                uint32_t sign = b & 0x80000000u, mag = a & 0x7fffffffu, res;
                switch (funct3) {
                case 0: res = mag | sign; break;
                case 1: res = mag | (sign ^ 0x80000000u); break;
                case 2: res = mag | ((a ^ b) & 0x80000000u); break;
                default: illegal_instruction(m, trap_val); return c->halted;
                }
                c->f[rd] = 0xFFFFFFFF00000000ull | res; fs_dirty(c);
            }
            break;
        }
        case 0x14: case 0x15: /* FMIN/FMAX.S/D */
            if (funct7 & 1) f_setd(c, rd, funct3 == 0 ? rv_fmin_d(f_getd(c, rs1), f_getd(c, rs2))
                                                       : rv_fmax_d(f_getd(c, rs1), f_getd(c, rs2)));
            else f_sets(c, rd, funct3 == 0 ? rv_fmin_s(f_gets(c, rs1), f_gets(c, rs2))
                                           : rv_fmax_s(f_gets(c, rs1), f_gets(c, rs2)));
            break;
        case 0x20: /* FCVT.S.D: narrow D -> S */
            f_sets(c, rd, (float)f_getd(c, rs1));
            break;
        case 0x21: /* FCVT.D.S: widen S -> D */
            f_setd(c, rd, (double)f_gets(c, rs1));
            break;
        case 0x50: case 0x51: { /* FEQ(2)/FLT(1)/FLE(0).S/D */
            int res;
            if (funct7 & 1) {
                double a = f_getd(c, rs1), b = f_getd(c, rs2);
                res = (funct3 == 0) ? (a <= b) : (funct3 == 1) ? (a < b) : (a == b);
            } else {
                float a = f_gets(c, rs1), b = f_gets(c, rs2);
                res = (funct3 == 0) ? (a <= b) : (funct3 == 1) ? (a < b) : (a == b);
            }
            xw(c, rd, (uint64_t)res);
            break;
        }
        case 0x60: case 0x61: { /* FCVT.W/WU/L/LU.S/D: float -> int, rs2 selects dest type.
            * The spec requires NaN and out-of-range inputs to saturate to
            * the destination type's max (NaN and overflow) or min
            * (underflow) representable value, never wrap or trap; C's own
            * float-to-integer cast is undefined behavior for exactly
            * those inputs, and on this host (x86-64, SSE2 cvttsd2si/
            * cvttss2si) produces the "integer indefinite" pattern instead
            * - INT32_MIN/INT64_MIN rather than the spec's INT32_MAX/
            * INT64_MAX for NaN or positive overflow - a real, silent
            * divergence from real QEMU's own softfloat this project's own
            * differential test (test R, `tests/selftest.S`) exists to
            * catch, so this checks range explicitly before ever casting
            * rather than relying on the cast for anything but the
            * already-in-range case. 32-bit results are still sign-
            * extended into the 64-bit register on write-back, per spec,
            * for both the signed and unsigned forms. */
            double v = (funct7 & 1) ? f_getd(c, rs1) : (double)f_gets(c, rs1);
            uint64_t res;
            switch (rs2) {
            case 0:                                            /* FCVT.W: signed 32-bit */
                if (isnan(v) || v >= 2147483648.0) res = (uint64_t)(int64_t)(int32_t)0x7FFFFFFF;
                else if (v < -2147483648.0) res = (uint64_t)(int64_t)(int32_t)0x80000000;
                else res = (uint64_t)(int64_t)(int32_t)(int64_t)v;
                break;
            case 1:                                            /* FCVT.WU: unsigned 32-bit */
                if (isnan(v) || v >= 4294967296.0) res = (uint64_t)(int64_t)(int32_t)0xFFFFFFFFu;
                else if (v < 0.0) res = 0;
                else res = (uint64_t)(int64_t)(int32_t)(uint32_t)(int64_t)v;
                break;
            case 2:                                            /* FCVT.L: signed 64-bit */
                if (isnan(v) || v >= 9223372036854775808.0) res = (uint64_t)INT64_MAX;
                else if (v < -9223372036854775808.0) res = (uint64_t)INT64_MIN;
                else res = (uint64_t)(int64_t)v;
                break;
            case 3:                                            /* FCVT.LU: unsigned 64-bit */
                if (isnan(v) || v >= 18446744073709551616.0) res = UINT64_MAX;
                else if (v < 0.0) res = 0;
                else res = (uint64_t)v;
                break;
            default: illegal_instruction(m, trap_val); return c->halted;
            }
            xw(c, rd, res);
            break;
        }
        case 0x68: case 0x69: { /* FCVT.S/D.W/WU/L/LU: int -> float, rs2 selects source type */
            int64_t iv = (int64_t)xr(c, rs1);
            double dv;
            switch (rs2) {
            case 0: dv = (double)(int32_t)iv; break;
            case 1: dv = (double)(uint32_t)iv; break;
            case 2: dv = (double)iv; break;
            case 3: dv = (double)(uint64_t)iv; break;
            default: illegal_instruction(m, trap_val); return c->halted;
            }
            if (funct7 & 1) f_setd(c, rd, dv); else f_sets(c, rd, (float)dv);
            break;
        }
        case 0x70: /* FMV.X.W(0) / FCLASS.S(1), rs2 == 0 */
            if (funct3 == 0) xw(c, rd, (uint64_t)(int64_t)(int32_t)(uint32_t)c->f[rs1]);
            else if (funct3 == 1) xw(c, rd, fclassify_s(f_gets(c, rs1)));
            else { illegal_instruction(m, trap_val); return c->halted; }
            break;
        case 0x71: /* FMV.X.D(0) / FCLASS.D(1), rs2 == 0 */
            if (funct3 == 0) xw(c, rd, c->f[rs1]);
            else if (funct3 == 1) xw(c, rd, fclassify_d(f_getd(c, rs1)));
            else { illegal_instruction(m, trap_val); return c->halted; }
            break;
        case 0x78: /* FMV.W.X, rs2 == 0 */
            c->f[rd] = 0xFFFFFFFF00000000ull | (uint32_t)xr(c, rs1);
            fs_dirty(c);
            break;
        case 0x79: /* FMV.D.X, rs2 == 0 */
            c->f[rd] = xr(c, rs1);
            fs_dirty(c);
            break;
        default:
            illegal_instruction(m, trap_val);
            return c->halted;
        }
        break;
    }
    default:
        unimplemented(m, trap_val);
        return c->halted;
    }

    c->pc = next_pc;
    return c->halted;
}
