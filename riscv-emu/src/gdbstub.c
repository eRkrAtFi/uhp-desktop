/* vision/VISION.md roadmap item 3, M3: a GDB Remote Serial Protocol
 * (RSP) stub - --gdb <port> in main.c routes the whole instruction-
 * stepping loop through gdbstub_run below instead of main.c's own plain
 * loop; without --gdb, main.c's loop is completely untouched, so an
 * ordinary run pays nothing for this file existing (confirmed directly:
 * boot-test's own timing, before and after this file was added, is
 * unchanged - see its own Makefile target).
 *
 * Deliberately not a full gdbserver: exactly the commands a debugger
 * actually needs to attach, set a breakpoint, step, read/write
 * registers and memory, and detach - qSupported, qRegisterInfo (each
 * register's real DWARF number, generic role, and offset into a 'g'
 * packet - lldb's own source-level/"frame variable" evaluator needs
 * this specifically, confirmed directly; qSupported deliberately does
 * NOT advertise qXfer:features:read - see its own comment for why),
 * qXfer:features:read (target.xml for riscv:rv64, answered correctly
 * if asked directly regardless), ?, g/G, p/P, m/M, c, s, Z0/z0 (software
 * breakpoints, an internal table - never patching guest memory with a
 * trap instruction, so no original-byte save/restore bookkeeping is
 * needed and a breakpoint on executable ROM/flash-like memory works the
 * same as one in RAM), k, D, and an asynchronous Ctrl-C (the raw byte
 * 0x03, outside any $...# packet) to break into a running target.
 * vCont and anything else not listed above gets the standard "empty
 * reply means unsupported" response, which makes a client that prefers
 * vCont (lldb, by default) fall back to plain c/s.
 *
 * One client at a time, no reconnect: once a debugger detaches (D) or
 * its connection drops, the target just keeps running on its own to
 * completion (same as the non-gdb loop) rather than waiting for another
 * one to attach - nothing in roadmap item 3 needs more than that.
 */
#include "emu.h"
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <sys/socket.h>
#include <sys/select.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#define SOCKET int
#define INVALID_SOCKET (-1)
#define SOCKET_ERROR (-1)
#define closesocket close
#endif

#define GDB_MAX_BREAKPOINTS 64
#define GDB_PACKET_BUF 8192
#define GDB_SIGTRAP 5
#define GDB_SIGINT  2

typedef struct {
    SOCKET sock;
    char last_reply[GDB_PACKET_BUF];
    size_t last_reply_len;
} GdbConn;

typedef struct {
    uint64_t addr[GDB_MAX_BREAKPOINTS];
    int count;
} BreakpointTable;

static void bp_insert(BreakpointTable *bp, uint64_t addr)
{
    int i;
    for (i = 0; i < bp->count; i++) if (bp->addr[i] == addr) return; /* already set */
    if (bp->count < GDB_MAX_BREAKPOINTS) bp->addr[bp->count++] = addr;
}

static void bp_remove(BreakpointTable *bp, uint64_t addr)
{
    int i;
    for (i = 0; i < bp->count; i++) {
        if (bp->addr[i] == addr) {
            bp->addr[i] = bp->addr[--bp->count];
            return;
        }
    }
}

static int bp_hit(const BreakpointTable *bp, uint64_t addr)
{
    int i;
    for (i = 0; i < bp->count; i++) if (bp->addr[i] == addr) return 1;
    return 0;
}

/* --- wire framing --------------------------------------------------- */

static int gdb_send_raw(GdbConn *gc, const char *data, size_t len)
{
    size_t sent = 0;
    while (sent < len) {
        int n = send(gc->sock, data + sent, (int)(len - sent), 0);
        if (n <= 0) return 0;
        sent += (size_t)n;
    }
    return 1;
}

static int gdb_send_packet(GdbConn *gc, const char *payload)
{
    size_t len = strlen(payload);
    unsigned char checksum = 0;
    char header[GDB_PACKET_BUF + 8];
    size_t i;
    int n;
    for (i = 0; i < len; i++) checksum += (unsigned char)payload[i];
    n = snprintf(header, sizeof(header), "$%s#%02x", payload, checksum);
    if (n < 0 || (size_t)n >= sizeof(header)) return 0;
    memcpy(gc->last_reply, header, (size_t)n);
    gc->last_reply_len = (size_t)n;
    return gdb_send_raw(gc, header, (size_t)n);
}

/* Reads one full RSP unit: either the single asynchronous interrupt
 * byte (0x03, never wrapped in $...#) or a complete, checksum-verified
 * $...#XX packet (already ack'd with '+'; a bad checksum is nak'd with
 * '-' and the sender is expected to retry, same as any real RSP stub).
 * buf is the packet payload (no $, no #XX), NUL-terminated. Returns 0
 * on a closed/broken connection. */
static int gdb_recv_unit(GdbConn *gc, char *buf, size_t bufsize, int *out_is_break)
{
    for (;;) {
        unsigned char ch;
        int n = recv(gc->sock, (char *)&ch, 1, 0);
        size_t len;
        unsigned char csum;
        char hex[3];
        unsigned int want_csum;

        if (n <= 0) return 0;
        if (ch == 0x03) { *out_is_break = 1; return 1; }
        if (ch == '+') continue;  /* ack of our last reply */
        if (ch == '-') { gdb_send_raw(gc, gc->last_reply, gc->last_reply_len); continue; }
        if (ch != '$') continue; /* resync: ignore stray bytes before a packet start */

        len = 0;
        csum = 0;
        for (;;) {
            n = recv(gc->sock, (char *)&ch, 1, 0);
            if (n <= 0) return 0;
            if (ch == '#') break;
            if (len + 1 >= bufsize) return 0; /* malformed/oversized: give up on this connection */
            buf[len++] = (char)ch;
            csum += ch;
        }
        buf[len] = '\0';
        if (recv(gc->sock, &hex[0], 1, 0) <= 0) return 0;
        if (recv(gc->sock, &hex[1], 1, 0) <= 0) return 0;
        hex[2] = '\0';
        want_csum = (unsigned int)strtoul(hex, NULL, 16);
        if (want_csum != csum) { gdb_send_raw(gc, "-", 1); continue; }
        gdb_send_raw(gc, "+", 1);
        *out_is_break = 0;
        return 1;
    }
}

/* Non-blocking: 1 if the socket has a byte waiting right now, 0 if not
 * (or on error) - select() with a zero timeout, portable to both
 * Winsock and BSD sockets, used only to poll for an asynchronous Ctrl-C
 * while 'c'/'s' are actually running the target (see gdb_run_until_stop). */
static int gdb_has_pending_byte(SOCKET sock)
{
    fd_set rfds;
    struct timeval tv = {0, 0};
    FD_ZERO(&rfds);
    FD_SET(sock, &rfds);
    return select((int)sock + 1, &rfds, NULL, NULL, &tv) > 0;
}

/* --- hex helpers ------------------------------------------------------ */

static void hex_encode(const uint8_t *data, size_t len, char *out)
{
    static const char hexchars[] = "0123456789abcdef";
    size_t i;
    for (i = 0; i < len; i++) {
        out[i * 2]     = hexchars[data[i] >> 4];
        out[i * 2 + 1] = hexchars[data[i] & 0xf];
    }
    out[len * 2] = '\0';
}

/* Decodes exactly len/2 bytes of hex into out (out_cap must be large
 * enough) - returns the byte count, or -1 on malformed input. */
static int hex_decode(const char *hex, uint8_t *out, size_t out_cap)
{
    size_t len = strlen(hex);
    size_t n = len / 2;
    size_t i;
    if (len % 2 != 0 || n > out_cap) return -1;
    for (i = 0; i < n; i++) {
        char tmp[3] = { hex[i * 2], hex[i * 2 + 1], 0 };
        out[i] = (uint8_t)strtoul(tmp, NULL, 16);
    }
    return (int)n;
}

/* --- register access ---------------------------------------------------
 * GDB register numbering, fixed for the whole session the first time
 * target.xml is actually served (see gdbstub_run's own fpu_enabled
 * snapshot): 0-31 = x0-x31, 32 = pc, and - only if mstatus.FS was non-
 * zero ("enabled") at that moment - 33-64 = f0-f31, 65 = fcsr. g/G and
 * p/P both use this same numbering and byte width per register. */

static int gdb_reg_total(int fpu_enabled) { return fpu_enabled ? 66 : 33; }

static uint64_t gdb_reg_read(const Machine *m, int idx, int *out_width)
{
    const CPU *c = &m->cpu;
    if (idx >= 0 && idx < 32) { *out_width = 8; return c->x[idx]; }
    if (idx == 32) { *out_width = 8; return c->pc; }
    if (idx >= 33 && idx <= 64) { *out_width = 8; return c->f[idx - 33]; }
    if (idx == 65) { *out_width = 4; return c->fcsr & 0xffffffffu; }
    *out_width = 0;
    return 0;
}

static void gdb_reg_write(Machine *m, int idx, uint64_t val)
{
    CPU *c = &m->cpu;
    if (idx == 0) return; /* x0 is hardwired to zero, like real hardware */
    if (idx > 0 && idx < 32) { c->x[idx] = val; return; }
    if (idx == 32) { c->pc = val; return; }
    if (idx >= 33 && idx <= 64) { c->f[idx - 33] = val; return; }
    if (idx == 65) { c->fcsr = val & 0xffffffffu; return; }
}

/* --- target.xml ---------------------------------------------------------
 * Matches real GDB's own riscv64 feature names (org.gnu.gdb.riscv.cpu /
 * .fpu) closely enough for lldb's generic XML target-description parser
 * - a single "fcsr" register rather than gdb's own three-way fflags/
 * frm/fcsr split, since nothing here needs finer-grained access to that
 * one CSR than a whole-register read/write. */
static size_t build_target_xml(char *out, size_t cap, int fpu_enabled)
{
    size_t len = 0;
    int i;
    len += (size_t)snprintf(out + len, cap - len,
        "<?xml version=\"1.0\"?>\n"
        "<!DOCTYPE target SYSTEM \"gdb-target.dtd\">\n"
        "<target version=\"1.0\">\n"
        "  <architecture>riscv:rv64</architecture>\n"
        "  <feature name=\"org.gnu.gdb.riscv.cpu\">\n");
    for (i = 0; i < 32; i++)
        len += (size_t)snprintf(out + len, cap - len, "    <reg name=\"x%d\" bitsize=\"64\" regnum=\"%d\"/>\n", i, i);
    len += (size_t)snprintf(out + len, cap - len,
        "    <reg name=\"pc\" bitsize=\"64\" type=\"code_ptr\" regnum=\"32\"/>\n"
        "  </feature>\n");
    if (fpu_enabled) {
        len += (size_t)snprintf(out + len, cap - len, "  <feature name=\"org.gnu.gdb.riscv.fpu\">\n");
        for (i = 0; i < 32; i++)
            len += (size_t)snprintf(out + len, cap - len,
                "    <reg name=\"f%d\" bitsize=\"64\" type=\"ieee_double\"/>\n", i);
        len += (size_t)snprintf(out + len, cap - len,
            "    <reg name=\"fcsr\" bitsize=\"32\"/>\n"
            "  </feature>\n");
    }
    len += (size_t)snprintf(out + len, cap - len, "</target>\n");
    return len;
}

/* --- memory access (like --peek: through the current address translation,
 * not raw guest-physical) ------------------------------------------------ */

static int gdb_mem_read(Machine *m, uint64_t va, uint64_t len, uint8_t *out)
{
    uint64_t i;
    for (i = 0; i < len; i++) {
        uint64_t pa;
        if (!cpu_translate_for_debug(m, va + i, &pa)) return 0;
        out[i] = phys_read8(m, pa);
    }
    return 1;
}

static int gdb_mem_write(Machine *m, uint64_t va, const uint8_t *data, uint64_t len)
{
    uint64_t i;
    for (i = 0; i < len; i++) {
        uint64_t pa;
        if (!cpu_translate_for_debug(m, va + i, &pa)) return 0;
        phys_write8(m, pa, data[i]);
    }
    return 1;
}

/* --- run control ---------------------------------------------------------
 * Runs until one of: a breakpoint is reached (checked BEFORE executing
 * the instruction there, so a breakpoint address is never itself
 * executed), the target halts (test-finisher), the instruction budget
 * runs out, or the debugger sends an asynchronous Ctrl-C. A single step
 * always executes exactly one instruction regardless of any breakpoint
 * at the current pc (standard RSP semantics) and never checks for
 * Ctrl-C (one instruction cannot run long enough to need interrupting).
 * Returns a GDB stop-reply payload (without the leading '$'/trailing
 * checksum - gdb_send_packet adds those) into reply, sized
 * reply_cap. */
static void gdb_run_until_stop(Machine *m, GdbConn *gc, BreakpointTable *bp,
                               uint64_t max_insns, int single_step,
                               char *reply, size_t reply_cap)
{
    uint64_t polls = 0;

    for (;;) {
        if (!single_step && bp_hit(bp, m->cpu.pc)) {
            snprintf(reply, reply_cap, "S%02x", GDB_SIGTRAP);
            return;
        }
        if (m->cpu.insns >= max_insns) {
            snprintf(reply, reply_cap, "W%02x", 0);
            return;
        }
        if (cpu_step(m)) {
            /* Halted via the test-finisher (or an unimplemented
             * instruction) - report as a normal process exit, the
             * standard RSP way to say "nothing more to debug here". */
            snprintf(reply, reply_cap, "W%02x", (unsigned)(m->cpu.exit_code & 0xff));
            return;
        }
        if (m->net && (m->cpu.insns & 0xFFFull) == 0) net_poll(m);
        if (single_step) {
            snprintf(reply, reply_cap, "S%02x", GDB_SIGTRAP);
            return;
        }
        /* Polled, not checked every instruction: an actual recv() call
         * on every single retired instruction would dominate runtime
         * for a feature that only ever needs to notice a human pressing
         * Ctrl-C, which no host can physically do thousands of times a
         * second anyway - same reasoning as main.c's own net_poll
         * cadence. */
        if ((polls++ & 0xFFFull) == 0 && gdb_has_pending_byte(gc->sock)) {
            unsigned char ch;
            int n = recv(gc->sock, (char *)&ch, 1, 0);
            if (n > 0 && ch == 0x03) {
                snprintf(reply, reply_cap, "S%02x", GDB_SIGINT);
                return;
            }
            /* Anything else arriving here is unexpected (a client only
             * ever sends a bare 0x03 asynchronously while the target is
             * running) - dropped rather than risking desyncing the
             * packet framer on a byte that was never a real packet
             * start. */
        }
    }
}

/* --- command dispatch -----------------------------------------------
 * Returns 0 to end the whole gdb session (k, or the connection
 * dropped), 1 to keep handling packets, 2 if the target should now run
 * to completion UNMONITORED (D: detach). *halted_after_dispatch is set
 * if the target ran to completion (exit via W reply) during this
 * call - the caller stops reading further packets in that case, since
 * there is nothing left to debug. */
static int gdb_dispatch(Machine *m, GdbConn *gc, BreakpointTable *bp,
                        uint64_t max_insns, int *fpu_enabled_session,
                        const char *pkt, int *halted_after_dispatch)
{
    char reply[GDB_PACKET_BUF];
    reply[0] = '\0';

    if (!strncmp(pkt, "qSupported", 10)) {
        /* Deliberately NOT advertising qXfer:features:read+ here, even
         * though the handler for it (below) is fully implemented and
         * correct: confirmed directly that when it IS advertised, lldb
         * fetches target.xml, is satisfied by it for ordinary register
         * access, and - this is the actual problem - NEVER ALSO asks
         * qRegisterInfo (below) for the real per-register DWARF numbers
         * that only that packet carries, which its own DWARF/CFI-based
         * local-variable evaluator specifically needs ("frame variable"
         * failed with "unable to convert register kind=1 reg_num=2 to a
         * native register number" - kind 1 is lldb's own
         * eRegisterKindDWARF). Not advertising it steers lldb onto
         * qRegisterInfo instead, which this stub answers with everything
         * target.xml would have said AND the DWARF numbers - confirmed
         * directly: "frame variable" then reads real values. A real GDB
         * client (unlike lldb, built around qXfer:features:read as its
         * own primary mechanism) can still send that query directly
         * without this advertisement - the handler below still answers
         * it correctly either way, this only changes what lldb chooses
         * to ask for first. */
        gdb_send_packet(gc, "PacketSize=4000");
    } else if (!strncmp(pkt, "qRegisterInfo", strlen("qRegisterInfo"))) {
        /* N is this stub's own GDB-protocol register number (the same
         * one p/P/g/G already use); the "dwarf:" field in the reply is
         * the separate, real ELF/DWARF register number the RISC-V psABI
         * defines (x0-31 -> 0-31, f0-31 -> 32-63 - NOT the same
         * numbering as this stub's own 0-31/32=pc/33-64=f scheme, which
         * reserves 32 for pc with no RISC-V DWARF equivalent to put
         * there, so pc/fcsr are reported with no "dwarf:" field at all
         * rather than a wrong guess). */
        int idx = (int)strtol(pkt + strlen("qRegisterInfo"), NULL, 16);
        int fpu;
        int total;
        if (*fpu_enabled_session < 0)
            *fpu_enabled_session = ((m->cpu.mstatus >> 13) & 0x3) != 0; /* mstatus.FS != Off */
        fpu = *fpu_enabled_session > 0;
        total = gdb_reg_total(fpu);
        if (idx < 0 || idx >= total) {
            gdb_send_packet(gc, "E45");
        } else {
            char name[16];
            int width, offset = 0, i;
            char info[256];
            int pos = 0;
            for (i = 0; i < idx; i++) { int w; gdb_reg_read(m, i, &w); offset += w; }
            gdb_reg_read(m, idx, &width);
            if (idx < 32) snprintf(name, sizeof(name), "x%d", idx);
            else if (idx == 32) snprintf(name, sizeof(name), "pc");
            else if (idx <= 64) snprintf(name, sizeof(name), "f%d", idx - 33);
            else snprintf(name, sizeof(name), "fcsr");
            pos += snprintf(info + pos, sizeof(info) - pos,
                "name:%s;bitsize:%d;offset:%d;encoding:uint;format:hex;set:General Purpose Registers;",
                name, width * 8, offset);
            if (idx < 32) pos += snprintf(info + pos, sizeof(info) - pos, "dwarf:%d;", idx);
            else if (idx >= 33 && idx <= 64) pos += snprintf(info + pos, sizeof(info) - pos, "dwarf:%d;", 32 + (idx - 33));
            if (idx == 32) pos += snprintf(info + pos, sizeof(info) - pos, "generic:pc;");
            else if (idx == 1) pos += snprintf(info + pos, sizeof(info) - pos, "generic:ra;");
            else if (idx == 2) pos += snprintf(info + pos, sizeof(info) - pos, "generic:sp;");
            else if (idx == 8) pos += snprintf(info + pos, sizeof(info) - pos, "generic:fp;");
            gdb_send_packet(gc, info);
        }
    } else if (!strncmp(pkt, "qXfer:features:read:target.xml:", strlen("qXfer:features:read:target.xml:"))) {
        static char xml[16384];
        const char *args = pkt + strlen("qXfer:features:read:target.xml:");
        uint64_t off, want_len;
        size_t xml_len;
        if (sscanf(args, "%llx,%llx", (unsigned long long *)&off, (unsigned long long *)&want_len) != 2) {
            gdb_send_packet(gc, "E00");
            return 1;
        }
        /* The FPU feature is decided once, the first time target.xml is
         * actually requested - not re-evaluated per read (a qXfer read
         * is usually split across several requests at increasing
         * offsets; switching the feature set mid-transfer would make
         * them disagree about what each register number even means). */
        if (*fpu_enabled_session < 0)
            *fpu_enabled_session = ((m->cpu.mstatus >> 13) & 0x3) != 0; /* mstatus.FS != Off */
        xml_len = build_target_xml(xml, sizeof(xml), *fpu_enabled_session);
        if (off >= xml_len) {
            gdb_send_packet(gc, "l");
        } else {
            size_t chunk = xml_len - off;
            char out[GDB_PACKET_BUF];
            int more_follows;
            if (chunk > want_len) chunk = want_len;
            if (chunk > sizeof(out) - 2) chunk = sizeof(out) - 2;
            more_follows = (off + chunk < xml_len);
            out[0] = more_follows ? 'm' : 'l';
            memcpy(out + 1, xml + off, chunk);
            out[1 + chunk] = '\0';
            gdb_send_packet(gc, out);
        }
    } else if (!strcmp(pkt, "?")) {
        snprintf(reply, sizeof(reply), "S%02x", GDB_SIGTRAP);
        gdb_send_packet(gc, reply);
    } else if (!strcmp(pkt, "g")) {
        int fpu = *fpu_enabled_session > 0;
        int total = gdb_reg_total(fpu);
        char out[GDB_PACKET_BUF];
        size_t pos = 0;
        int i;
        for (i = 0; i < total; i++) {
            int width;
            uint64_t val = gdb_reg_read(m, i, &width);
            uint8_t bytes[8];
            int b;
            for (b = 0; b < width; b++) bytes[b] = (uint8_t)(val >> (8 * b));
            hex_encode(bytes, (size_t)width, out + pos);
            pos += (size_t)width * 2;
        }
        out[pos] = '\0';
        gdb_send_packet(gc, out);
    } else if (pkt[0] == 'G') {
        int fpu = *fpu_enabled_session > 0;
        int total = gdb_reg_total(fpu);
        const char *p = pkt + 1;
        int i;
        for (i = 0; i < total; i++) {
            int width;
            uint64_t dummy;
            uint8_t bytes[8] = {0};
            int b;
            gdb_reg_read(m, i, &width);
            for (b = 0; b < width; b++) {
                char tmp[3] = { p[0], p[1], 0 };
                bytes[b] = (uint8_t)strtoul(tmp, NULL, 16);
                p += 2;
            }
            dummy = 0;
            for (b = width - 1; b >= 0; b--) dummy = (dummy << 8) | bytes[b];
            gdb_reg_write(m, i, dummy);
        }
        gdb_send_packet(gc, "OK");
    } else if (pkt[0] == 'p') {
        int idx = (int)strtol(pkt + 1, NULL, 16);
        int width;
        uint64_t val = gdb_reg_read(m, idx, &width);
        if (width == 0) {
            gdb_send_packet(gc, "E00");
        } else {
            char out[32];
            uint8_t bytes[8];
            int b;
            for (b = 0; b < width; b++) bytes[b] = (uint8_t)(val >> (8 * b));
            hex_encode(bytes, (size_t)width, out);
            gdb_send_packet(gc, out);
        }
    } else if (pkt[0] == 'P') {
        const char *eq = strchr(pkt, '=');
        int idx;
        int width;
        uint64_t dummy;
        if (!eq) { gdb_send_packet(gc, "E00"); return 1; }
        idx = (int)strtol(pkt + 1, NULL, 16);
        gdb_reg_read(m, idx, &width);
        if (width == 0) {
            gdb_send_packet(gc, "E00");
        } else {
            uint8_t bytes[8] = {0};
            int n = hex_decode(eq + 1, bytes, sizeof(bytes));
            int b;
            if (n <= 0) { gdb_send_packet(gc, "E00"); return 1; }
            dummy = 0;
            for (b = n - 1; b >= 0; b--) dummy = (dummy << 8) | bytes[b];
            gdb_reg_write(m, idx, dummy);
            gdb_send_packet(gc, "OK");
        }
    } else if (pkt[0] == 'm') {
        uint64_t addr, len;
        uint8_t data[4096];
        char out[sizeof(data) * 2 + 1];
        if (sscanf(pkt + 1, "%llx,%llx", (unsigned long long *)&addr, (unsigned long long *)&len) != 2
            || len > sizeof(data)) {
            gdb_send_packet(gc, "E00");
        } else if (!gdb_mem_read(m, addr, len, data)) {
            gdb_send_packet(gc, "E01");
        } else {
            hex_encode(data, (size_t)len, out);
            gdb_send_packet(gc, out);
        }
    } else if (pkt[0] == 'M') {
        uint64_t addr, len;
        const char *colon = strchr(pkt, ':');
        uint8_t data[4096];
        int n;
        if (!colon || sscanf(pkt + 1, "%llx,%llx", (unsigned long long *)&addr, (unsigned long long *)&len) != 2
            || len > sizeof(data)) {
            gdb_send_packet(gc, "E00");
            return 1;
        }
        n = hex_decode(colon + 1, data, sizeof(data));
        if (n < 0 || (uint64_t)n != len || !gdb_mem_write(m, addr, data, len)) {
            gdb_send_packet(gc, "E01");
        } else {
            gdb_send_packet(gc, "OK");
        }
    } else if (pkt[0] == 'c' || pkt[0] == 's') {
        int single_step = (pkt[0] == 's');
        if (pkt[1] != '\0') m->cpu.pc = strtoull(pkt + 1, NULL, 16); /* optional resume address */
        gdb_run_until_stop(m, gc, bp, max_insns, single_step, reply, sizeof(reply));
        gdb_send_packet(gc, reply);
        if (reply[0] == 'W') *halted_after_dispatch = 1;
    } else if (!strncmp(pkt, "Z0,", 3) || !strncmp(pkt, "z0,", 3)) {
        uint64_t addr, kind;
        (void)kind; /* breakpoint size/kind: irrelevant - we match on pc alone, never on instruction bytes */
        if (sscanf(pkt + 3, "%llx,%llx", (unsigned long long *)&addr, (unsigned long long *)&kind) != 2) {
            gdb_send_packet(gc, "E00");
        } else {
            if (pkt[0] == 'Z') bp_insert(bp, addr); else bp_remove(bp, addr);
            gdb_send_packet(gc, "OK");
        }
    } else if (!strcmp(pkt, "k")) {
        return 0; /* no reply, per RSP - the connection is simply gone now */
    } else if (!strcmp(pkt, "D")) {
        gdb_send_packet(gc, "OK");
        return 2;
    } else if (!strncmp(pkt, "vCont", 5)) {
        gdb_send_packet(gc, ""); /* unsupported - the client falls back to plain c/s */
    } else {
        gdb_send_packet(gc, ""); /* standard RSP "unrecognized" reply */
    }
    return 1;
}

/* --- entry point --------------------------------------------------------
 * Owns the instruction-stepping loop for the whole lifetime of a --gdb
 * session: accepts exactly one connection (blocking - a debugger is
 * expected to actually attach, same as QEMU's own -gdb), then either
 * sits stopped (wait_for_debugger, matching QEMU's -S) or starts
 * running immediately (an implicit first 'c') until the client's first
 * real command. After the client detaches or its connection drops, the
 * target keeps running unmonitored to completion exactly like main.c's
 * own non-gdb loop would. */
void gdbstub_run(Machine *m, int port, int wait_for_debugger, uint64_t max_insns)
{
    SOCKET listen_sock, client_sock;
    struct sockaddr_in addr;
    socklen_t addr_len;
    GdbConn gc;
    BreakpointTable bp;
    int fpu_enabled_session = -1; /* decided on the first qXfer:features:read */
    char pkt[GDB_PACKET_BUF];

#ifdef _WIN32
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fprintf(stderr, "riscv-emu: WSAStartup failed, --gdb ignored\n");
        return;
    }
#endif

    memset(&bp, 0, sizeof(bp));

    listen_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listen_sock == INVALID_SOCKET) {
        fprintf(stderr, "riscv-emu: --gdb: socket() failed\n");
        return;
    }
    {
        int yes = 1;
        setsockopt(listen_sock, SOL_SOCKET, SO_REUSEADDR, (const char *)&yes, sizeof(yes));
    }
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    /* 127.0.0.1 only, deliberately - never 0.0.0.0 - a debug stub that
     * can read/write arbitrary guest memory has no business listening
     * on anything but loopback. */
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons((uint16_t)port);
    if (bind(listen_sock, (struct sockaddr *)&addr, sizeof(addr)) == SOCKET_ERROR) {
        fprintf(stderr, "riscv-emu: --gdb: bind() to 127.0.0.1:%d failed\n", port);
        closesocket(listen_sock);
        return;
    }
    if (listen(listen_sock, 1) == SOCKET_ERROR) {
        fprintf(stderr, "riscv-emu: --gdb: listen() failed\n");
        closesocket(listen_sock);
        return;
    }
    fprintf(stderr, "riscv-emu: waiting for a GDB/lldb connection on 127.0.0.1:%d ...\n", port);
    addr_len = sizeof(addr);
    client_sock = accept(listen_sock, (struct sockaddr *)&addr, &addr_len);
    closesocket(listen_sock); /* one client, ever - no point keeping this open */
    if (client_sock == INVALID_SOCKET) {
        fprintf(stderr, "riscv-emu: --gdb: accept() failed\n");
        return;
    }
    fprintf(stderr, "riscv-emu: debugger connected\n");
    gc.sock = client_sock;
    gc.last_reply_len = 0;

    /* Without --gdb-wait, the target runs immediately rather than
     * sitting stopped at the reset vector until the client's first
     * explicit 'c' - but a reply must never be sent before the client
     * has sent its OWN first byte (an unsolicited reply that early
     * confuses a real client's handshake state machine - confirmed
     * directly), and every client's own handshake queries (qSupported,
     * qXfer:features:read, ...) still need answering normally and
     * promptly rather than being silently dropped or blocked behind a
     * long run. Every real client (confirmed against lldb) asks '?' at
     * a fixed point in its own handshake, after everything that must
     * stay fast - that is the one point where running the target for
     * the first time, then answering with its *real* resulting stop
     * reason, is both correct and expected: this is intended for a
     * short bare-metal program (examples/baremetal-hello) that reaches
     * its own natural stop quickly, not a multi-second/many-instruction
     * boot - that would instead run into the client's own reply-timeout
     * for this one query, a separate concern from this run itself;
     * --gdb-wait (or a Ctrl-C sent while this is running, via the same
     * polling gdb_run_until_stop always does) is the way to attach to a
     * longer-running target instead. */
    {
        int auto_run_pending = !wait_for_debugger;

        for (;;) {
            int is_break;
            int halted = 0;
            if (!gdb_recv_unit(&gc, pkt, sizeof(pkt), &is_break)) { closesocket(client_sock); return; }
            if (is_break) {
                /* A Ctrl-C with nothing running yet (still mid-
                 * handshake, or --gdb-wait and no 'c' sent yet) - real
                 * gdbservers simply ignore this; there is nothing to
                 * interrupt. */
                continue;
            }
            if (auto_run_pending && !strcmp(pkt, "?")) {
                char reply[GDB_PACKET_BUF];
                auto_run_pending = 0;
                gdb_run_until_stop(m, &gc, &bp, max_insns, 0, reply, sizeof(reply));
                gdb_send_packet(&gc, reply);
                if (reply[0] == 'W') { closesocket(client_sock); return; }
                continue;
            }
            {
                int rc = gdb_dispatch(m, &gc, &bp, max_insns, &fpu_enabled_session, pkt, &halted);
                if (rc == 0) { closesocket(client_sock); return; }       /* k */
                if (rc == 2) { closesocket(client_sock); goto run_free; } /* D */
            }
            if (halted) { closesocket(client_sock); return; } /* a 'W' stop-reply was just sent */
        }
    }

run_free:
    /* Detached: finish unmonitored, exactly like main.c's own ordinary
     * (non-gdb) loop. */
    while (!m->cpu.halted && m->cpu.insns < max_insns) {
        cpu_step(m);
        if (m->net && (m->cpu.insns & 0xFFFull) == 0) net_poll(m);
    }
}
