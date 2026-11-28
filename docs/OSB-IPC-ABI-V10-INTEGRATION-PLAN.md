# OSB IPC — ABI v10 and OSBKT Integration Plan

**Card:** 3.1 (integration plan)
**Status:** **ABI v10 IMPLEMENTED** (kernel dispatch + OSBKT + `ABI.md`, Phase 3).
**Authorization grants are DEFERRED/BLOCKED** (Q-A1 open) — see §9.
**Depends on:** Kernel IPC core (Phase 1, `Processes/Ipc*.cs`) — complete and validated.

This plan describes how to expose the internal IPC core to Ring 3 **without leaving
the Kernel and OSBKT inconsistent**. It must be executed as one atomic change set
(kernel dispatch + ABI bump + OSBKT + `ABI.md`) — never piecemeal.

---

## 1. Why an ABI bump is required

- The Ring 3 ABI is `INT 21h`, function in `AH`, args in `RDI/RSI/RDX/RCX`, result
  in `RAX` (`osbkt/docs/ABI.md`, v9). IPC needs new function numbers.
- The frozen error space is `-1..-8`. Phase 1 introduces `QueueFull` and `Eagain`,
  which have **no** v9 code. Exposing them requires extending the error space.
- Therefore IPC requires **ABI v10**: new `AH` values, two new error codes, and a
  documented handle/encoding contract. `AbiVersion` and `ABI.md` must change together.

**Migration impact:** existing programs never call the new `AH` values and are
unaffected. Programs that gate on `GETABI` and want IPC must require `>= 10`. No v9
behavior changes.

## 2. Proposed syscall surface (v10)

Free `AH` range today includes `0x83-0xFF`. **Ratified and implemented** allocation
(kernel `Cpu/Syscalls.Ipc.cs`, OSBKT `Syscall.Ipc.cs`, `osbkt/docs/ABI.md`):

| AH | Operation | Args | Result |
|---|---|---|---|
| `0x83` | `IPC_CREATE` | RDI=name ptr, RSI=len (1..31), AL=flags (bit0 system) | RAX=TopicHandle (u64) / neg error |
| `0x84` | `IPC_DESTROY` | RDI=TopicHandle | 0 / neg error |
| `0x85` | `IPC_SUBSCRIBE` | RDI=TopicHandle | 0 / neg error |
| `0x86` | `IPC_UNSUBSCRIBE` | RDI=TopicHandle | 0 / neg error |
| `0x87` | `IPC_PUBLISH` | RDI=TopicHandle, RSI=payload ptr, RDX=len | RAX=MessageId (u64) / neg error |
| `0x88` | `IPC_RECEIVE` | RDI=TopicHandle, RSI=dest, RDX=cap, RCX=out 24-byte view | 0 / -10 EAGAIN / neg error |
| `0x89` | `IPC_ACK` | RDI=DeliveryHandle | 0 / neg error |
| `0x8A` | `IPC_FAIL` | RDI=DeliveryHandle, AL=transient | 0 / neg error |

`ACL` management is deferred until the ACL model (Q-A1/A2/A3) is decided; the
current `IPC_SUBSCRIBE` is **open** (no grant enforcement) — see §9.

## 3. Kernel dispatch changes (future)

- Add a `Syscalls.Ipc.cs` partial with `IpcCreateTopic`, `IpcPublish`, … mirroring
  `Syscalls.Process.cs` style.
- Every pointer arg validated with `UserPtr` (A7) **before** any copy; bad range →
  `-1 EFAULT`, never a Ring 0 fault.
- The caller PID is `Scheduler.Get(Scheduler.Current).Pid` (never from the request).
- Map `IpcStatus` → ABI: `Ok→0`, `Efault→-1`, `Eacces→-3`, `Enoent→-4`,
  `Einval→-5`, `Enomem→-6`, `QueueFull→-9 (new)`, `Eagain→-10 (new)`.
- Register the new `AH` cases in the `Syscalls.Handle` switch.

## 4. OSBKT changes (future)

- Add `osbkt/Syscall.Ipc.cs` with `Osb.Ipc` wrappers over the new `AH` values
  (transport stays `osb_syscall`, RAX = `AH<<8|AL`).
- Public API (illustrative): `CreateTopic/DestroyTopic/Subscribe/Unsubscribe/
  Publish/Receive/Ack/Polld`. Handles exposed as `ulong` (index+generation).
- No direct `INT 21h` outside OSBKT.

## 5. ABI documentation (future)

Update `osbkt/docs/ABI.md`: header version → 10, new function table rows, new error
codes `-9 QueueFull`, `-10 Eagain`, handle encoding (index+generation), and the
byte-string/pointer rules IPC reuses. Update any `GETABI` consumers.

## 6. Consistency rule (mandatory)

The ABI bump, kernel dispatch, OSBKT wrappers, and `ABI.md` land in **one** change.
Do not add a syscall the OSBKT does not wrap, or document a code the kernel does not
return. Until then, the IPC core stays internal (Phase 1).

## 7. Sequencing and current status

1. ~~Ratify open questions (ACL, blocking Receive, ID exhaustion, error-code values).~~
   **Partially done:** error codes, blocking Receive (deferred), and the ABI surface
   are ratified. **ACL model (Q-A1/A2/A3) remains OPEN.**
2. **DONE (Phase 3):** kernel dispatch + ABI v10 + OSBKT + `ABI.md` landed together.
3. **DONE (Phase 2, commit `fba0e9e`):** `Ipc.OnProcessExit` wired into
   `Scheduler.Release` (`Processes/Scheduler.cs:271`); `AdvanceRetries` wired into
   IRQ0 (`Cpu/Interrupts.cs:148`). (An earlier Phase 3 report wrongly called these
   "deferred"; source and history confirm they are present.)
4. **NOT STARTED (Phase 3B):** Ring 3 end-to-end test (QEMU) exercising
   publish/receive/ack. No such test exists; QEMU boot to `login:` is **not** IPC
   validation.

## 9. Phase 3A.1 authorization status (verified)

- **Ratified policy:** default-deny; separate `Create`/`Publish`/`Subscribe`/`Manage`
  capabilities; explicit grants; Kernel-derived PID; dynamic ownership = creator;
  system topics Kernel-managed.
- **Implementation status:** **BLOCKED.** The kernel has **no ACL representation**
  (no grant table, no grant/revoke API) and Q-A1 is open, so explicit `Subscribe`
  grants cannot be represented safely. No partial ACL was invented (milestone gate).
- **Current effective behavior (verified in source):**
  - `CREATE` (`Ipc.Registry.cs:20`): system topic ⇒ Kernel only (`EACCES` for Ring 3);
    dynamic topic owner = caller.
  - `PUBLISH` (`Ipc.Publish.cs:32-33`): system ⇒ Kernel only; dynamic ⇒ owner or Kernel.
  - `DESTROY` (`Ipc.Registry.cs:51-52`): system ⇒ Kernel only; dynamic ⇒ owner or Kernel.
  - `SUBSCRIBE` (`Ipc.Registry.cs:73-98`): **open** — any process may subscribe to any
    existing topic; idempotent per (PID, Topic). **Deviation from default-deny.**
  - `UNSUBSCRIBE`/`RECEIVE`/`ACK`/`FAIL`: own subscription/delivery only
    (`Ipc.Registry.cs:105`, `Ipc.Deliver.cs:28/66/79`).
  - `CREATE` with an **existing name**: **no uniqueness check** — returns `Ok` and
    creates a distinct topic with the same name (verified `Ipc.Registry.cs:16-41`).
- **Unblock requires:** an explicit architecture decision on ACL representation
  (Q-A1), target model (Q-A2), and ownership operations (Q-A3), then a grant/revoke
  API and enforcement in `Subscribe`.

## 8. Non-goals

This plan does not implement anything. No syscall number, ABI version, or OSBKT API is
changed by the Phase 1 milestone.