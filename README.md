# Osb.Kernel — the bootable kernel of OSB

First **bootable** version of OSB (reboot of the original project from 2000 in QBasic),
written in **pure freestanding C#** in the style of the [Cosmos](https://github.com/CosmosOS/Cosmos) project:
no host operating system, no external framework — compiled with **NativeAOT**
(no runtime, no BCL) and its own **Multiboot2 bootloader**.

## What this version does

- Boots via **GRUB (Multiboot2)** on x86-64 (long mode), from an ISO (BIOS).
- C# kernel natively compiled with `ilc` (NativeAOT) against a **custom mini-CoreLib**
  (`MiniCoreLib.cs`) — the only "libraries" required for the C# compiler to work
  without an OS.
- Text output in **VGA text mode** (`0xB8000`) mirrored to **COM1 (serial)**.
- **IDT (256 gates) + PIC 8259A** remapped (IRQ0-15 → vectors **0x30-0x3F**): CPU
  exceptions become readable panics (vector + RIP), and the **PS/2 keyboard is interrupt-driven**
  (IRQ1 → vector 0x31, circular scancode queue, explicit EOI) — no polling. The remap
  to 0x30 leaves vector **0x21** free for the DOS-like syscall `INT 21h`.
- **Serial console (COM1, IRQ4 → vector 0x34)**: besides the PS/2 keyboard, the shell also
  reads characters received over serial. This makes the session **headless** usable
  (`./build.sh run` uses `-serial stdio`): what you type in the terminal reaches the shell.
  Serial output uses CRLF.
- **PIT timer** (8254, channel 0) at **100 Hz via IRQ0** (vector 0x30): global tick
  counter, uptime in seconds, and the `TICKS`/`SLEEP <seconds>` commands (tick-driven wait).
- **RTC clock** (CMOS, IRQ8 → vector 0x38): 24-hour + binary mode, "update-ended"
  interrupt (~1 Hz) that keeps the kernel's software clock; `TIME` and `DATE` commands
  show real time and date.
- **Preemptive round-robin scheduler** over a **task table** (8 slots): slot 0 is the
  **console/shell** (runs on the boot stack) and the others are tasks/processes created by
  commands (`DEMO`, `SPAWN`), each with its own **16 KiB kernel stack**. Switching happens
  by **preemption**: the PIT handler (IRQ0) switches context every tick (stack switch via
  `osb_switch`), no `yield`; free slots are skipped. The interrupted ISR frame stays
  "parked" on each task's stack until it is rescheduled.
- **Processes with their own address space** (Slice 2):
  - **Physical frame allocator** (4 KiB bitmap) over the Multiboot2 mmap; pool from
    `0x200000` up to the top of RAM (here 64 MiB; ~15,872 frames). The window
    `0x400000-0x5FFFFF` (the `pd[2]` of every space) is reserved. `MEM` shows usage/range.
  - Each process has its own `PML4+PDPT+PD` (3 frames). The kernel is copied as an identity
    map (`pd[0..1]`, U=0) and `pd[2]` points to a **private 2 MiB physical block**:
    code goes at `0x400000` and the user stack at `0x480000` — the *same virtual address*
    in every process, but with distinct physical memory. The scheduler does `mov cr3` on every
    switch and adjusts `TSS.RSP0` to the process's kernel stack.
  - **`spawn`/`exit`/`wait`**: a terminated process becomes a **zombie** (with exit code)
    until a `WAIT` reaps it and frees frames; `KILL` kills and frees immediately.
- **Task management (job control)**, in the DOS+multitasking spirit:
  - `DEMO [&]` starts a demo task; `SPAWN [BAD] [&]` (alias: `RING3`) starts a
    ring-3 process with its own address space.
  - **`&` at the end** runs the command in **background** and returns the prompt (e.g. `SPAWN &`).
  - **Without `&`** runs in **foreground**: the shell waits for the job to finish and
    **collects the exit code**; **Ctrl+C** aborts and returns to the prompt (type-ahead preserved).
  - `TASK` (or `PS`) lists PID/name/state (run/zombie)/memory block — a simple `ps`;
    `KILL <pid>` kills; `WAIT <pid>` waits and reaps; `TAB <pid>` brings to foreground.
- **Ring 3 (user mode)**: GDT built at runtime with user segments (DPL3) + **TSS**
  (`ltr`; `RSP0` = top of the ring-3 process's kernel stack, adjusted by the scheduler);
  paging marks only the `0x400000-0x5FFFFF` window as **U/S=1** (the kernel stays U=0,
  inaccessible to user); the IDT exposes vector `0x21` as a **DPL3** gate. Each process
  gets its own **2 MiB block** in that window and enters CPL3 via `iretq`. **DOS-like syscalls**
  via `INT 21h` (function in `AH`): `AH=09h` prints a `$`-terminated string (RSI),
  `AH=30h` getpid, `AH=4Ch` exit(code in AL), `AH=80h` spawn(program id),
  `AH=81h` wait(pid). Failures coming from CPL3 (e.g. `cli` in ring 3 → **#GP**) are
  **recoverable**: the kernel terminates only that process (becomes zombie, code `-1`) and
  keeps running.
- **Shutdown/reboot**: `POWEROFF` does ACPI S5 (PM1a_CNT port 0x604) — in QEMU the
  emulator exits cleanly (useful: nothing stays capturing host keyboard/mouse);
  `REBOOT` pulses reset via the 8042 controller.
- **PS/2 keyboard** and **serial console** input with a mini-shell:
  `VER`, `ABOUT`, `HELP`, `CLS`, `ECHO <text>`, `TICKS`, `SLEEP <s>`, `TIME`, `DATE`, `MEM`,
  `DEMO [&]`, `SPAWN [BAD] [&]`, `RING3 [BAD] [&]`, `TASK`/`PS`, `KILL <pid>`, `WAIT <pid>`,
  `TAB <pid>`, `HALT`, `POWEROFF`, `REBOOT`.
- **History and autocomplete in the shell**:
  - `HISTORY [n]` lists numbered history (the **last 100** by default);
  - `(:N)` re-runs entry N of history (e.g. `(:45)`);
  - **Up/Down arrows** (or `ESC [ A` / `ESC [ B` over serial) navigate history,
    preserving the line being edited;
  - **TAB** completes command names (first token), flags (`/S` of `LN`)
    and file/directory names on ext4 in `A:` (relative, absolute, with automatic `\`
    for directories).
- **Login system** (unix-style):
  - boot drops to the `login:` prompt; **root logs in without a password**; accounts with
    a password are asked `Password:` (no echo);
  - `USER` lists accounts; `USER /ADD <name> [password]`, `USER /CHANGE <name>
    [password]`, `USER /DEL <name> [/HOME]`, `USER /SUDO` / `USER /UNSUDO`;
  - `LOGIN [name]` switches user; `EXIT`/`LOGOUT` returns to `login:`;
  - `CHOWN <user> <path>` (root), `CHMOD <octal> <path>` (owner/root),
    `SUDO <command>` (those in SUDOERS, confirming their own password);
  - persistence in `A:\OSB\CONF\PASSWD` (`name:password:uid:home`, mode 0600) and
    `A:\OSB\CONF\SUDOERS`;
  - everything under `A:\OSB` belongs to root (uid 0), except `A:\OSB\HOME\<user>`
    (owned by the user, created on `/ADD`, is the login's initial PWD) and `A:\OSB\TEMP`
    (mode 0777, shared). `DIR` shows the owner's name.
- **Command help**: every internal command accepts `/?` (or `-?`) and shows its
  help page (usage, flags, examples); `HELP <command>` shows the same page,
  and `HELP` alone lists all commands. External commands receive `/?` and handle
  help themselves.
- **Pipes**: `A | B` captures A's output and appends it after the first `\n` of B's
  command line; `GREP [/I] <pattern> [file]` filters lines (with `[file]` reads the file;
  without, filters the pipe's stdin). Pipelines run in foreground.
- **Network**: polled Intel PRO/1000 (e1000) driver over PCI. `PCI` lists the bus
  (`PCI net` details the adapter; `PCI B:D.F` details a device); `PING <ip>
  [n]` sends ICMP echo and shows round-trip time; `NET` shows addressing,
  `NET <ip>` sets a static address and `NET DHCP` (or `NET AUTO`)
  negotiates with a DHCP server. Without DHCP there is a built-in static address
  (10.0.2.15/24, gateway 10.0.2.2) — by default the shell does not configure the
  network at boot, to avoid blocking boot waiting for a server.

  Known limitation: QEMU's user network does not carry ICMP to the internet.
  Slirp encapsulates external ICMP inside UDP/7 and nothing responds on that port
  today, so `PING 8.8.8.8` times out even with correct routing — the packet leaves
  addressed to the gateway, as it should. Targets inside QEMU's network (for example
  10.0.2.2 itself) respond normally.
- **Graphical desktop**: `GFX` opens a Windows 3.11-style desktop
  (icons, windows, taskbar), drawn by `Gfx` over two backends: the Bochs/DISPI VBE
  linear framebuffer (preferred — what shows up with QEMU's `-vga std`)
  and legacy VGA mode 12h (640x480, four planes), for machines without the Bochs
  device. `GFX TEST` draws a test screen; `GFX TEXT` returns to the console;
  **Ctrl+F4** closes the desktop and returns to the shell (the console's 8x16 font
  is saved before entering graphics mode and restored on exit, so text comes back
  readable).

## Architecture

```
src/Osb.Kernel/
├── Osb.Kernel.csproj   # "no .NET" C# project: NoStdLib, nothing referenced
├── MiniCoreLib.cs      # mini-CoreLib: System.Object/String/primitives + stubs the
│                       # ilc requires from the "system module" (ThrowHelpers, Buffer, TypeCast...)
├── Kernel.cs           # only orchestration: Main/kmain (boot order) + banner
├── Hardware/           # kernel I/O boundary
│   ├── Vga.cs          # 80x25 text mode (mirrors everything to COM1)
│   ├── Serial.cs       # COM1: output + input ring fed by IRQ4
│   ├── Keyboard.cs     # PS/2 scancodes (IRQ1) + Shift map
│   ├── ConsoleControl.cs # console signals (Ctrl+C) between IRQ and shell
│   └── NativeMethods.cs  # "osbnative" P/Invoke (inb/outb, lgdt/lidt, switch, CR3...)
├── Memory/             # physical memory
│   ├── Layout.cs       # constants (VGA, IDT/PIC, GDT, ring 3, ATA, ext4, tasks)
│   └── PhysicalMemory.cs # frame bitmap + Multiboot2 map detection
├── Cpu/                # timers, control memory and IRQ entry
│   ├── Pit.cs          # 8254 channel 0 @ 100 Hz + tick counter
│   ├── Rtc.cs          # CMOS/RTC: software clock (IRQ8), TIME/DATE
│   ├── Gdt.cs          # kernel/user segments + TSS (RSP0)
│   ├── Interrupts.cs   # 8259A, IDT and dispatcher (osb_handle_interrupt)
│   └── Syscalls.cs     # INT 21h port (print/getpid/exit/spawn/wait)
├── Processes/          # tasks and per-process memory
│   ├── Scheduler.cs    # slot table, preemptive round-robin, jobs
│   └── AddressSpace.cs # private CR3 per process (2 MiB block)
├── Storage/            # persistence
│   ├── Ata.cs          # ATA PIO (512-byte sectors)
│   ├── Ext4.cs         # simplified ext4 (blocks, inodes, directories, resolution)
│   └── Cwd.cs          # current drive + directory (DOS namespace)
├── Network/            # networking (PCI, driver and IPv4 stack)
│   ├── Pci.cs          # config space (0xCF8/0xCFC) + bus scan
│   ├── E1000.cs        # polled Intel PRO/1000 driver (TX/RX rings, no IRQ)
│   ├── NetStack.cs     # interface state, frame/checksum helpers
│   ├── NetStack.Ip.cs  # IPv4/UDP/ICMP (transmission)
│   ├── NetStack.Arp.cs # address resolution
│   └── Dhcp.cs         # DHCP client: DISCOVER -> OFFER -> REQUEST -> ACK
├── Shell/              # prompt and commands
│   ├── Shell.cs        # input loop (serial/keyboard) + command dispatch
│   ├── Environment.cs  # $VAR / SET
│   ├── Prompt.cs       # DOS-style $PROMPT ($P$G...)
│   ├── JobControl.cs   # foreground/background, wait and reap
│   ├── History.cs      # history ring + HISTORY/(:N)/Up/Down arrows
│   ├── Completion.cs   # TAB: commands, /flags and file names
│   ├── Users.cs        # accounts, sessions, permissions (PASSWD/SUDOERS)
│   ├── ShellText.cs    # text comparisons/parse/padding
│   └── Commands/       # SystemCommands, EnvironmentCommands, FileCommands,
│                       # UserCommands (USER/LOGIN/CHOWN/CHMOD/SUDO), TaskCommands
├── OslangKernel.cs     # OSLANG 0.72 interpreter (lexer/parser/runtime) in the kernel
├── boot.S              # Multiboot2 bootstrap: 32-bit -> long mode -> calls kmain
│                       # (+ helpers: outb/inb, mem*, lidt, sti/cli, halt, lgdt/ltr,
│                       #  enter_user, load_cr3, kernel page tables)
├── sched.S             # context switch (osb_switch) + trampolines (demo/user)
├── user.S              # ring-3 programs: HELLO (isolation test) + BAD (cli->#GP)
├── interrupts.S        # generated at build: 256 ISR stubs + isr_common + stub table
├── linker.ld           # ELF layout (loaded at 0x100000)
├── grub.cfg            # GRUB menu (multiboot2)
├── ROADMAP.md          # plan: multiuser/ext4, vertical slices, ring 3+
└── build.sh            # full build -> osb.iso
```

Each subsystem is an `internal static` class in its own namespace
(`Osb.Kernel.Hardware`, `Osb.Kernel.Memory`, `Osb.Kernel.Cpu`,
`Osb.Kernel.Processes`, `Osb.Kernel.Storage`, `Osb.Kernel.Shell`): state lives
with whoever uses it and `Kernel.cs` only orders the machine's bring-up.

Build pipeline:

```
C# (mini-CoreLib)  ──csc──▶  OsbKernel.dll (IL)
OsbKernel.dll      ──ilc──▶  OsbKernel.o   (freestanding x86-64 object)
boot.S + sched.S + user.S + interrupts.S ─clang▶ boot.o + sched.o + user.o + interrupts.o
(...objects) + OsbKernel.o ──ld.lld + linker.ld─▶ kernel.elf (Multiboot2 ELF)
kernel.elf         ──grub-mkrescue─▶       osb.iso
```

## Prerequisites

- .NET SDK 10.x
- clang, lld (ld.lld), xorriso, grub-mkrescue (grub-pc-bin), nasm (optional)
- qemu-system-x86_64 (for testing)

## Build and run

```sh
cd src/Osb.Kernel
chmod +x build.sh
./build.sh            # generates src/Osb.Kernel/osb.iso
./build.sh run        # build + boot in QEMU (serial in terminal, no window)
./build.sh run-gui    # same, but opens the VGA window (careful with input grab)
./build.sh check      # validates that kernel.elf is Multiboot2
```

`run` uses `-display none -serial stdio`: no window, so QEMU **does not capture**
the host keyboard/mouse. The kernel mirrors VGA to serial and **also reads serial as
input**, so you type directly into the OSB shell through the terminal — just type a
command and Enter. To exit: `POWEROFF` at the OSB prompt (ACPI S5 → QEMU exits on its own).

`run` attaches an e1000 on a QEMU user network (`-netdev user -device e1000`),
which is what `PING` and `NET DHCP` expect. On that network the address is 10.0.2.15/24,
gateway 10.0.2.2 and the DHCP server is 10.0.2.2 itself.

When running with a window (`run-gui` or manual QEMU), input grab may trap the mouse/
keyboard of the session: **Ctrl+Alt+G** releases the grab and **Ctrl+Alt+2** opens the
QEMU monitor (then `quit`). The `POWEROFF` command also shuts down the emulator and
releases input.

## Suggested next steps

- **Processes with their own address space already work** (Slice 2): per-process CR3,
  `spawn`/`exit`/`wait` and proven isolation — see `ROADMAP.md`, Slice 2.
- **Multi-user** (Slices 3/6): accounts, `LOGIN`, sessions/consoles, permissions from ext4.
- **ext4** (Slices 4/5): reading already operational (`DIR`/`CD`/`TYPE`); missing
  **writing** (block/inode bitmaps → `MD`/`RD`) and then `COPY DEL REN`. The disk is
  `A:` in DOS parlance; it will enable external commands (`.EXE`) and scripts.
- **Shell comfort already implemented:** environment variables `$VAR` (case-insensitive
  names), `SET [NAME]` / `SET NAME=value`, DOS `PROMPT` (`$P$G` and tokens
  `$D/$T/$N/...`) with boot defaults `PATH PROMPT USER HOME TEMP COMSPEC OS VER LASTDRIVE`.
- ACPI + MMIO, framebuffer via `multiboot2` (graphics mode) instead of VGA text.
- **Hardening**: IST for exceptions, SMAP/SMEP, stricter syscall pointer validation.

## Notes

- ilc is located automatically in the NuGet cache
  (`runtime.linux-x64.microsoft.dotnet.ilcompiler`).
- `MiniCoreLib.cs` is the boundary of the "runtime": everything C# requires without an OS.
  Keep it small; no GC/allocation — the kernel uses fixed buffers/stackalloc.
- `interrupts.S` is generated deterministically by `build.sh` (256 stubs: each pushes
  `[vector][dummy]`, discarding the CPU error code on vectors 8/10-14/17).
  `isr_common` saves **GPRs + XMM0-15** (the SysV handler may clobber vectorized registers
  of the interrupted code), realigns the stack to 16 bytes, calls `osb_handle_interrupt`
  and does `iretq`.
- The shell "sleeps" with `sti; hlt` when the scancode queue is empty; IRQ1 delivers
  the scancode and IRQ0 (100 Hz) wakes the idle every tick.
- **Any slave IRQ (IRQ8-15) requires cascading on the master**: without IRQ2
  unmasked on the 8259A master (OCW1 = `0xF8` = IRQ0/IRQ1/IRQ2), the RTC's IRQ8 never
  reaches the CPU. `0xF8`, not `0xFB` (which masks precisely IRQ0 and IRQ1).
- **Preemption**: `SchedulerTick` runs inside the IRQ0 handler (therefore with IF=0, hence
  atomic) and performs `osb_switch` to the other task. EOI is sent **before** the switch,
  so the PIC can deliver the next tick to the new task. When the task is rescheduled, it
  resumes from inside `SchedulerTick` itself, unwinds the handler, restores GPRs+XMM from
  the frame and does `iretq` — the interrupted context comes back intact.
- `osb_switch` saves only **callee-saved** registers (rbx, rbp, r12-r15). That is enough
  because `isr_common` already saves **all** GPRs and XMM in the interrupt frame before
  calling the handler: what the context switch must preserve are only the callee-saved
  registers live between the `iretq` and the switch point.
- The demo task has a fixed 16 KiB stack in `.bss`; an IRQ may interrupt it and runs on
  the task's own stack (no TSS), so the space must cover ISR + handler.
- **SSE requires CR0.EM=0**: with `CR0.EM=1` every SSE instruction in the C# code (e.g.
  `movups` from a stackalloc) raises `#UD`. `boot.S` forces CR0 (PG|NE|WP, EM/TS=0)
  and CR4 (PAE|OSFXSR|OSXMMEXCPT) on entry to long mode.
- **PIC remapped to 0x30-0x3F** (not 0x20-0x2F): frees vector **0x21** for the
  `INT 21h` syscall, without colliding with IRQ1 (keyboard). Constants `PicBase=0x30`,
  `TimerVector=0x30`, `KbdVector=0x31`, `RtcVector=0x38`.
- **Ring 3 + processes**: the GDT is built in `Cpu/Gdt.cs` (user code segment `0x18` /
  data `0x20`, both DPL3, + TSS descriptor at `0x28`), loaded with `osb_lgdt`/`osb_ltr`.
  The TSS `RSP0` points to the top of the **user process's** kernel stack — that is where
  the CPU switches when an interrupt/syscall arrives from CPL3. On user entry we use
  selectors with RPL3 (`0x1B`/`0x23`). Each process has its own page tables (`PML4/PDPT/PD`);
  we copy the kernel's identity map (`osb_master_pd()`) and replace only `pd[2]` with a
  private 2 MiB physical block. The program (`user.S`) is copied to the start of the block
  and the scheduler swaps CR3 (`osb_load_cr3`) on every context. A second blob (`BAD`)
  executes `cli` to prove `#GP` in CPL3.
- **Why `pd[2]` and the reserved window**: since the kernel is *identity-mapped* in 0-1 GiB,
  the physical address of a freshly allocated block only matches the virtual one outside the
  user window. That is why the allocator never hands out frames in `0x400000-0x600000`
  (512 frames): this way the kernel can initialize any block through its identity, even
  while running under a process's CR3 that called `spawn`. Zombies nobody waits for occupy
  a slot, but frames only come back on reaping.
- **Resource failure on user fault**: an exception with CS.RPL==3 is treated as a program
  failure (prints vector/err/RIP, marks the task as **zombie** with code `-1` and schedules
  another); CPL0 exceptions remain fatal panics.
