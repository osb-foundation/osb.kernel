// Test-only accessors for the Kernel IPC core (Card 3.1 Phase 1).
//
// Compiled ONLY into the host unit-test assembly (tests/unit). The production
// kernel build does not reference tests/unit, so these read-only accessors do
// not widen the production surface. They let the tests assert on the real
// internal state of the linked Ipc implementation.
using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Ipc
{
    public static void TestOnly_Reset()
    {
        Reset();
    }

    public static void TestOnly_Counts(out int topics, out int subs, out int messages,
        out int deliveries, out long payloadBytes, out int liveMessages, out int liveDeliveries)
    {
        topics = _topicCount;
        subs = _subCount;
        messages = _messageCount;
        deliveries = _deliveryCount;
        payloadBytes = _payloadBytes;
        liveMessages = _liveMessages;
        liveDeliveries = _liveDeliveries;
    }

    public static bool TestOnly_Topic(int index, out uint ownerPid, out uint flags, out uint subCount, out uint generation)
    {
        ownerPid = flags = subCount = generation = 0;
        if (index < 0 || index >= _topicCount) return false;
        ownerPid = _topics[index].OwnerPid;
        flags = _topics[index].Flags;
        subCount = _topics[index].SubCount;
        generation = _topics[index].Generation;
        return true;
    }

    public static int TestOnly_TopicName(int index, byte[] dst)
    {
        if (index < 0 || index >= _topicCount || dst == null) return -1;
        int n = 0;
        while (n < IpcLimits.MaxNameBytes && _topics[index].Name[n] != 0 && n < dst.Length)
        {
            dst[n] = (byte)_topics[index].Name[n];
            n++;
        }
        return n;
    }

    public static bool TestOnly_Sub(int index, out uint pid, out uint topicIndex, out int q,
        out int inFlight, out int retry, out int capacity, out uint generation)
    {
        pid = topicIndex = generation = 0;
        q = inFlight = retry = capacity = 0;
        if (index < 0 || index >= _subCount) return false;
        pid = _subs[index].Pid;
        topicIndex = _subs[index].TopicIndex;
        q = _subs[index].QueueCount;
        inFlight = _subs[index].InFlight;
        retry = _subs[index].RetryPending;
        capacity = _subs[index].Capacity;
        generation = _subs[index].Generation;
        return true;
    }

    public static int TestOnly_FindSub(uint pid, uint topicIndex)
    {
        return FindSub(pid, topicIndex);
    }

    public static bool TestOnly_Delivery(int index, out uint pid, out uint topicIndex, out uint messageIndex,
        out int state, out int attempts, out uint generation)
    {
        pid = topicIndex = messageIndex = generation = 0;
        state = attempts = 0;
        if (index < 0 || index >= _deliveryCount) return false;
        pid = _deliveries[index].Pid;
        topicIndex = _deliveries[index].TopicIndex;
        messageIndex = _deliveries[index].MessageIndex;
        state = _deliveries[index].State;
        attempts = _deliveries[index].Attempts;
        generation = _deliveries[index].Generation;
        return true;
    }

    public static int TestOnly_FindDelivery(uint pid, uint topicIndex, int state)
    {
        for (int d = 0; d < _deliveryCount; d++)
        {
            if (_deliveries[d].Generation == 0) continue;
            if (_deliveries[d].Pid != pid || _deliveries[d].TopicIndex != topicIndex) continue;
            if (_deliveries[d].State != state) continue;
            return d;
        }
        return -1;
    }

    public static bool TestOnly_Message(int index, out ulong messageId, out int size, out int refCount, out uint generation)
    {
        messageId = 0; size = refCount = 0; generation = 0;
        if (index < 0 || index >= _messageCount) return false;
        messageId = _messages[index].MessageId;
        size = _messages[index].Size;
        refCount = _messages[index].RefCount;
        generation = _messages[index].Generation;
        return true;
    }
}