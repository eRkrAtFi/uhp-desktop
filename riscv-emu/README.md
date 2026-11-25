# riscv-emu

A RISC-V (RV64) system emulator, built in phases toward actually booting a
real Linux kernel with our own code - the project this became after
`QemuManager/` proved that *driving* real QEMU already lets you boot any
Linux image today. This is the other direction: our own C, no QEMU involved
at runtime, verified at every phase by comparing against QEMU's own
`qemu-system-riscv64` byte for byte.

RISC-V, not x86-64, is the deliberate choice here: Linux cannot run in 8086
real mode at all (the `emu86` project's ISA), and a full x86-64 target
capable of booting Linux (long mode, paging, APIC, ACPI, a real device set)
is roughly Bochs-scale, not a side project. RISC-V's privileged
architecture is dramatically simpler - no segmentation, a handful of CSRs,
one paging mode to support for 64-bit Linux (Sv39), and a minimal required
device set - which is exactly why hobbyist "boot real Linux" emulators
(Fabrice Bellard's own TinyEMU being the best-known) target it.

## Phase map

1. **RV64IM core, M-mode only, flat physical memory** - done and verified.
   No CSRs, no traps, no paging: a guest runs as if it were the very first
   code after reset on real hardware. Two MMIO devices (16550 UART, SiFive
   test-finisher) are enough for a bare-metal test program to produce
   console output and cleanly end the run.
2. **CSRs, trap/exception handling, M/S/U privilege levels, PMP** - done
   and verified. Timer interrupts need a timer device (CLINT, Phase 4) and
   are not part of this phase; the trap *plumbing* they will use
   (mie/mip/mstatus bits, delegation) already is.
3. **Sv39 paging (the mode riscv64 Linux uses)** - done and verified.
4. **CLINT and real interrupt delivery, with a legacy-SBI timer call** - done
   and verified. A minimal PLIC (for UART/device interrupts, as opposed to
   CLINT's timer/software ones) is deferred: a headless boot's console can
   poll the UART directly without it, revisited only if Phase 5 actually
   needs it.
5. Boot an actual riscv64 Linux kernel + initramfs, with real interactive
   console input - done. A real shell prompt, under our own CPU, no real
   QEMU/OpenSBI at runtime, F/D (floating point) implemented, and typed
   commands actually work reliably (see "Phase 5, finished" for the
   interrupt-routing work this took, and the "root-caused" update there for
   a real bug this surfaced and fixed in the UART/PLIC model).
6. **virtio-blk over virtio-mmio** - done and verified standalone against
   its own test program (differentially, against real QEMU, like every
   other phase).
7. **virtio-blk wired into the real Linux boot** - done. The kernel probes
   and binds the device, a real read/write round trip through the actual
   block layer was verified from the live shell, a real ext2 filesystem
   on it was verified persistent and differentially matched against real
   QEMU, and the kernel boots directly from it with `root=/dev/vda` and no
   initramfs at all (`boot/boot_root.sh`).
8. **virtio-net, via a user-mode NAT** (not TAP - see "Phase 9") - done.
   `ping` and `wget` both reach the real internet from the guest.

This was a multi-session project; see "Phase 5, done", "Phase 6", "Phase
7", "Phase 9", and "Where this stands" below for the full path there.
What remains: nothing on the virtio-blk disk is filesystem-shaped beyond
the minimal busybox root `boot_root.sh` boots, so a real general-purpose
distro is further out.

## Phase 1: what's implemented and how it's verified

- Full RV64IM instruction set: LUI, AUIPC, JAL, JALR, all six branches,
  all seven loads (including RV64-only LWU), all four stores, the full
  OP-IMM and OP-IMM-32 groups (including SLLI/SRLI/SRAI with 6-bit RV64
  shift amounts and their 32-bit W-suffixed counterparts), the full OP and
  OP-32 groups, and the M extension (MUL/MULH/MULHSU/MULHU/DIV/DIVU/REM/REMU
  plus their W-suffixed word forms) in both groups.
- `tests/selftest.S` is a differential test in the same spirit as
  `emu86/tests/selftest.asm`: 15 cases (ids `1`-`F`) covering the easy ways
  to get this wrong - 8-bit/32-bit sign extension on loads and on `ADDIW`,
  `SRAI` vs `SRLI` preserving or clearing the sign bit, signed vs unsigned
  branches and `SLT`/`SLTU` on the same bit pattern, `MULH`'s 128-bit
  product, and the RISC-V-mandated (not trapped!) results for division by
  zero and for `INT64_MIN / -1` in both the 64-bit and word-sized forms.
- It builds with **LLVM/clang**, not GNU binutils: no RISC-V GNU cross
  toolchain is installed on this host, but upstream LLVM is multi-target by
  default, so `clang --target=riscv64-unknown-elf -march=rv64im` plus
  `ld.lld` and `llvm-objcopy` is a complete, zero-extra-install cross
  toolchain. `tests/link.ld` places `_start` at exactly `0x80000000` with no
  ELF header taking up space first - needed because our own loader reads a
  flat binary dump, while QEMU's `-kernel` reads the ELF directly and
  trusts its `e_entry` regardless of layout; without the linker script the
  two would disagree about where execution actually begins.
- **Verified, not just built**: `make compare` runs the identical guest
  image under `riscv-emu` and under `qemu-system-riscv64 -M virt -bios none`,
  and diffs the two UART output streams.

  ```
  $ make compare
  riscv-emu matches QEMU
  ```

  The two output files are literally byte-identical (same MD5). The guest
  ends both runs itself, by writing to the SiFive test-finisher device - the
  same mechanism QEMU's virt machine implements - so neither run needs a
  timeout or an external kill.

  One Windows-specific gotcha repeated from `emu86`: stdout must be put in
  binary mode (`_setmode(_fileno(stdout), _O_BINARY)` in `main.c`), or the
  C runtime turns the guest's own `\n` into `\r\n` and the two streams will
  never match even when the emulation itself is correct.

## Phase 2: what's implemented and how it's verified

- All six CSR instructions (`CSRRW/S/C` and their `I` immediate forms), with
  the real-hardware rule that `CSRRW` always writes while `CSRRS`/`CSRRC`
  skip the write when `rs1` (or the immediate) is `x0`/zero - not an
  optimization, a documented spec requirement so a pure-read access through
  those forms never triggers a CSR's side effects.
- Full trap entry/exit: `ECALL` (cause depends on current privilege: 8/9/11
  for U/S/M), `EBREAK`, illegal-instruction traps for genuinely malformed
  encodings within a recognized opcode (as opposed to an opcode this
  emulator simply hasn't modelled at all, which still halts the host - see
  `cpu.c`'s `unimplemented()` vs `illegal_instruction()`), and `MRET`/`SRET`
  with the full privilege/interrupt-enable bit dance (`MPP`/`MPIE`/`MIE`,
  `SPP`/`SPIE`/`SIE`) the spec requires.
- Trap **delegation** via `medeleg`/`mideleg`: a trap taken while already
  below M can be routed to the S-mode handler (`stvec`) instead of M's, with
  privilege staying at S rather than elevating - the mechanism `MRET`-ing
  into S-mode and then letting S-mode handle its own `ECALL`s depends on.
- **PMP** (Physical Memory Protection): `pmpcfg0`/`pmpcfg2` and 16
  `pmpaddr` registers, with TOR/NA4/NAPOT address matching. This was not
  originally scoped for "CSRs and traps," but real hardware (QEMU included)
  denies *every* S/U-mode fetch and memory access by default when no PMP
  region is active - M-mode firmware always configures one before ever
  dropping into S-mode. Without it, nothing beyond M-mode can be observed
  doing anything at all, which makes it load-bearing for "privilege levels"
  rather than a separate feature.
- `tests/selftest.S` gained cases `G`-`L`: `CSRRW/S/C` and the immediate
  forms on `mscratch` (a side-effect-free CSR, safe to batter on), an
  `ECALL` from M-mode trapping to `mtvec` and `MRET` returning correctly, a
  deliberately-illegal encoding (an undefined `OP-IMM-32` `funct3`) trapping
  with the right `mcause`/`mtval`, `EBREAK`, and - the capstone - setting up
  a permissive PMP region, delegating `ECALL`-from-S via `medeleg`, `MRET`-ing
  into S-mode, taking a *delegated* trap there, and `SRET`-ing back, all
  while remaining in S-mode throughout.
- **Caught by the differential test, not by inspection**: two real bugs.
  One was in the *test*, not the emulator - `(A|B) & ~B` is `A & ~B`, not
  `A`, whenever `A` and `B` share set bits, so case `G`'s "restore the
  original value" comment was simply wrong about what CSRRC does; a stderr
  printf of the actual CSR values during the one run that disagreed with
  itself (`G FAIL` on *both* systems, so `make compare` alone wouldn't have
  flagged it - the self-inconsistency did) found it in a minute. The other
  was real and only visible against actual hardware: `make compare` hung
  with QEMU spinning forever, because our emulator let S-mode fetch and
  access memory freely while real QEMU correctly enforced PMP's
  deny-by-default and faulted - `qemu-system-riscv64 -d int,guest_errors`
  (not `in_asm`/`trace`, which aren't valid `-d` categories; `-d help` lists
  the real ones) showed the exact `riscv_cpu_do_interrupt` fault QEMU took
  that our own trace couldn't, since our emulator had no fault to show.

## Phase 3: what's implemented and how it's verified

- A real three-level Sv39 walk (`translate()` in `cpu.c`): VPN[2]/VPN[1]/VPN[0]
  indexing, the canonical-address check (bits `[63:39]` must equal bit 38,
  the same idea as x86-64's canonical addresses), the reserved `R=0,W=1`
  PTE encoding, superpage alignment checks at levels 1 and 2, `U`-bit vs.
  current privilege (gated by `mstatus.SUM` for S-mode touching a `U` page),
  `mstatus.MXR` (loads may read an execute-only page), and A/D-bit auto-set
  on access/write (the spec permits either auto-set or a software-managed
  trap; auto-set needs no new trap-and-retry path in an interpreter that has
  no separate TLB-fill step to hook into).
- Translation sits in front of every fetch, load, and store; PMP is checked
  *after* translation, on the resulting physical address - M-mode still
  bypasses both (real hardware's `mstatus.MPRV`, letting M-mode optionally
  translate on S/U's behalf, is not modelled; no guest code here needs it).
- `SFENCE.VMA` is accepted as a no-op: there is no TLB cache to flush, since
  every access re-walks the table fresh.
- `tests/selftest.S` gained cases `M` and `N`. `M` builds page tables *from
  S-mode* (satp's minimum privilege is S - no M-mode involvement needed,
  exactly like real Linux setting up its own page tables) with two identity
  superpages (covering UART+test-finisher and our own RAM) plus one genuine
  three-level mapping of a deliberately non-identity virtual address
  (`0x1_4000_0000`) to a scratch physical page; writing through that alias
  and reading the same byte back through the identity mapping is the proof
  that the walk computed a real translation rather than a VA==PA
  passthrough coincidence. `N` accesses a virtual address with no mapping at
  all and confirms the resulting page fault (cause 13) reaches `mtrap_handler`
  undelegated, which still `MRET`s back to S correctly.
- **Two more test bugs, both in the test, not the emulator** - the pattern
  from Phase 2 held. Debugged both the same way: a temporary `fprintf` in
  `translate()` printing the VA/PA it actually computed, since the test
  itself gave no indication of which side was wrong, only that `M` failed.
  First was `(A|B)&~B` logic repeated from Phase 2's bug in a different
  spot. Second, more interesting: `LW` sign-extends its 32-bit result to 64
  bits unconditionally, but `li t6, 0xCAFEBABE` assembles to `lui+slli+addi`
  in a way that leaves the upper 32 bits at zero rather than sign-extending
  them (the `lui`'s own 32-bit result has bit 31 clear before the `slli`
  shifts it into place, so standard LUI sign-extension never triggers) -
  comparing the two directly XORs a sign-extended value against a
  zero-extended one that happen to share only the low 32 bits. Fixed by
  explicitly clearing the loaded value's upper 32 bits (`slli`/`srli` by 32)
  before comparing, rather than trusting two differently-synthesized 32-bit
  constants to extend the same way.

## Phase 4: what's implemented and how it's verified

- **CLINT** (`devices.c`): `mtime` (a free-running counter, advanced once
  per retired instruction - deliberately *not* wall-clock, so `make compare`
  stays reproducible without needing our clock and QEMU's real-time one to
  tick in lockstep; tests arm `mtimecmp` to an already-past value, which is
  "expired" under any correct clock immediately, so the observable outcome
  never depends on exactly when either clock ticks), `mtimecmp`, and `msip`,
  at QEMU's real SiFive CLINT addresses.
- **Real interrupt delivery** (`check_interrupt()` in `cpu.c`, called once
  per instruction boundary): the spec's fixed priority order among
  simultaneously pending+enabled causes, and the *actual* enable rule - not
  just "is the target level's own global enable bit set," but "is the
  current privilege level even allowed to have that bit matter" (running
  below an interrupt's target privilege always takes it, regardless of that
  level's enable bit; running above it never does). `mideleg` continues to
  decide the target exactly as exceptions already did since Phase 2's
  `raise_trap` - interrupts reuse it unchanged.
- **No new emulator-side SBI code.** The "firmware" servicing an SBI call is
  guest-executed M-mode RISC-V assembly in the test, not a host intercept -
  unlike `emu86`'s BIOS, we already have full M-mode CPU execution, so there
  is nothing to intercept. This mirrors what OpenSBI actually is: ordinary
  M-mode software.
- `tests/selftest.S` gained case `O`, built around the one legacy SBI call
  (`EID 0`, "set timer") a hart without the newer Sstc extension actually
  needs: CLINT's timer is M-mode-only hardware, so a timer interrupt always
  reaches `mtrap_handler` first (MTI is never delegated); on its first
  occurrence that handler disarms the timer, injects a pending supervisor
  timer interrupt by writing `mip` directly (a plain CSR write M-mode is
  always permitted), delegates STI (`mideleg` bit 5) so it reaches S
  directly from then on, and - since a real SBI call must always reach M,
  never S - stops delegating `ECALL`-from-S (undoing Phase 2 test L's own,
  narrower choice to delegate it for that test's own purpose). S-mode's
  timer handler then needs M's help again to actually *clear* the
  interrupt: it issues `ecall` with `a7=0` (legacy SBI "set timer"),
  `a0=`new `mtimecmp`, and `mtrap_handler`'s new SBI branch services it.
- **A third bug, and this one is a real finding about hardware, not a test
  mistake**: the first implementation let S-mode clear `STIP` through the
  `sip` CSR alias, same as `SSIP`. Real `qemu-system-riscv64` disagreed -
  `make compare` hung with the same interrupt re-firing at the same `epc`
  forever (`-d int` showed `s_timer` retriggering nonstop; `STIP` was never
  actually clearing). **`STIP` is read-only through `sip`**: without Sstc,
  only M-mode can clear a software-injected supervisor timer interrupt,
  which is the entire reason the legacy SBI "set timer" call exists at all
  - a fact this project would have simply assumed wrong without a real
  system to disagree with it. Fixed with a separate, narrower write mask
  for `sip` (`SSIP` only) distinct from its read mask (`SSIP`/`STIP`/`SEIP`),
  and by implementing the real SBI call rather than clearing the bit
  directly.
- A smaller, second bug surfaced alongside it: enabling `mie.MTIE` once at
  boot (required - it is only writable from M-mode, and the test is in
  S-mode for good by test L) combined with `mtimecmp`'s implementation-defined
  reset value let a timer interrupt fire *immediately*, in the middle of
  test L's own unrelated setup, before test O ever ran - and that premature
  firing's one-time `medeleg` fixup (undelegating `ECALL`-from-S) broke
  test L's own deliberate delegation of it. Fixed by disarming `mtimecmp`
  (writing it to all-ones) *before* enabling `MTIE`, not after - the same
  order real firmware uses, for the same reason.
- Both handlers now save and restore every register they touch, on the
  stack, before returning - not needed for the synchronous exceptions
  Phases 2-3 added (those only ever fire at a test's own deliberate,
  known instruction), but unavoidable once an *interrupt* can land between
  any two instructions of whatever the interrupted code was doing, and
  essential once `strap_handler` itself calls back into `mtrap_handler` via
  its own nested `ecall`.

## Phase 5: booting a real Linux kernel

Attempted with a real downloaded riscv64 kernel + initramfs (6.11.0-rc4,
`boot/KernelImage` + `boot/initramfs.cpio.gz`; independently confirmed to
boot to a shell under real `qemu-system-riscv64` + OpenSBI first, so any
failure here is ours, not a bad image). `boot/boot.sh` builds the full
command line: firmware at `RAM_BASE`, the kernel at its own `text_offset`
(read from the Image header, not hardcoded), the initramfs and a
hand-built DTB (`boot/make_dtb.py` - no `dtc` on this host) elsewhere in
RAM, and `a0`/`a1`/`t3` set the way `boot/firmware.S` (a minimal from-scratch
M-mode firmware: PMP, `medeleg`, legacy+base SBI, MTI->STI forwarding, all
reusing Phase 2-4's own machinery - nothing host-side to intercept, exactly
like Phase 4's SBI) expects.

Two new extensions turned out not to be optional: every mainstream
toolchain defaults to `-march=rv64gc`, so **A** (LR/SC, AMO*) and **C**
(compressed 16-bit encodings) had to be added before a stock kernel Image
could execute even its own first few instructions. A single-reservation
LR/SC model is enough (one hart). The fetch path now translates and
PMP-checks the first halfword, decides compressed-or-not from its low 2
bits, and - only for a 32-bit instruction - separately translates the
second halfword, since a 4-byte instruction can itself straddle a page
boundary once alignment is only 2 bytes. `mtval`/`stval` on an illegal
instruction holds the real faulting bits (16 for compressed, not the
synthesized 32-bit expansion) - Linux's own lazy-FPU-enable trap handler
decodes that value, so getting it wrong misfires that logic, not just
cosmetic. F/D (floating point) stayed out: Linux enables FPU access
lazily, per process, on its first FP instruction - a boot to a shell is a
bet that nothing on this path ever executes one, and that bet held.

A direct-mapped 256-entry TLB was also added, in `translate()` itself -
not planned, but a full 3-level Sv39 walk with no caching at all on every
single fetch/load/store made an actual boot attempt impractically slow to
ever observe the result. A `satp` write or `sfence.vma` flushes the whole
array rather than tracking what a change could affect.

Three real bugs surfaced, each found by bisecting `--max` down to the
exact instruction where a run stopped making progress, then tracing only
that narrow window (`--trace-after N`, added to `main.c` - tracing from
instruction 0 is far too slow once the count is in the hundreds of
millions):

- **Firmware bug**: `medeleg` was never set, so the kernel's first real
  page fault went to M-mode's own catch-all handler (which doesn't know
  what a page fault is) instead of the kernel's. Fixed by delegating every
  synchronous cause the kernel's own handler is meant to field, keeping
  only `ECALL`-from-S (SBI calls) at M, same as real firmware.
- **Real emulator bug, in `csr_write`'s `satp` case**: any `MODE` value
  was accepted uncritically. Linux's `head.S` probes for the best paging
  mode by speculatively writing Sv57 (mode 10), then reading `satp` back -
  on real hardware a write with an unsupported mode has *no effect at all*,
  which is the only way software learns to fall back to Sv39. Accepting
  the write instead left `satp` holding a mode `translate()` doesn't
  implement, so every fetch immediately started page-faulting forever.
  Fixed to only accept mode 0 (Bare) or 8 (Sv39); anything else is now a
  silent no-op, matching real hardware.
- **DTB config bug**: `timebase-frequency` was set to 1 (Hz) on the theory
  that it should honestly describe `mtime` (which advances once per
  *instruction* here, not per real second - see Phase 4). This broke the
  kernel's clocksource mult/shift conversion outright: any `rdtime`-based
  wait (delay calibration, RCU stall checks, the end-of-boot wait for the
  next tick) turned into a wait for hundreds of millions of ticks -
  observed directly as the boot stalling for 500M+ instructions at a
  `csrrs a1, time, zero` loop. Fixed by claiming the same value real
  `qemu-system-riscv64 -M virt` does (10 MHz) instead of an "honest" one -
  the kernel's math assumes a realistic-magnitude frequency, not whatever
  the backing clock actually is.

With all three fixed, the kernel enables Sv39, relocates to its real
high-half virtual address (`0xffffffff80...`), and makes several genuine
SBI calls - all of Phase 2-4's privilege/trap/SBI machinery is correctly
exercised by real, unmodified kernel code at this point, not just a test
written to exercise it. It then reaches a new, *confirmed-genuine* (not
merely slow - verified by raising `--max` to 3 billion instructions with
zero pc movement) infinite spin: a tight `ld` / `div`-by-zero / `fence` /
`beq` loop polling one fixed memory address that nothing ever updates.

The PLIC guess above turned out to be wrong, ruled out two ways: real
`qemu-system-riscv64` boots this exact kernel+initramfs fine under
`-smp 1` (so it is not an SMP/secondary-hart issue), and the stalling
address itself, once actually disassembled (`--peek`, added for this -
dumps guest memory through the *current* page tables, because the kernel
had by then relocated itself; the raw Image file on disk no longer
matches what is physically there), turned out to be a legitimate
`rdtime`-based delay-with-backoff primitive, not a lock. Two real gaps
addressed as a result: the devicetree had no `clint@` node at all (so
Linux's CLINT timer driver had nothing to bind to), and the emulator had
no **Sstc** extension (real hardware this project was tested against has
it - a kernel that detects it never touches CLINT or legacy SBI timer
calls at all). Implementing both got interrupts flowing for the first
time (confirmed via real `scause` values, not just inference) and moved
the stall to a *different* address, now recognizable as Linux's own RNG
entropy-gathering (`random_init`) - plausible root cause: this emulator's
`mtime` is perfectly deterministic (one tick per retired instruction, no
actual jitter at all), and timing jitter is exactly what that code is
trying to harvest. Not pursued further this session (deliberately
descoped in favor of `QemuManager` work - see its own README) - open
thread for whenever Phase 5 picks back up.

### Revisited: the `a0`/`a1` fix didn't change the stall, and why that's useful

Picked back up in a later session specifically to re-test the `a0`/`a1`
fix above against the boot stall, since it was found afterward and never
re-checked. It didn't move anything: same `pc` range
(`0xffffffff809f2ed0`-`...ede`), same instruction pattern. Disassembling
that range confirms it decodes to `csrrs a2, time, zero` plus a
compressed branch - the same `rdtime`-in-a-loop shape as before.

What *is* new, from `RISCV_EMU_SBIDBG=1` (an existing env-var-gated trace
in `raise_trap`, `src/cpu.c`, logging every ECALL-from-S and timer
interrupt - this was already there, just not pointed at this question
before): the kernel makes exactly **18 SBI calls total**, all within the
first ~21.6M instructions of a 2-billion-instruction run - base-extension
probes (`get_mvendorid`/`marchid`/`mimpid`, `probe_extension` on `TIME`,
one other unidentified extension, and `HSM`) - and then **none at all**
for the remaining ~1.98 billion, while **49,526 supervisor timer
interrupts fire** in that same window with zero corresponding
`sbi_legacy_set_timer` calls to rearm or clear them. That absence isn't a
bug by itself - it's consistent with the kernel having detected Sstc (see
above) and managing `stimecmp` directly via CSR, which needs no SBI call
at all - but it does confirm the stall is *after* Sstc takes over, not a
timer-delivery problem.

**Also newly confirmed, and more surprising**: across the *entire* run,
the kernel never once calls the legacy console putchar (EID 1) - or any
other SBI call shaped like console output. Implemented the modern SBI
**DBCN** (Debug Console, EID `0x4442434E`) extension in `boot/firmware.S`
on the theory that a kernel this new might require it rather than merely
prefer it over the legacy call - `sbi_debug_console_write`/`_write_byte`/
`_read`, reusing only already-trap-saved registers (`t0`-`t3`, `a2`; see
its own comment for why `t4`-`t6` were never safe to touch), plus
advertising it from `probe_extension`. The `RISCV_EMU_SBIDBG` trace above
confirms this didn't help *because the kernel never probes for DBCN
either* - it does not reach for a console at all, by any known path, at
any point in this run. The implementation is real and kept (it is
correct, and will matter once whatever is actually blocking console
registration is found), but it was not the fix here.

Also tried: a `/chosen/rng-seed` property (`boot/make_dtb.py`, consumed by
`early_init_dt_scan_chosen()` → `add_bootloader_randomness()`) on the
entropy-starvation theory above - a fixed 64-byte constant, not real
entropy, kept fixed so a boot attempt stays reproducible for debugging.
No change to the stall either.

**Where this leaves Phase 5**: the `a0`/`a1` fix is confirmed *not* the
cause of this particular stall (it may still have been a real, separate
bug worth having fixed - the console-input echo test that found it is
unaffected either way). The RNG-entropy hypothesis is now secondary to a
more fundamental open question the SBI trace actually raised: **why does
this kernel never attempt console output or an SBI console probe at all**
during a run that is otherwise clearly alive (49K+ timer interrupts,
multiple base-extension probes, 21M instructions of real work before
settling into the observed loop)? Pinning down the exact function at
`0xffffffff809f2ed0` needs either matching kernel debug symbols (no
`vmlinux`/`System.map` for this exact build is present in this repo) or
further bisection - not attempted this session; the SBI-call-count finding
above is the concrete lead to start from next.

Also found, independently of the boot stall, while implementing real
guest console input (see "Console input" below) with a tiny standalone
echo-loop test program: `boot/firmware.S`'s trap handler saved **and
restored** `a0`/`a1` around every trap, silently discarding the return
value of *every* SBI call made this entire phase (legacy and base alike) -
the restored value was always just whatever `a0` held at the moment of
the `ecall`, not what the handler had just computed. Fixed by dropping
`a0`/`a1` from that save/restore (every path through this handler means
to return through them; nothing here treats them as callee-saved). This
plausibly explains why adding HSM support earlier did not change the
kernel's behavior at all - the "yes, HSM is present" answer was being
overwritten back into a nonzero (therefore "failed") value before the
kernel ever saw it. Worth re-testing the kernel boot now that this is
fixed, but not yet done.

## Console input

The guest UART (`src/devices.c`) now has a real receive side: a
background thread (`console_input_start` in `src/main.c`, Windows-only,
using `_read()` against stdin's raw fd rather than buffered
`fgetc`/`fread` - stdio silently switches to large fully-buffered mode on
a non-console stdin, e.g. a redirected file or a GUI's piped console
input, which would sit on bytes for a while before this ever saw them)
feeds a lock-free single-producer/single-consumer ring buffer
(`StdinRing` in `src/emu.h`); the interpreter only ever pops from it, so
it never blocks on host I/O. `boot/firmware.S`'s legacy SBI getchar call
now actually polls LSR's data-ready bit before reading RBR, instead of
unconditionally answering "no input" - real hardware convention, not a
shortcut. Verified with a tiny standalone test (`boot/echo_test.S`+`.ld`:
loops on getchar, echoes whatever it reads via putchar) piping bytes into
`riscv-emu.exe`'s stdin and confirming they come back out byte-for-byte on
stdout - this is also what caught the `a0`/`a1` bug above, since the
echoed bytes were wrong (always 0) before that fix.

A second, host-side-only bug surfaced later, through QemuManager's own
Phase 5 terminal (xterm.js - see `QemuManager/QemuManager/Assets/terminal.html`):
a lone trailing `\r` (a real terminal's Enter key, with nothing queued
after it) sent from a parent process that gave this one **no console of
its own** (Win32's `CREATE_NO_WINDOW`, exactly what
`RiscvEmuProcessManager.Start` uses) never reached `stdin_reader_thread`
at all - confirmed directly: the exact same write succeeded immediately
both from a real console-attached shell and from a child process allowed
its own console, and failed consistently (for any amount of waiting)
only under `CREATE_NO_WINDOW`. Cause: stdin defaults to **text mode** on
this C runtime, same as stdout did before its own `_setmode(...,
_O_BINARY)` fix above - but in the read direction, text mode's `\r\n`
translation logic holds a lone `\r` back, waiting to see whether a
following `\n` arrives before deciding how to deliver it, and with
nothing else queued behind it, that wait never ends. Fixed the same way
stdout already was: `_setmode(_fileno(stdin), _O_BINARY)`, right next to
it in `main()`.

## Build

```sh
export PATH="../tools/w64devkit/bin:/c/Program Files/LLVM/bin:/c/Program Files/qemu:$PATH"
make            # riscv-emu.exe
make test       # run the self-test in riscv-emu alone
make compare    # run it in both riscv-emu and real QEMU, diff the output
make trace      # --trace output for debugging the interpreter
```

Add LLVM and QEMU to `PATH` rather than hard-coding their install paths in
the Makefile: a literal `/c/Program Files/...` path embedded in a recipe
resolves *inconsistently* depending on what launched `make` (an MSYS/git-bash
parent vs. a native PowerShell/cmd parent each get different quote/escape
handling from w64devkit's `sh`) - the same reason `emu86`'s Makefile already
resolves `gcc` and `nasm` through `PATH` instead of an absolute path. Fighting
that mismatch is a waste of time; resolving the tool by name through `PATH`
sidesteps it entirely.

## How this maps onto QEMU

| riscv-emu | QEMU |
| --- | --- |
| `src/cpu.c` switch dispatch | `target/riscv/` plus TCG translation blocks |
| `src/devices.c` host-served MMIO | `hw/riscv/virt.c` + real device models |
| `src/main.c` image loading | `system/vl.c` machine setup |
| `tests/selftest.S` + `make compare` | `qemu-system-riscv64 -M virt -bios none -kernel` |
| the test-finisher device | `hw/misc/sifive_test.c` |

## Where this stands

Phases 1-4 are complete and differentially verified against real
`qemu-system-riscv64` - RV64IM(AC), CSRs, traps, M/S/U privilege,
delegation, PMP, Sv39 paging (with a TLB), and a real CLINT-driven
interrupt with a working legacy SBI timer call all byte-for-byte match,
including a guest that builds its own page tables, runs through a
non-identity translation, and services its own timer interrupt through a
correctly-privileged SBI round trip.

Phase 5 (see above) got a real kernel through MMU enable, the jump to its
high-half virtual address, and multiple genuine SBI calls, by finding and
fixing real bugs one at a time, bisecting `--max` to the exact stalling
instruction and tracing (or `--peek`-ing guest memory through its *current*
page tables, once the kernel started relocating itself) only that narrow
window each time: a missing `medeleg` delegation, the emulator accepting
any `satp` MODE instead of rejecting unsupported ones, a DTB timer
frequency that broke the kernel's own clocksource math, a missing `clint@`
devicetree node, a missing Sstc extension, and - found via an unrelated
console-input test, not the boot stall itself - every SBI call's return
value being silently discarded by the firmware's own trap handler. It has
not yet reached a shell. Re-verified in a later session against the
`a0`/`a1` fix (see "Revisited" above): no change - same stall, same
`rdtime`-loop shape. New finding from that session, via the
`RISCV_EMU_SBIDBG` trace: the kernel makes its last SBI call at
instruction ~21.6M (of a 2-billion run) and never attempts console
output - neither legacy putchar nor the newly-added SBI DBCN extension -
at any point. SBI DBCN support and a DTB `rng-seed` property were both
added on plausible theories; neither changed the outcome, but DBCN is a
real, correct addition kept regardless. The open question going into the
next session shifted from "why is the RNG loop never fed jitter" to "why
does this kernel never reach for a console at all." Guest console
*input* now genuinely works (verified with a standalone echo-loop test),
independently of whether boot itself completes. Treat this as the
foundation the next session builds on, not as a short remaining list.

## Phase 5, resolved: the stall was never an emulator bug

The "why does this kernel never reach for a console" question above had a
mundane answer, found by comparing against real hardware instead of staring
at another trace: `boot/real_qemu_smp1_hvc0.log` is this exact kernel image
booted under real `qemu-system-riscv64` + real OpenSBI with
`console=hvc0 earlycon=sbi` - the same cmdline `boot.sh` was building - and
it *also* ends in `Warning: unable to open an initial console.`, going no
further. `boot/real_qemu_smp1.log` is the same kernel with `console=ttyS0`
instead, which boots clean to a shell prompt. This kernel build simply has
no SBI-hvc (`CONFIG_HVC_RISCV_SBI`) console driver compiled in - OpenSBI
itself offers `dbcn` (confirmed in its own banner) and this project's
firmware does too (see "SBI DBCN extension" above), but neither matters
when the kernel-side driver that would consume it does not exist. No
emulator bug, real or imagined, would ever have fixed this: `console=hvc0`
was never going to work here, under any correct implementation.

Fixed by switching to `console=ttyS0`: `boot/make_dtb.py` now emits a
`ns16550a` node at `UART_BASE` (deliberately with no `interrupts`/
`interrupt-parent` property - Linux's 8250 OF driver treats a missing irq
as "use polling mode" rather than a probe failure, confirmed by the boot
log's own `of_serial 10000000.serial: error -ENXIO: IRQ index 0 not found`
immediately followed by a successful `ttyS0 at MMIO 0x10000000 ... is a
16550A`, so this still needs no PLIC), and `boot.sh`'s cmdline switched to
match. Real kernel log output appeared immediately - the exact thing 21.6M
instructions of SBI tracing in an earlier session never saw.

That got as far as a second, equally mundane bug: a kernel panic,
`VFS: Unable to mount root fs on unknown-block(0,0)`, because `boot.sh`'s
cmdline passed `rdinit=/sbin/init`, a path that does not exist in this
initramfs - confirmed by parsing `boot/initramfs.cpio.gz`'s newc headers
directly (no `cpio` binary on this host) rather than guessing, which found
an `init` entry at the archive root and no `sbin/` directory at all. Real
QEMU's own working boot (`real_qemu_smp1.log`) never passed `rdinit=`
either, defaulting to the same `/init` the kernel already tries first.
Fixed by changing `rdinit=/sbin/init` to `rdinit=/init`.

With both fixed, the kernel now boots end to end under `riscv-emu` -
mounts the initramfs as root, prints `Run /init as init process`, and
actually starts executing real userspace code at PID 1, not emulator or
firmware code - something this project had never reached before this
session. It gets a few hundred thousand userspace instructions in before
hitting the next wall: `unimplemented instruction 0000b920 at
pc=0000000000010490`. Decoded, not guessed: `0x0000b920`'s low halfword
`0xb920` is quadrant 0, funct3 `101` - `C.FSD`, a compressed
floating-point store - and `cpu.c` rejects `C.FLD`/`C.FSD` by explicit,
documented design (see Phase 1 above): this emulator has never implemented
F/D at all. `/init` (or whatever runs first out of this initramfs) was
built by a normal toolchain targeting the standard `rv64gc` profile, which
freely emits `fld`/`fsd` for ordinary 8-byte memory moves - unrelated to
whether the C source does any actual floating-point math - so essentially
any real-world userspace binary built the normal way will hit this same
wall immediately. This is a clean, understood, correctly-scoped next
phase (a real F/D implementation: `f0`-`f31`, `fcsr`/`frm`/`fflags`, the
IEEE 754 arithmetic/compare/convert instruction groups), not another
mystery to chase - and it is the first time this project has gotten far
enough to need one.

## Phase 5, done: a real Linux shell, under our own CPU

F/D is now implemented - `f0`-`f31` (NaN-boxed for single-precision, per
spec), `fcsr`/`frm`/`fflags`, every arithmetic/compare/convert/classify/move
instruction in the 32-bit `LOAD-FP`/`STORE-FP`/`OP-FP`/`FMADD`-family
opcodes, and the four compressed forms (`C.FLD`/`C.FSD`/`C.FLDSP`/
`C.FSDSP`) that `expand_c()` had been rejecting since Phase 1. `mstatus.FS`
(and its `sstatus` alias, now added to `SSTATUS_MASK`) gates every FP
opcode behind an ordinary illegal-instruction trap when `Off` - no new
mechanism needed, since Linux's own lazy-FPU-enable trap handler already
expects exactly that (see the `trap_val` comment in `cpu_step`) and answers
it with a `sstatus` write this emulator already had the plumbing for.
`boot/make_dtb.py`'s `riscv,isa` string changed from `rv64imac_sstc` to
`rv64imafdc_sstc` to match - without `f`/`d` there, `riscv_fill_hwcap()`
never sets the hwcap bits that make the kernel treat an FP opcode as a
lazy-enable candidate instead of a real `SIGILL`. Arithmetic itself uses
the host's native `float`/`double` (including `fma`/`fmaf` for the fused
multiply-add family, for genuine single-rounding fidelity) rather than a
from-scratch soft-float implementation - correct for normal values under
the default round-to-nearest-even mode both sides already use; `frm`'s
other rounding modes and precise `fflags` accumulation are not
implemented (a documented simplification: nothing in this boot path reads
the flags, and getting this far at all was the open question).

With that, `boot/boot.sh` now boots clean, end to end, under `riscv-emu`
alone - no real QEMU, no real OpenSBI involved at runtime, only the two
devicetree/cmdline fixes above plus this new F/D support. The log ends the
same way `boot/real_qemu_smp1.log` (the real-hardware reference used to
find all of this) does:

```
Welcome to you Linux RISC-V 64 Environment!
arch: riscv64: Linux (none) 6.11.0-rc4-gc97670d2ca04 #1 SMP ... riscv64 GNU/Linux
~ #
```

**Phase 5 debt paid off, in a later session**: `tests/selftest.S` now has
a test R covering every F/D opcode group (arithmetic, FMADD-family,
compare, min/max, convert, classify, sign-injection, NaN-boxing, fcsr/frm,
the four compressed FP forms, and `mstatus.FS` gating) and `make compare`
matches real `qemu-system-riscv64` byte-for-byte, closing the one gap
this section used to describe.

Getting there surfaced a real bug, in this emulator, not in F/D itself,
and not something a smoke-tested kernel boot was ever going to expose:
test L (added back in Phase 2, for delegated-ECALL/SRET testing) drops to
S-mode and - by design - never returns to M except through an interrupt.
Every test from L through the end of the file, including test O's own
MTI handling, turns out to actually run in S-mode on real hardware: MTI
is undelegated so it's *serviced* in M, but `mtrap_handler`'s own MRET
returns to whatever privilege was live when the timer fired, not to M
unconditionally - a distinction this project's own earlier comments
(see test O's) got backwards. The first test to actually need an M-only
CSR write after L - test R's own original `csrs mstatus` FS-enable -
exposed it: a differential compare against real QEMU showed every
single FP sub-check failing in a way that pointed at FS never actually
turning on.

That alone would just mean test R was written assuming the wrong
privilege. The deeper finding, confirmed with `tests/selftest.S`'s own
new test S (`csrr t0, mstatus` from S-mode, expecting mcause=2) run
against both sides: **this emulator's own `do_csr()` raised the illegal-
instruction trap correctly, then `cpu_step`'s SYSTEM-opcode case did
`break` instead of `return`, falling through to the unconditional
`c->pc = next_pc` at the bottom of the function - silently overwriting
the trap's own redirect into `mtrap_handler`.** Execution just resumed
normally, one instruction further on, now mistakenly believing it was
M-mode (since `raise_trap` had already flipped `c->priv` before the
redirect got clobbered) - a real, confirmed divergence from real
hardware, invisible to every prior test because none of them attempted
a privileged CSR access while actually in S-mode. Fixed by making
`do_csr` return whether it took a trap, and having `cpu_step` `return`
immediately when it did, the same contract every other `illegal_
instruction(...); return c->halted;` call site in the file already
follows (`do_mret`/`do_sret` were already correct this way; `do_csr`
was the one path that wasn't).

Test R itself needed no privilege workaround once this was understood
correctly: `sstatus` is S-mode-legal and aliases the same `FS` bits as
`mstatus` (see `SSTATUS_MASK` in `cpu.c`), so enabling FS through
`sstatus` instead works regardless of which privilege level actually
happens to be live - sidestepping the question rather than fighting it,
and removing an earlier, more fragile attempt (masking `mie` and
retrying the `mstatus` write in a bounded loop) that treated this as a
timing race instead of the deterministic privilege bug it actually was.
(The FS-gating sub-check further down test R had the exact same latent
issue - a `csrc mstatus` that would itself fault from S-mode, making the
check "pass" for the wrong reason without ever reaching the `fadd.d` it
meant to exercise - fixed the same way, via `sstatus`.)

**Audited for the same bug shape elsewhere**: every other `illegal_
instruction`/`raise_trap` call site in `cpu.c` was checked for the same
"nested function traps, caller falls through instead of returning"
pattern. `do_mret`/`do_sret` were already correct (their own callers in
`cpu_step` `return` unconditionally right after calling them, trap or
not). `translate()`'s many internal fault sites all `return 0`
immediately after `raise_trap`, and all of *its* callers already check
that return value and `return c->halted` on failure - the established,
correct pattern `do_csr` was the one exception to. Every inline
`illegal_instruction(...)` call directly inside `cpu_step`'s own opcode
switch is already paired with its own `return c->halted;` on the same
line. The only two `raise_trap`-adjacent call sites that *don't* `return`
immediately (`cpu_translate_for_debug`'s debug-only page-table peek, and
a `RISCV_EMU_SBIDBG` ppoll-argument dump inside `raise_trap` itself) are
both explicitly debug tooling, gated behind env vars `make compare` never
sets, and the former already saves and restores every piece of trap
state `translate()` could have touched specifically because it is not a
real guest access.

**Test S hardened against a partial fix of the same bug reappearing
differently**, not just re-confirming this exact symptom: it also checks
`mstatus.MPP` really recorded S (not M) as the privilege this trap came
from - `mtrap_handler`'s cause==2 case now captures that into a new
`illegal_mpp_seen` byte specifically for this - and that `t0`, the
faulting `csrr`'s own destination register, poisoned with a sentinel
beforehand, is still holding that sentinel afterward: a CSR access that
traps must never have written its destination at all, independent of
whether `illegal_seen` happens to get set. Both of the original bug's
two compounding symptoms (the wrong privilege sticking around, and
execution resuming as if the read had silently succeeded) are now each
checked directly rather than only inferred from test R turning green
again. Confirmed to actually catch something: temporarily reverting the
`do_csr` fix reintroduces `S FAIL` immediately (test R stays green, since
it no longer depends on this bug at all after the `sstatus` fix above -
exactly the point of giving each concern its own dedicated check).

**What test R does and doesn't cover**: alongside the original 17 checks,
four more were added - `FCVT.W.D`/`FCVT.L.D` of a NaN, `FCVT.W.D` of
+Infinity, `FMIN.D`/`FMAX.D` with one NaN operand, and `FDIV.D` by zero -
specifically because these are exactly where a host's native float
arithmetic is most likely to quietly disagree with the RISC-V spec, and
one of them did: `FCVT.W/WU/L/LU.S/D` cast the input `double`/`float` to
the destination integer type directly in C, which is *undefined
behavior* for a NaN or out-of-range input, not merely imprecise - on this
host (x86-64, `cvttsd2si`/`cvttss2si`) it silently produced the SSE2
"integer indefinite" pattern (`INT32_MIN`/`INT64_MIN`) for exactly the
inputs the RISC-V spec requires to saturate to the *maximum* representable
value (`INT32_MAX`/`INT64_MAX`) instead - the wrong sign, not just the
wrong magnitude. `cpu.c` now checks `isnan()` and range explicitly before
ever casting, for all four signed/unsigned 32/64-bit forms, matching real
QEMU's own softfloat exactly (confirmed via `make compare`, and via
temporarily reverting the fix to confirm test R's new checks actually
catch it going back to `R FAIL`). `FMIN.D`/`FMAX.D`'s NaN handling and
`FDIV.D`'s divide-by-zero value both already matched, using the host's
own `fmin`/`fmax`/IEEE division directly - worth the explicit check
precisely because that was previously assumed, not verified.

**A second pass, a later session, found two more real divergences** the
first pass's four new checks didn't happen to probe - both exactly where
a host's native float is most likely to quietly disagree with the spec,
the same reasoning as the `FCVT` saturation bug above, just a different
corner of it:

- **Canonical NaN**: the spec requires any arithmetic result that is NaN
  to be *the* canonical NaN (`0x7FF8000000000000` for double,
  `0x7FC00000` for single - positive sign, only the quiet bit set),
  discarding whatever an input NaN's own payload was. `FADD`/`FSUB`/
  `FMUL`/`FDIV`/`FSQRT`/the `FMADD` family/`FCVT.S.D`/`FCVT.D.S` all used
  the host's native `+`/`-`/`*`/`/`/`sqrt`/`fma` directly with no such
  canonicalization - on this host (x86-64 SSE2), `0.0/0.0` and
  `+Infinity + (-Infinity)` both produce a NaN with the sign bit *set*
  (`0xFFF8...`) where real QEMU's softfloat gives the spec's plain
  `0x7FF8...`, and adding to an input NaN propagates that NaN's own
  payload instead of replacing it with the canonical pattern - all
  confirmed with a small standalone probe (`fmv.x.d` to read the raw
  bits back, since `feq.d` can't tell canonical and non-canonical NaN
  apart) before touching `cpu.c`. Fixed once, centrally, inside
  `f_setd`/`f_sets` themselves (the two functions every one of those
  opcodes' results already passed through to write the `f` register) -
  checking `isnan()` on the way in and substituting the canonical
  pattern - rather than at each of the half-dozen call sites
  individually, since a raw bit move (`FLD`/`FLW`, `FMV.{W,D}.X`,
  `FSGNJ*`) never goes through those two functions at all and correctly
  keeps bypassing this, exactly as spec requires for those.
- **`FMIN`/`FMAX` with signed zeros**: IEEE 754-2008's own minNum/maxNum
  (which `FMIN.S/D`/`FMAX.S/D` are built on) leaves `+0` vs `-0` as
  "numerically equal" and doesn't pin down which one a tie returns - but
  the RISC-V ISA manual does, explicitly: `FMIN(-0,+0) = -0`,
  `FMAX(-0,+0) = +0`. This host's own libm `fmin` does not reliably
  honor that (observed: it picked `+0` for `fmin(-0.0, +0.0)`, matching
  neither the manual pin-down specifically). New `rv_fmin_d`/`rv_fmax_d`/
  `rv_fmin_s`/`rv_fmax_s` wrappers handle the NaN-operand rule (unchanged
  from before) and this exact zero-sign case explicitly, falling back to
  the host's own `fmin`/`fmax` only once neither special case applies.

Test R gained three more checks for these (`0.0/0.0`, an `FADD.D` with a
deliberately non-canonical input NaN payload, and `FMIN.D`/`FMAX.D` of
`-0.0`/`+0.0` checked via `FMV.X.D` rather than `FEQ.D`, since `FEQ.D`
treats `-0.0 == +0.0` and would never see a wrong-signed result at all),
plus coverage for cases the very first `FCVT` saturation fix never got
its own explicit check for: `FCVT.WU.D`/`FCVT.LU.D` (the *unsigned*
forms have their own distinct saturation targets - 0 for a negative
input, not `INT32_MIN`/`INT64_MIN`'s signed cousins - and `FCVT.WU`'s
32-bit result is still sign-extended into the 64-bit register on
write-back, so `UINT32_MAX` must read back as all-ones) and `FCVT.W.S`
(confirming the saturation fix, which is genuinely shared code for both
`.S` and `.D` sources rather than two separate switches, actually covers
the `.S` encoding too and not just the `.D` one every other check
happens to use). All of it confirmed both ways, same as every fix in
this section: `make compare` passes with the fix in, and temporarily
reverting each of the three fixes independently (`f_setd`/`f_sets`'
canonicalization, the signed-zero special case, and `FCVT.WU`'s own
saturation) turns `R FAIL` on its own, one at a time. One of those three
verification passes caught a bug in the *test* itself, not the emulator
- `dbl_uint32_max`'s own constant was first written zero-extended
(`0x00000000FFFFFFFF`) while its own comment already said "sign-extended
form," and the bisection (a handful of inline `report()` checkpoints
printing `s2`'s running count, removed again once the culprit was
found) pointed at a check that was actually correct in `cpu.c` and wrong
in its own expected value - fixed to `0xFFFFFFFFFFFFFFFF`, matching what
sign-extension of `0xFFFFFFFF` actually is.

What remains a **known, deliberate gap, not an oversight**: `frm` (any
rounding mode other than the host's own, effectively round-to-nearest
regardless of what the instruction or `fcsr` actually requests) and
`fflags` accumulation from arithmetic (DZ, NX, and the rest - see this
same section, above) are still not implemented, exactly as already
documented. Test R's own fcsr/frm check only round-trips the CSR's
storage, never asserts that a specific rounding mode changes a result or
that an inexact/divide-by-zero operation actually sets its flag bit -
adding such a check would just be a second way to confirm an already-
known, already-documented limitation, not uncover a new one, so it's
left undone rather than padding the test count.

## Phase 5, finished: interactive input, via a minimal PLIC

Typing at that `~ #` prompt - piping commands into `riscv-emu.exe`'s stdin
on a timer, after the boot-to-shell delay - produced nothing at all on the
first attempt: no echo, no output, for ~190M further instructions of real
wall-clock time (ruling out "just needed to wait longer"). The cause:
`boot/make_dtb.py`'s UART node had no `interrupts`/`interrupt-parent`
property by design (see the section above) - fine for console *output*,
which Linux's 8250 driver always polls for regardless of interrupts, but
its *receive* path has nothing to ever notice new data without a working
IRQ. This was Phase 4's PLIC deferral ("revisited only if Phase 5 actually
needs it") finally being needed.

Added: a minimal PLIC (`PLIC_BASE` in `emu.h`, `plic_read`/`plic_write`/
`plic_claim_peek` in `devices.c`) - one real interrupt source (the UART's
receive-ready line, derived live from `console_input_ready()`, not
latched), two contexts per QEMU virt's own convention (context 0 = this
hart's M-mode view, context 1 = its S-mode view), and just enough of the
real register map (priority, pending, per-context enable, threshold,
claim/complete) for Linux's own PLIC driver to probe and drive it
correctly. `plic_claimed` tracks "claimed but not yet completed" per irq -
without it, a still-physically-pending level source would re-assert the
instant the handler returned, before it could actually drain the UART,
live-locking instead of ever making progress. `check_interrupt` in `cpu.c`
drives `mip`'s SEIP bit (9) from `plic_claim_peek` every step, the same way
it already derived MTIP from CLINT - hardware-driven, not something
software sets directly. `boot/make_dtb.py` gained a `plic@c000000` node
(`interrupts-extended` pairing the cpu's own interrupt-controller phandle
with M-external(11)/S-external(9)) and the UART node gained
`interrupt-parent`/`interrupts` to match.

**Found the hard way, not by inspection**: the first attempt at all of
this still froze - not in Linux, but spinning forever at `firmware.S`'s
own `mtrap_unexpected: j mtrap_unexpected`, confirmed by disassembling
`boot/firmware.elf` at the exact stuck `pc`. Added a `RISCV_EMU_SBIDBG`
trace line for SEI delivery (matching the existing MTI/STI/ECALL ones) to
find out why, and the very first (and only) line it printed before the
freeze read `priv=1 delegated=0 mideleg=20` - `mideleg` held only bit 5
(STI), not the `(1<<5)|(1<<9)` `firmware.S` had just been changed to set.
The reason: `boot/firmware.bin`/`firmware.elf` are prebuilt artifacts the
Makefile has no rule for (only `tests/*.S` get an automatic build rule) -
editing `firmware.S` alone silently kept running the *previous* binary,
mideleg-bit-9-less, with no error or warning anywhere. Rebuilding it
manually with the same LLVM cross-toolchain `tests/*.S` uses (just against
`boot/firmware.ld` instead of `tests/link.ld`) fixed it immediately - SEI
now correctly delegates to S-mode, Linux's PLIC driver claims/services/
completes it, and typing at the prompt works:

```
~ # echo hello from riscv-emu
hello from riscv-emu
~ # uname -a
Linux (none) 6.11.0-rc4-gc97670d2ca04 #1 SMP Sat Sep 14 14:53:54 -03 2024 riscv64 GNU/Linux
~ #
```

Rebuilding `boot/firmware.bin`/`.elf` after any `firmware.S` change is now
a required manual step this project has no automation for - worth a
Makefile rule in a future session, since this exact silent-stale-binary
trap cost real debugging time once already (it already got one: `make
firmware`, which `boot.sh` now calls automatically).

**Not yet reliable - the honest state of interactive input at the time this
was written** (both symptoms below are now root-caused and fixed - see
"Update, root-caused" further down, in particular point 3): typing
*after* the prompt appears sometimes works exactly as shown above and
sometimes does not (the guest keeps running normally, just never shows the
typed command) - reproduced with identical `boot.sh` arguments on back-to-
back runs, one succeeding and one not, so this is not a one-off fluke.
Separately and more consistently (reproduced every time tried, not
intermittently): if stdin has *any* live, open connection from very early
in boot - either real data arriving at `t=0` with no delay, or a held-open
named pipe with nothing written to it yet - the boot never gets past `Run
/init as init process`: `/etc/init.d/rcS`'s very first line (a plain
`echo "initing..."`, no I/O dependency of its own) never prints, for 800M+
further instructions of real progress elsewhere (the main interpreter loop
itself is not hung - `riscv-emu: NNN insns...` progress keeps advancing
normally; whatever is stuck is entirely inside the guest). Every run that
delayed its first input by several real seconds *did* get past this point
(`initing...`/`mounting...`/`Setup DHCP...`/`Welcome...`/`~ #` all
printed) regardless of what happened with typed input afterward - so
"stdin interaction very early in boot" and "stdin interaction after
reaching a shell" are two distinct symptoms, not the same bug observed
twice. Neither has a root cause yet. Likely worth investigating next:
instrumenting exactly when busybox's init itself first touches the
console (before `rcS` even starts) for the early-hang case, and auditing
`plic_claimed`/enable-register timing around the kernel's own PLIC/8250
driver init for the post-prompt flakiness - both are plausible but neither
is confirmed. Treat "interactive input works" as proven possible, not
proven reliable.

**Update, root-caused - this turned out to be three separate findings, not
one.** Automating what had only ever been eyeballed before
(`boot/boot_test.sh`, `boot/interactive_test.sh` - see Phase 6's "Testing"
section) surfaced that the stuck-after-`Run /init` symptom was far more
frequent than this README had assumed, which prompted the deeper dig the
user explicitly asked for (a determinism check across repeated identical
runs, then a live byte-injection experiment while stuck): a real `RISCV_EMU_SBIDBG`
syscall trace was added for ECALL-from-U (cause 8 - Linux syscalls: a7,
a0-a2 logged at entry, a0 logged on the `SRET` back to U-mode that
presumably returns it) alongside the existing SBI-call tracing, plus a
one-off decoder for `ppoll`'s `struct pollfd` array specifically.

1. **The `/dev/null`-style hang is not a bug.** Three `/dev/null` runs with
   the same `--max` produced byte-identical UART output (same MD5) and
   stopped at the exact same `pc` every time - fully deterministic, ruling
   out a race for this case outright. The syscall trace showed why: the
   last thing PID 1 (or a child) does is `ppoll(fds, 1, NULL)` on
   `pollfd[0] = {fd: 0, events: POLLIN}` - a blocking wait, no timeout, on
   stdin - and it never returns. `/etc/inittab`'s `::askfirst:/bin/sh`
   entry is exactly this: busybox's init genuinely waits for the console to
   have *something* to read before proceeding, on purpose, the same way it
   would on real hardware with nobody at the keyboard. An unattended
   `/dev/null` console correctly waits forever. This was never a defect to
   fix.
2. **The live-fifo "injection" experiment's null result was a test-
   harness artifact, not a finding about riscv-emu.** Writing a byte into
   an already-open named pipe (`mkfifo` + `exec 3<>`) produced no reaction
   at all, repeatedly - which looked like confirmation that something
   deeper was broken, until the same exact fifo mechanism was checked
   against plain `cat` as the reader (works instantly) versus
   `riscv-emu.exe` as the reader (the separate writing process's own
   `printf > fifo` hung for the full test timeout, meaning the byte was
   never delivered at all). `riscv-emu.exe` is a plain native Win32
   binary (built with w64devkit/MinGW), not an MSYS-runtime one like
   `cat`; MSYS's bridging of a named pipe into a native, non-MSYS
   process's inherited stdin handle does not reliably work on this host.
   Re-run with a plain bash pipe instead of a named fifo
   (`(sleep 2; printf 'A') | riscv-emu.exe ... boot/echo_test.bin` - Phase
   4's own proven getchar/putchar echo-loop test, run correctly this time
   under firmware.S rather than standalone, which an earlier attempt in
   this same investigation got wrong first), the byte arrived and was
   echoed back exactly as expected. Lesson: a plain `cmd1 | cmd2` pipe is
   trustworthy for feeding this project's own binaries; a separately-
   opened named pipe written from a second process is not, on this host,
   and should not be used for future input-timing experiments.
3. **Root-caused: a real non-determinism, but not in `StdinRing`.** Adding
   a second trace (`uart THR write` for every byte `uart_write` actually
   transmits, `uart RBR read` for every byte the guest consumes) and
   deliberately holding a run stuck - process alive, `riscv-emu: NNN
   insns...` still advancing, console output frozen at `Run /init as init
   process` - showed the syscall-level write()s busybox's `rcS` was making
   (`initing...`, `mounting...`, the `Welcome...` banner, all with correct
   lengths, all returning success) never reached `uart_write` at all: zero
   matching trace lines, for any of them, no matter how long the run was
   left going. Sending exactly one byte into the still-open stdin
   immediately flushed the *entire* backlog - every buffered line plus the
   `~ #` prompt - in one shot.

   The actual bug: this UART model's `uart_write` transmits a byte the
   instant the guest writes `THR` (`putchar`+`fflush`, no "in flight"
   delay at all), so `LSR`'s `THRE`/`TEMT` bits always read as 1 -
   correct for a *polling* console writer (which is how kernel `printk`
   reaches the console, via `earlycon`/the console driver's direct write
   path, and why boot messages always appeared fine). But Linux's
   `serial8250` driver services a userspace `write()`'s *rest* of the data
   (past whatever the first synchronous kick transmits) from its transmit
   interrupt handler, driven by the UART's transmit-holding-register-empty
   (THRE) interrupt - and `plic_irq_live()`'s UART condition only ever
   considered the receive side (`console_input_ready()`), never transmit.
   With no TX interrupt ever asserted, every byte past that first one sat
   in the kernel's own circular buffer forever, *unless some other UART
   interrupt happened to fire too* - because real 8250 hardware shares one
   IRQ line between RX and TX conditions, and the driver's single ISR
   checks and services both whenever it runs for *any* reason. An RX byte
   arriving was exactly such an incidental trigger: it is what made typing
   "unstick" a frozen console, and precisely how far the backlog drained
   before the guest's own `IER` (which this model accepted but silently
   discarded) re-disabled THRE depended on exact instruction timing - a
   real race, but in *when buffered output got serviced*, not in the
   stdin ring buffer `StdinRing`'s plain-`volatile` synchronization
   (which, once isolated from this confound, was never actually implicated
   by any experiment above).

   **Fix**: `CPU` gained a `uart_ier` field (`emu.h`); `uart_write` now
   stores whatever the guest writes to IER (offset 1) instead of discarding
   it; `plic_irq_live()`'s UART condition is now
   `console_input_ready(m) || (uart_ier & UART_IER_THRI)` - i.e. the
   interrupt is live whenever the guest has enabled the THRE interrupt,
   since THR is architecturally always empty in this model. This self-
   regulates correctly: the driver disables THRI once its buffer is
   actually empty, so there is no interrupt storm. After this fix, a pure
   `/dev/null` run (no input, ever) reaches the full `~ #` prompt - banner,
   DHCP message, everything - in under half the instructions the old
   RX-triggered-flush path needed, with no keystroke involved at all.
   `boot/interactive_test.sh` passed 16/16 consecutive runs after the fix
   (zero before it, consistently).

   Two unrelated test-harness bugs surfaced and were fixed alongside this:
   `interactive_test.sh` still used the named-fifo delivery mechanism point
   2 above had already shown unreliable for this native binary - replaced
   with a bash `coproc` (a plain OS pipe, the same mechanism a direct
   `cmd1 | cmd2` pipe uses, proven reliable); and the script's `> "$LOG"
   2>&1` merged riscv-emu's own stderr progress line into the same file as
   the guest's console text with no newline coordination between them,
   which could smash `INTERACTIVE_TEST_OK` together with a mid-stream
   `riscv-emu: NNN insns...` line and break the test's exact-line check -
   fixed by giving stderr its own temp file, never read by the test's own
   pass/fail logic.

## Phase 6: virtio-blk, a real disk via virtio-mmio

The goal of this phase (and the reason for it): boot a real Linux
distribution from an actual disk image, not just the fixed initramfs every
earlier phase used. That needs a block device the kernel can mount a root
filesystem from - `virtio-blk` over `virtio-mmio`, not PCI (a PCI host
bridge, ECAM, and BARs would be a lot of machinery this project does not
otherwise need for one disk).

**What's implemented**: the "modern" (version 2, not legacy) virtio-mmio
transport - magic/version/device-id/vendor-id, feature negotiation (we
offer exactly one bit, `VIRTIO_F_VERSION_1`, nothing else, so there is
nothing to actually negotiate down to), the status register, and the
queue-setup registers (`QueueSel`/`QueueNumMax`/`QueueNum`/`QueueReady`/
`QueueDesc*`/`QueueDriver*`/`QueueDevice*`) - plus a generic split-virtqueue
walk (descriptor chain, avail ring, used ring) and the virtio-blk device
logic on top of it: `VIRTIO_BLK_T_IN`/`_OUT` against a plain raw disk image
file opened via the new `--disk <path>` flag. One queue, one real feature
bit, no indirect descriptors, no `EVENT_IDX` - just enough for Linux's
`virtio_blk` + `virtio_mmio` drivers. Interrupts route through the PLIC
built in Phase 5: `plic_irq_live()` gained a second real source (virtio's
`virtio_isr != 0`, level-triggered like the UART, but *latched* rather than
derived live - there is no underlying "still pending" condition to
re-derive once the guest has drained the used ring, unlike the UART's
receive buffer) alongside the UART's, which the existing claim/complete/
priority/enable machinery already handled generically without needing any
changes of its own.

**Verified the same way every phase before it was**: `tests/virtio_test.c`
(the first guest test program in this project written in C, not assembly -
building a virtqueue's descriptor/avail/used ring layout by hand in RISC-V
asm is tedious and error-prone enough that the LLVM freestanding C
toolchain already in use for everything else was worth reaching for) writes
a 512-byte pattern to sector 1 via `VIRTIO_BLK_T_OUT`, reads it back via
`VIRTIO_BLK_T_IN` into a different buffer, and checks the round trip
byte-for-byte - polling the used ring rather than assuming synchronous
completion, since this emulator happens to finish a request inside the
`QueueNotify` write itself but real QEMU's backend makes no such promise,
and a test that only passes by accident on one side is not a real
differential proof. `make virtio-compare` runs it under both `riscv-emu`
and real `qemu-system-riscv64 -M virt -device virtio-blk-device` against
independent copies of the same starting disk image, and diffs *both* the
console output and the resulting disk image byte-for-byte - matching on
both sides.

**Two real bugs found getting there, neither an emulator-logic bug**:

1. `VIRTIO_BASE` was first set to `0x10001000` (QEMU virt's lowest-
   addressed virtio-mmio slot, an entirely reasonable-looking choice) and
   the test read back `DeviceID = 0` - "nothing plugged in here" - even
   though `-device virtio-blk-device` was clearly on the command line.
   Confirmed, not guessed, with `-M virt,dumpdtb=...` and a small ad-hoc
   Python FDT parser (no `dtc` on this host, same situation as
   `boot/make_dtb.py`'s own writer side): QEMU's virt machine creates 8
   virtio-mmio slots (`0x10001000`-`0x10008000`) but fills them
   **highest-address-first** as devices are plugged in, so a single
   `-device virtio-blk-device` lands on `0x10008000`, not `0x10001000` -
   confirmed further with a slot-scanning version of the test that probed
   all 8 addresses directly. `VIRTIO_BASE`/`VIRTIO_IRQ` (8, that slot's own
   fixed IRQ, also read from the dumped DTB rather than assumed) were fixed
   to match.
2. After fixing `emu.h`, the test *still* failed with the exact same
   `DeviceID = 0` symptom under `riscv-emu` specifically (real QEMU was
   fine) - because `tests/virtio_test.c` necessarily has its own, separate
   `VIRTIO_BASE` `#define` (guest code cannot `#include "emu.h"`, the same
   reason `boot/firmware.S` hardcodes its own device addresses as `.equ`s),
   and only the host-side copy in `emu.h` had been updated. A reminder that
   every address shared between host and guest in this project is
   duplicated by necessity, not by oversight, and has to be kept in sync by
   hand.

Also needed, unrelated to virtio itself: compiling `virtio_test.c` with the
default code model failed at link time (`ld.lld: ... relocation R_RISCV_HI20
out of range`) - the default model generates `gp`-relative addressing for
`.bss` globals that assumes a real `crt0` initialized `gp` to point at a
linker-placed `__global_pointer$`, which this freestanding environment
(like every other guest program here) deliberately has none of. Fixed with
`-mcmodel=medany`, which addresses everything PC-relative instead and has
no such dependency - every other guest program here is assembly and never
hit this, since hand-written `.S` tests already address their own globals
PC-relative explicitly (`la`/`lla`) rather than relying on the compiler's
default `.bss`-addressing strategy.

**Where this stood at the end of Phase 6**: virtio-blk worked standalone,
proven only against its own purpose-built test program and a disk image
with nothing filesystem-shaped on it. It was not yet wired into the real
Linux boot at all - `boot/make_dtb.py` had no virtio-mmio node, and
`boot/boot.sh` never passed `--disk`. That was Phase 7.

## Phase 7: virtio-blk, wired into the real Linux boot

What Phase 6 proved standalone now needed to be something the kernel's own
`virtio_mmio`/`virtio_blk` drivers probe and bind to during a real boot,
not just something a bare-metal test program pokes directly.

**`boot/make_dtb.py`** gained one more node, `virtio_mmio@10008000` -
`compatible = "virtio,mmio"`, `reg` covering `VIRTIO_BASE`/`VIRTIO_SIZE`
from `emu.h`, `interrupt-parent` pointing at the PLIC node's phandle, and
`interrupts = <8>` (`VIRTIO_IRQ`). Confirmed against real QEMU rather than
typed from the virtio spec - the same `-M virt,dumpdtb=...` technique
Phase 6 used to pin down `VIRTIO_BASE`/`VIRTIO_IRQ` in the first place,
this time with `-device virtio-blk-device` actually attached, dumped and
decoded with the same ad-hoc Python FDT parser (still no `dtc` on this
host). Real QEMU's virt machine exposes all 8 virtio-mmio slots
(`0x10001000`-`0x10008000`) as nodes regardless of how many devices are
actually plugged in; this emulator only implements the one Phase 6 built,
so only that one slot got a node here.

**`boot/boot.sh`** now creates `boot/disk.img` (16 MiB, zero-filled) the
first time it is missing and passes it via `--disk` on every run after -
deliberately *not* regenerated every time the way `firmware.bin`/the DTB
are, since the point of a persistent disk is that what a session writes
to it is still there the next time, the same way a real disk would behave.

**Verified two ways**: `dmesg` under a real boot shows
`virtio_blk virtio0: [vda] 32768 512-byte logical blocks (16.8 MB/16.0
MiB)` - the driver actually probed and bound, matching `boot/disk.img`'s
real size exactly, not a hardcoded assumption. More importantly, an actual
read/write round trip through the real kernel's block layer, not just the
probe message: from the live `~ #` shell,
`echo HELLO_VIRTIO_DISK | dd of=/dev/vda bs=512 count=1 conv=notrunc;
sync` followed by `dd if=/dev/vda bs=512 count=1` reads back
`HELLO_VIRTIO_DISK` exactly. This exercises a completely different code
path than Phase 6's own test program did (the real VFS/block layer's I/O
scheduling and request merging, not a hand-written virtqueue walk), so it
is a genuinely new piece of evidence that the device model holds up under
real-kernel use, not a retest of Phase 6's own result.

**Where this stood after the raw block-device work**: the kernel sees and
can read/write the disk at the block-device level. Nothing on it was
filesystem-shaped yet - no partition table, no filesystem Phase 6/7's own
tooling had written.

**A real filesystem on it**: this busybox build ships `mkfs.ext2` (and
`mke2fs`) as applets, so `mkfs.ext2 -F /dev/vda` followed by
`mount -t ext2 /dev/vda /mnt/disk` works from the live shell exactly as it
would on real hardware - the kernel mounts it via its ext4 driver's ext2
compatibility mode (`EXT4-fs (vda): mounting ext2 file system using the
ext4 subsystem`), and a file written, `sync`'d, and read back
(`echo ... > /mnt/disk/testfile.txt; md5sum /mnt/disk/testfile.txt`)
round-trips correctly.

**Persistence across a fresh process** ("reboot" in the sense that
matters here: a second, independent invocation of `riscv-emu.exe` against
the same `boot/disk.img`, not a clean guest shutdown - this project has no
guest-triggerable poweroff/reset path to the host wired up): killing the
first boot (deliberately abruptly, via `taskkill`, not a clean unmount -
the harder case) and starting a second one over the *same*
`boot/disk.img` reproduces the exact same filesystem UUID (proof it is
the same filesystem, not a freshly-made one) and the exact same
`md5sum` for `testfile.txt`. `uart_write`'s `fflush` after every byte
(`src/devices.c`) and `virtio_blk_process_queue`'s `fflush` after every
write request both already flush straight through to the host OS, which
is why this holds even without a clean unmount in between - a write this
emulator reports as complete to the guest really has landed on
`boot/disk.img` by the time it does.

**`e2fsck` is not available to check metadata consistency directly** -
neither this busybox build (`fsck`/`fsck.minix` only, no `e2fsck`/
`fsck.ext2`) nor the host itself ships it. The persistence check above
(an independent process reading back identical content via the normal
VFS/ext2/virtio-blk path) is the correctness evidence this project has in
its place; a real `e2fsck -fn` pass is a gap, not a result, if a future
session gets e2fsprogs onto either side.

**Differential test**: `boot/fs_compare.sh` (`make fs-compare`) runs the
same `mkfs.ext2` + `mount` + write + `md5sum` + `umount` sequence under
both riscv-emu and real `qemu-system-riscv64 -bios default` (OpenSBI, this
project's own `firmware.S` never involved on that side - same convention
as every other full-kernel differential in this project) against fresh,
independent disk images, and diffs the two transcripts. What is and is not
compared, and why, is documented in the script's own header; in short:
`mkfs.ext2`'s own report (a pure function of disk size, no randomness)
and the written file's `md5sum` must match exactly and do; the
filesystem's UUID and the kernel boot banner before the interactive
session starts are expected to differ (real/fixed entropy, real
OpenSBI/this project's own firmware.S) and are excluded. Getting this
script actually reliable on this host surfaced three more environment
quirks worth recording since they will bite the next script that tries
something similar here, not because they say anything about riscv-emu
itself: a bash `coproc`'s pipe into `qemu-system-riscv64.exe`
specifically (not `riscv-emu.exe`, which tolerates it fine) goes bad
("Bad file descriptor") once left open and idle for more than a few
seconds before the first write, so a scripted direct pipe with built-in
delays is used for both sides instead, same delivery method proven
reliable all along; `python3` and both guest binaries are native Windows
builds that do not understand an MSYS-style `/tmp/...` path (it resolves
against the wrong drive root and fails to open) - `cygpath -w` first, for
anything handed to a native binary's own command line, `dd` (an MSYS
binary) for anything created through MSYS itself; and w64devkit ships its
own `dd.exe` that shadows MSYS's once w64devkit's bin directory is on
`PATH` and does not understand `/dev/zero` the same way, so `dd` has to
be called as `/usr/bin/dd` explicitly once that `PATH` line has run.

**`boot/boot_root.sh`: booting with `root=/dev/vda`, no initramfs at
all.** Populated `boot/disk.img`'s ext2 filesystem with a real root -
booted normally under the existing initramfs, then from its shell:
`mkfs.ext2 -F /dev/vda; mount -t ext2 /dev/vda /mnt/disk; cp -a /bin /etc
/home /sbin /usr /var /root /init /linuxrc /mnt/disk/`, plus empty
`proc`/`sys`/`dev`/`tmp`/`mnt` mount-point directories (the initramfs's
own `dev` is populated by `devtmpfs` at runtime, not static files, so
nothing there needs copying - confirmed by `rcS`'s own
`mount -t devtmpfs devtmpfs /dev` line), then `umount`. `boot/boot_root.sh`
loads the kernel *without* any `--load`-ed initrd at all (an initramfs
present always wins over `root=` as the kernel's real init source,
regardless of `root=`'s value - the only way to actually test a root-
device boot is to leave it out entirely) and sets the cmdline to
`root=/dev/vda rootfstype=ext2 rw init=/init` (`make_dtb.py`'s
initrd-start/-end both passed as 0, which the kernel treats as "no
initrd present", the same as real hardware/real QEMU simply never passing
`-initrd`).

Booted clean on the first real attempt: `VFS: Mounted root (ext2
filesystem) on device 254:0`, `devtmpfs: mounted` (automatic - the one
harmless side effect is `rcS`'s own later `mount -t devtmpfs devtmpfs
/dev` now failing with "Device or resource busy", since the kernel beat
it to it; everything after that line runs exactly as before), all the way
to the same `~ #` prompt. `mount | grep " / "` from that shell confirms
`/dev/root on / type ext2 (rw,relatime)` - this really is the virtio-blk
disk serving as `/`, not initramfs. A file written to `/root/` there,
`sync`'d, read back in the same session: correct, as expected (this is
exactly the ext2-on-virtio-blk path already proven above, just now also
serving as `/` rather than a secondary mount).

**Where this stands**: a real, persistent ext2 filesystem works on the
virtio-blk disk, verified by an independent process re-reading it and by
a differential match against real QEMU doing the identical sequence, and
the kernel can boot directly from it as `root=` with no initramfs
involved at all. What `boot/boot_root.sh` boots is still exactly the same
busybox-based minimal userspace the initramfs always used, just relocated
onto a disk rather than loaded into RAM each time - a real general-
purpose root filesystem/distro is further out and not attempted here.

## Phase 9: virtio-net, via a user-mode NAT

The goal: real networking from the guest - `ping` and `wget` reaching the
actual internet - without TAP (which needs installing a kernel driver on
Windows, a persistent system change this project cannot ask of anyone
running it) or a bridge. The same role real QEMU's `-netdev user` plays,
built from scratch: `src/net.c`'s own top comment covers the full design
and its limits; this section covers what it took to get working.

**The device itself** (`src/devices.c`'s `virtio_net_read`/`write`,
registers in `emu.h`): a second, independent virtio-mmio slot at
`VIRTIO_NET_BASE` (`0x10007000`, IRQ 7) - confirmed the same empirical way
the first slot's own address was (Phase 6): `cat /proc/iomem` from a real
QEMU boot with both `-device virtio-blk-device` and `-device
virtio-net-device` attached, in that order - QEMU's virt machine still
fills slots highest-address-first per device *created*, so the second
device created takes the next slot down from the first. Two virtqueues
(receiveq/transmitq, no control queue - this device offers nothing a
control queue would manage), feature bits `VIRTIO_NET_F_MAC` and
`VIRTIO_F_VERSION_1` only, config space exposing a fixed MAC
(`net_guest_mac`, one definition in `net.c`, not duplicated in
`devices.c` - see its own comment on why that specific duplication has
already cost this project real time once, in Phase 6).

**Two real bugs found getting the device itself right, both found by
instrumenting and comparing actual bytes, not by re-reading the spec
harder**:
1. The first frame the guest ever transmitted decoded as ethertype
   `0x3456` - nonsense, until noticing those exact two bytes are the tail
   of `net_guest_mac` (`...34:56`), meaning the parser was reading the
   Ethernet header two bytes too early. This kernel's `virtio_net` driver
   frames every transmitted packet with the 12-byte
   `virtio_net_hdr_mrg_rxbuf` layout regardless of whether
   `VIRTIO_NET_F_MRG_RXBUF` was negotiated (it was not) - the spec's own
   10-byte `virtio_net_hdr` is correct for a compliant driver, but not for
   this one in practice. Fixed by using 12 throughout (`NET_HDR_LEN`),
   confirmed empirically, documented as empirical in the code rather than
   presented as spec-derived.
2. DHCP never got a reply: `net_handle_ip`'s own sender-address filter
   (only answer packets claiming to be from the one guest IP this NAT
   knows about) was rejecting the DHCPDISCOVER itself, which is sent from
   `0.0.0.0` by design - the guest has no address yet, that is the exact
   problem DHCP exists to solve. Fixed by letting `0.0.0.0` through
   alongside the guest's real address.

**ping**: real ICMP via the IP Helper API's `IcmpSendEcho` (the same
mechanism `ping.exe` itself uses), not a raw socket, which needs
Administrator on Windows and so could not be assumed available. One more
real bug here: `ping 10.0.2.2` (this NAT's own gateway address) got no
reply at first, because the code handed it to `IcmpSendEcho` like any
other destination - but nothing on the host is listening at a fictitious
10.0.2.2, so the real ping just timed out. Real routers answer a ping
addressed to themselves directly rather than relaying it anywhere
(confirmed against real QEMU's own slirp gateway, which does exactly
this); fixed by special-casing the gateway's own address to reply
immediately rather than going through `IcmpSendEcho` at all. After both
fixes: `ping -c 3 10.0.2.2` and `ping -c 3 8.8.8.8` both succeed, 0%
packet loss, 2-5ms round trip.

**wget**: needed two more pieces. DNS is relayed, not resolved here - a
query's raw bytes go to a real upstream resolver (`8.8.8.8:53`) over a
real UDP socket, and the reply's raw bytes come straight back,
repackaged; `wget`'s own resolution of `example.com` to a real IP
confirmed this works. TCP is a NAT splicer, not a full stack (`net.c`'s
own comment on why no retransmission logic is needed toward the guest -
there is no lossy medium on that side, only in-process memory). Found by
tracing actual segments (`RISCV_EMU_NETDBG=1`, kept in the tree as a
permanent diagnostic alongside `RISCV_EMU_SBIDBG`): after the remote
HTTP server closed its end and `recv()` started reporting EOF, every
single subsequent poll re-sent a FIN to the guest with an incremented,
now-wrong sequence number - a tight, fast loop - because nothing stopped
polling a socket that had already reported EOF once. Fixed with a
`fin_sent` guard. After that: `wget -O- http://example.com/` succeeds,
full 577-byte page, twice in a row.

**A larger download, to actually exercise flow control and seq/ack
translation across many segments** - the examples above are all small
enough to fit in a handful of packets; a real test of the TCP relay's
window handling and sequence bookkeeping needs enough data to force many
round trips. `wget -O /tmp/5mb.test http://cachefly.cachefly.net/5mb.test`
from the guest (a well-known, UA-agnostic plain-HTTP test file host -
several others tried first, `thinkbroadband`/`download.thinkbroadband.com`
among them, turned out to reject requests with no/a minimalist
User-Agent, which is exactly what busybox `wget` sends; nothing to do
with this NAT) pulled all 5,242,880 bytes across roughly 3,700 segments
in 6.8s, `wget`'s own progress climbing cleanly through every percentage.
`md5sum` of the result inside the guest and `curl`'s own download of the
identical URL on the host, independently, both produced the exact same
hash (`5f363e0e58a95f06cbe9bbc662c5dfb6`) and the exact same 5,242,880-
byte size - a real, byte-for-byte correctness proof for the window-
filling and sequence/ack translation this project's own smaller tests
never had enough data to actually exercise.

**A slow receiver, to exercise real zero-window backpressure on the
download side** - the 5MB download above never has to actually stall;
it only proves the bytes end up correct, not that the window-based
throttle in `net_poll` (`in_flight + TCP_RECV_CHUNK <= t->guest_window`)
does anything when it matters. `wget -O- http://cachefly.cachefly.net/
5mb.test | { sleep 20; md5sum; }` - the guest kernel's own TCP receive
buffer fills while nothing drains it for 20s, forcing a real zero
window - confirms it does: `RISCV_EMU_NETDBG=1` traced `window=0` 76
times across the stall before it climbed back to 65535 as `md5sum`
started draining the pipe, and the resulting hash still matched the
reference exactly. One dead end on the way there, worth recording since
it looked at first like a NAT bug and was not: `wget -O- ... | (sleep
20; md5sum)` (a parenthesised subshell) reproducibly truncated the HTTP
request mid-line (losing exactly `.test` from `/5mb.test`, confirmed via
`RISCV_EMU_NETDBG`'s own TX payload dump) and got a `404` back - but the
*identical* command reproduced the *identical* truncation under real
QEMU's own slirp too, so this is a busybox ash quirk with the `(...)`
subshell form specifically, not anything in this NAT; `{ sleep 20;
md5sum; }` (a plain brace group, no subshell) sends the correct request
on both.

**Upload - a separate code path (guest to socket), and a separate class
of bug from anything the download direction exercised** - `nc 10.0.2.2
<port> < /tmp/5mb.test` from the guest, to a plain Python TCP listener
on the host (neither `nc` nor `ncat` is available on this host, so a
~20-line script fills the same role `nc -l -p PORT` would), reached by a
connection to this NAT's own gateway address now redirecting to the
host's loopback interface instead of a doomed real `connect()` to a
fictitious IP (a deliberate convenience `net_handle_tcp`'s own comment
covers - real QEMU's slirp does not do this by default either, without
an explicit `hostfwd=` rule). Against a listener that drains as fast as
it can, this round-trips 5,242,880 bytes correctly - but that is the
easy case, the upload-side mirror of the 5MB download above: no real
socket backpressure ever gets exercised when the kernel's own `send()`
always has room. Pointing the same upload at a listener that
deliberately drains slowly (`recv(512)` plus `time.sleep(0.003)` per
read, roughly 170KB/s) reproduced two real, distinct bugs, neither
found by anything short of actually stalling a real socket this way:

1. **Silent data loss.** The original code called `send()` directly
   against the guest's segment and, on anything other than a full
   write (`WSAEWOULDBLOCK` included), just dropped whatever `send()`
   didn't take - no buffering, no retry. The slow listener's backlog
   produced exactly that (`sent=-1` traced twice), and the file it
   received was 57,416 bytes short of the original - real, confirmed
   corruption, not a theoretical risk. Fixed with `upload_buf`, a
   64KB-per-connection buffer: guest data is accepted into it (and
   acked) only as far as it currently has room, `net_poll` drains it
   toward the real socket a little at a time, and the window this
   device advertises to the guest is derived from how much of that
   buffer is actually free - real backpressure on the upload side too,
   not just a bigger fixed number to delay the same problem.
2. **A window-update deadlock**, found immediately after fixing (1):
   once the advertised window shrank toward zero, the guest correctly
   stopped sending and waited - and then waited forever, because
   nothing told it the window had reopened once `net_poll` drained
   some of `upload_buf`. A TCP receiver whose window has gone to zero
   has to *announce* when it reopens; nothing obliges the sender to
   keep probing on its own, and in practice the guest's stack just
   went quiet. Fixed with a bare ACK sent whenever draining `upload_buf`
   actually freed space.

After both fixes, the identical slow-listener upload delivers all
5,242,880 bytes, and `md5sum` on the host's received copy matches the
guest's own source file and the original host-downloaded reference,
all three identical (`5f363e0e58a95f06cbe9bbc662c5dfb6`).

**Not differentially tested against real QEMU's own slirp** - unlike
every other device in this project, there is no practical way to
byte-for-byte compare two independent NAT implementations' handling of
real internet traffic against a real, live remote server. What was
checked instead: the guest-observable outcome (`ping`/`wget` succeeding)
matches what the same commands do under real QEMU's `-netdev user`,
checked by hand early in this phase before writing any of this device's
own code (its DHCP lease, gateway, and DNS address all come from that
check, not from the virtio-net spec).

## Phase 10: a real distro - Alpine Linux riscv64

Goal: not another bespoke test image, but a genuine, unmodified
general-purpose distro's root filesystem - `apk update`/`apk add`
reaching the real internet through this project's own NAT, on a
persistent disk a `poweroff` leaves in a clean, re-bootable state.
Staged the same way virtio-blk itself was: verify each step against
real `qemu-system-riscv64` first, so a failure later points at this
emulator specifically, not at the kernel/rootfs/approach itself.

**Step 0+3 combined, on real QEMU first**: built Alpine riscv64
3.20.10's own minirootfs tarball (downloaded directly, sha256-verified
against Alpine's own published checksum) onto a fresh 512MB disk image,
entirely from inside a guest shell - this project's own existing
busybox initramfs (confirmed, via `busybox --list`, to already carry
`mkfs.ext2`, `wget`, `tar`, `udhcpc`, `ip`, `poweroff` - a full build,
not the minimal one a first look at the raw `cpio` archive's visible
regular-file entries suggested) booted under real QEMU with that disk
attached, `mkfs.ext2 /dev/vda`, `mount`, `udhcpc` for a real DHCP lease,
`wget` from a plain Python `http.server` on the host (reached at
`10.0.2.2`, exactly the hostfwd-less "connect to the gateway" convenience
this project's own NAT already documents for its own `nc` tests above),
`tar -xzf` the downloaded tarball onto the mounted disk, `sync`. Tests
the disk and network paths together under real load, not just each in
isolation.

**Step 0's actual verification, same disk**: rebooted straight from that
disk (`root=/dev/vda rootfstype=ext2 rw init=/bin/sh` - no initrd at all,
and deliberately `/bin/sh` rather than Alpine's own `/sbin/init` first,
since a minirootfs is built for containers and may not have a full init
system wired up) under real QEMU again. Alpine's own `/bin/sh` (ash)
came up as PID 1; manual `udhcpc -i eth0` got a lease and auto-populated
`/etc/resolv.conf` with `nameserver 10.0.2.3` on its own (no manual DNS
setup needed, contrary to the original plan's assumption); `apk update`
fetched both the `main` and `community` `APKINDEX.tar.gz` and reported
22124 available packages; `apk add --no-cache curl` installed 10
packages (12 MiB) cleanly. One assumption this corrected: Alpine
3.20.10's own `/etc/apk/repositories` defaults to `https://`, not `http://`
- TLS was genuinely needed and genuinely worked (real QEMU's slirp
reaching the real internet) - though this turns out not to matter for
this project's *own* NAT either way, addressed below.

**SBI SRST** (system reset extension, EID `0x53525354`), added to
`boot/firmware.S` specifically because a persistent root filesystem
changes what "just kill the process" costs: this project's own prior
test images were always rebuilt fresh or treated as disposable scratch,
so a hard-killed `riscv-emu.exe` never mattered: a real, kept-across-runs
Alpine disk would come back next time as an uncleanly-unmounted
filesystem the kernel's `syscon-poweroff`/`syscon-reboot` path could not
have ended on their own. Implemented as a flat extension - FID 0
(system_reset) ignores its reset-type/reason arguments and just performs
the exact same `TESTDEV` write (`FINISHER_PASS`) the existing legacy
EID-8 shutdown call already does, since this firmware has nowhere to
actually *reboot into* anyway.

That alone **did not fix the actual symptom**: Alpine's `poweroff -f`
produced `reboot: Power off not available: System halted instead`
instead of reaching the SRST handler at all - confirmed, not guessed,
by a temporary distinctive-byte UART marker in both `sbi_base_probe`'s
own SRST comparison and `sbi_srst` itself, neither of which ever fired.
Diagnosed by checking what real QEMU's `-bios default` (real OpenSBI)
actually does differently for the *same* kernel and disk, which it
`poweroff`s cleanly: `-machine dumpdtb=...`, decoded with a small
from-scratch FDT parser (no `dtc` on this host), showed real QEMU's own
auto-generated device tree carries a `test@100000` node (`compatible =
"sifive,test1", "sifive,test0", "syscon"` - the exact same device this
project's own `TESTDEV`/`testdev_write` already models, confirmed by
address) with separate root-level `poweroff`/`reboot` child nodes
(`compatible = "syscon-poweroff"`/`"syscon-reboot"`, each a `regmap`
phandle reference plus an `offset`/`value` pair - `0x5555`/`0x7777`,
`FINISHER_PASS`/`FINISHER_RESET` exactly). Linux's own generic
`drivers/power/reset/syscon-poweroff.c`/`syscon-reboot.c` bind to
*that* and register `pm_power_off`/the restart handler directly - a
mechanism entirely independent of SBI, and the one a real kernel
actually reaches first on real hardware/real QEMU, with SBI SRST
layered on only as a fallback this particular kernel build apparently
never needed. `boot/make_dtb.py` (which, unlike real QEMU's own
auto-generated tree, has no "soc" container at all - every device node
here, already, is already a direct child of the root node, so the new
ones fit the project's own existing flat layout without inventing a new
one) gained the matching `test@100000`/`poweroff`/`reboot` nodes.
Confirmed with the same temporary-trace discipline: a dedicated
`RISCV_EMU_TESTDEV_DBG` trace directly in `devices.c`'s own
`testdev_write` showed the expected `addr=0x100000 width=4
val=0x5555` the moment the DTB nodes were added, and the kernel's own
message changed to the same `reboot: Power down` real QEMU itself
prints. SBI SRST is kept anyway (a real, if now redundant for this
particular kernel, extension few guests would ever strictly need once
the syscon path exists - and it's what a kernel that did lack a
syscon-reset-capable devicetree, or ran with `-bios none` firmware that
never advertised one, would actually have to fall back to).

**End-to-end persistence, confirmed on this emulator itself, not just
reasoned about**: booted the Alpine disk, wrote a marker file,
`poweroff -f`'d (kernel printed `Power down`, exactly matching real
QEMU), then booted the *same* disk file fresh in a brand new process -
the marker file was there, byte-for-byte, proving the whole chain (`apk`
and the filesystem writes it does -> `poweroff` -> this emulator's own
process actually exiting -> the host-side disk file genuinely holding
what was written, not just appearing to while the process stayed
resident) end to end, not merely that each link works in isolation.

**Trying the same `apk update`/`apk add` sequence through this project's
own `--net` NAT (not real QEMU's slirp) found a genuinely serious CPU bug**
- silent memory corruption, nothing to do with networking at all, that
"architecturally this NAT shouldn't care about TLS" (the TCP relay is a
dumb byte pipe) correctly predicted would *not* be the actual problem.

**Symptom**: `wget`ing the exact same tarball through this emulator's own
NAT instead of real QEMU's produced a file of the *right size* but the
*wrong* MD5 - every time, but to a *different* wrong value each run.
Byte-diffing against the original showed something too precise to be
noise: exactly 6 corrupted bytes, at the exact same relative offset,
repeating every exactly 65536 bytes, for the entire file. Capturing the
TCP relay's own outgoing segments directly (a temporary raw dump inside
`tcp_send_segment`, written to a file as the NAT sent them) showed they
already matched the original byte-for-byte - the corruption was real,
but it was happening *after* this NAT had already done its job
correctly, somewhere in getting those bytes into the guest or the
guest's own handling of them. Downloading to tmpfs instead of the ext2
disk reproduced the identical pattern, ruling out virtio-blk/the
filesystem entirely.

**Root cause**: every ordinary LOAD/STORE (and FLW/FLD/FSW/FSD)
translated only the access's *first* byte's virtual address, then read
or wrote the remaining bytes contiguously in this emulator's flat
physical-memory array - correct exactly as long as the access stays on
one page, which an old comment on this exact code explicitly (and
incorrectly) assumed "compiler-emitted RISC-V code" always does. RISC-V
does not require loads/stores to be aligned (only AMO/LR/SC do, and
natural alignment to a page-size-dividing power of two provably can
never straddle a page, which is exactly why those were never at risk
here) - and a page-unaligned heap buffer, the kind a userspace
allocator hands `wget`'s own `read()` calls with no obligation to
page-align it, routinely produces unaligned multi-byte copies that do
cross a page boundary. When that happens, this emulator's own
physically-flat memory model stops being an equivalent stand-in for "two
virtually adjacent pages are also physically adjacent" - a real page
table has no obligation to map them to physically adjacent pages at
all - so the tail of such an access silently lands in whatever guest
physical page happens to sit next to the first one in *this emulator's*
own layout, not the one the second virtual page's own page table entry
actually names. That explained every observed detail at once: *where*
the corruption fell depended only on the buffer's own page alignment,
hence perfectly repeating every page-crossing access at the same
relative offset; *what* it corrupted to varied run to run, because it
was really just picking up whatever stale content happened to already
be sitting in that unrelated neighboring physical page; and it survived
the guest kernel's own TCP checksum validation untouched, because the
checksum is computed over - and the segment accepted as - exactly the
bytes this NAT actually sent, which were always correct; the corruption
happened to the already-validated data afterward, while it sat in a
userspace buffer the kernel itself doesn't re-check once delivered.

**Fix**: `translate()`'s own single-call-per-access assumption now holds
by construction rather than by convention - two new helpers, `mem_load`/
`mem_store`, check whether `(va & 0xFFF) + size` exceeds one page before
doing anything else. The common case (it doesn't) takes the exact same
single-`translate()`-plus-`phys_read/writeN` path as before, so the
normal, overwhelmingly common case pays nothing extra. The rare case
(it does) translates, PMP-checks, and fault-checks every single byte of
the access independently, so each byte lands wherever its *own* page
table entry actually says it should, never assumed-contiguous with its
neighbor's. Every `LOAD`/`STORE`/`FLW`/`FLD`/`FSW`/`FSD` opcode case now
goes through these; `AMO`/`LR`/`SC` were left exactly as they were,
since their own spec-mandated natural alignment already rules the
whole failure mode out. Confirmed fixed, not merely plausible: the
exact same `wget` that previously produced a different wrong MD5 every
run now reproduces the original's MD5 exactly, both to tmpfs and to the
ext2 disk, and `make compare`/`test-all`/`boot-test` all still pass.

**A second, independently-caught mistake in the first version of this
fix**: the crossing path's very first draft translated, PMP-checked,
and immediately read-or-wrote each byte in the same loop iteration -
correct for a load's own observable behavior (`*val` is never touched
until every byte has already succeeded), but not for a store: a fault
on a *later* byte would still leave the *earlier* bytes - the ones on
the page that translated fine - already written to memory, even though
the instruction as a whole never completed. Real hardware's precise-
exception model gets away with this by retrying the whole instruction
after the handler fixes whatever faulted; this emulator does not
implement instruction retry, so a partial write here would simply be a
permanent, silent side effect of an instruction that appeared (correctly,
from the trap's own point of view) to never have happened. Both
`mem_load` and `mem_store` now translate and PMP-check every byte of a
crossing access *before* touching any of them - for `mem_load`, this
also closes a narrower, more theoretical sibling: a byte read from a
device region with its own read side effect (draining a FIFO, say)
must not happen for a byte the access never legitimately completes,
either, even though no actual device crossing has been observed in
practice the way the store corruption was.

**A dedicated regression test** (`tests/selftest.S` test T) exists
specifically because every other CPU bug this project has found got
one and this is exactly the kind of mistake a future TLB rewrite (a
host-pointer-per-entry cache, discussed but not yet started - see
"Performance" above) could easily reintroduce without anyone noticing
for a while: two virtually adjacent pages (`l0_pt`'s own free slots 1
and 2) deliberately mapped to two physically *non*-adjacent ones
(`page_a`/`page_b`, declared 64KB apart specifically to rule out
coincidental adjacency). Three cases, matching real QEMU exactly: an
8-byte store split 6-and-2 across the boundary, the same store split
2-and-6 the other way, and - the ordering fix's own dedicated case,
not just the crossing-detection logic's - the second page left
deliberately unmapped, expecting a store page fault with `mtval`
landing exactly on the first byte of the access that was actually on
the unmapped page, *and* the first (valid, in-range) page coming back
completely untouched. Every check reads back with single-byte `lbu`s
against the physical pages directly, deliberately never a multi-byte
load back through the same crossing path being tested, so a
hypothetical symmetric bug on the read side could never cancel out a
real bug on the write side. Confirmed to actually catch both of this
section's own bugs, not just plausibly related to them: temporarily
reverting the crossing-path detection entirely, and separately
reverting just the translate-everything-before-writing-anything
ordering while leaving crossing detection intact, each independently
turn test T red on their own, restoring either fix turns it green
again.

**A second, independent gap, found by the same `apk` attempt once the
corruption above no longer masked it**: `apk update` itself ran into TLS
certificate validation failures that real QEMU's own run never showed.
The guest's own boot log explained why directly - no `goldfish_rtc`
driver message ever appeared, unlike real QEMU's own `-M virt`, meaning
this emulator's Linux never had a devicetree node for one and so never
set its system clock away from whatever it defaults to at boot; a
clock stuck far in the past makes every real CA certificate's own
`notBefore` date look like it hasn't happened yet. Fixed by adding a
`google,goldfish-rtc` device (`src/devices.c`'s new `rtc_read` plus a
matching `boot/make_dtb.py` node, address and compatible string matched
to real QEMU's own via the same `-machine dumpdtb=...` technique used
for the `syscon` nodes above) that reports genuine host wall-clock
time - TIME_LOW latches both halves on read and TIME_HIGH returns what
was latched, matching real goldfish-rtc hardware's own documented
behavior exactly, rather than two independent, potentially inconsistent
samples of the clock. This is a deliberate, singular exception to this
emulator's otherwise fully deterministic design (`mtime` still advances
one tick per retired instruction, not real time, everywhere else), and
a real cost, not a free one: no *existing* differential test reads this
device (it's bare-metal `tests/selftest.S` code, which never had a
reason to) or breaks from it, but a full Linux boot reads it
unconditionally at boot to set its own clock and prints that read's own
result straight to the console log - so the exact byte-for-byte
boot-log comparison technique that confirmed both performance
optimizations earlier in this document changed nothing observable would
see a different timestamp every single run from here on, for a reason
that has nothing to do with whatever is actually being verified.
`RISCV_EMU_RTC_FIXED=<unix-timestamp>` exists for exactly this: set, it
pins what every read returns instead of sampling the host clock, so a
test that wants that same repeatable-boot-log technique back - this
project's own `boot_test.sh` doesn't need it, only pattern-matching for
the shell prompt appearing - does not have to give up determinism to
use this device at all.

With both fixed, `apk update` through this project's *own* NAT reached
the real Alpine mirrors over HTTPS and reported the same 22124 available
packages real QEMU's own run did.

## Performance

Starting point: ~27 MIPS (million guest instructions/second), measured
the same way every number in this section is - `boot.sh --max 600000000`,
the exact deterministic workload `boot_test.sh` already uses as a
pass/fail check, read off `riscv-emu`'s own periodic progress line.

**Profiling tooling first, before touching anything**: `gprof` (built
with `-pg` via `make`'s own `gcc`, which this project's binary is already
built with) produced a completely empty profile - no flat-profile
samples, no call-graph entries at all - on this MinGW/Windows build.
MinGW's `-pg` support has long been known to lean on a profiling timer
POSIX signal delivery doesn't reliably reach on native Windows; rather
than chase that further (or reach for WSL, which would mean profiling a
cross-compiled Linux build, not the actual binary this project ships),
temporary `rdtsc`-based instrumentation - gated behind `RISCV_EMU_PROF`,
zero overhead unless set - went directly around the three specific
candidates `cpu_step` itself, `check_interrupt`, and `translate`, as a
far more Windows-reliable substitute for a sampling profiler. First
reading, same workload: `check_interrupt` 28.5% of all `cpu_step` time,
`translate` 21.7%, the rest (decode/dispatch/execute) 49.8% - over half
of all cycles spent before an instruction is even decoded.

**Fix 1 - `check_interrupt`'s own PLIC scan** (28.5% -> 15.1% of
`cpu_step`, a 57% cut in its own absolute cost): `plic_claim_peek()`,
called on *every* instruction for the SEIP check, scanned all 31 possible
PLIC IRQ numbers even though `plic_irq_live()` only ever recognizes
three (`VIRTIO_NET_IRQ`=7, `VIRTIO_IRQ`=8, `UART_IRQ`=10) - every other
number can only ever read as "not live." Narrowing the scan to exactly
those three is mathematically identical to the full scan (skipping
entries that can only lose the priority comparison can't change which
one wins), just without redoing that proof 28 extra times every single
step.

**Fix 2 - a redundant second `translate()` per 4-byte fetch** (`translate`
21.7% -> 23.6% of a now-smaller total, but its own absolute cost down
23%; overall `cpu_step` cycles down a further 12.5% on top of fix 1):
every non-compressed (4-byte) instruction fetch translated its upper
half's address completely independently of its lower half's, in case the
two land on different physical pages - correct, but only actually
*necessary* in the last 2 bytes of a page (offset `0xFFE`/`0xFFF`), the
one case the two halves can genuinely differ. Every other time - the
overwhelming majority, confirmed by the TLB-hit counter dropping by
~245M, exactly the eliminated calls, with the miss counter completely
unchanged since page-crossing fetches were never the ones being skipped
- the upper half's physical address is just the lower half's own
already-resolved one plus 2, derived directly instead of walking the TLB
a second time for an answer that can only come out the same. PMP is
still re-checked for the derived address rather than assumed identical,
since a PMP region's own granularity (down to 4 bytes via NAPOT) isn't
guaranteed to line up with a 4KB page's the way the TLB's cached
permissions already are.

**Net result**: ~27 -> **~49.8 MIPS**, roughly 1.84x, from two targeted,
low-risk changes - both pure address-translation/interrupt-polling
bookkeeping, nothing touching instruction semantics or timing-sensitive
device behavior, which is exactly why they could be verified the
stronger way the first optimization (interrupt polling, nearer
asynchronous device state) couldn't as cleanly: before/after
`boot.sh --max 600000000` runs hit the *exact* same instruction count at
the *exact* same `pc` at every progress checkpoint along the way, and
their guest console output - diffed and `md5sum`-compared after
stripping the one expected difference (the timing numbers on
`riscv-emu`'s own progress lines, which interleave mid-line into
whatever the guest happened to be printing with no coordinating
newline, a cosmetic artifact already documented elsewhere in this
file) - came out byte-identical. `make compare`/`test-all`/`boot-test`
all still pass.

Next candidates, not yet done: `check_interrupt`'s remaining cost (only
recomputing on an actual `mip`/`mie`/`mstatus` change or a timer
deadline, rather than every step) and a last-fetched-page cache ahead of
the TLB array itself (a cached host pointer + virtual page, valid until
`satp`/`SFENCE.VMA`/PMP changes) - both discussed, both judged riskier
or more work than this round's two fixes, deliberately left for a
dedicated follow-up rather than rushed in here.

## Testing

Five distinct kinds of test now exist, each answering a different
question and each with its own Makefile target or script - not one
undifferentiated pile growing by accretion as new phases need new checks.

| Kind | Answers | How |
| --- | --- | --- |
| **Unit** | Does one instruction/CSR/device behave correctly in isolation? | `tests/selftest.S`, `make test` |
| **Differential** | Does our own implementation match real `qemu-system-riscv64` byte-for-byte? | `make compare` (CPU/CSR/devices), `make virtio-compare` (virtio-blk, console output + disk contents both), `make integration-compare` (SBI dispatch, ours vs real OpenSBI) |
| **Integration** | Does `boot/firmware.S`'s *real* SBI dispatch work, as opposed to the CPU's trap/ECALL mechanics alone? | `tests/integration_sbi.S`, `make integration` / `make integration-compare` |
| **Boot** | Does a real Linux kernel + initramfs actually reach a shell? | `boot/boot_test.sh` (`make boot-test`) |
| **Interactive** | Does typing at that shell actually get echoed and executed? | `boot/interactive_test.sh` (`make interactive-test`) |

`make test-all` runs everything that finishes in seconds (unit +
differential + integration); boot and interactive tests are deliberately
excluded from it, since a full Linux boot takes tens of seconds even when
nothing goes wrong, and because - see immediately below - a FAIL from
either one right now does not reliably mean a regression.

**Unit vs differential, as two axes, not two buckets**: nothing in this
project has ever had hardcoded "golden" expected register values checked
in - every selftest case's correctness is defined by matching real QEMU,
never by a value someone decided was right ahead of time (deliberately:
the project's whole premise is not trusting assumptions about correct
RISC-V behavior without checking them against real hardware's own
emulator). So today, "unit" describes *scope* (one instruction, one CSR,
one device, exercised in a small self-contained program) and
"differential" describes *how it's verified* (against real QEMU) - the
same test file serves both roles at once, rather than there being a
separate hardcoded-expected-value suite. Splitting some unit tests onto
real hardcoded expected values (for spec-mandated behavior unambiguous
enough not to need an oracle at all, e.g. "division by zero returns -1")
is a reasonable future refinement, not done yet.

**Integration vs differential, concretely**: `tests/selftest.S`'s own
Phase 2-4 CSR/trap/interrupt cases hand-roll their own minimal M-mode trap
setup inline and never load or execute `boot/firmware.S` at all - they
prove the CPU's trap/ECALL *mechanics* are correct, not that firmware.S's
own SBI call dispatch table does the right thing for a real caller.
`tests/integration_sbi.S` closes exactly that gap: it hands off to S-mode
the normal Phase 5 way (firmware at `RAM_BASE`, this payload at
`RAM_BASE + 0x200000`, matching `boot.sh`'s own real-kernel address) and
probes the base/HSM/legacy SBI calls a real kernel actually relies on -
then, for the differential half, the *identical* guest binary runs again
under real `qemu-system-riscv64 -M virt -bios default` (real OpenSBI) and
the two outputs' tails are compared. Finding this gap existed, and
building the test that closes it, happened in the same session as finding
that `boot/firmware.bin`/`.elf` had no Makefile rule at all (see Phase 5)
- both are instances of the same lesson: a thing with no test covering it
specifically can go stale or wrong silently, no matter how much coverage
exists one layer up or down from it.

**Boot and interactive tests are scripts, not Makefile recipes** - both
need real wall-clock polling loops (watching a growing log file for a
marker, or for the process to exit) that a `make` recipe is an awkward
place to express. `boot_test.sh` polls for `Welcome to you Linux RISC-V`
(or a panic / an unimplemented-instruction trap / firmware's own
`mtrap_unexpected` loop, any of which end the test as an immediate FAIL
rather than waiting out the full timeout). `interactive_test.sh` goes
further: it waits for the real `~ #` prompt to actually appear before
typing anything, rather than guessing a fixed delay - the literal method
that produced inconsistent results when this project tried it by hand
(see "Phase 5, finished") - then sends one command over a bash `coproc`
(not a named fifo - see "Phase 5, finished" for why that does not work
reliably against this project's own native binary) and checks it was both
echoed and actually executed (its own output line, not just the typed
text reappearing).

**Neither boot nor interactive tests were reliable at first, and that was
the honest, documented state at the time, not a bug in the tests**:
writing `boot_test.sh` and simply running it a few times in a row
(something nobody had done before - every previous session, including
this one until just now, only ever ran `boot.sh` by hand a handful of
times and moved on once it happened to work) immediately surfaced that the
stuck-after-`Run /init` hang this README had previously pinned on "live
early stdin interaction" is neither that specific nor that rare - see the
correction in "Phase 5, finished" above. This is exactly the kind of thing
a real test suite, run repeatedly rather than once, is supposed to catch;
it caught it on its first real use, and the actual root cause (a missing
UART transmit-interrupt, not a `StdinRing` race) is now fixed there too -
`interactive_test.sh` also needed its own fix once that one was resolved
(the named-fifo delivery it still used, and a stdout/stderr-merging bug
that broke its exact-line match - both covered in the same section).

## Known debt: no host abstraction layer yet (net.c/main.c are Windows-only)

`src/net.c` (809 lines) and `src/main.c`'s console input path are not
portable: they call Winsock directly (`SOCKET`, `closesocket`,
`WSAStartup`, `ioctlsocket`, `WSAGetLastError`) and the Windows ICMP API
(`IcmpCreateFile`/`IcmpSendEcho` - Linux has no equivalent call at all;
raw/"ping socket" ICMP there is a different enough shape that it is a
genuine reimplementation, not a rename), and `main.c`'s non-blocking
stdin read is a `CreateThread` + `<windows.h>` affair with no POSIX
analog in the same form. None of this is new - it predates this
refactor - but QemuManager's own architecture refactor (the C#
UHP.Core/WPF split) made the equivalent split on the emulator's own C
side worth calling out explicitly rather than leaving implicit, since the
same "don't block a Linux port, don't build it yet" goal applies to both.

**Why this is not a quick rename**: a real split means designing a small
host interface (socket open/close/send/recv/non-blocking-connect/
readiness-check, ICMP echo, non-blocking console read - call it
`host.h`), rewriting `net.c`'s NAT/DHCP/port-forwarding logic to call
through that interface instead of Winsock symbols directly (on the order
of 150-250 of its 809 lines touch a socket API call), moving `main.c`'s
stdin thread behind the same interface, and writing `src/host_win.c` as
the first (and for now, only) implementation of it. The portable packet-
building/parsing logic in `net.c` (Ethernet/IP/TCP/UDP framing, the NAT
table, the DHCP state machine) stays where it is and does not change
shape - only what currently reaches into Winsock directly would move
behind the interface. No `host_linux.c` gets written as part of this -
the point is only to stop blocking one, per the user's own instruction.

**Honest estimate**: roughly a half-day to a full day of focused work for
one person already familiar with this codebase - most of it reading
`net.c` carefully enough to design an interface that will not need a
redesign once a Linux implementation actually gets written, then the
mechanical rewrite, then re-running `make test-all`, `make boot-test`,
`make fs-compare`, and the NAT-dependent paths (Phase 9's DHCP/ping/curl
work, QemuManager.SelfTest's disk+network scenario) to prove zero
behavioral change - this touches the one code path every network-dependent
test in this project relies on, so "it compiles" is not evidence enough
on its own.

**Until this is done**: no new Windows-specific code should be added to
`net.c` or `main.c` directly (route anything new through existing
patterns there, or flag it here rather than deepening the same debt
silently).
