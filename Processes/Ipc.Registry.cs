// Kernel IPC core — topic registry, subscriptions, and descriptor growth
// (Card 3.1, Phase 1). Split from Ipc.cs to keep each file under 400 lines.
//
// Storage grows geometrically via Kernel.OsAlloc; the old buffer is released
// only after the new allocation and copy succeed (no managed collections).
// No lock is taken (single-core, IF=0 syscall context; see Ipc.cs).

using Osb.Kernel;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Ipc
{
    // Create a topic. System topics (Flags bit0) may only be created by the
    // Kernel (pid 0). Dynamic topics are owned by the caller's actual PID (D21).
    public static IpcStatus CreateTopic(ulong pid, byte* name, int nameLen, bool system, out IpcHandle handle)
    {
        handle = default;
        if (!ValidateName(name, nameLen)) return IpcStatus.Einval;
        if (system && pid != 0) return IpcStatus.Eacces;      // D20: Kernel-managed
        if (_topicCount >= IpcLimits.MaxTopics && FindFreeTopic() < 0) return IpcStatus.Enomem;

        int idx = FindFreeTopic();
        if (idx < 0)
        {
            if (!GrowTopics()) return IpcStatus.Enomem;
            idx = _topicCount++;
        }
        else if (idx >= _topicCount) _topicCount = idx + 1;

        _topics[idx].Generation = NextGen(ref _topicGen);
        _topics[idx].OwnerPid = system ? 0UL : pid;
        _topics[idx].Flags = system ? SystemFlag : 0u;
        _topics[idx].SubCount = 0;
        ClearName(_topics[idx].Name);
        CopyName(_topics[idx].Name, name, nameLen);

        Initialized = true;
        handle = new IpcHandle((uint)idx, _topics[idx].Generation);
        return IpcStatus.Ok;
    }

    // Destroy a topic and everything attached to it. System topics survive
    // (only the Kernel may destroy them); a dynamic topic is destroyed by its
    // owner (D22/D24).
    public static IpcStatus DestroyTopic(ulong pid, IpcHandle handle)
    {
        int idx;
        if (!ResolveTopic(handle, out idx)) return IpcStatus.Enoent;
        bool system = (_topics[idx].Flags & SystemFlag) != 0;
        if (system && pid != 0) return IpcStatus.Eacces;
        if (!system && _topics[idx].OwnerPid != pid && pid != 0) return IpcStatus.Eacces;

        DestroyTopicLocked(idx);
        return IpcStatus.Ok;
    }

    // Cancel every subscription on a topic and invalidate the topic slot.
    private static void DestroyTopicLocked(int idx)
    {
        uint topicIndex = (uint)idx;
        for (int s = 0; s < _subCount; s++)
        {
            if (_subs[s].Generation == 0 || _subs[s].TopicIndex != topicIndex) continue;
            CancelSubscriptionLocked(s);
        }
        _topics[idx].Generation = 0;   // free slot; stale handles now fail
        _topics[idx].SubCount = 0;
    }

    // Subscribe is idempotent per (PID, Topic) (D25/D26): a repeat returns Ok
    // and creates no second queue.
    public static IpcStatus Subscribe(ulong pid, IpcHandle handle)
    {
        int t;
        if (!ResolveTopic(handle, out t)) return IpcStatus.Enoent;
        uint topicIndex = (uint)t;
        if (FindSub(pid, topicIndex) >= 0) return IpcStatus.Ok;   // idempotent

        if (_subCount >= IpcLimits.MaxSubscriptions && FindFreeSub() < 0) return IpcStatus.Enomem;
        int idx = FindFreeSub();
        if (idx < 0)
        {
            if (!GrowSubs()) return IpcStatus.Enomem;
            idx = _subCount++;
        }
        else if (idx >= _subCount) _subCount = idx + 1;

        _subs[idx].Generation = NextGen(ref _subGen);
        _subs[idx].Pid = pid;
        _subs[idx].TopicIndex = topicIndex;
        _subs[idx].QueueCount = 0;
        _subs[idx].InFlight = 0;
        _subs[idx].RetryPending = 0;
        _subs[idx].Capacity = IpcLimits.QueueDepth;
        _topics[t].SubCount++;
        return IpcStatus.Ok;
    }

    // Unsubscribe removes the caller's subscription and cancels its deliveries.
    public static IpcStatus Unsubscribe(ulong pid, IpcHandle handle)
    {
        int t;
        if (!ResolveTopic(handle, out t)) return IpcStatus.Enoent;
        int s = FindSub(pid, (uint)t);
        if (s < 0) return IpcStatus.Enoent;
        CancelSubscriptionLocked(s);
        return IpcStatus.Ok;
    }

    // Remove a subscription slot and cancel all its live deliveries.
    private static void CancelSubscriptionLocked(int s)
    {
        ulong pid = _subs[s].Pid;
        uint topicIndex = _subs[s].TopicIndex;
        for (int d = 0; d < _deliveryCount; d++)
        {
            if (_deliveries[d].Generation == 0) continue;
            if (_deliveries[d].Pid != pid || _deliveries[d].TopicIndex != topicIndex) continue;
            if (_deliveries[d].State == (int)IpcDeliveryState.Free) continue;
            CancelDeliveryLocked(d);
        }
        _topics[topicIndex].SubCount--;
        _subs[s].Generation = 0;
    }

    // ---- growth (geometric x2, release old only after new copy succeeds) ----

    private static bool GrowTopics()
    {
        int newCap = _topicCap == 0 ? 16 : _topicCap * 2;
        if (newCap > IpcLimits.MaxTopics) newCap = IpcLimits.MaxTopics;
        if (newCap <= _topicCap) return false;
        var ptr = (IpcTopic*)Kernel.OsAlloc(newCap * sizeof(IpcTopic));
        if (ptr == null) return false;
        for (int i = 0; i < newCap; i++) ptr[i] = default;
        for (int i = 0; i < _topicCount; i++) ptr[i] = _topics[i];
        if (_topics != null) Kernel.OsFree(_topics, _topicCap * sizeof(IpcTopic));
        _topics = ptr; _topicCap = newCap;
        return true;
    }

    private static bool GrowSubs()
    {
        int newCap = _subCap == 0 ? 16 : _subCap * 2;
        if (newCap > IpcLimits.MaxSubscriptions) newCap = IpcLimits.MaxSubscriptions;
        if (newCap <= _subCap) return false;
        var ptr = (IpcSubscription*)Kernel.OsAlloc(newCap * sizeof(IpcSubscription));
        if (ptr == null) return false;
        for (int i = 0; i < newCap; i++) ptr[i] = default;
        for (int i = 0; i < _subCount; i++) ptr[i] = _subs[i];
        if (_subs != null) Kernel.OsFree(_subs, _subCap * sizeof(IpcSubscription));
        _subs = ptr; _subCap = newCap;
        return true;
    }

    private static bool GrowMessages()
    {
        int newCap = _messageCap == 0 ? 16 : _messageCap * 2;
        var ptr = (IpcMessage*)Kernel.OsAlloc(newCap * sizeof(IpcMessage));
        if (ptr == null) return false;
        for (int i = 0; i < newCap; i++) ptr[i] = default;
        for (int i = 0; i < _messageCount; i++) ptr[i] = _messages[i];
        if (_messages != null) Kernel.OsFree(_messages, _messageCap * sizeof(IpcMessage));
        _messages = ptr; _messageCap = newCap;
        return true;
    }

    private static bool GrowDeliveries()
    {
        int newCap = _deliveryCap == 0 ? 16 : _deliveryCap * 2;
        var ptr = (IpcDelivery*)Kernel.OsAlloc(newCap * sizeof(IpcDelivery));
        if (ptr == null) return false;
        for (int i = 0; i < newCap; i++) ptr[i] = default;
        for (int i = 0; i < _deliveryCount; i++) ptr[i] = _deliveries[i];
        if (_deliveries != null) Kernel.OsFree(_deliveries, _deliveryCap * sizeof(IpcDelivery));
        _deliveries = ptr; _deliveryCap = newCap;
        return true;
    }
}