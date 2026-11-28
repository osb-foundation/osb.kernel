# OSB IPC — Phase 2: Lifecycle and Timer Integration

**Card:** 3.1 (implementation, Phase 2)
**Baseline:** `6077e5a` (Phase 1 core)
**Status:** Implemented — internal only; **no** syscall/ABI/OSBKT change.

Phase 2 wires the Phase 1 IPC core into the real process lifecycle and the timer.
It does not add any Ring 3 interface.

---

## 1. Process lifecycle integration

**Choke point:** `Scheduler.Release(slot)` is the single place a process's resources
are finally released. It is reached from `Reap` (normal exit via `WaitFor`), the KILL
syscall, session teardown (`Session.cs`), and user teardown (`Shell/Users.cs`).
`ExitCurrent` only marks a Zombie; the actual release happens at `Reap` → `Release`.

**Change:** `Release` now calls `Ipc.OnProcessExit(_tasks.pid[slot])` **first**, before
the address space and MEMGROW frames are freed, while the PID is still recorded:

- cancels the process's outstanding deliveries (as subscriber),
- removes its subscriptions,
- destroys the dynamic topics it owns,
- preserves Kernel-managed system topics.

This runs in syscall context (IF=0), so it is atomic with respect to other broker
operations; `OnProcessExit` allocates nothing and takes no lock. It is idempotent, so
a double release is harmless. `slot <= Console` is still excluded (the console/kernel
slot is never "terminated" through this path).

## 2. Timer integration

**Hook:** the IRQ0 (PIT, 100 Hz) branch in `Cpu/Interrupts.cs` now calls
`Ipc.AdvanceRetries(Pit.Ticks)` after `Pit.Tick()` and before `Scheduler.Preempt()`.

**Safety:** `AdvanceRetries` does not allocate and takes no lock. A non-blocking IPC
syscall runs with IF=0 (DPL3 interrupt gate), so it cannot interleave with the IRQ0
handler on this single core; the two are mutually exclusive. This respects the M0 rule
that IRQ handlers must not acquire the (allocator-owned) spinlock.

**Efficiency:** a `_retryPending` counter is maintained by `FailDelivery` (retry),
`AdvanceRetries` (requeue), and `ReleaseDeliveryLocked` (cancel). `AdvanceRetries`
returns immediately when the counter is zero, so the per-tick cost is O(1) when no
retry is waiting.

## 3. PID type correction

Phase 1 stored `Pid`/`OwnerPid` as `uint`, truncating the authoritative 64-bit PID
(`TaskTable.pid` is `fixed ulong[]`, never reused within a boot). Phase 2 widens all
IPC `Pid`/`OwnerPid` fields and parameters to `ulong` so the Kernel PID is used
without truncation. This is a targeted correction, not a redesign; behavior for the
small PIDs used in tests is unchanged.

## 4. Deferred timer behavior (unchanged)

`AdvanceRetries` is now driven by the timer, but the **backoff policy** (linear base
`RetryBaseTicks`) and the **transient-failure classification** remain Phase-1 internal
placeholders (open Q-T1). No blocking Receive, no NACK, no ABI.

## 5. Validation

- `dotnet build -c Release Osb.Kernel.csproj` — 0 errors.
- `./build.sh` (NativeAOT + ISO) — succeeded.
- Unit tests — all pass (IPC suite incl. new timer fast-path assertion).
- QEMU headless boot — reached `login:` and ran ~15 s under the 100 Hz timer with the
  new `AdvanceRetries` hook; no panic/fault (regression check only; no IPC syscall path
  exists to exercise).

## 6. Files touched

`Processes/Scheduler.cs` (Release hook), `Cpu/Interrupts.cs` (timer hook),
`Processes/Ipc*.cs` (ulong PID + `_retryPending` counter), `tests/unit/Ipc*.cs`
(ulong PID + fast-path assertion). No file exceeds 400 lines (`Scheduler.cs` = 399).

## 7. Non-goals

No syscall/ABI/OSBKT/Ring 3 implementation, no blocking Receive, no policy changes.
The ABI v10 integration remains a separate plan (`OSB-IPC-ABI-V10-INTEGRATION-PLAN.md`).