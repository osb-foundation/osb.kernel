// Scheduler publication helper: common post-SeedSlot initialization for user
// processes. Extracted from SpawnUser and SpawnFileInode to eliminate
// duplicated task-context wiring. Pure refactor — preserves exact store order.

using System.Runtime;
using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    // Complete the publication of a user task after SeedSlot has prepared it.
    // Initializes TLS, wires the address-space fields, records the parent PID,
    // captures the child PID, commits the task (state=TaskReady), and flushes
    // console input. The slot must already be seeded.
    private static void PublishUserTask(int slot, ulong entryOffset, ulong block,
        ulong cr3, ulong ptphys, int parentSlot, out int pid)
    {
        InitTaskTls(slot, block);
        _tasks.arg[slot] = Layout.UserBase + entryOffset;
        _tasks.cr3[slot] = cr3;
        _tasks.ublock[slot] = block;
        _tasks.ptphys[slot] = ptphys;
        _tasks.ppid[slot] = _tasks.pid[parentSlot];
        pid = _tasks.pid[slot];
        CommitTask(slot);
        ConsoleInput.Flush();
    }
}