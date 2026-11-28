// Kernel IPC core — non-blocking Receive, explicit ACK, retry/terminal
// handling (Card 3.1, Phase 1).
//
// Semantics:
//   - Receive is non-blocking: it returns Eagain when nothing is queued.
//   - Receive copies the immutable payload into the caller's buffer and moves
//     the delivery Queued -> InFlight. It does NOT acknowledge (D6).
//   - ACK is explicit, validates ownership, and frees capacity (D5).
//   - A transient failure with attempts < MaxAttempts moves the SAME delivery
//     (same Delivery ID) to RetryPending; redelivery reuses the queue slot and
//     does not consume extra capacity. The Nth failure is terminal (R1).
//   - No NACK is implemented (not required by the approved spec).

using Osb.Kernel;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Ipc
{
    // Dequeue one available delivery for (pid, topic) and copy its payload.
    // Non-blocking. Does not ACK.
    public static IpcStatus Receive(uint pid, IpcHandle handle, byte* dest, int cap, out IpcDeliveryView view)
    {
        view = default;

        int t;
        if (!ResolveTopic(handle, out t)) return IpcStatus.Enoent;
        int s = FindSub(pid, (uint)t);
        if (s < 0) return IpcStatus.Enoent;

        int best = -1;
        uint bestOrder = 0;
        for (int d = 0; d < _deliveryCount; d++)
        {
            if (_deliveries[d].Generation == 0) continue;
            if (_deliveries[d].Pid != pid || _deliveries[d].TopicIndex != (uint)t) continue;
            if (_deliveries[d].State != (int)IpcDeliveryState.Queued) continue;
            if (best < 0 || _deliveries[d].Order < bestOrder) { best = d; bestOrder = _deliveries[d].Order; }
        }
        if (best < 0) return IpcStatus.Eagain;   // nothing available (non-blocking)

        int mIdx = (int)_deliveries[best].MessageIndex;
        int size = _messages[mIdx].Size;
        if (size > 0 && dest == null) return IpcStatus.Efault;
        if (cap < size) return IpcStatus.Einval; // destination too small

        byte* src = _messages[mIdx].Payload;
        for (int i = 0; i < size; i++) dest[i] = src[i];

        _subs[s].QueueCount--;
        _subs[s].InFlight++;
        _deliveries[best].State = (int)IpcDeliveryState.InFlight;
        _deliveries[best].Attempts++;            // initial 0->1, redelivery 1->2, 2->3

        view.MessageId = _messages[mIdx].MessageId;
        view.Delivery = new IpcHandle((uint)best, _deliveries[best].Generation);
        view.Size = size;
        return IpcStatus.Ok;
    }

    // Explicitly acknowledge an in-flight delivery owned by pid.
    public static IpcStatus Ack(uint pid, IpcHandle handle)
    {
        int d;
        if (!ResolveDelivery(handle, out d)) return IpcStatus.Enoent;
        if (_deliveries[d].Pid != pid) return IpcStatus.Eacces;    // foreign ACK
        if (_deliveries[d].State != (int)IpcDeliveryState.InFlight) return IpcStatus.Einval;
        ReleaseDeliveryLocked(d);
        return IpcStatus.Ok;
    }

    // Record a failed attempt. transient=true retries (up to MaxAttempts);
    // otherwise (or when attempts are exhausted) the delivery is terminal.
    // nowTicks is the current tick source (the timer hook is deferred, Phase 1).
    public static IpcStatus FailDelivery(uint pid, IpcHandle handle, bool transient, ulong nowTicks)
    {
        int d;
        if (!ResolveDelivery(handle, out d)) return IpcStatus.Enoent;
        if (_deliveries[d].Pid != pid) return IpcStatus.Eacces;
        if (_deliveries[d].State != (int)IpcDeliveryState.InFlight) return IpcStatus.Einval;

        int s = FindSub(pid, _deliveries[d].TopicIndex);
        if (transient && _deliveries[d].Attempts < IpcLimits.MaxAttempts)
        {
            // Retry: same Delivery ID, InFlight -> RetryPending, no extra slot.
            if (s >= 0) { _subs[s].InFlight--; _subs[s].RetryPending++; }
            _deliveries[d].State = (int)IpcDeliveryState.RetryPending;
            _deliveries[d].NextRetryTick = nowTicks + IpcLimits.RetryBaseTicks * (ulong)_deliveries[d].Attempts;
            return IpcStatus.Ok;
        }

        // Terminal: release capacity but keep an observable tombstone.
        if (s >= 0 && _subs[s].InFlight > 0) _subs[s].InFlight--;
        ReleaseMessageRef(d);
        _deliveries[d].State = (int)IpcDeliveryState.TerminalFail;
        _liveDeliveries--;
        return IpcStatus.Ok;
    }

    // Move due RetryPending deliveries back to Queued. Not wired to a timer in
    // Phase 1 (no callback timer exists); a future Scheduler hook calls this.
    public static int AdvanceRetries(ulong nowTicks)
    {
        int moved = 0;
        for (int d = 0; d < _deliveryCount; d++)
        {
            if (_deliveries[d].Generation == 0) continue;
            if (_deliveries[d].State != (int)IpcDeliveryState.RetryPending) continue;
            if (_deliveries[d].NextRetryTick > nowTicks) continue;
            int s = FindSub(_deliveries[d].Pid, _deliveries[d].TopicIndex);
            if (s >= 0 && _subs[s].RetryPending > 0) { _subs[s].RetryPending--; _subs[s].QueueCount++; }
            _deliveries[d].State = (int)IpcDeliveryState.Queued;
            moved++;
        }
        return moved;
    }

    // Release a live delivery's capacity and free its slot (Ack / Cancel).
    private static void ReleaseDeliveryLocked(int d)
    {
        int s = FindSub(_deliveries[d].Pid, _deliveries[d].TopicIndex);
        if (s >= 0)
        {
            switch (_deliveries[d].State)
            {
                case (int)IpcDeliveryState.Queued: if (_subs[s].QueueCount > 0) _subs[s].QueueCount--; break;
                case (int)IpcDeliveryState.InFlight: if (_subs[s].InFlight > 0) _subs[s].InFlight--; break;
                case (int)IpcDeliveryState.RetryPending: if (_subs[s].RetryPending > 0) _subs[s].RetryPending--; break;
            }
        }
        ReleaseMessageRef(d);
        _deliveries[d].Generation = 0;   // free slot; stale handle now fails
        _deliveries[d].State = (int)IpcDeliveryState.Free;
        _liveDeliveries--;
    }

    // Decrement the message reference count; free the payload + slot at zero.
    private static void ReleaseMessageRef(int d)
    {
        int mIdx = (int)_deliveries[d].MessageIndex;
        if (mIdx < 0 || mIdx >= _messageCount) return;
        if (_messages[mIdx].RefCount > 0) _messages[mIdx].RefCount--;
        if (_messages[mIdx].RefCount == 0 && _messages[mIdx].Generation != 0)
        {
            if (_messages[mIdx].Payload != null)
            {
                Kernel.OsFree(_messages[mIdx].Payload, _messages[mIdx].Size);
                _payloadBytes -= _messages[mIdx].Size;
            }
            _messages[mIdx].Generation = 0;
            _messages[mIdx].Payload = null;
            _liveMessages--;
        }
    }

    // Cancel a live delivery (process exit / unsubscribe / topic destroy).
    private static void CancelDeliveryLocked(int d)
    {
        ReleaseDeliveryLocked(d);
    }
}