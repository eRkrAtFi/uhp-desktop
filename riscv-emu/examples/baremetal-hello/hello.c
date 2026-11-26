/* vision/VISION.md roadmap item 3's own example program: a bare-metal
 * RISC-V binary with no kernel, no firmware, no SBI - started directly
 * in M-mode at its own ELF entry point (see --elf in main.c). Writes a
 * few lines to the 16550 UART and a counter in a loop, then exits via
 * the test-finisher - deliberately not calling an SBI legacy console
 * putchar the way boot/echo_test.S does, since there is no firmware
 * here to service that ECALL.
 *
 * Built two ways from the exact same sources (see Makefile's own
 * baremetal-hello/baremetal-compare targets): under riscv-emu itself
 * (--elf), and under real qemu-system-riscv64 (-M virt -bios none
 * -kernel, which loads any ELF by its own e_entry regardless of our
 * own --elf flag even existing) - the same differential-testing
 * principle every other guest program in this repo already follows.
 */
#include <stdint.h>

#define UART_BASE    0x10000000ULL
#define TESTDEV_BASE 0x00100000ULL
#define FINISHER_PASS 0x5555u

static volatile uint8_t *const uart = (volatile uint8_t *)UART_BASE;
static volatile uint32_t *const testdev = (volatile uint32_t *)TESTDEV_BASE;

static void putc_(char c) { *uart = (uint8_t)c; }
static void puts_(const char *s) { while (*s) putc_(*s++); }

static void put_u32(uint32_t v)
{
    char buf[11];
    int i = 10;
    buf[10] = '\0';
    if (v == 0) { putc_('0'); return; }
    while (v && i > 0) { buf[--i] = (char)('0' + (v % 10)); v /= 10; }
    puts_(&buf[i]);
}

int main(void)
{
    puts_("hello from bare-metal RISC-V\n");
    for (uint32_t i = 0; i < 5; i++) {
        puts_("count=");
        put_u32(i);
        puts_("\n");
    }
    *testdev = FINISHER_PASS;
    for (;;) {}
}
