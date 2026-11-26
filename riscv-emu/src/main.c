/* Machine setup. Two modes:
 *   - A single positional <raw-binary> loads at RAM_BASE and runs from
 *     there, matching qemu-system-riscv64 -M virt -bios none -kernel
 *     <elf-at-same-address> - this is what every Phase 1-4 test still uses.
 *   - --load/--entry/--reg place one or more files at arbitrary addresses
 *     and set the initial pc/registers explicitly, which is what Phase 5's
 *     real-Linux attempt needs: a firmware stub at RAM_BASE, the kernel
 *     Image at its own text_offset, an initramfs and a DTB elsewhere in
 *     RAM, with a0/a1 set the way the real RISC-V Linux boot protocol
 *     expects (hartid, DTB physical address) - entirely driven by whatever
 *     built this command line (see boot/boot.sh), not hardcoded here.
 * See emu.h for the phase map. */
#include "emu.h"
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#endif

/* --- console input: a background thread feeds the ring buffer declared in
 * emu.h, so the interpreter loop never blocks on stdin itself. Uses the
 * low-level _read() against stdin's raw fd, not fgetc()/fread() - stdio
 * switches to a large fully-buffered mode on a non-console stdin (a
 * redirected file, or a GUI's piped console input), which would sit on
 * input for a while before this ever saw a byte; _read() returns whatever
 * is immediately available instead. */
#ifdef _WIN32
static Machine *g_stdin_machine; /* one machine per process; see console_input_start */

static unsigned long WINAPI stdin_reader_thread(void *arg)
{
    (void)arg;
    Machine *m = g_stdin_machine;
    for (;;) {
        unsigned char ch;
        int n = _read(_fileno(stdin), &ch, 1);
        if (n <= 0) break; /* EOF or error: stdin is closed, nothing more to feed */
        StdinRing *ring = &m->stdin_ring;
        /* write_pos: only this thread ever writes it, so reading back
         * its own last value needs no cross-thread ordering (RELAXED). */
        unsigned int w = __atomic_load_n(&ring->write_pos, __ATOMIC_RELAXED);
        unsigned int r;
        /* Wait for room rather than drop (or overwrite an unread byte)
         * if the guest hasn't drained the ring yet - confirmed directly
         * (boot/burst_test.sh) that dropping here silently lost bytes
         * under a single large (~4KB) burst with no host-side pacing,
         * which real hardware (or this ring, sized generously enough
         * that normal interactive use never fills it) never would. This
         * is a background OS thread with nothing else to do, so spinning
         * briefly here costs nothing the interpreter would otherwise be
         * doing; the actual backpressure this creates on whatever is
         * writing to this process's stdin (a host-side pipe write
         * blocking once its own OS buffer fills) is the same cooperative
         * slowdown a real UART's own flow control would produce.
         * ACQUIRE: must see the interpreter's own read of buf[r%SIZE]
         * (ordered before its read_pos release store, below) as
         * happening-before this thread reuses that same slot - otherwise
         * nothing stops this thread from overwriting a byte the
         * interpreter has not actually finished reading yet. */
        for (;;) {
            r = __atomic_load_n(&ring->read_pos, __ATOMIC_ACQUIRE);
            if (w - r < STDIN_RING_SIZE) break;
            Sleep(1);
        }
        ring->buf[w % STDIN_RING_SIZE] = ch;
        /* RELEASE: publishes both this buf[] write and the new write_pos
         * together - the interpreter's own ACQUIRE load of write_pos
         * (console_input_ready/console_getchar) is what makes that
         * ordering guarantee meaningful on its side. */
        __atomic_store_n(&ring->write_pos, w + 1, __ATOMIC_RELEASE);
        if (getenv("RISCV_EMU_SBIDBG"))
            fprintf(stderr, "[stdin-thread] pushed %d ('%c') (ring r=%u w=%u->%u)\n",
                    (int)ch, (ch >= 32 && ch < 127) ? ch : '?', r, w, w + 1);
    }
    return 0;
}

void console_input_start(Machine *m)
{
    g_stdin_machine = m;
    CreateThread(NULL, 0, stdin_reader_thread, NULL, 0, NULL);
}
#else
void console_input_start(Machine *m) { (void)m; }
#endif

int console_input_ready(Machine *m)
{
    StdinRing *ring = &m->stdin_ring;
    /* ACQUIRE on write_pos: must see the producer thread's own write to
     * buf[] (ordered before its write_pos release store) before this
     * ever reports a byte as ready - read_pos needs no such ordering,
     * only this thread (the interpreter) ever writes it. */
    unsigned int w = __atomic_load_n(&ring->write_pos, __ATOMIC_ACQUIRE);
    unsigned int r = __atomic_load_n(&ring->read_pos, __ATOMIC_RELAXED);
    return r != w;
}

int console_getchar(Machine *m)
{
    StdinRing *ring = &m->stdin_ring;
    unsigned int w = __atomic_load_n(&ring->write_pos, __ATOMIC_ACQUIRE);
    unsigned int r = __atomic_load_n(&ring->read_pos, __ATOMIC_RELAXED);
    if (r == w) return -1;
    unsigned char ch = ring->buf[r % STDIN_RING_SIZE];
    /* RELEASE: publishes that this slot has now been read, so the
     * producer thread's own ACQUIRE load of read_pos is guaranteed to
     * see it before reusing the slot. */
    __atomic_store_n(&ring->read_pos, r + 1, __ATOMIC_RELEASE);
    return ch;
}

static void usage(const char *argv0)
{
    fprintf(stderr,
        "riscv-emu - an RV64IMAFDC(Zicsr,Sstc) system emulator\n"
        "\n"
        "Usage: %s [options] <raw-binary>\n"
        "   or: %s [options] --entry <addr> --load <addr> <file> [--load ...]\n"
        "   or: %s [options] --kernel <Image> [--initrd <f>] [--append <args>]\n"
        "\n"
        "  --trace            print one line per instruction to stderr\n"
        "  --max <n>          stop after n instructions (default 2000000000)\n"
        "  --deterministic-time  mtime advances once per instruction instead\n"
        "                     of tracking real host time - for differential/\n"
        "                     MD5 tests that must get the identical result on\n"
        "                     every run, not for normal use (sleep()/ping/\n"
        "                     date inside the guest will not keep real time)\n"
        "  --dump             print the register file when execution stops\n"
        "  --load <addr> <f>  copy file f's bytes into guest RAM at addr (repeatable)\n"
        "  --entry <addr>     set the initial pc (default 0x%08x)\n"
        "  --reg <name> <v>   set an initial integer register, e.g. --reg a1 0x...\n"
        "  --disk <file>      attach an existing raw disk image to virtio-blk\n"
        "  --net              attach virtio-net, NATed through this host process\n"
        "  --mem <bytes>      guest RAM size (default %u)\n"
        "  --kernel <Image>   boot this riscv64 Linux Image: places firmware,\n"
        "                     kernel and an internally-generated DTB in guest\n"
        "                     RAM and sets pc/a0/a1/t3 per the boot protocol -\n"
        "                     the one-flag equivalent of boot/boot.sh, not\n"
        "                     combinable with --load/--entry/a raw-binary\n"
        "  --elf <file>       load this ELF64/EM_RISCV file by its own PT_LOAD\n"
        "                     program headers and start at its own e_entry, in\n"
        "                     M-mode, with no firmware/kernel involved - for a\n"
        "                     bare-metal program (see examples/baremetal-hello);\n"
        "                     not combinable with --load/--entry/a raw-binary\n"
        "  --initrd <f>       initramfs to go with --kernel (optional)\n"
        "  --append <args>    extra kernel cmdline, appended after this\n"
        "                     machine's own console=/earlycon= args\n"
        "  --firmware <f>     firmware stub for --kernel (default boot/firmware.bin)\n"
        "  --help             show this message\n"
        "\n"
        "With a bare <raw-binary> and no --load/--entry/--kernel, the file is\n"
        "loaded at 0x%08x and executed from there, matching qemu-system-riscv64\n"
        "-M virt -bios none -kernel <elf-at-same-address>. Exit status comes\n"
        "from a guest write to the test-finisher device (0 pass, else the\n"
        "guest's code), or 2 on an unimplemented instruction.\n",
        argv0, argv0, argv0, RAM_BASE, MEM_SIZE, RAM_BASE);
}

static uint8_t *read_file(const char *path, uint64_t *out_len)
{
    FILE *f = fopen(path, "rb");
    uint8_t *buf;
    long len;

    if (!f) { perror(path); return NULL; }
    if (fseek(f, 0, SEEK_END) != 0) { perror(path); fclose(f); return NULL; }
    len = ftell(f);
    if (len < 0) { perror(path); fclose(f); return NULL; }
    rewind(f);
    buf = malloc((size_t)len ? (size_t)len : 1);
    if (!buf) { fclose(f); return NULL; }
    if (len && fread(buf, 1, (size_t)len, f) != (size_t)len) {
        fprintf(stderr, "riscv-emu: short read on %s\n", path);
        free(buf);
        fclose(f);
        return NULL;
    }
    fclose(f);
    *out_len = (uint64_t)len;
    return buf;
}

static int load_at(Machine *m, uint64_t addr, const char *path)
{
    uint64_t len;
    uint8_t *data = read_file(path, &len);
    if (!data) return 0;
    if (addr < RAM_BASE || addr - RAM_BASE + len > m->mem_size) {
        fprintf(stderr, "riscv-emu: %s at 0x%llx does not fit in guest RAM\n",
                path, (unsigned long long)addr);
        free(data);
        return 0;
    }
    memcpy(m->mem + (addr - RAM_BASE), data, len);
    fprintf(stderr, "riscv-emu: loaded %s at 0x%llx (%llu bytes)\n",
            path, (unsigned long long)addr, (unsigned long long)len);
    free(data);
    return 1;
}

static uint64_t align_up(uint64_t value, uint64_t alignment)
{
    return (value + alignment - 1) / alignment * alignment;
}

/* vision/VISION.md roadmap item 3, M2: --elf <file> loads a real ELF64
 * file by its own PT_LOAD program headers instead of a flat raw-binary
 * dump at RAM_BASE (the plain <raw-binary> / --load path above) - no
 * firmware, no kernel, starts directly at e_entry in M-mode (cpu_reset's
 * own default privilege level, unchanged). Only what qemu-system-riscv64
 * -M virt -bios none -kernel <elf> itself needs to agree with: ELF64,
 * little-endian, EM_RISCV, each PT_LOAD segment's file bytes copied to
 * its own p_paddr with p_memsz - p_filesz trailing bytes zeroed (.bss). */
typedef struct __attribute__((packed)) {
    uint8_t  e_ident[16];
    uint16_t e_type;
    uint16_t e_machine;
    uint32_t e_version;
    uint64_t e_entry;
    uint64_t e_phoff;
    uint64_t e_shoff;
    uint32_t e_flags;
    uint16_t e_ehsize;
    uint16_t e_phentsize;
    uint16_t e_phnum;
    uint16_t e_shentsize;
    uint16_t e_shnum;
    uint16_t e_shstrndx;
} Elf64Header;

typedef struct __attribute__((packed)) {
    uint32_t p_type;
    uint32_t p_flags;
    uint64_t p_offset;
    uint64_t p_vaddr;
    uint64_t p_paddr;
    uint64_t p_filesz;
    uint64_t p_memsz;
    uint64_t p_align;
} Elf64Phdr;

#define ELF_PT_LOAD   1u
#define ELF_EM_RISCV  243u
#define ELFCLASS64    2u
#define ELFDATA2LSB   1u

static int load_elf(Machine *m, const char *path, uint64_t *out_entry)
{
    uint64_t len;
    uint8_t *data = read_file(path, &len);
    Elf64Header hdr;
    int loaded_any = 0;
    uint16_t i;

    if (!data) return 0;
    if (len < sizeof(hdr)) {
        fprintf(stderr, "riscv-emu: %s is too short to be a valid ELF file\n", path);
        free(data);
        return 0;
    }
    memcpy(&hdr, data, sizeof(hdr));

    if (hdr.e_ident[0] != 0x7f || hdr.e_ident[1] != 'E' ||
        hdr.e_ident[2] != 'L'  || hdr.e_ident[3] != 'F') {
        fprintf(stderr, "riscv-emu: %s is not an ELF file\n", path);
        free(data);
        return 0;
    }
    if (hdr.e_ident[4] != ELFCLASS64) {
        fprintf(stderr, "riscv-emu: %s is not a 64-bit ELF file (ELFCLASS64)\n", path);
        free(data);
        return 0;
    }
    if (hdr.e_ident[5] != ELFDATA2LSB) {
        fprintf(stderr, "riscv-emu: %s is not a little-endian ELF file (ELFDATA2LSB)\n", path);
        free(data);
        return 0;
    }
    if (hdr.e_machine != ELF_EM_RISCV) {
        fprintf(stderr, "riscv-emu: %s is not a RISC-V ELF file (e_machine=%u, expected %u)\n",
                path, hdr.e_machine, ELF_EM_RISCV);
        free(data);
        return 0;
    }
    if (hdr.e_phoff == 0 || hdr.e_phnum == 0) {
        fprintf(stderr, "riscv-emu: %s has no program headers to load\n", path);
        free(data);
        return 0;
    }
    if (hdr.e_phoff > len ||
        (uint64_t)hdr.e_phnum * hdr.e_phentsize > len - hdr.e_phoff) {
        fprintf(stderr, "riscv-emu: %s's program header table runs past the end of the file\n", path);
        free(data);
        return 0;
    }

    for (i = 0; i < hdr.e_phnum; i++) {
        Elf64Phdr ph;
        memcpy(&ph, data + hdr.e_phoff + (uint64_t)i * hdr.e_phentsize, sizeof(ph));
        if (ph.p_type != ELF_PT_LOAD) continue;
        if (ph.p_offset > len || ph.p_filesz > len - ph.p_offset) {
            fprintf(stderr, "riscv-emu: %s's segment %u runs past the end of the file\n", path, i);
            free(data);
            return 0;
        }
        if (ph.p_paddr < RAM_BASE || ph.p_memsz > m->mem_size ||
            ph.p_paddr - RAM_BASE > m->mem_size - ph.p_memsz) {
            fprintf(stderr, "riscv-emu: %s's segment %u at 0x%llx (%llu bytes) does not fit in guest RAM\n",
                    path, i, (unsigned long long)ph.p_paddr, (unsigned long long)ph.p_memsz);
            free(data);
            return 0;
        }
        memcpy(m->mem + (ph.p_paddr - RAM_BASE), data + ph.p_offset, ph.p_filesz);
        if (ph.p_memsz > ph.p_filesz)
            memset(m->mem + (ph.p_paddr - RAM_BASE) + ph.p_filesz, 0, ph.p_memsz - ph.p_filesz);
        fprintf(stderr, "riscv-emu: loaded %s segment %u at 0x%llx (%llu bytes, %llu bss)\n",
                path, i, (unsigned long long)ph.p_paddr, (unsigned long long)ph.p_filesz,
                (unsigned long long)(ph.p_memsz - ph.p_filesz));
        loaded_any = 1;
    }
    if (!loaded_any) {
        fprintf(stderr, "riscv-emu: %s has no PT_LOAD segments\n", path);
        free(data);
        return 0;
    }

    *out_entry = hdr.e_entry;
    free(data);
    return 1;
}

/* Reads the Image header's text_offset field (little-endian u64 at file
 * offset 8) - see Documentation/arch/riscv/boot-image-header.rst. Returns 0
 * on any I/O error (already reported via perror by the caller's read_file-
 * style error path, except here it is folded into one open+seek+read). */
static int read_text_offset(const char *path, uint64_t *out)
{
    FILE *f = fopen(path, "rb");
    uint8_t buf[8];
    if (!f) { perror(path); return 0; }
    if (fseek(f, 8, SEEK_SET) != 0 || fread(buf, 1, 8, f) != 8) {
        fprintf(stderr, "riscv-emu: %s is too short to be a valid Image file\n", path);
        fclose(f);
        return 0;
    }
    fclose(f);
    *out = 0;
    for (int i = 7; i >= 0; i--) *out = (*out << 8) | buf[i]; /* little-endian */
    return 1;
}

/* The one-flag equivalent of boot/boot.sh: places firmware at RAM_BASE, the
 * kernel Image at its own text_offset, an optional initramfs and an
 * internally-built DTB past it with comfortable margins between each blob
 * (same layout boot.sh / QemuManager's RiscvEmuBoot.cs used to compute
 * externally), and sets pc/a0/a1/t3 the way boot/firmware.S's own boot
 * protocol expects. Returns 1 on success, 0 (with a message already
 * printed) on any failure. */
static int setup_kernel_boot(Machine *m, const char *firmware_path, const char *kernel_path,
                              const char *initrd_path, const char *extra_bootargs)
{
    const uint64_t margin = 1ull * 1024 * 1024;
    uint64_t text_offset, kernel_len, initrd_len = 0;
    uint64_t kernel_addr, after_kernel, initrd_addr = 0, initrd_end = 0, after_initrd, dtb_addr;
    char bootargs[1024];
    uint8_t *dtb_bytes;
    uint64_t dtb_len;

    if (!load_at(m, RAM_BASE, firmware_path)) return 0;
    if (!read_text_offset(kernel_path, &text_offset)) return 0;

    kernel_addr = RAM_BASE + text_offset;
    if (!load_at(m, kernel_addr, kernel_path)) return 0;
    {
        FILE *f = fopen(kernel_path, "rb");
        if (!f || fseek(f, 0, SEEK_END) != 0 || (long)(kernel_len = (uint64_t)ftell(f)) < 0) {
            fprintf(stderr, "riscv-emu: could not size %s\n", kernel_path);
            if (f) fclose(f);
            return 0;
        }
        fclose(f);
    }
    after_kernel = align_up(kernel_addr + kernel_len + margin, 0x1000);

    snprintf(bootargs, sizeof(bootargs), "console=ttyS0 earlycon=sbi");
    if (initrd_path) {
        initrd_addr = after_kernel;
        if (!load_at(m, initrd_addr, initrd_path)) return 0;
        FILE *f = fopen(initrd_path, "rb");
        if (!f || fseek(f, 0, SEEK_END) != 0 || (long)(initrd_len = (uint64_t)ftell(f)) < 0) {
            fprintf(stderr, "riscv-emu: could not size %s\n", initrd_path);
            if (f) fclose(f);
            return 0;
        }
        fclose(f);
        initrd_end = initrd_addr + initrd_len;
        after_initrd = align_up(initrd_end + margin, 0x1000);
        /* /init, not /sbin/init: this is where boot/initramfs.cpio.gz's own
         * init script actually lives (see boot/boot.sh's own cmdline) - the
         * kernel's rdinit= exec silently falls through to a normal root=
         * mount attempt on any failure, including a wrong path, which is
         * indistinguishable from "rdinit was never given" except by reading
         * the panic's own call trace (mount_root_generic, not an rdinit
         * failure message) - confirmed directly against this exact kernel
         * Image the first time this path was ported into C and used the
         * QemuManager-side C# port's own (wrong) "/sbin/init" guess. */
        snprintf(bootargs + strlen(bootargs), sizeof(bootargs) - strlen(bootargs), " rdinit=/init");
    } else {
        after_initrd = after_kernel;
    }
    snprintf(bootargs + strlen(bootargs), sizeof(bootargs) - strlen(bootargs), " loglevel=7");
    if (extra_bootargs && extra_bootargs[0])
        snprintf(bootargs + strlen(bootargs), sizeof(bootargs) - strlen(bootargs), " %s", extra_bootargs);

    dtb_addr = after_initrd;
    dtb_bytes = build_dtb(RAM_BASE, m->mem_size, initrd_path != NULL, initrd_addr, initrd_end, bootargs, &dtb_len);

    if (dtb_addr < RAM_BASE || dtb_addr - RAM_BASE + dtb_len > m->mem_size) {
        fprintf(stderr, "riscv-emu: kernel/initramfs/dtb together do not fit in %llu bytes of guest RAM\n",
                (unsigned long long)m->mem_size);
        free(dtb_bytes);
        return 0;
    }
    memcpy(m->mem + (dtb_addr - RAM_BASE), dtb_bytes, dtb_len);
    fprintf(stderr, "riscv-emu: generated dtb at 0x%llx (%llu bytes)\n",
            (unsigned long long)dtb_addr, (unsigned long long)dtb_len);
    free(dtb_bytes);

    m->cpu.pc = RAM_BASE;
    m->cpu.x[10 /* a0 */] = 0;
    m->cpu.x[11 /* a1 */] = dtb_addr;
    m->cpu.x[28 /* t3 */] = kernel_addr;
    return 1;
}

/* Maps the ABI register names a program handing off to us would use (the
 * RISC-V Linux boot protocol sets a0 = hartid, a1 = dtb address) to our
 * flat x[32] array, so --reg can use the names anyone reading a boot
 * protocol doc actually sees instead of bare numbers. */
static int reg_index(const char *name)
{
    static const char *names[32] = {
        "zero","ra","sp","gp","tp","t0","t1","t2",
        "s0","s1","a0","a1","a2","a3","a4","a5",
        "a6","a7","s2","s3","s4","s5","s6","s7",
        "s8","s9","s10","s11","t3","t4","t5","t6",
    };
    for (int i = 0; i < 32; i++)
        if (!strcmp(names[i], name)) return i;
    return -1;
}

int main(int argc, char **argv)
{
    Machine m;
    const char *image = NULL;
    const char *elf_path = NULL;
    const char *kernel_path = NULL, *initrd_path = NULL, *append_args = NULL;
    const char *firmware_path = "boot/firmware.bin";
    int dump = 0, i, explicit_entry = 0;
    uint64_t len = 0, max_insns = 2000000000ull, entry = RAM_BASE, trace_after = 0;
    uint64_t peek_va = 0, peek_count = 0;
    uint64_t mem_size = MEM_SIZE;
    int peek_requested = 0;
    uint8_t *data;

    memset(&m, 0, sizeof(m));

    /* --mem is read in its own pass, not the main option loop below: it has
     * to be known before the one calloc() that sizes guest RAM, but every
     * other option (notably --load) acts immediately as it's seen and so
     * needs m.mem already allocated - there is no ordering of a single
     * pass that satisfies both. */
    for (i = 1; i < argc; i++)
        if (!strcmp(argv[i], "--mem") && i + 1 < argc) mem_size = strtoull(argv[++i], NULL, 0);

    /* stderr defaults to fully buffered (not line buffered) once it is
     * redirected to a file, so the progress line below would otherwise sit
     * unseen in the buffer for many millions of instructions. */
    setvbuf(stderr, NULL, _IOLBF, 0);

#ifdef _WIN32
    /* Guest UART output must pass through byte for byte to compare against
     * QEMU's own stdout; in text mode the C runtime would turn the guest's
     * own LF into CR LF, which QEMU's raw passthrough does not do. */
    _setmode(_fileno(stdout), _O_BINARY);
    /* stdin needs the same treatment, for the opposite-direction reason:
     * in (the default) text mode, the CRT's own \r\n translation layer
     * holds a lone \r read from a non-console (pipe/file) stdin, waiting
     * to see whether the next byte is \n before deciding how to deliver
     * it - confirmed directly (a bulk host-side write ending in a bare
     * \r, with nothing queued after it, never reached stdin_reader_thread
     * at all when this process had no console of its own - e.g. launched
     * with Win32's CREATE_NO_WINDOW, as QemuManager's own
     * RiscvEmuProcessManager does - while the exact same write succeeded
     * immediately both from a real console-attached shell and from a
     * child process allowed its own console). A guest whose line
     * discipline treats bare \r as Enter (most of them: see VmConfig's
     * own EnterSends remarks) would otherwise have that Enter silently
     * swallowed whenever nothing else arrives on stdin right after it -
     * exactly the symptom this fixes, not merely a cosmetic CRLF detail
     * the way the stdout case above is. */
    _setmode(_fileno(stdin), _O_BINARY);
#endif

    m.mem_size = mem_size;
    m.mem = calloc(m.mem_size, 1);
    if (!m.mem) { fprintf(stderr, "riscv-emu: out of memory\n"); return 1; }
    cpu_reset(&m);
    console_input_start(&m);

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--trace")) { m.trace = 1; }
        else if (!strcmp(argv[i], "--deterministic-time")) { m.deterministic_time = 1; }
        else if (!strcmp(argv[i], "--trace-after") && i + 1 < argc) {
            trace_after = strtoull(argv[++i], NULL, 0);
        }
        else if (!strcmp(argv[i], "--peek") && i + 2 < argc) {
            peek_va = strtoull(argv[++i], NULL, 0);
            peek_count = strtoull(argv[++i], NULL, 0);
            peek_requested = 1;
        }
        else if (!strcmp(argv[i], "--dump")) { dump = 1; }
        else if (!strcmp(argv[i], "--max") && i + 1 < argc) {
            max_insns = strtoull(argv[++i], NULL, 0);
        } else if (!strcmp(argv[i], "--help") || !strcmp(argv[i], "-h")) {
            usage(argv[0]); free(m.mem); return 0;
        } else if (!strcmp(argv[i], "--entry") && i + 1 < argc) {
            entry = strtoull(argv[++i], NULL, 0);
            explicit_entry = 1;
        } else if (!strcmp(argv[i], "--load") && i + 2 < argc) {
            uint64_t addr = strtoull(argv[++i], NULL, 0);
            const char *path = argv[++i];
            if (!load_at(&m, addr, path)) { free(m.mem); return 1; }
        } else if (!strcmp(argv[i], "--reg") && i + 2 < argc) {
            const char *name = argv[++i];
            uint64_t val = strtoull(argv[++i], NULL, 0);
            int idx = reg_index(name);
            if (idx < 0) {
                fprintf(stderr, "riscv-emu: unknown register name '%s'\n", name);
                free(m.mem);
                return 1;
            }
            m.cpu.x[idx] = val;
        } else if (!strcmp(argv[i], "--disk") && i + 1 < argc) {
            const char *path = argv[++i];
            /* "r+b", not "rb": virtio-blk writes (VIRTIO_BLK_T_OUT) need to
             * actually land on disk, not be silently accepted and dropped -
             * the file must already exist (this does not create one), the
             * same convention as every other --load path here. */
            m.disk_file = fopen(path, "r+b");
            if (!m.disk_file) { perror(path); free(m.mem); return 1; }
            if (fseek(m.disk_file, 0, SEEK_END) != 0) { perror(path); free(m.mem); return 1; }
            long dsz = ftell(m.disk_file);
            if (dsz < 0) { perror(path); free(m.mem); return 1; }
            rewind(m.disk_file);
            m.disk_size = (uint64_t)dsz;
            fprintf(stderr, "riscv-emu: attached disk %s (%llu bytes) as virtio-blk\n",
                    path, (unsigned long long)m.disk_size);
        } else if (!strcmp(argv[i], "--net")) {
            net_init(&m);
        } else if (!strcmp(argv[i], "--mem") && i + 1 < argc) {
            i++; /* already consumed in the pre-scan pass above */
        } else if (!strcmp(argv[i], "--elf") && i + 1 < argc) {
            elf_path = argv[++i];
        } else if (!strcmp(argv[i], "--kernel") && i + 1 < argc) {
            kernel_path = argv[++i];
        } else if (!strcmp(argv[i], "--initrd") && i + 1 < argc) {
            initrd_path = argv[++i];
        } else if (!strcmp(argv[i], "--append") && i + 1 < argc) {
            append_args = argv[++i];
        } else if (!strcmp(argv[i], "--firmware") && i + 1 < argc) {
            firmware_path = argv[++i];
        } else if (argv[i][0] == '-') {
            fprintf(stderr, "riscv-emu: unknown option %s\n", argv[i]);
            free(m.mem);
            return 1;
        } else {
            image = argv[i];
        }
    }

    if (kernel_path) {
        if (image || explicit_entry || elf_path) {
            fprintf(stderr, "riscv-emu: --kernel is not combinable with --load/--entry/--elf/a raw-binary\n");
            free(m.mem);
            return 1;
        }
        if (!setup_kernel_boot(&m, firmware_path, kernel_path, initrd_path, append_args)) {
            free(m.mem);
            return 1;
        }
    } else if (elf_path) {
        uint64_t elf_entry;
        if (image || explicit_entry) {
            fprintf(stderr, "riscv-emu: --elf is not combinable with --load/--entry/a raw-binary\n");
            free(m.mem);
            return 1;
        }
        if (!load_elf(&m, elf_path, &elf_entry)) {
            free(m.mem);
            return 1;
        }
        m.cpu.pc = elf_entry;
    } else if (image) {
        data = read_file(image, &len);
        if (!data) { free(m.mem); return 1; }
        if (len > m.mem_size) { fprintf(stderr, "riscv-emu: image too large\n"); free(data); free(m.mem); return 1; }
        memcpy(m.mem, data, len);
        free(data);
        m.cpu.pc = entry;
    } else if (!explicit_entry) {
        usage(argv[0]);
        free(m.mem);
        return 1;
    } else {
        m.cpu.pc = entry;
    }

    /* Guest mtime==0 starts here, not at process start - everything above
     * (argument parsing, loading a multi-hundred-MB kernel+initramfs from
     * disk) must not count against the guest's own perceived uptime, the
     * same way real hardware/real QEMU's own emulated clock would not
     * charge a human's command-line-typing time to the guest either. */
    if (!m.deterministic_time) realtime_clock_init(&m);

    {
        clock_t t_start = clock();
        uint64_t next_report = 200000;
        while (!m.cpu.halted && m.cpu.insns < max_insns) {
            if (trace_after && m.cpu.insns >= trace_after) m.trace = 1;
            cpu_step(&m);
            /* Network I/O is polled on a cadence, not once per instruction -
             * a real socket syscall (select/recv) on every single
             * instruction would dominate runtime for no benefit, since
             * nothing on the other end of any of these sockets can
             * possibly respond faster than a few thousand times a second
             * anyway. Gated on m.net so a run with no --net pays nothing
             * but the mask check. */
            if (m.net && (m.cpu.insns & 0xFFFull) == 0) net_poll(&m);
            if (m.cpu.insns >= next_report) {
                double secs = (double)(clock() - t_start) / CLOCKS_PER_SEC;
                double ips = secs > 0 ? (double)m.cpu.insns / secs : 0.0;
                fprintf(stderr, "riscv-emu: %llu insns, %.1fs, %.0f/s, pc=%016llx tlbh=%llu tlbm=%llu\n",
                        (unsigned long long)m.cpu.insns, secs, ips,
                        (unsigned long long)m.cpu.pc,
                        (unsigned long long)m.cpu.tlb_hits, (unsigned long long)m.cpu.tlb_misses);
                /* Same numbers, a second time, in a strict key=value line a
                 * caller can parse without screen-scraping the human-
                 * readable one above (whose units/spacing are free to keep
                 * changing for a person reading it directly) - this is the
                 * "separate channel" QemuManager's stats tiles read; both
                 * still go to stderr, which already carries no guest UART
                 * bytes, so no new fd/pipe is needed, only a stable format.
                 * The "riscv-emu:" prefix is kept so every existing
                 * `grep -v '^riscv-emu:'` filter (boot_test.sh, fs_compare.sh)
                 * continues to strip this line exactly like the one above. */
                fprintf(stderr, "riscv-emu: stats insns=%llu secs=%.1f mips=%.1f pc=%016llx\n",
                        (unsigned long long)m.cpu.insns, secs, ips / 1e6,
                        (unsigned long long)m.cpu.pc);
                next_report += 200000;
            }
        }
    }

    if (!m.cpu.halted)
        fprintf(stderr, "riscv-emu: instruction limit %llu reached at pc=%016llx\n",
                (unsigned long long)max_insns, (unsigned long long)m.cpu.pc);
    if (dump) {
        fprintf(stderr, "riscv-emu: stopped after %llu instructions\n",
                (unsigned long long)m.cpu.insns);
        cpu_dump(&m.cpu, stderr);
    }

    if (peek_requested) {
        uint64_t off;
        for (off = 0; off < peek_count; off += 16) {
            uint64_t line_va = peek_va + off;
            uint64_t pa;
            fprintf(stderr, "%016llx:", (unsigned long long)line_va);
            for (uint64_t j = 0; j < 16 && off + j < peek_count; j++) {
                uint64_t byte_va = line_va + j;
                if (!cpu_translate_for_debug(&m, byte_va, &pa)) {
                    fprintf(stderr, " ??");
                    continue;
                }
                fprintf(stderr, " %02x", phys_read8(&m, pa));
            }
            fprintf(stderr, "\n");
        }
    }

    i = m.cpu.exit_code;
    free(m.mem);
    return i;
}
