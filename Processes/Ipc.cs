// Kernel IPC core — data model, handles, resource accounting, and shared
// helpers (Card 3.1, Phase 1). Topic/subscription operations live in
// Ipc.Registry.cs; publish in Ipc.Publish.cs; delivery/ACK/retry in
// Ipc.Deliver.cs; process cleanup in Ipc.Cleanup.cs.
//
// Design constraints (see docs/OSB-IPC-ARCHITECTURE-REVIEW.md):
//   - Single core; non-blocking IPC syscalls run with IF=0 (DPL3 interrupt
//     gate), so broker operations are serialized without a broker lock. This
//     file takes NO lock and must never acquire the M0 SpinLock (the allocator
//     owns it; recursive M0 acquisition deadlocks).
//   - Storage comes from Kernel.OsAlloc/OsFree (frame allocator). No managed
//     heap, no BCL, no Span, no generics.
//   - Message payloads are immutable and Kernel-owned; Receive copies out.
//   - Message ID (monotonic u64) and Delivery ID (index+generation handle) are
//     distinct. Reusable objects use index+generation handles so a stale handle
//     cannot resolve to a reused slot.
//   - The caller PID is always supplied by the Kernel (Scheduler), never forged.

using Osb.Kernel;
using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

// Internal status codes. Values -1..-8 match the frozen ABI error space; the
// IPC-specific codes are Phase-1 internal values and are NOT yet an ABI
// contract (the v10 mapping is a separate integration plan).
internal enum IpcStatus
{
    Ok = 0,
    Efault = -1,
    Enosys = -2,
    Eacces = -3,
    Enoent = -4,
    Einval = -5,
    Enomem = -6,
    Eintr = -7,
    Ebadf = -8,
    QueueFull = -9,   // explicit, distinct from Enomem (D12)
    Eagain = -10,     // non-blocking receive found nothing
}

internal enum IpcDeliveryState : int
{
    Free = 0,
    Queued = 1,
    InFlight = 2,
    RetryPending = 3,
    Acked = 4,
    TerminalFail = 5,
    Cancelled = 6,
}

// Index + generation handle for a reusable object. Generation 0 means "free
// slot"; a handle whose generation does not match the live slot is stale.
internal readonly struct IpcHandle
{
    public readonly uint Index;
    public readonly uint Generation;

    public IpcHandle(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool IsNone => Generation == 0;

    public ulong ToU64() => ((ulong)Generation << 32) | Index;

    public static IpcHandle FromU64(ulong v)
    {
        return new IpcHandle((uint)(v & 0xFFFFFFFFu), (uint)(v >> 32));
    }
}

// One subscriber-specific logical delivery, returned by Receive.
internal struct IpcDeliveryView
{
    public ulong MessageId;
    public IpcHandle Delivery;
    public int Size;
}

// Internal, configurable Phase-1 limits. These are PROPOSALS (review §E), not
// ABI guarantees, and are intentionally independent of the task-slot count.
// They are internal constants only; exposing them is a later ABI decision.
internal static class IpcLimits
{
    public const int MaxTopics = 64;
    public const int MaxSubscriptions = 256;
    public const int QueueDepth = 8;          // per-subscriber capacity
    public const int MaxPayload = 64 * 1024;  // bytes per message
    public const int MaxNameBytes = 31;       // topic name usable length
    public const long MaxIpcMemory = 1 * 1024 * 1024; // total IPC kernel bytes
    public const int MaxAttempts = 3;         // total attempts incl. initial (R1)
    public const ulong RetryBaseTicks = 10;   // backoff base (deferred timer)
}

internal unsafe struct IpcTopic
{
    public uint Generation;          // 0 = free slot
    public ulong OwnerPid;            // 0 = system / Kernel-owned (D20)
    public uint Flags;               // bit0 = system topic
    public uint SubCount;            // active subscriptions
    public fixed byte Name[IpcLimits.MaxNameBytes + 1];
}

internal unsafe struct IpcSubscription
{
    public uint Generation;          // 0 = free slot
    public ulong Pid;                 // subscriber (Kernel-derived)
    public uint TopicIndex;          // index into _topics
    public int QueueCount;           // deliveries in Queued
    public int InFlight;             // deliveries in InFlight
    public int RetryPending;         // deliveries in RetryPending
    public int Capacity;             // = IpcLimits.QueueDepth
}

internal unsafe struct IpcMessage
{
    public uint Generation;          // 0 = free slot (internal; not a handle)
    public ulong MessageId;          // monotonic, unique per boot
    public int Size;                 // payload bytes
    public int RefCount;             // deliveries still referencing
    public byte* Payload;            // Kernel-owned immutable, or null if empty
}

internal unsafe struct IpcDelivery
{
    public uint Generation;          // 0 = free slot
    public ulong Pid;                 // subscriber owner
    public uint TopicIndex;
    public uint MessageIndex;        // index into _messages
    public uint Order;               // FIFO order (commit order)
    public int State;                // IpcDeliveryState
    public int Attempts;             // attempts made (1 after first receive)
    public ulong NextRetryTick;      // for RetryPending
}

internal static unsafe partial class Ipc
{
    private const uint SystemFlag = 0x1;

    private static IpcTopic* _topics;
    private static int _topicCount;
    private static int _topicCap;
    private static uint _topicGen;

    private static IpcSubscription* _subs;
    private static int _subCount;
    private static int _subCap;
    private static uint _subGen;

    private static IpcMessage* _messages;
    private static int _messageCount;
    private static int _messageCap;
    private static ulong _nextMessageId;

    private static IpcDelivery* _deliveries;
    private static int _deliveryCount;
    private static int _deliveryCap;
    private static uint _deliveryGen;

    private static uint _orderCounter;
    private static long _payloadBytes;   // total live payload bytes
    private static int _liveMessages;
    private static int _liveDeliveries;  // active (Queued+InFlight+RetryPending)
    private static int _retryPending;    // deliveries in RetryPending (timer fast-path)

    public static bool Initialized { get; private set; }

    // Boot / test reset: free all storage and zero every field. Safe when
    // nothing is allocated.
    public static void Reset()
    {
        FreeTopics();
        FreeSubs();
        FreeMessages();
        FreeDeliveries();
        _topicCount = _topicCap = 0; _topicGen = 0;
        _subCount = _subCap = 0; _subGen = 0;
        _messageCount = _messageCap = 0; _nextMessageId = 0;
        _deliveryCount = _deliveryCap = 0; _deliveryGen = 0;
        _orderCounter = 0;
        _payloadBytes = 0;
        _liveMessages = 0;
        _liveDeliveries = 0;
        _retryPending = 0;
        Initialized = false;
    }

    private static void FreeTopics()
    {
        if (_topics != null) { Kernel.OsFree(_topics, _topicCap * sizeof(IpcTopic)); _topics = null; }
    }
    private static void FreeSubs()
    {
        if (_subs != null) { Kernel.OsFree(_subs, _subCap * sizeof(IpcSubscription)); _subs = null; }
    }
    private static void FreeMessages()
    {
        if (_messages != null)
        {
            for (int i = 0; i < _messageCount; i++)
                if (_messages[i].Payload != null) Kernel.OsFree(_messages[i].Payload, _messages[i].Size);
            Kernel.OsFree(_messages, _messageCap * sizeof(IpcMessage));
            _messages = null;
        }
    }
    private static void FreeDeliveries()
    {
        if (_deliveries != null) { Kernel.OsFree(_deliveries, _deliveryCap * sizeof(IpcDelivery)); _deliveries = null; }
    }

    // ---- handle resolution ----

    private static bool ResolveTopic(IpcHandle h, out int idx)
    {
        idx = -1;
        if (h.Generation == 0 || h.Index >= (uint)_topicCount) return false;
        int i = (int)h.Index;
        if (_topics[i].Generation != h.Generation) return false;
        idx = i;
        return true;
    }

    private static bool ResolveDelivery(IpcHandle h, out int idx)
    {
        idx = -1;
        if (h.Generation == 0 || h.Index >= (uint)_deliveryCount) return false;
        int i = (int)h.Index;
        if (_deliveries[i].Generation != h.Generation) return false;
        if (_deliveries[i].State == (int)IpcDeliveryState.Free) return false;
        idx = i;
        return true;
    }

    private static int FindSub(ulong pid, uint topicIndex)
    {
        for (int i = 0; i < _subCount; i++)
            if (_subs[i].Generation != 0 && _subs[i].Pid == pid && _subs[i].TopicIndex == topicIndex)
                return i;
        return -1;
    }

    private static uint NextGen(ref uint counter)
    {
        counter++;
        if (counter == 0) counter = 1; // skip 0 (free marker)
        return counter;
    }

    private static int FindFreeTopic()
    {
        for (int i = 0; i < _topicCount; i++) if (_topics[i].Generation == 0) return i;
        return -1;
    }
    private static int FindFreeSub()
    {
        for (int i = 0; i < _subCount; i++) if (_subs[i].Generation == 0) return i;
        return -1;
    }
    private static int FindFreeMessage()
    {
        for (int i = 0; i < _messageCount; i++) if (_messages[i].Generation == 0) return i;
        if (_messageCount < _messageCap) return _messageCount; // fresh slot in spare capacity
        return -1;
    }
    private static int FindFreeDelivery()
    {
        for (int i = 0; i < _deliveryCount; i++) if (_deliveries[i].Generation == 0) return i;
        if (_deliveryCount < _deliveryCap) return _deliveryCount; // fresh slot in spare capacity
        return -1;
    }

    private static bool ValidateName(byte* name, int len)
    {
        if (name == null || len <= 0 || len > IpcLimits.MaxNameBytes) return false;
        return true;
    }
    private static void ClearName(byte* dst)
    {
        for (int i = 0; i <= IpcLimits.MaxNameBytes; i++) dst[i] = 0;
    }
    private static void CopyName(byte* dst, byte* src, int len)
    {
        for (int i = 0; i < len; i++) dst[i] = src[i];
        dst[len] = 0;
    }

    // ---- accounting helpers ----

    private static bool SubHasCapacity(int s)
    {
        return _subs[s].QueueCount + _subs[s].InFlight + _subs[s].RetryPending < _subs[s].Capacity;
    }

    private static bool MemoryAvailable(long extra)
    {
        return _payloadBytes + extra <= IpcLimits.MaxIpcMemory;
    }
}