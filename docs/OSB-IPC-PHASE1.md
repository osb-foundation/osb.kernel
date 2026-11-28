# OSB IPC — Phase 1 Kernel Core: Implementation Notes

**Card:** 3.1 (implementation, Phase 1)
**Status:** Implemented (internal core) — **not** exposed via syscall/ABI
**Authoritative inputs:** `OSB-IPC-ARCHITECTURE-AND-REQUIREMENTS.md`, `OSB-IPC-ARCHITECTURE-REVIEW.md`

This note records what Phase 1 actually implements, what is deliberately deferred,
and which unresolved decisions shaped the result. It is not an ABI contract.

---

## 1. Files

| File | Responsibility |
|---|---|
| `Processes/Ipc.cs` | Data model, index+generation handles, limits, resource accounting, reset, shared helpers. |
| `Processes/Ipc.Registry.cs` | Topic create/destroy, subscribe/unsubscribe, descriptor growth (`OsAlloc`). |
| `Processes/Ipc.Publish.cs` | Two-phase atomic publish (prepare → commit, rollback). |
| `Processes/Ipc.Deliver.cs` | Non-blocking Receive, explicit ACK, retry/terminal, `AdvanceRetries`. |
| `Processes/Ipc.Cleanup.cs` | `OnProcessExit` (subscriptions, deliveries, dynamic topics). |
| `tests/unit/Ipc*.cs` | Host unit tests + test-only accessors (test assembly only). |

No production file exceeds 400 lines. No existing behavior or ABI was modified.

## 2. Implemented behavior

- **Topics** — growable registry; system topics (Kernel/pid 0 only) vs dynamic
  topics owned by the creator's actual PID. Index+generation handles; stale handles
  rejected.
- **Subscriptions** — one per `(PID, Topic)`; repeated `Subscribe` is idempotent.
- **Atomic publish** — two-phase: validate + snapshot + capacity pre-check →
  allocate payload (outside any lock) → ensure descriptor capacity → **commit**
  (re-validate, link all deliveries, assign MessageId). Any failure rolls back with
  **no partial fan-out**; `QueueFull` is explicit and distinct from `ENOMEM`.
- **Message ID vs Delivery ID** — `MessageId` is a monotonic `ulong`; `DeliveryId`
  is an index+generation handle. A fan-out yields one MessageId and N DeliveryIds.
- **Receive** — non-blocking; copies the immutable payload into the caller's buffer;
  `Queued → InFlight`; does **not** ACK.
- **ACK** — explicit; validates ownership (foreign ACK → `EACCES`); frees capacity.
- **Retry** — transient failure with `Attempts < 3` moves the **same** Delivery ID to
  `RetryPending` (no extra queue slot, capacity unchanged); `AdvanceRetries(now)`
  requeues due deliveries; the 3rd failure is terminal (tombstone) and releases all
  capacity. At-least-once; no NACK.
- **Cleanup** — `OnProcessExit(pid)` cancels the process's deliveries, removes its
  subscriptions, destroys its dynamic topics, and preserves system topics.
- **Accounting** — live payload bytes, live messages, active deliveries; all return
  to zero after full cleanup.

## 3. Concurrency model (as built)

No broker lock is taken. This is correct for Phase 1 because a non-blocking IPC
syscall runs with `IF=0` (DPL3 interrupt gate) on a single core, so no other broker
operation or the timer can interleave (review AM-1). `Kernel.OsAlloc` (which takes
the M0 lock internally) is never called while holding any lock. If SMP or IRQ-context
entry is added later, a distinct broker lock + the IRQ-safety rule become mandatory.

## 4. Limits — normative vs proposal

**Normative (existing, not changed by this card):** `Layout.TaskCount = 8` (task
slots), `AbiVersion = 9`, the frozen ABI error codes `-1..-8`, 64 MiB default guest.

**Phase-1 internal proposals** (`IpcLimits`, not ABI guarantees, configurable):
`MaxTopics=64`, `MaxSubscriptions=256`, `QueueDepth=8`, `MaxPayload=64 KiB`,
`MaxIpcMemory=1 MiB`, `MaxAttempts=3` (normative R1), `RetryBaseTicks=10`.
Subscription capacity is **not** derived from the task-slot count.

## 5. Deferred (with reason)

- **Timer hook for retry** — no callback timer exists; `AdvanceRetries(now)` is
  implemented and tested but is not wired to `Pit.Tick`. Wiring it is a Scheduler
  change, out of Phase 1 scope. **Deferred.**
- **Scheduler cleanup hook** — `OnProcessExit` is implemented and tested but not yet
  called from `Scheduler.Release/Reap`. Wiring is a later, separate change.
  **Deferred.**
- **Blocking Receive** — not implemented (non-blocking only), per scope.
- **Ring 3 ABI / OSBKT** — not exposed; see the ABI v10 integration plan.
- **ACL policy** — only the decided rules are enforced (Ring 3 cannot publish to
  system topics; dynamic topic owner-only publish). Full ACL (Q-A1/A2/A3) is an
  isolated placeholder, not a public contract. **Blocked by open decision.**

## 6. Unresolved decisions that shaped Phase 1

| Decision | Effect on Phase 1 |
|---|---|
| Q-A1/A2/A3 (ACL) | Minimal owner/system authorization only; no PID ACL table. |
| Q-S1 (blocking Receive) | Non-blocking only. |
| Q-T1 (timeout/backoff) | Fixed linear backoff constant; no timer. |
| Q-H1 (ID exhaustion) | `ulong` MessageId; index+generation handles; exhaustion → `ENOMEM`. |
| Q-D3 (dedup) | Consumer responsibility; broker guarantees MessageId uniqueness. |
| Q-ABI (syscall split) | No syscalls added. |

## 7. Test coverage (host, `dotnet run tests/unit`)

Topic create/lookup/ownership, system-topic protection, subscription idempotency,
multiple subscribers, MessageId≠DeliveryId, atomic fan-out, QueueFull with no partial
publication, allocation-failure rollback (payload-alloc and growth-alloc), non-blocking
empty receive, receive-without-ack, explicit ACK, invalid/foreign ACK, retry with 3
attempts and no extra slot, terminal cleanup, process-termination cleanup, stale-handle
rejection, and resource accounting returning to zero. **378** total assertions pass
(`IPC: ALL TESTS PASSED`).

## 8. Validation performed

- `dotnet build -c Release Osb.Kernel.csproj` — **succeeded** (0 errors).
- `./build.sh` (NativeAOT link + ISO) — **succeeded** (`osb.iso` produced).
- `dotnet run -c Release tests/unit/Osb.Kernel.Unit.csproj` — **all pass**.
- QEMU runtime: **not exercised** (no IPC syscall path exists yet; nothing to boot
  into IPC). Not claimed.