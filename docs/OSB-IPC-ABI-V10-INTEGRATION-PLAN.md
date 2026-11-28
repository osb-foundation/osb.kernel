# OSB IPC — ABI v10 and OSBKT Integration Plan

**Card:** 3.1 (integration plan)
**Status:** PLAN ONLY — **nothing in this document is implemented**
**Depends on:** Kernel IPC core (Phase 1, `Processes/Ipc*.cs`) being complete and validated.

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

Free `AH` range today includes `0x83-0xFF`. Proposed allocation (to be ratified):

| AH | Operation | Args | Result |
|---|---|---|---|
| `0x83` | `IPC_CREATE_TOPIC` | RDI=name ptr, RSI=len, RDX=flags | RAX=TopicId (u64) / neg error |
| `0x84` | `IPC_DESTROY_TOPIC` | RDI=TopicId | 0 / neg error |
| `0x85` | `IPC_SUBSCRIBE` | RDI=TopicId | 0 / neg error |
| `0x86` | `IPC_UNSUBSCRIBE` | RDI=TopicId | 0 / neg error |
| `0x87` | `IPC_PUBLISH` | RDI=TopicId, RSI=payload ptr, RDX=len | RAX=MessageId / neg error |
| `0x88` | `IPC_RECEIVE` | RDI=TopicId, RSI=out rec, RDX=buf, RCX=cap, AL=block | RAX=len / neg error |
| `0x89` | `IPC_ACK` | RDI=DeliveryId | 0 / neg error |
| `0x8A` | `IPC_POLL` | RDI=TopicId | queued depth / neg error |

`ACL` management is deferred until the ACL model (Q-A1/A2/A3) is decided.

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

## 7. Sequencing

1. Ratify open questions (ACL, blocking Receive, ID exhaustion, error-code values).
2. Land kernel dispatch + ABI v10 + OSBKT + `ABI.md` together.
3. Wire `Ipc.OnProcessExit` into `Scheduler.Release/Reap`; wire `AdvanceRetries` into
   the timer (Scheduler changes, separate card).
4. Add a Ring 3 end-to-end test (QEMU) exercising publish/receive/ack.

## 8. Non-goals

This plan does not implement anything. No syscall number, ABI version, or OSBKT API is
changed by the Phase 1 milestone.