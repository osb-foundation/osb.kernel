# OSB IPC Architecture Review — Feasibility and Design Decisions

**Card:** 3.1 (review of the specification)
**Reviews:** `osb.kernel/docs/OSB-IPC-ARCHITECTURE-AND-REQUIREMENTS.md` (commit `181ce85`)
**Document type:** Architecture review — **not** an implementation
**Status labels:** **[IMPLEMENTED]** verified in source · **[FEASIBLE]** design works under current constraints · **[NEEDS-INFRA]** requires new infrastructure · **[DEFERRED]** out of initial scope · **[RISK]** unresolved risk

> This review evaluates the proposed IPC architecture against the **actual** kernel.
> It does not implement IPC, change syscalls/ABI, or modify the Scheduler. Where the
> original specification needs correction, the change is listed in Section 8 as a
> **proposed amendment** and is **not** applied to the original document.

---

## 1. Method and scope

Source inspected (all paths relative to `osb.kernel/` unless noted):

- `Memory/PhysicalMemory.cs`, `Memory/Layout.cs`, `Kernel.cs` (allocator)
- `Sync/SpinLock.cs`, `Cpu/Interrupts.cs`, `Cpu/Syscalls*.cs`, `boot.S`, `sched.S`, `sync.S`
- `Processes/Scheduler*.cs`, `Processes/AddressSpace.cs`, `Processes/PidAllocator.cs`, `Processes/EmbeddedRegistry*.cs`
- `osbkt/docs/ABI.md`, `osbkt/Syscall.cs`

The five review objectives (A–E) each get: evidence, feasibility, alternatives,
recommendation (classified), and consequences/risks.

---

## 2. Executive summary

**Overall verdict: the architecture is feasible under current constraints, with three
material refinements** (Section 8). The single most important discovery is that the
kernel's syscall entry is a **DPL3 interrupt gate** (`Layout.IdtUserGate = 0xEE`,
`Layout.cs:27`; installed at `Interrupts.cs:52-56`), so **every non-blocking IPC
syscall runs with IF=0 and is already atomic with respect to the 100 Hz timer and
other tasks** on this single-core kernel. This removes the need for a new broker lock
for syscall-context serialization and reshapes the atomic-publish design.

| Area | Verdict | Classification |
|---|---|---|
| A. Immutable payload sharing across private address spaces | Feasible as **kernel-owned immutable payload + copy-on-receive**; true shared user page is not | Safe (copy) / Deferred (shared page) |
| B. Atomic publish under allocator + SpinLock M0 | Feasible with **two-phase (allocate outside, commit inside)**; allocation must not run under a held lock | Safe |
| C. Blocking Receive | Feasible with **zero Scheduler changes** by reusing the `WaitFor` `halt()` loop; non-blocking is simpler | Safe |
| D. Minimum ABI surface | Feasible in free AH range with a **v9→v10 bump**; no existing call breaks | Needs-Infra (ABI bump) |
| E. Resource limits | Feasible; safe initial limits derived from existing structures | Safe |

---

## 3. A. Memory model — payload ownership, copying, isolation

### A.1 Existing implementation evidence

- **Private per-process address spaces.** Each Ring 3 process gets a private 2 MiB
  block at `UserBase` (`AddressSpace.cs:34-96`); only `pd[16]` is retargeted
  (`AddressSpace.cs:83-90`); the kernel half is supervisor-only (U/S=0)
  (`AddressSpace.cs:70-71`). `UserBase=0x2000000`, `UserLimit=0x2200000`
  (`Layout.cs:42-43`). **[IMPLEMENTED]**
- **No shared-memory primitive.** There is no syscall or mapping facility that places
  a page U/S=1 into two processes; the only user mapping is the private block plus
  MEMGROW blocks (`Scheduler.Memory.cs`). **[IMPLEMENTED]**
- **Kernel allocator.** `Kernel.OsAlloc/OsFree` round to frames from the bitmap
  allocator (`Kernel.cs:199-218`); the kernel is identity-mapped (phys == virt in
  Ring 0). **[IMPLEMENTED]**
- **Allocator is self-locking.** `PhysicalMemory.Alloc/Free` take the M0 SpinLock
  internally (`PhysicalMemory.cs:211-218, 271-276`); the scan is O(frames) worst case
  (`AllocLocked`, `PhysicalMemory.cs:220-240`). **[IMPLEMENTED]**

### A.2 Feasibility

- **Kernel-owned immutable payload + per-delivery record reference + copy-on-receive:
  [FEASIBLE] and Safe.** The payload is copied once into kernel frames at commit; each
  delivery record references it; `Receive` copies into the caller's user buffer. This
  respects isolation because Ring 3 never reads kernel memory (U/S=0) and never
  receives a kernel pointer.
- **True shared user page (same physical page mapped U/S=1 in N processes):
  [NEEDS-INFRA].** No mapping facility exists; building one means new page-table
  surgery and a new U/S=1 shared window, with guard/aliasing risks. Not justified for
  v1.
- **Per-subscriber kernel copies (N copies in kernel memory): [FEASIBLE] but
  wasteful.** Simpler to reason about, but multiplies kernel memory by subscriber
  count and complicates quota accounting.

### A.3 Alternatives

| Option | Isolation | Kernel memory | Ring 3 change | Verdict |
|---|---|---|---|---|
| Shared immutable payload + copy-on-receive | Strong | 1× payload + N records | none | **Recommended** |
| Per-subscriber kernel copies | Strong | N× payload | none | Wasteful |
| Shared user page mapping | Strong if done right | 1× payload | new mapping ABI | Deferred (infra) |

### A.4 Recommendation — **Safe for initial implementation**

Adopt **kernel-owned immutable payload + copy-on-receive**. Allocation of the payload
and delivery records uses `OsAlloc`; failure returns `ENOMEM` and changes nothing.
Ownership/lifetime: the payload is owned by the broker and freed when the **last**
delivery referencing it is released (ACK/terminal/cancel/owner-exit); delivery records
are owned by the subscriber queue and freed on release.

### A.5 Consequences and unresolved risks

- **[RISK]** Copy-on-receive doubles peak memory transiently (payload in kernel +
  copy in user buffer). Bounded by the payload cap (Section 7, Q-M2).
- **[RISK]** `Receive` copies to user memory; the destination buffer must be validated
  with `UserPtr` (`Syscalls.cs:127-135`) and the copy must be bounded by the payload
  length. A bad length must not fault Ring 0.
- **[DEFERRED]** Shared user pages remain deferred until a mapping facility exists.

---

## 4. B. Atomic multi-subscriber publication

### B.1 Existing implementation evidence

- **Syscall context is interrupt-disabled.** Vector `0x21` is installed with
  `IdtUserGate = 0xEE` — a **DPL3 interrupt gate** (`Layout.cs:27`,
  `Interrupts.cs:52-56`). An interrupt gate clears IF on entry, so `Syscalls.Handle`
  runs with IF=0; the 100 Hz timer (`Interrupts.cs:142-148`) cannot fire mid-syscall.
  **[IMPLEMENTED]**
- **Single core, no SMP.** `EmbeddedRegistry.cs:9-13`; `SpinLock.cs:20-21`. **[IMPLEMENTED]**
- **SpinLock M0 is non-recursive and IRQ-hostile.** Recursive acquisition of the same
  lock deadlocks (`SpinLock.cs:16`); IRQ handlers must not take M0 (`SpinLock.cs:17`);
  nested **distinct** locks are allowed (`SpinLock.cs:18-19`). **[IMPLEMENTED]**
- **Allocator takes M0.** `PhysicalMemory.Alloc/Free` acquire M0 internally
  (`PhysicalMemory.cs:214, 273`). **[IMPLEMENTED]**

### B.2 Feasibility

- **Atomicity of a non-blocking publish is already provided by IF=0.** Because the
  whole publish syscall runs with IF=0 on one core, **no other broker operation can
  interleave**; the timer cannot preempt. The linearization point is the syscall's
  execution itself. **[FEASIBLE]**
- **Allocation inside the critical section is unsafe if a lock is held.** If the broker
  acquires a lock and then calls `OsAlloc`, and that lock is the M0 word, the nested
  M0 acquisition **deadlocks** (`SpinLock.cs:16`). Even with a distinct broker lock,
  allocating under IF=0 holds interrupts off for the O(frames) bitmap scan
  (`AllocLocked`, `PhysicalMemory.cs:220-240`). **[FEASIBLE only with two-phase]**
- **Two-phase (allocate outside, commit inside) is [FEASIBLE] and Safe.** Phase 1
  allocates payload + records with no broker lock held (still IF=0, still atomic vs.
  the timer). Phase 2 is pure bookkeeping (counters, links) with no allocation. If
  phase 2 re-validation fails, phase 1 allocations are freed (also outside any lock).

### B.3 How reservations stay valid

On this kernel "reservation" is enforced by **IF=0 mutual exclusion**, not by a lock a
concurrent task could contend for: while the publish syscall runs, no other task or
IRQ touches broker state. Capacity counters are incremented in phase 2; since nothing
interleaves, the reservation cannot be consumed by another publisher before commit.
If future SMP or IRQ-context broker entry is introduced, a **distinct** broker lock
word (nested with M0, never the M0 word itself) plus the IRQ-safety rule
(`SpinLock.cs:17`) becomes mandatory — **[NEEDS-INFRA]** at that point.

### B.4 Rollback after partial preparation

Phase 1 may partially succeed (some records allocated, one allocation fails). Rollback
frees every phase-1 allocation via `OsFree` (`Kernel.cs:213-218`); `Free` is
idempotent and rejects misaligned/out-of-range frees (`PhysicalMemory.cs:278-291`), so
rollback is safe. No phase-2 state is visible until commit, so a failure leaves **no
partial fan-out** (matches D10/D11).

### B.5 Owner termination / subscription change during publication

Because publish runs to completion under IF=0, an owner cannot terminate or a
subscription cannot change **during** the publish syscall. The ordering is decided by
which syscall runs first: if `DestroyTopic`/owner-exit runs first, the publish sees an
invalid topic → `ENOENT`; if publish runs first, its deliveries are committed and then
cancelled by cleanup (Section 6). **[FEASIBLE]**

### B.6 Exact linearization point

The commit step of phase 2 (counter increments + record linking) is the linearization
point; it is a bounded, allocation-free, IF=0 sequence. **[FEASIBLE]**

### B.7 Recommendation — **Safe for initial implementation**

Two-phase publish: **allocate (payload + per-subscriber records) outside any lock →
re-validate topic/subscribers → commit bookkeeping (no allocation) → on any failure
free phase-1 allocations and return `QueueFull`/`ENOENT`/`EACCES`.** Do **not** add a
broker lock for syscall-context serialization; rely on IF=0. Document the SMP/IRQ
precondition.

### B.8 Consequences and unresolved risks

- **[RISK]** The design's correctness depends on the interrupt-gate property; if a
  future change makes the syscall gate a **trap** gate (IF preserved) or adds SMP, the
  "no lock needed" claim breaks. This must be asserted in a test/comment.
- **[RISK]** `AllocLocked` is O(frames); doing it in phase 1 (still IF=0) is a bounded
  but nonzero interrupt-off window. Acceptable for small payloads; revisit if payload
  sizes grow.
- **[DEFERRED]** A general broker lock for SMP is deferred until SMP exists.

---

## 5. C. Receive, wait/wakeup, and Scheduler integration

### C.1 Existing implementation evidence

- **Halt-based wait, no wait queue.** `Scheduler.WaitFor` loops
  `while state==Ready { halt(); ... }` (`Scheduler.cs:312-327`). **[IMPLEMENTED]**
- **`halt()` = `sti; hlt; ret`.** It enables interrupts and sleeps until the next
  interrupt (`boot.S:290-295`; `NativeMethods.cs:26`). The timer then drives
  `Scheduler.Preempt()` (`Interrupts.cs:142-148`). **[IMPLEMENTED]**
- **No per-slot wait channel / wakeup primitive exists.** **[IMPLEMENTED]**
- **Termination path.** `KILL`→`Release` (`Syscalls.Process.cs:118-129`,
  `Scheduler.cs:264-280`); `WAIT`→`Reap` (`Scheduler.cs:283-327`). **[IMPLEMENTED]**

### C.2 Feasibility

- **Non-blocking Receive: [FEASIBLE], zero Scheduler change.** The whole syscall runs
  under IF=0; it dequeues or returns empty/`EAGAIN`. No new primitive.
- **Blocking Receive via the `halt()` loop: [FEASIBLE], zero Scheduler change.** Mirror
  `WaitFor`: `while (queue empty && slot alive) { halt(); if CtrlC return EINTR; }`.
  `halt()` sets IF=1, so the timer preempts and publishers run; the loop re-checks the
  condition after each wake, so **no lost wakeup** (condition checked before and after
  `halt`). Enqueue happens in a publisher's IF=0 syscall, so the queue is always
  consistent when observed. **[FEASIBLE]**

### C.3 Wakeup races, interrupts, termination

- **Lost-wakeup:** avoided by the check-then-halt-then-check loop (same pattern as
  `WaitFor`).
- **Interrupts:** `halt()` deliberately sets IF=1; after it returns, IF stays 1 until
  the syscall returns to user mode. The loop body must therefore tolerate preemption
  and re-check slot liveness each iteration.
- **Termination during blocking Receive:** the loop must detect that the caller's slot
  was released (state no longer the caller's) and return `EINTR`/error; `WaitFor`
  already models this by testing slot state (`Scheduler.cs:316-325`).

### C.4 Blocking vs non-blocking for the first implementation

| Aspect | Non-blocking | Blocking (halt-loop) |
|---|---|---|
| Scheduler change | none | none |
| CPU use | caller polls | yields via `hlt` |
| Lost-wakeup risk | none | none (loop re-checks) |
| Complexity | lowest | low (mirrors `WaitFor`) |

### C.5 Recommendation — **Safe for initial implementation**

Ship **non-blocking `Receive` (+ optional `IPC_POLL`) first** (truly zero Scheduler
interaction). Add **blocking `Receive` using the `WaitFor` `halt()` loop** only if
profiling shows busy-poll is a problem — still **zero Scheduler changes**. Do **not**
build a new wait-queue/wakeup primitive for v1.

### C.6 Consequences and unresolved risks

- **[RISK]** Blocking Receive holds the task in a halt-loop; Ctrl+C must be wired to
  return `EINTR` (as `WaitFor` does) or the task is unkillable while blocked.
- **[RISK]** The loop must re-validate the caller slot each iteration; otherwise a
  killed-and-reused slot could observe a foreign queue.
- **[DEFERRED]** A true wait-channel/wakeup is deferred unless a non-halt blocking
  model is required.

---

## 6. D. ABI and osbkt surface

### D.1 Existing implementation evidence

- **INT 21h, AH=function, RDI/RSI/RDX/RCX args, RAX result** (`ABI.md:145-157`;
  `Syscalls.cs:24-96`). **[IMPLEMENTED]**
- **Pointer rule A7** via `UserPtr` (`ABI.md:176-183`; `Syscalls.cs:127-135`).
  **[IMPLEMENTED]**
- **Byte-string convention B1** (`ABI.md:168-175`). **[IMPLEMENTED]**
- **Error codes** `-1..-8`, `-1/-2` frozen (`ABI.md:159-167`). **[IMPLEMENTED]**
- **Version** `AbiVersion = 9` (`Syscalls.cs:22`); `GETABI` (82h). **[IMPLEMENTED]**
- **Free AH values** (derived from `Syscalls.cs:48-93` + partials): `0x02-0x08`,
  `0x0A-0x0F`, `0x12-0x1F`, `0x2D-0x2F`, `0x31-0x3F`, `0x5A-0x5F`, `0x62`,
  `0x83-0xFF`. **[IMPLEMENTED]**

### D.2 Minimum operations

Necessary and sufficient for the confirmed model:

| Op | Args (proposed) | Result |
|---|---|---|
| `CreateTopic` | RDI=name ptr, RSI=len, RDX=flags | TopicId (u64) / error |
| `DestroyTopic` | RDI=TopicId | 0 / error |
| `Subscribe` | RDI=TopicId | 0 / error (idempotent) |
| `Unsubscribe` | RDI=TopicId | 0 / error |
| `Publish` | RDI=TopicId, RSI=payload ptr, RDX=len | MessageId / `QueueFull`/`ENOENT`/`EACCES` |
| `Receive` | RDI=TopicId, RSI=out record, RDX=buf, RCX=cap, AL=block | count / `EAGAIN`/error |
| `Ack` | RDI=DeliveryId | 0 / error |
| `Acl` | RDI=TopicId, RSI=subject PID, RDX=perm, RCX=subop | 0 / error |

ACL management is folded into one `Acl` op with a sub-operation in RCX (grant/revoke/
list) to avoid a per-permission AH.

### D.3 Encoding and compatibility

- Handles (`TopicId`, `DeliveryId`) are u64 passed **by value** in a register
  (index+generation, `OSLANG-0.8-SPEC §2` precedent). `MessageId` is u64.
- Payload is `(ptr,len)` with `UserPtr` validation; strings/names are byte strings (B1).
- **No existing syscall changes.** IPC uses previously-free AH values (e.g. `0x83-0x8F`);
  existing programs never call them and are unaffected.

### D.4 Recommendation — **Needs-Infra (ABI bump) when implemented**

Allocate IPC AHs in the free range, **bump `AbiVersion` to 10**, and update
`osbkt/docs/ABI.md` in the same change (P5). Add `Osb.Ipc` wrappers in osbkt. This is
**infrastructure work**, not part of this review, and must not be done piecemeal.

### D.5 Consequences and unresolved risks

- **[RISK]** Migration: any program that checks `GETABI` and requires IPC must require
  `≥ 10`; programs that don't use IPC are unaffected. The ABI.md header/version must
  move in lockstep (existing divergence noted in `OSB_ARCHITECTURE.md §16`).
- **[RISK]** Handle exhaustion and generation wrap must be defined (Q-H1) before the
  ABI freezes.
- **[DEFERRED]** Final AH assignment and struct layouts are deferred to the
  implementation card.

---

## 7. E. Resource limits

### E.1 Basis in existing structures

- **Task slots:** `TaskCount = 8` (`Layout.cs:162`) → at most **7** Ring 3 PIDs plus
  the console; distinct subscriber PIDs are bounded by live tasks. **[IMPLEMENTED]**
- **Physical memory:** default 64 MiB guest (`Layout.cs:98`; `build.sh` `-m 64M`,
  `OSB_MEM` override). **[IMPLEMENTED]**
- **User block:** 2 MiB per process (+ up to 8 MEMGROW blocks = 16 MiB)
  (`Layout.cs:42-43,103`; `Scheduler.cs:46-51`). **[IMPLEMENTED]**
- **Payload precedent:** `WRITEFILE` already allows up to 64 KiB (`ABI.md:141`).
  **[IMPLEMENTED]**
- **Registry growth:** `ProgramRegistry` grows geometrically via `OsAlloc`
  (`EmbeddedRegistry.cs:1-7`) — the model for a growable topic table. **[IMPLEMENTED]**

### E.2 Recommended initial limits (with basis)

| Resource | Recommended initial limit | Basis |
|---|---|---|
| Task slots / distinct subscriber PIDs | 8 (existing) | `Layout.TaskCount` |
| Topics (system + dynamic) | 64 | Bounded descriptor array; 64×small desc ≪ 1 frame; growable later |
| Subscriptions per topic | 8 | ≤ live PIDs (7) + kernel; one per `(PID,Topic)` (D25) |
| Total subscriptions | 256 | 64 topics × ~4 avg; bounded kernel memory |
| Queue entries per subscriber | 8 | Small ring; bounds one queue's memory; `ENOMEM`/`QueueFull` beyond |
| Payload bytes per message | 64 KiB | Matches `WRITEFILE` precedent; fits 2 MiB user block copy |
| ACL entries per topic | 8 | PID-targeted (Q-A2); ≤ live PIDs + kernel |
| Total IPC kernel memory | 1 MiB | ~1.5% of 64 MiB default; hard cap via frame accounting |

All limits are **initial, conservative, and derived**; each returns `ENOMEM`/`QueueFull`
rather than faulting. They are not invented in isolation — each cites a structure above.

### E.3 Recommendation — **Safe for initial implementation**

Adopt the table above as **initial** limits, enforce them in the broker with explicit
`ENOMEM`/`QueueFull`, and expose current usage via a diagnostic counter (mechanism
only, P1). Revisit after measurement.

### E.4 Consequences and unresolved risks

- **[RISK]** 64 topics / 256 subscriptions may be low for a future service-heavy
  system; the growable-registry pattern allows raising them without ABI change.
- **[RISK]** Total IPC memory cap must be reconciled with MEMGROW and user-block
  accounting so IPC cannot starve process memory.
- **[DEFERRED]** Dynamic tuning and per-uid quotas are deferred.

---

## 8. Proposed amendments to the original specification (not applied)

These correct or refine the original spec; they are **proposals** for the owner.

1. **AM-1 (Atomicity/locking).** The original says "single broker lock (M0-style)".
   Correction: on this single-core kernel the INT 21h **interrupt gate** (IF=0) already
   serializes syscall-context broker operations; **no new broker lock is required for
   v1**, and the M0 lock must **not** be acquired recursively (the allocator owns it).
   A distinct broker lock + IRQ-safety is required only if SMP or IRQ-context entry is
   added. (Evidence: `Layout.cs:27`, `Interrupts.cs:52-56`, `SpinLock.cs:16-21`,
   `PhysicalMemory.cs:214,273`.)
2. **AM-2 (Publish phases).** Replace "reserve resources inside the critical section"
   with **two-phase**: allocate payload+records **outside** any held lock, then commit
   bookkeeping with **no allocation**; roll back phase 1 on failure. (Evidence:
   `PhysicalMemory.cs:220-240`, `Kernel.cs:199-218`.)
3. **AM-3 (Receive).** The original leaves blocking Receive open (Q-S1). Correction:
   blocking Receive needs **no new Scheduler primitive** — reuse the `WaitFor`
   `halt()` loop; non-blocking is the recommended first step. (Evidence:
   `Scheduler.cs:312-327`, `boot.S:290-295`.)
4. **AM-4 (Terminology).** "Linearization point" should be stated as the phase-2
   commit, and the document should record the **interrupt-gate precondition** as an
   explicit invariant with a test (T-new) that asserts IF=0 on syscall entry.

No other section of the original spec is contradicted by this review.

---

## 9. Consolidated classification

| Recommendation | Class |
|---|---|
| A: kernel-owned immutable payload + copy-on-receive | **Safe (initial)** |
| A: shared user page mapping | Deferred |
| B: two-phase publish (allocate outside, commit inside) | **Safe (initial)** |
| B: rely on IF=0 for syscall serialization (no broker lock) | **Safe (initial)** |
| B: broker lock for SMP/IRQ entry | Needs-Infra (when SMP) |
| C: non-blocking Receive first | **Safe (initial)** |
| C: blocking Receive via `halt()` loop | **Safe (initial)** |
| C: new wait-queue/wakeup primitive | Deferred |
| D: IPC AHs + ABI v9→v10 + ABI.md | Needs-Infra |
| E: initial resource limits (Section 7) | **Safe (initial)** |
| E: dynamic/per-uid quotas | Deferred |

---

## 10. Evidence index

| Claim | Evidence |
|---|---|
| Syscall gate is DPL3 interrupt gate (IF=0) | `Memory/Layout.cs:27`; `Cpu/Interrupts.cs:52-56` |
| Syscall dispatch | `Cpu/Syscalls.cs:24-96`; `Cpu/Interrupts.cs:101-110` |
| Single core, no SMP | `Processes/EmbeddedRegistry.cs:9-13`; `Sync/SpinLock.cs:20-21` |
| SpinLock non-recursive, IRQ-hostile, nested distinct OK | `Sync/SpinLock.cs:16-19` |
| Allocator takes M0; O(frames) scan | `Memory/PhysicalMemory.cs:211-240,271-291` |
| Kernel allocator | `Kernel.cs:199-218` |
| Private address spaces, no shared mem | `Processes/AddressSpace.cs:34-96`; `Memory/Layout.cs:42-43` |
| Halt-based wait | `Processes/Scheduler.cs:312-327`; `boot.S:290-295`; `Hardware/NativeMethods.cs:26` |
| Termination/reap | `Processes/Scheduler.cs:264-327`; `Cpu/Syscalls.Process.cs:118-129` |
| Task slots = 8 | `Memory/Layout.cs:162` |
| 64 MiB default memory | `Memory/Layout.cs:98`; `build.sh` (`-m ${OSB_MEM:-64M}`) |
| 64 KiB payload precedent | `osbkt/docs/ABI.md:141` |
| ABI conventions/errors | `osbkt/docs/ABI.md:145-183`; `Cpu/Syscalls.cs:22,127-135` |
| Growable registry pattern | `Processes/EmbeddedRegistry.cs:1-7` |
| Index+generation handles | `osb.oslang/OSLANG-0.8-SPEC.md §2` |

---

## 11. Non-goals (restated)

No IPC runtime implementation, no syscall/ABI modification, no Scheduler
modification, no change to existing behavior, no Trello closure or completion claims.
This review is documentation only; the original specification is unchanged (amendments
are proposed in Section 8).