# AGENTS.md — OSB Kernel

This file guides coding agents working in the `osb.kernel` repository.

## Project Identity

**OSB Kernel** is the bootable, bare-metal core of **OSB - Operating System Basic**.
It is a small, DOS-like operating system built from scratch by the project owner,
originally created in BASIC/BC7 and now reborn as a native kernel.

Primary target: a **DOS-like operational system**, not a Unix-like system and not a
hosted .NET application. It must not depend on Linux, BSD, macOS, Windows, DOS, or
any preexisting OS at runtime. It is its own OS.

Design intent:

- Drive letters (`A:`, etc.), `\` path separators, per-drive current directories.
- DOS-like internal commands (`DIR`, `CD`, `TYPE`, `COPY`, `DEL`, `REN`, `MD`,
  `RD`, `CLS`, `VER`, `TRASH`, `GREP`, etc.).
- External programs loaded from the on-disk filesystem.
- Multitasking/preemptive scheduling without Unix process semantics.
- Syscalls through a DOS-like `INT 21h`-style ABI.
- Text-first console and VGA/serial output.

## New Architecture (Userspace Shell & OsbKT)

Starting from 2026‑10‑17 the project introduced a clean separation between the
freestanding kernel and userspace programs:

* **Osb.Kernel** – the freestanding NativeAOT kernel (no BCL, no runtime).
  It provides hardware initialization, memory management, process scheduling,
  the INT 21h syscall gate (`Osb.Kernel.Cpu.Syscalls.Handle`), and minimal
  filesystem services (Ext4 driver). The kernel no longer contains the legacy
  shell command loop; instead it can spawn userspace programs via the `SPAWN`
  and `WAIT` syscalls.

* **OsbKT** – a static library that wraps the kernel's INT 21h
  syscalls in a clean C# API (console: `PrintString`, `PutChar`,
  `GetKeyScancode`; process: `Exit`, `Spawn`, `Wait`, `GetPid`, `GetABI`;
  memory: `MemInfo`; read-only fs: `ResolvePath`, `DirNext`; environment:
  `GetCwd`, `GetArgs`). It also ships the ring-3 infrastructure every
  userspace program needs: `crt0.S` (startup + INT 21h shim + mem helpers),
  `user.ld` (link at 0x2000000) and `mkosbx.py` (OSBX packaging). The
  authoritative ABI contract is `osbkt/docs/ABI.md` — file create/write/
  delete, directories, environment and cooked input are **not** implemented
  yet (reserved AH numbers return ENOSYS). Userspace programs consume osbkt
  and the kernel's MiniCoreLib by **linked sources** (sibling checkouts),
  so each program stays a single freestanding NativeAOT module.

* **Osb.Shell.Userspace** – a new userspace shell project that implements a simple
  REPL using only `OsbKT` for console I/O and file operations. It is compiled as
  a freestanding NativeAOT executable (`SHELL.EXE`) and placed on the ext4 image
  at `/OSB/BIN/SHELL.EXE`. The kernel's boot sequence spawns this userspace
  shell directly (`Scheduler.SpawnFile`); `SPAWN`/`WAIT` (AH=80h/81h) remain
  available to ring-3 programs for built-in test tasks.

The build expects the OSB repos **side by side** (`osb.kernel`, `osb.shell`,
`osbkt`, `osb.core`; `osb.xwin` optional). `build.sh` fails with a clear
message when a required sibling repo is missing.

This architecture makes the kernel truly modular: userspace programs (shell,
OSLANG interpreter, utilities) can be developed, replaced, or extended without
recompiling the kernel, as long as they respect the `OsbKT` ABI.

## Code policy

1. **Implementation languages**
   - Use **C#** for kernel and tooling logic.
   - Use **OSLANG** for scripts/programs that should run as OSB applications.
   - Use **assembly only where necessary** for lowest-level boot, interrupt stubs,
     context switching, CPU control, and primitive hardware access.
   - Keep assembly minimal and isolated in `.S`/boot helper files. Do not spread
     inline/assembly logic into ordinary C# subsystems.

2. **Build from scratch**
   - Do not add runtime dependencies on host OS services or standard CLI wrappers.
   - Freestanding constraints are intentional: no BCL convenience, no GC
     allocations, no exceptions for normal control flow, and fixed/stack buffers
     where appropriate.
   - Prefer deterministic, bounded behavior over convenience frameworks.

3. **OS personality**
   - Preserve DOS-like behavior and user experience.
   - Do not refactor toward Unix concepts: no `/etc`, no `fork()` semantics, no
     POSIX file model, no pipe-heavy design unless explicitly requested.
   - New features should fit OSB's command model and path/drive model.

## Architecture map

Main namespaces/classes:

- `Osb.Kernel.Memory`
  - `Layout`: hardware/memory/FS/scheduler constants.
  - `PhysicalMemory`: frame bitmap allocator.
- `Osb.Kernel.Cpu`
  - `Gdt`, `Interrupts`, `Pit`, `Rtc`, `Syscalls`.
- `Osb.Kernel.Hardware`
  - `Vga`, `Serial`, `Keyboard`, `ConsoleControl`, `NativeMethods`.
- `Osb.Kernel.Processes`
  - `Scheduler`, `AddressSpace`, `TaskInfo`, task table, stack pool.
- `Osb.Kernel.Storage`
  - `Ata`, `Ext4`, `Cwd`.
- `Osb.Kernel.Shell`
  - `Shell`, `Environment`, `Prompt`, `History`, `Completion`, `JobControl`,
    `ShellText`, `Users`, command modules.
- `Osb.Kernel.Shell.Commands`
  - `FileCommands` (DEL moves files into the per-user trash), `TrashCommands`
    (`TRASH [/F pattern]` search, `TRASH /U name` undelete; trash is
    `$HOME\TRASH` on the ext4 volume, per user, root included),
    `GrepCommand` (`GREP [/I] pattern [file]` line filter; without [file]
    it filters piped stdin).
- `Kernel` partial classes
  - Boot orchestration: serial/VGA, PIC/PIT/RTC, GDT, memory, scheduler, userspace shell spawn.
- `MiniCoreLib` (linked from `../osb.core/src/MiniCoreLib.cs`)
  - Minimal runtime/type stubs required for freestanding NativeAOT; the single
    canonical copy lives in osb.core — never add a local one here.

Key subsystem concepts:

- Boot path: Serial/VGA -> PIC/PIT/RTC -> GDT -> physical memory detection -> IDT
  -> scheduler -> environment -> (legacy) shell **or** userspace shell spawn.
- Memory: frames start around `0x200000`; reserved user window `0x400000-0x5FFFFF`.
- Interrupts: PIC remapped to `0x30-0x3F`; syscall vector `0x21`.
- Tasks: 8 slots, slot 0 is console/shell.
- Ring 3 ABI via `INT 21h`: `01h` write, `09h` print, `10h` meminfo, `20h` resolve,
  `21h` dirnext, `22h` getcwd, `23h` getargs, `30h` getpid, `4Ch` exit, `80h`
  spawn, `81h` wait, `82h` getabi, plus `40h-59h` graphics/input. The full
  contract is `osbkt/docs/ABI.md`.
- Storage: ATA PIO + simplified ext4 read/write support, mounted as `A:`.
- User model: accounts, password files, sudoers, ownership/mode checks.
- Environment: `$VAR`, `SET`, `PROMPT`, history, completion.
- Pipes: `A | B` captures A's console output (Vga capture mode) and
  appends it after the first `\n` of B's command line; `GREP` reads it
  there. Pipelines run in the foreground.

Embedded language: none. OSLANG was removed from the kernel in phase O0
(2026-11-19); the port is archived read-only at
`osb.oslang/archive/kernel-port/` and the Ring-3 interpreter ships as
`A:\OSB\BIN\OSLANG.EXE` (see `osb.oslang/OSLANG-PLAN.md`, phase O0).

## Coding standards

Apply these to all new or modified kernel code:

### SOLID

- **S - Single Responsibility**: one subsystem or command family per file/class.
  Keep hardware concerns, scheduler concerns, file system concerns, and shell
  command concerns separate.
- **O - Open/Closed**: add new commands/effects/APIs by extension (new methods
  or modules) instead of rewriting stable dispatch paths.
- **L - Liskov Substitution**: maintain behavior contracts; do not weaken
  validation/error handling when extending functions.
- **I - Interface Segregation**: avoid giant multi-purpose interfaces/modules.
  Keep command modules focused (`SystemCommands`, `FileCommands`, etc.).
- **D - Dependency Inversion**: depend on stable abstractions/contracts where
  practical; in this freestanding kernel, keep subsystem calls explicit and avoid
  hidden global state changes when possible.

### KISS

- Prefer simple, explicit control flow.
- Avoid speculative frameworks, reflection-like indirection, or heavy abstractions.
- Keep low-level code readable and narrowly scoped.
- Avoid "clever" optimizations unless measured and necessary.

### DRY

- Reuse existing helpers (`ShellText`, `Environment`, `Ext4`, `Users`, etc.).
- Factor repeated allocation/parser/validation logic into local helpers.
- Do not duplicate command parsing or ext4 logic in multiple command modules.

### File size rule

- **No source file may exceed 400 lines.**
- If a file approaches 400 lines, split it into focused partials or modules.
- Preserve namespace/class cohesion when splitting.

### Freestanding safety

- Do not use exceptions for ordinary control flow; use status bools/error flags,
  as existing subsystems do.
- Avoid managed heap allocation; use fixed buffers, stackalloc, arenas.
- Preserve bounded loops and array limits.
- Keep hardware invariants documented in comments (PIC masks, vector layout,
  reserved frames, reserved user window).

### Assembly boundary

- Assembly changes should be limited to boot/startup, interrupt stub generation,
  context switch, and hardware primitives.
- Any new assembly primitive must be exposed through `NativeMethods` or an
  equivalent boundary and called from C#.

## Build and validation

```sh
chmod +x build.sh
./build.sh            # builds osb.iso
./build.sh run        # build + run in QEMU, serial stdio
./build.sh run-gui    # build + run with VGA window
./build.sh check      # validate Multiboot2 ELF
```

Validation expectations:

- Build succeeds.
- `run` does not hang during boot.
- Shell accepts commands and returns results.
- `POWEROFF` cleanly exits QEMU.
- If changes touch interrupts/syscalls/scheduling, test demo/process commands.
- If changes touch ext4, test `DIR`, `CD`, `TYPE`, allocation/write paths.
- If changes touch user/auth, test `USER`, `LOGIN`, permissions.

## Change workflow for agents

1. Inspect relevant files first.
2. Keep edits targeted.
3. Do not overwrite unrelated user changes.
4. Update docs when behavior changes (README/ROADMAP/AGENTS).
5. Check file lengths before finishing.
6. Run the narrowest validation available.

## Non-goals and cautions

- Do not convert OSB into a Linux/Unix subsystem.
- Do not introduce host-OS runtime dependencies for kernel execution.
- Do not remove DOS-like command behavior for minor code cleanliness.
- Do not exceed 400 lines in any source file.
- Do not expose private person data or secrets; kernel stores credentials in
  `PASSWD`/`SUDOERS` format and should continue to handle them carefully.

## Documentation anchors

- `README.md`: high-level project/user documentation.
- `ROADMAP.md`: planned vertical slices and architecture decisions.
- `osb.oslang/OSLANG-SPEC.md` (+ per-version `OSLANG-0.*-SPEC.md`): language specification.