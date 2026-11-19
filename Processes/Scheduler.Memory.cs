// MEMGROW support: track additional user frames per process.

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    // MEMGROW support: track additional user frames per process
    public static int GetUserFrames(int slot)
    {
        return _tasks.userFrames[slot];
    }

    public static bool AddUserFrames(int slot, int frames)
    {
        if (slot < 0 || slot >= Count) return false;
        _tasks.userFrames[slot] += frames;
        return true;
    }
}