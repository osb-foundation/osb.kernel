using Osb.Kernel.Memory;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 10h MEMINFO(out in RDI): [u64 totalKB, usedKB, freeKB]
    private static void HandleMemInfo(ulong* frame)
    {
        ulong rdi = frame[9];
        if (!UserPtr(rdi, 24))
        {
            frame[14] = unchecked((ulong)-1);
            return;
        }
        ulong* o = (ulong*)rdi;
        ulong total = PhysicalMemory.Total * Layout.FrameSize / 1024UL;
        ulong used = PhysicalMemory.Used * Layout.FrameSize / 1024UL;
        o[0] = total;
        o[1] = used;
        o[2] = total > used ? total - used : 0;
        frame[14] = 1;
    }

    // 11h MEMGROW: grant memory in 2 MiB steps
    // RDI = requested additional frames (each 4 KiB; must be multiple of 512 for 2 MiB steps),
    //       or 0 to query current limit
    // RAX = 0 on success; new total frames on query (RDI=0). Negative on error.
    // Error codes: -1 EFAULT, -5 EINVAL (not multiple of 512, or would exceed per-process limit),
    //              -6 ENOMEM (insufficient physical frames), -3 EACCES
    private static void HandleMemGrow(ulong* frame)
    {
        ulong rdi = frame[9];

        const int FramesPer2MiB = 512; // 2 MiB / 4 KiB
        const int MaxFramesPerProcess = 4096; // 16 MiB default limit

        int currentFrames = Scheduler.GetUserFrames(Scheduler.Current);
        ulong requestedFrames = rdi;

        // Query current limit
        if (requestedFrames == 0)
        {
            frame[14] = (ulong)currentFrames;
            return;
        }

        // Must be multiple of 512 (2 MiB granularity)
        if (requestedFrames % FramesPer2MiB != 0)
        {
            frame[14] = unchecked((ulong)-5); // EINVAL
            return;
        }

        int newTotalFrames = currentFrames + (int)requestedFrames;

        if (newTotalFrames > MaxFramesPerProcess)
        {
            frame[14] = unchecked((ulong)-5); // EINVAL (would exceed per-process limit)
            return;
        }

        // Check physical memory availability
        if (!PhysicalMemory.TryAllocFrames((int)requestedFrames, out int _))
        {
            frame[14] = unchecked((ulong)-6); // ENOMEM
            return;
        }

        // Add frames to process
        if (!Scheduler.AddUserFrames(Scheduler.Current, (int)requestedFrames))
        {
            // Rollback physical allocation
            PhysicalMemory.FreeFrames((int)requestedFrames);
            frame[14] = unchecked((ulong)-6); // ENOMEM
            return;
        }

        frame[14] = 0;
    }
}