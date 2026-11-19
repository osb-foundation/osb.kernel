# OSB-ARCHITECTURE

Reference architecture for OSB (Operating System Basic) across all repositories.
Every repository links here; this document wins over any README/ROADMAP on
architectural questions, and the per-area documents (ABI.md, MIGRATION.md,
the OSLANG spec) win over this one on details.

**Status labels** used throughout:

* **[DECIDED]** — the project owner decided it.
* **[CURRENT]** — how the code behaves today (verified against `phase-3` on 2026-11-18).
* **[PROPOSED]** — recommended, not yet decided; needs an explicit yes.
* **[OPEN]** — a question that must be answered before the work it blocks.

Keep this file under ~400 lines. When code and this document disagree, fix one
of them in the same change.

---

## 1. Vision and goals

OSB is a DOS-like, multi-user, multitasking, protected-mode operating system
written in C#, booting on x86-64.

1. **[DECIDED]** Microkernel direction: services move out of Ring 0 over time. See §13 for
   the staging; today the kernel is a hybrid (§3).
2. **[DECIDED]** Ring-3 applications reach the system only through the SDK (`osbkt`).
3. **[DECIDED]** ext4 on disk, DOS experience on top (drive letters, `\`, DOS commands/messages).
4. **[DECIDED]** Multi-user, multitasking, protected: security is enforced by the kernel
   (and later by services), never by the shell.
5. **[DECIDED]** OSLANG is the default scripting language, a first-class citizen running as a
   separate Ring-3 executable in `A:\OSB\BIN` (§12). It is not part of the kernel.
6. **[DECIDED]** Graphical interface comes after the service architecture is ready.

Non-goals: Unix-likeness (no `fork`, no `/etc`, no "everything is a file"); a POSIX layer;
host-OS runtime dependencies.

---

## 2. Principles

* **P1 Mechanism in the kernel, presentation in userspace.** The kernel enforces
  security and provides primitives. It never prints command messages, parses command
  lines, or formats output for a shell. *(Violated today by syscalls 26h–29h/61h/62h, §3.)*
* **P2 The kernel trusts nobody.** Not Ring 3, and not the SDK. Every pointer, length,
  handle, uid and flag is validated in the kernel. The SDK is a convenience boundary, not a
  security boundary: a program can issue `INT 21h` itself and gains nothing extra.
* **P3 One SDK.** Syscall numbers and register conventions exist in `osbkt` only.
  No second copy of crt0/startup/linker script/packaging anywhere.
* **P4 Freestanding discipline.** Ring-3 and kernel C# code: no BCL, no GC allocations,
  no exceptions for ordinary control flow, fixed/stack buffers, max 400 lines per source
  file. *(OSLANG's engine is the one place that needs its own managed-object heap, §12.)*
* **P5 Contracts are versioned.** `osbkt/docs/ABI.md` and `Syscall.AbiVersion` change in
  lockstep; the kernel's `GETABI` must be ≥ the SDK's. A CI check enforces equality.
* **P6 Legacy is removed only after parity is confirmed by the owner.**
* **P7 Docs are code.** Stale docs are bugs. READMEs describe what exists, not what is planned.

---

## 3. Current state vs target

**[CURRENT]** Ring-3 userland exists and works, but the kernel is monolithic-hybrid:

| Area | Today | Target |
|---|---|---|
| Scheduler, address spaces, interrupts | kernel | kernel |
| ext4 (read/write), block I/O (ATA PIO) | kernel | filesystem + block services (§13), later |
| Network (E1000, ARP/IP, DHCP), PCI | kernel | network service, later |
| Users, sessions, PASSWD/SUDOERS | kernel (`Shell/Users.cs`, `Processes/Session.cs`) | kernel keeps session/credentials; policy for files moves with the FS service |
| File-operation logic (`Shell/Commands/FileCommands.cs`, `Trash*`) | kernel, still command-line oriented, prints to VGA | path-based primitives; **no presentation** (cleanup pending) |
| Graphics (Bochs/VGA, window-manager primitives) | kernel (`Graphics/`, `Cpu/SyscallGfx.cs`) | display service, with the GUI phase |
| Shell | Ring 3 (`SHELL.EXE`, `osb.shell`) | Ring 3 |
| OSLANG | in-kernel port, **removed** (O0, 2026-11-19; sources archived in `osb.oslang/archive/kernel-port/`) | Ring-3 `OSLANG.EXE` (§12) |
| Desktop (XWIN) | Ring 3 ELF-like OSBX program, uses the raw gfx/input range | Ring 3 app via `osbkt` |

Known divergences between docs and code are tracked in §16.

---

## 4. Repositories

Sibling checkouts side by side. Dependency direction is strictly downward.

| Repo | Role |
|---|---|
| `osb.kernel` | Kernel and boot image build (`build.sh` produces `osb.iso`). Requires siblings `osb.shell`, `osbkt`, `osb.core`, `osb.oslang`; fails with a clear message if one is missing. |
| `osbkt` | The only supported interface to the kernel for applications: wrappers, `crt0.S`, `user.ld`, `mkosbx.py`, and **`docs/ABI.md` (the ABI contract)**. Consumed as linked sources, no `osbkt.dll` at runtime. |
| `osb.core` | Canonical `MiniCoreLib.cs` (freestanding runtime subset) — single copy, linked into every freestanding module. |
| `osb.shell` | DOS-style shell, Ring 3. Owns presentation, parsing, expansion, dispatch, help. |
| `osb.oslang` | Language spec, conformance corpus, engine, `OSLANG.EXE`, host libraries (§12). |
| `oslang.vscode` | Editor tooling; must track the official spec version. |
| `osb.xwin` | Desktop/window manager, Ring 3 app. *(Not audited for this document.)* |
| `osb.toolkit` | UI toolkit over the SDK. *(Not audited.)* |
| `osb.legacy` | Historical archive, no production dependencies. *(Not audited.)* |

**[CURRENT]** Why linked sources: a `ProjectReference` from a consumer to `osbkt` was tested
and does not work with the NoStdLib/MiniCoreLib arrangement (`csc` cannot import
predefined types from a referenced NoStdLib library). Consequence: `internal` in `osbkt` gives
**no** access control (same assembly). **[PROPOSED]** enforce "only `osbkt` issues syscalls"
by a CI check that fails when `INT 21h`, `Invoke(`, or raw syscall numbers appear outside
`osbkt`.

---

## 5. Layers and communication

```
Applications   SHELL.EXE · OSLANG.EXE · .EXE programs · XWIN.EXE
                 |  public, high-level API
osbkt          Console · File · Process · Env · User · Time · Memory · (Input, Graphics, Ipc later)
                 |  transport: INT 21h today (AH = function), future: IPC to services
osb.kernel     Ring 0: CPU, memory, scheduler, interrupts, syscalls, drivers (today: also FS, net, gfx)
                 |
Hardware       x86-64 · ATA · VGA/Bochs · PS/2 · COM1 · PCI · E1000
```

The application must not know whether an operation is served by a syscall or, later, by a
service behind IPC. Keep the public SDK surface transport-agnostic.

### 5.1 ABI conventions (authoritative copy: `osbkt/docs/ABI.md`)

* `INT 21h`, function in `AH`, args in registers (RDI/RSI/RDX/RCX), result in `RAX`.
* Strings crossing the boundary are **byte strings with an explicit length**.
* Pointers are validated including the end of the range (overflow-safe); a bad range returns
  `-1 EFAULT`, never a Ring-0 fault.
* Error codes: `-1` EFAULT, `-2` ENOSYS, `-3` EACCES, `-4` ENOENT, `-5` EINVAL,
  `-6` ENOMEM, `-7` EINTR, `-8` EBADF. Per-syscall exceptions are listed in ABI.md
  (e.g. `GETCHAR`: `-1` = no byte, `-2` = Ctrl+C).
* Per-task handle table of 8: 0/1/2 = stdin/stdout/stderr, `OPEN` returns 3–7.
* `SPAWNPATH` (6Ah) takes a 40-byte `SpawnReq`; redirection uses the caller's open handles,
  never inodes. Arguments are capped at 128 bytes.
* Version negotiation: shell/SDK refuse a kernel whose `GETABI` is lower than theirs.

### 5.2 Public SDK shape **[PROPOSED]**

Namespaces `Osb.Console`, `Osb.FileSystem`, `Osb.Process`, `Osb.Memory`, `Osb.User`,
`Osb.Time`, later `Osb.Network`, `Osb.Input`, `Osb.Graphics`, `Osb.Ipc`. Low-level
transport (numbers, registers, request blocks) stays private to the SDK sources.

---

## 6. Kernel responsibilities

**Stays in Ring 0 (target):** interrupt dispatch, physical/virtual memory, context switch,
scheduler, address spaces, IPC, protection and isolation, minimal hardware access, session
and credential table.

**Leaves Ring 0 over time (§13):** ext4 and block I/O, TCP/IP, graphics services, window
management. **Never in the kernel:** shell, command parsing/printing, OSLANG.

Drivers in userspace need mediated IRQ delivery, MMIO/port access and DMA via explicit
kernel permissions; that is a prerequisite for moving any driver out.

---

## 7. Processes, sessions, identity, permissions

**[CURRENT]**

* Preemptive round-robin scheduler, 100 Hz PIT; each process has its own CR3.
* Boot: kernel initialises environment and users, spawns `A:\OSB\BIN\SHELL.EXE`, waits.
  (`CONF/BOOT` is honoured only in `BOOT_FROM_CONF` test builds.)
* The **console session** starts with no uid; `LOGIN`/`LOGOUT`/`SUDO`/`END_SUDO`/`WHOAMI`
  (65h–69h) are kernel-owned. `LOGIN`/`LOGOUT` are accepted only from the session leader.
  Unknown user and wrong password are indistinguishable (`-3`). Passwordless login is allowed
  only at the initial boot login.
* `SUDO` elevates the **calling task only** (checked against SUDOERS + password) and is
  reverted by `END_SUDO`/end of command; background tasks never inherit it.
* Environment is per task, copied at spawn; a child's `SETENV` never alters the parent.
  `USER`/`HOME` are kernel-locked after login.
* `SPAWNPATH` requires an execute bit for the task's uid (root always); the image build
  stages every `.EXE` with mode 0755. `KILL`/`SETFG` require same uid or root; the console
  cannot be killed.
* File permissions: owner/other bits, root bypass; creation/removal needs write+execute on
  the parent directory. `DIR` metadata (`STAT`) does not require read permission on the file.

**Debt** (tracked): `PASSWD` stores plaintext passwords; no hashing yet.
No SMEP/SMAP; user memory is one fixed 2 MiB block per process (§8).

**Model [PROPOSED]:** keep three concepts distinct — *User* (identity), *Session*
(login/console context), *Process* (a program under a credential). A process inherits the
session's credentials but never another process's temporary privileges. Administrative
elevation must be explicit, scoped and auditable.

---

## 8. Memory

**[CURRENT]** Each process gets a private **2 MiB** physical block mapped at
`0x2000000–0x2200000`: image, data, heap and stack all live there (stack top `0x2080000`).
There is no syscall to grow memory. `MEMINFO` (10h) reports totals only.

**[PROPOSED]** Growable per-process memory (new syscall, ABI.md entry first): grant memory in
2 MiB steps up to a per-process limit, accounted and returned when the process is reaped.
This is a hard prerequisite for `OSLANG.EXE` (§12) and any non-trivial application.
Add per-process limits and SMEP/SMAP/guard pages to the hardening backlog.

---

## 9. Filesystem

* **[DECIDED]** ext4 on disk; DOS presentation. Case-insensitive path resolution in the DOS
  layer while preserving on-disk names.
* **[CURRENT]** The kernel mounts one ext4 volume (`A:`), read/write, hand-written driver.
  The image is built with `mke2fs -t ext4 -b 4096 -O ^has_journal,^metadata_csum,^flex_bg,^dir_index`.
  **[OPEN]** Which of extents/64-bit/huge files the driver supports; document it as the
  **OSB ext4 profile** in `osb.kernel/docs/EXT4-PROFILE.md`.
* **Criterion:** an image written by OSB must remain readable and `e2fsck`-clean with Linux
  tools; shutdown without sync must not corrupt it. **[OPEN]** no journal today, so crash
  consistency needs a design.
* **[PROPOSED]** Introduce a *volume* abstraction (A:, B:, ...) even with one disk. The
  filesystem layer works with normalised paths, inodes, permissions; drive letters, `\` and
  messages belong to the shell.
* Trash, `DEL` semantics and `.ORIG` bookkeeping are shell policy over checked primitives.

---

## 10. Program formats and execution

* **[CURRENT]** Native programs are OSBX images (`mkosbx.py`) loaded at `0x2000000`;
  `.EXE` search follows `PATH` (absolute entries, `;`-separated) as `name` then `name.EXE`.
* **[DECIDED]** `.OSL` scripts can be run directly: typing `Hello.osl` (or `Hello`, when no
  `.EXE` of that name exists) is equivalent to `OSLANG Hello.osl`. Rules in §12.3.
* Exit status goes through `EXIT` and `WAIT`; arguments through `GETARGS` (≤128 bytes).

---

## 11. Graphics and desktop (later)

XWIN stays a Ring-3 application. Until the GUI phase, the 40h–59h range remains kernel
syscalls documented in ABI.md; XWIN must be moved onto `osbkt` wrappers (no private crt0,
linker script or packaging). A display/input service replaces the range only when the IPC
phase delivers it.

---

## 12. OSLANG

### 12.1 Decisions **[DECIDED]**

* `OSLANG.EXE` lives in `A:\OSB\BIN`, runs in Ring 3, implements the **entire 0.73 spec**.
* Freestanding: no .NET BCL (`System.IO`, `System.Text`, `System.Xml`, LINQ, ...).
* The in-kernel OSLANG port is **removed completely**.
* Network features (`OSB.NET`, HTTP) are out of scope until the kernel has networking for
  Ring 3; they must fail with a defined, catchable runtime error rather than be absent.

### 12.2 Architecture **[PROPOSED]**

```
script.osl ──► OSLANG.EXE ──► Engine (lexer · parser · AST · interpreter · heap/GC)
                                 └─► Library (OSL.CONSOLE/FILE/MATH/JSON/CSV/XML/CNF/I18N/APP)
                                       └─► Host layer ──► osbkt ──► kernel
```

* One engine source tree, compiled twice: for the **host** (normal .NET, fast conformance
  tests) and for **OSB** (freestanding, `OSLANG.EXE`). The host layer is the only code that
  differs.
* The existing BCL implementation (`Osb.Lang`) stays as a *reference/oracle* until the new
  engine passes the conformance corpus; then archive it.
* The engine needs a managed-object heap (strings, arrays, objects, closures). The
  freestanding runtime has no GC, so the engine owns one: tagged values, precise non-moving
  mark-sweep, explicit root stack. Never hold raw heap pointers in C# locals across
  allocations.
* Strings, numbers and dates need their own formatting/parsing/calendar code
  (no `ToString`, `Math`, `DateTime`).
* The spec is the source of truth. The conformance corpus is written from the spec and tagged
  by spec section; neither implementation is assumed correct.

### 12.3 Direct execution of `.OSL` **[DECIDED, details PROPOSED]**

* The shell resolves `X.osl` (explicit extension), or `X` when no `X.EXE` exists but `X.OSL`
  does, to `A:\OSB\BIN\OSLANG.EXE X.osl <args>`.
* The interpreter path is **fixed**, never taken from `PATH`: otherwise `SUDO Hello.osl`
  would execute an attacker-controlled `OSLANG.EXE` as root.
* The script needs **read** permission only (the interpreter opens it with the caller's uid;
  the kernel enforces it). No execute bit, no shebang, no setuid.
* Exit code of the script = exit code of the process; `&` and `SUDO` behave as for any `.EXE`.
* Script arguments share the 128-byte `GETARGS` limit. **[OPEN]** raise it?

### 12.4 Kernel/SDK prerequisites for the full spec

Growable memory (§8); a terminal API for `OSL.CONSOLE`/`OSL.APP` (cursor position/size,
raw key events with F-keys, Home/End, PgUp/PgDn, Ins/Del, modifiers, from PS/2 **and** serial);
complete file/directory I/O with write handles and seek; date/time and ticks (exist).

---

## 13. IPC and services **[PROPOSED]**

Order matters. Do not start IPC before the cleanup of §16 and the OSLANG prerequisites.

1. **Stabilise the ABI** (cleanup, tests, CI checks).
2. **Design IPC** on paper first: endpoints, handles/permissions, small synchronous messages,
   per-process queues, timeouts/cancel, controlled shared pages for bulk data. A kernel pipe
   object is a natural first primitive and replaces the old temp-file pipes.
3. **Extract one small, stateless service first** (console/input, or network/PCI), not ext4.
4. **Bootstrap problem:** the kernel currently loads `SHELL.EXE` from ext4. A filesystem
   service needs a boot image (initrd) containing it. Solve this before extracting the FS.
5. Filesystem service + block service; permission checks move with it.
6. Failure test: killing a service must not bring the kernel down.

---

## 14. Roadmap and acceptance

| Stage | Content | Status |
|---|---|---|
| A. Foundation | split repos, ABI.md, GETABI, hardening, ring-3 shell with login, line editor, env, file commands, jobs | **mostly done** (`phase-3`) |
| B. Cleanup | path-based file syscalls with real error codes, remove command-line syscalls, trash policy to shell, 400-line rule, ABI/doc sync, test levels, kernel README/ROADMAP rewrite | **next** |
| C. OSLANG | prerequisites (§12.4), conformance corpus, engine, library, `OSLANG.EXE`, `.OSL` dispatch, kernel port removal | planned (`OSLANG-PLAN.md`) |
| D. Security | hashed passwords, SMEP/SMAP, memory limits, audit of sudo/session paths | planned |
| E. Services | IPC, first service, FS/block service, crash-isolation test | later |
| F. GUI | display/input service, XWIN on `osbkt`, toolkit | later |

**Integration tests required before leaving stages C–E:**

| Test | Expected |
|---|---|
| Ring-3 app reads a file via `osbkt` | OK |
| App reads a forbidden file | denied (-3), kernel-side |
| App touches another process's memory | isolated fault, system alive |
| Two programs run concurrently | both complete |
| Two users on one volume | permissions respected |
| File created, system rebooted | file preserved, fsck clean |
| `Hello.osl` typed at the prompt | runs in Ring 3 via `OSLANG.EXE` |
| Script issues a raw syscall | no extra privilege |
| A service is killed | kernel keeps running |

---

## 15. Open decisions

1. **Microkernel vs hybrid by design:** keep the label "microkernel direction" with staged
   extraction (§13), or declare "small hybrid kernel with a stable ABI" as the end state?
2. Raise the 128-byte argument cap (§12.3)?
3. Which service is extracted first, and when to start IPC design (§13)?
4. ext4 profile and crash-consistency strategy (§9).
5. Hashing scheme for `PASSWD` (§7).
6. OSLANG string model (encoding, indexing unit) — settled in the OSLANG plan's phase O0.

---

## 16. Known divergences (fix list)

* `osbkt/docs/ABI.md` header says version 6; code is 7. Add a CI equality check.
* Kernel syscalls 26h–29h, 61h, 62h wrap legacy command-line functions, print to VGA and
  discard error codes.
* Several source files exceed 400 lines (`osb.shell/Program.cs`, `osbkt/Syscall.cs`,
  `osb.kernel/Cpu/Syscalls.cs`, `Processes/Scheduler.cs`, `osb.oslang` dispatchers/lexer).
* `osb.kernel/ROADMAP.md` and `README.md` still describe the legacy in-kernel shell and say
  ext4 writes are missing.
* Shell command table and HELP still list removed commands.
* OSLANG versions disagree: spec/README 0.73, `Osb.Lang` constant 0.72, VS Code extension 0.62;
  `OSLANG-GAP.md` and `Osb.Lang.csproj` still reference the pre-split `src/` layout.
* `PASSWD` plaintext (debt, §7).

---

## 17. Document map

| Topic | Document |
|---|---|
| This architecture | `OSB-ARCHITECTURE.md` (kept in `osb.kernel`, linked from every repo) |
| Syscall contract | `osbkt/docs/ABI.md` |
| Shell migration status | `osb.shell/docs/MIGRATION.md` |
| Language | `osb.oslang/OSLANG-SPEC.md` and per-version specs |
| OSLANG delivery plan | `OSLANG-PLAN.md` |
| ext4 profile | `osb.kernel/docs/EXT4-PROFILE.md` (to write) |
