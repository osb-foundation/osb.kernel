// Kernel IPC core — atomic two-phase publish (Card 3.1, Phase 1).
//
// Two-phase model (review AM-2 / spec §10.2):
//   Phase 1 (prepare): validate, snapshot eligible subscribers, pre-check
//     capacity, allocate the payload OUTSIDE any lock, and ensure descriptor
//     capacity. No delivery is visible yet.
//   Phase 2 (commit): re-validate topic/subscriber/process state, then link
//     every delivery and publish the message atomically. This is the
//     linearization point. Any failure rolls back with no partial fan-out.
//
// No lock is taken: on this single-core kernel a non-blocking syscall runs with
// IF=0, so no other broker operation or the timer can interleave. Allocation
// (Kernel.OsAlloc) is never called while holding the M0 lock.

using Osb.Kernel;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Ipc
{
    // Publish payload to every eligible subscriber of a topic.
    // Returns Ok + MessageId, or QueueFull (no partial fan-out), Enoent,
    // Eacces, Einval, Efault, Enomem.
    public static IpcStatus Publish(uint pid, IpcHandle handle, byte* payload, int len, out ulong messageId)
    {
        messageId = 0;

        // ---- Phase 1a: validate topic + authorization (no allocation) ----
        int t;
        if (!ResolveTopic(handle, out t)) return IpcStatus.Enoent;
        bool system = (_topics[t].Flags & SystemFlag) != 0;
        if (system && pid != 0) return IpcStatus.Eacces;                 // D4
        if (!system && _topics[t].OwnerPid != pid && pid != 0) return IpcStatus.Eacces;

        if (len < 0 || len > IpcLimits.MaxPayload) return IpcStatus.Einval;
        if (len > 0 && payload == null) return IpcStatus.Efault;
        if (!MemoryAvailable(len)) return IpcStatus.Enomem;

        // Snapshot + capacity pre-check. If any eligible subscriber is full the
        // whole publication is rejected before we allocate anything (D10/D12).
        int eligible = 0;
        for (int s = 0; s < _subCount; s++)
        {
            if (_subs[s].Generation == 0 || _subs[s].TopicIndex != (uint)t) continue;
            if (!SubHasCapacity(s)) return IpcStatus.QueueFull;
            eligible++;
        }

        // ---- Phase 1b: allocate payload first (rollback must free it) ----
        byte* copy = null;
        if (len > 0)
        {
            copy = (byte*)Kernel.OsAlloc(len);
            if (copy == null) return IpcStatus.Enomem;
            for (int i = 0; i < len; i++) copy[i] = payload[i];
        }

        // ---- Phase 1c: ensure descriptor capacity (rollback frees payload) ----
        if (!EnsureMessageSlot() || !EnsureDeliverySlots(eligible))
        {
            FreePayload(copy, len);
            return IpcStatus.Enomem;
        }

        // ---- Phase 2: commit (linearization point) ----
        // Re-validate every eligible subscriber still has capacity. With IF=0
        // this set is identical to the snapshot, but re-checking keeps the
        // commit correct if the invariant is ever weakened.
        for (int s = 0; s < _subCount; s++)
        {
            if (_subs[s].Generation == 0 || _subs[s].TopicIndex != (uint)t) continue;
            if (!SubHasCapacity(s)) { FreePayload(copy, len); return IpcStatus.QueueFull; }
        }

        int mIdx = FindFreeMessage();
        if (mIdx < 0) { FreePayload(copy, len); return IpcStatus.Enomem; } // unreachable after EnsureMessageSlot

        ulong mid = ++_nextMessageId;
        _messages[mIdx].Generation = 1;          // internal live marker
        _messages[mIdx].MessageId = mid;
        _messages[mIdx].Size = len;
        _messages[mIdx].RefCount = 0;
        _messages[mIdx].Payload = copy;
        if (mIdx >= _messageCount) _messageCount = mIdx + 1;
        _liveMessages++;
        _payloadBytes += len;

        int committed = 0;
        for (int s = 0; s < _subCount; s++)
        {
            if (_subs[s].Generation == 0 || _subs[s].TopicIndex != (uint)t) continue;

            int dIdx = FindFreeDelivery();
            if (dIdx < 0) { RollbackCommit(mIdx, len); return IpcStatus.Enomem; } // unreachable

            _deliveries[dIdx].Generation = NextGen(ref _deliveryGen);
            _deliveries[dIdx].Pid = _subs[s].Pid;
            _deliveries[dIdx].TopicIndex = (uint)t;
            _deliveries[dIdx].MessageIndex = (uint)mIdx;
            _deliveries[dIdx].Order = ++_orderCounter;
            _deliveries[dIdx].State = (int)IpcDeliveryState.Queued;
            _deliveries[dIdx].Attempts = 0;
            _deliveries[dIdx].NextRetryTick = 0;
            if (dIdx >= _deliveryCount) _deliveryCount = dIdx + 1;

            _subs[s].QueueCount++;
            _messages[mIdx].RefCount++;
            _liveDeliveries++;
            committed++;
        }

        messageId = mid;
        return IpcStatus.Ok;
    }

    private static void FreePayload(byte* p, int len)
    {
        if (p != null && len > 0) Kernel.OsFree(p, len);
    }

    // Defensive rollback for the (unreachable under IF=0) mid-commit failure:
    // free the just-claimed deliveries, then the message and its payload.
    private static void RollbackCommit(int mIdx, int len)
    {
        for (int d = 0; d < _deliveryCount; d++)
        {
            if (_deliveries[d].Generation == 0) continue;
            if (_deliveries[d].MessageIndex != (uint)mIdx) continue;
            if (_deliveries[d].State != (int)IpcDeliveryState.Queued) continue;
            int s = FindSub(_deliveries[d].Pid, _deliveries[d].TopicIndex);
            if (s >= 0 && _subs[s].QueueCount > 0) _subs[s].QueueCount--;
            _deliveries[d].Generation = 0;
            _liveDeliveries--;
        }
        if (_messages[mIdx].Payload != null) FreePayload(_messages[mIdx].Payload, _messages[mIdx].Size);
        _payloadBytes -= len;
        _messages[mIdx].Generation = 0;
        _messages[mIdx].Payload = null;
        _liveMessages--;
    }

    private static int CountFreeMessages()
    {
        int n = _messageCap - _messageCount;
        for (int i = 0; i < _messageCount; i++) if (_messages[i].Generation == 0) n++;
        return n;
    }

    private static bool EnsureMessageSlot()
    {
        while (CountFreeMessages() == 0)
        {
            if (!GrowMessages()) return false;
        }
        return true;
    }

    private static int CountFreeDeliveries()
    {
        int n = _deliveryCap - _deliveryCount;
        for (int i = 0; i < _deliveryCount; i++) if (_deliveries[i].Generation == 0) n++;
        return n;
    }

    private static bool EnsureDeliverySlots(int n)
    {
        while (CountFreeDeliveries() < n)
        {
            if (!GrowDeliveries()) return false;
        }
        return true;
    }
}