// MEMGROW support: track additional user frames per process and reclaim them.
//
// O8.12 M1: MEMGROW allocates physical frames through the frame allocator but
// the process must remember WHERE they are to free them again. The old code
// kept only a frame count, so FreeFrames(int) could only adjust accounting and
// the frames leaked. Each MEMGROW block's physical base is now retained; the
// block is freed by address on rollback (Syscalls.Memory) and on process exit
// (ReleaseUserFrames).

using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    public static int GetUserFrames(int slot)
    {
        return _tasks.userFrames[slot];
    }

    // Record a successfully-allocated MEMGROW block (base phys, frame count) for
    // a slot: update the running total and append the ownership handle so the
    // frames can be reclaimed later. Returns false (recording nothing) if the
    // slot is invalid or the per-slot block table is full; the caller then rolls
    // back the physical allocation it just made.
    public static bool AddUserFrames(int slot, int frames, ulong phys)
    {
        if (slot < 0 || slot >= Count) return false;
        int bc = _tasks.userBlockCount[slot];
        if (bc < 0 || bc >= MaxUserBlocks) return false;
        int idx = slot * MaxUserBlocks + bc;
        _tasks.userBlocks[idx] = phys;
        _tasks.userBlockFrames[idx] = frames;
        _tasks.userBlockCount[slot] = bc + 1;
        _tasks.userFrames[slot] += frames;
        return true;
    }

    // Reclaim every MEMGROW block owned by a slot and reset its accounting.
    // Called from Scheduler.Release when a process terminates. Each block is
    // freed by its recorded physical base, so the frames genuinely return to the
    // allocator's reusable pool (O8.12 M1).
    public static void ReleaseUserFrames(int slot)
    {
        if (slot < 0 || slot >= Count) return;
        int bc = _tasks.userBlockCount[slot];
        for (int b = 0; b < bc; b++)
        {
            int idx = slot * MaxUserBlocks + b;
            PhysicalMemory.Free(_tasks.userBlocks[idx], _tasks.userBlockFrames[idx]);
        }
        _tasks.userBlockCount[slot] = 0;
        _tasks.userFrames[slot] = 0;
    }
}