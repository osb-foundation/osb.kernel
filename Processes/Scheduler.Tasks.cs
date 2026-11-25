// Task state and credential helpers for the scheduler.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    public static bool IsFree(int slot) { return _tasks.state[slot] == Layout.TaskFree; }
    public static bool IsReady(int slot) { return _tasks.state[slot] == Layout.TaskReady; }
    public static bool IsZombie(int slot) { return _tasks.state[slot] == Layout.TaskZombie; }

    // The effective uid of the task currently running (1c: the real session
    // uid, not a hardcoded 0). Every permission check goes through this
    // helper so the credential source is one place, not a scattered Pid==1 hack.
    public static int CurrentUid() { return _tasks.uid[_current]; }

    // Group id of the task currently running (used by the 6Ah exec check).
    public static int CurrentGid() { return _tasks.gid[_current]; }

    // Effective uid of the task in `slot`, for permission checks by slot.
    public static int GetUid(int slot) { return _tasks.uid[slot]; }

    // Set effective uid of the task in `slot` (for SUDO/END_SUDO).
    public static void SetEffectiveUid(int slot, int uid)
    {
        if (slot >= 0 && slot < Count) _tasks.uid[slot] = uid;
    }

    // Stdout handle of the task currently running (-1 = VGA console).
    public static int GetStdout() { return _tasks.stdoutH[_current]; }

    // Set the stdout handle of the task currently running (-1 = VGA console).
    public static void SetStdout(int handle) { _tasks.stdoutH[_current] = handle; }

    // Stdin handle of the task currently running (-1 = console).
    public static int GetStdin() { return _tasks.stdinH[_current]; }

    // Set the stdin handle of the task currently running (-1 = console).
    public static void SetStdin(int handle) { _tasks.stdinH[_current] = handle; }

    // Copy the current task's file handles to `childSlot` (same relative offsets
    // 0-7), and set the child's stdin/stdout handles from absolute indices
    // (-1 = inherit console). Returns false if the child slot is invalid.
    public static bool PropagateHandles(int childSlot, int stdinAbs, int stdoutAbs)
    {
        if (childSlot < 0 || childSlot >= Count) return false;
        int parentBase = _current * 8;
        int childBase = childSlot * 8;
        for (int h = 0; h < 8; h++)
        {
            int pi = parentBase + h;
            int ci = childBase + h;
            _tasks.fhUsed[ci] = _tasks.fhUsed[pi];
            _tasks.fhInode[ci] = _tasks.fhInode[pi];
            _tasks.fhOff[ci] = _tasks.fhOff[pi];
            _tasks.fhSize[ci] = _tasks.fhSize[pi];
            _tasks.fhWrite[ci] = _tasks.fhWrite[pi];
        }
        // Convert parent-absolute handle indices to child-absolute (same rel offset)
        _tasks.stdinH[childSlot] = (stdinAbs >= 0) ? childBase + (stdinAbs - parentBase) : -1;
        _tasks.stdoutH[childSlot] = (stdoutAbs >= 0) ? childBase + (stdoutAbs - parentBase) : -1;
        return true;
    }

    // Find a live (ready) or zombie slot by pid; false if unknown.
    public static bool TryFindPid(int pid, out int slot)
    {
        for (int i = 0; i < Count; i++)
            if (_tasks.pid[i] == pid && (i == Console || _tasks.state[i] != Layout.TaskFree))
            {
                slot = i;
                return true;
            }
        slot = -1;
        return false;
    }

    // Top (16-byte aligned) of a slot's kernel stack: what the TSS RSP0 needs.
    public static ulong KernelStackTop(int slot)
    {
        fixed (byte* p = _stacks.bytes)
            return ((ulong)p + (ulong)slot * Layout.StackSize + Layout.StackSize) & ~(ulong)15;
    }

    private static void SetName(int slot, string name)
    {
        int o = slot * Layout.MaxName;
        for (int i = 0; i < Layout.MaxName; i++) _tasks.name[o + i] = 0;
        int n = name.Length; if (n > Layout.MaxName - 1) n = Layout.MaxName - 1;
        fixed (char* p = name)
        {
            for (int i = 0; i < n; i++) _tasks.name[o + i] = (byte)p[i];
        }
    }

    // Name from a char buffer (external commands carry no managed string).
    private static void SetNameChars(int slot, char* name, int len)
    {
        int o = slot * Layout.MaxName;
        for (int i = 0; i < Layout.MaxName; i++) _tasks.name[o + i] = 0;
        if (len > Layout.MaxName - 1) len = Layout.MaxName - 1;
        for (int i = 0; i < len; i++) _tasks.name[o + i] = (byte)name[i];
    }

    // The argument string handed to an external program (read back by the
    // AH=23h syscall). Trailing spaces are trimmed.
    public static void SetArgs(int slot, char* args, int len)
    {
        int o = slot * ArgsCap;
        for (int i = 0; i < ArgsCap; i++) _tasks.args[o + i] = 0;
        while (len > 0 && args[len - 1] == ' ') len--;
        if (len > ArgsCap - 1) len = ArgsCap - 1;
        for (int i = 0; i < len; i++) _tasks.args[o + i] = (byte)args[i];
        // DEBUG O8.15
        Serial.PutChar('['); Serial.PutChar((char)('0' + slot)); Serial.PutChar(']');
        Serial.PutChar(':'); Serial.PutChar((char)('0' + len / 10)); Serial.PutChar((char)('0' + len % 10));
        for (int i = 0; i < len && i < 16; i++) Serial.PutChar((char)_tasks.args[o + i]);
        Serial.PutChar('\r'); Serial.PutChar('\n');
    }

    // Copies the current task's argument string out (0-terminated).
    public static int GetArgs(int slot, byte* dst, int cap)
    {
        int o = slot * ArgsCap;
        int n = 0;
        while (n < ArgsCap - 1 && _tasks.args[o + n] != 0 && n < cap - 1)
        {
            dst[n] = _tasks.args[o + n];
            n++;
        }
        dst[n] = 0;
        return n;
    }
}