// Kernel IPC core — process lifecycle cleanup (Card 3.1, Phase 1).
//
// Invoked from the existing process-termination path (Scheduler.Release/Reap)
// in a later wiring step; Phase 1 provides the operation and tests it directly.
// It removes the exiting process's subscriptions, cancels its outstanding
// deliveries, and destroys the dynamic topics it owns. Kernel-managed system
// topics are preserved (D20/D22/D24, R3).

namespace Osb.Kernel.Processes;

internal static unsafe partial class Ipc
{
    // Release every IPC resource associated with a terminating process.
    public static void OnProcessExit(ulong pid)
    {
        // 1. Cancel deliveries where this process is the subscriber.
        for (int d = 0; d < _deliveryCount; d++)
        {
            if (_deliveries[d].Generation == 0) continue;
            if (_deliveries[d].Pid != pid) continue;
            if (_deliveries[d].State == (int)IpcDeliveryState.Free) continue;
            CancelDeliveryLocked(d);
        }

        // 2. Remove this process's subscriptions (cancels any remaining
        //    deliveries on them).
        for (int s = 0; s < _subCount; s++)
        {
            if (_subs[s].Generation == 0) continue;
            if (_subs[s].Pid != pid) continue;
            CancelSubscriptionLocked(s);
        }

        // 3. Destroy dynamic topics owned by this process. System topics
        //    (OwnerPid 0) are never owned by a service and are preserved.
        for (int t = 0; t < _topicCount; t++)
        {
            if (_topics[t].Generation == 0) continue;
            if ((_topics[t].Flags & SystemFlag) != 0) continue;
            if (_topics[t].OwnerPid != pid) continue;
            DestroyTopicLocked(t);
        }
    }
}