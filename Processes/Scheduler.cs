// Preemptive round-robin scheduler over a fixed table of task slots.
//
// Slot 0 is the console/shell (PID 1, kernel address space); the others are
// processes/jobs, each with its own kernel stack. A ring-3 process also owns
// a private address space (CR3) and a 2 MiB user block. The switch itself is
// done in assembly (osb_switch) and is only ever driven from the timer IRQ.

using System.Runtime;
using Osb.Kernel.Cpu;
using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Processes;

// Outcome of Scheduler.SpawnUser, so the caller can explain the refusal.
internal enum SpawnResult
{
    Ok,
    NoSlot,          // every process slot is in use
    OutOfMemory,     // no room for a private 2 MiB block
    NotFound,        // no such file on A:
    BadImage         // not a valid OSBEXE image
}

    // Read-only snapshot of one task slot, for the "ps" listing and job control.
    internal unsafe struct TaskInfo
    {
        public ulong Pid;            // authoritative 64-bit process identity
        public int State;
    public ulong UserBlock;      // 2 MiB user block (phys), 0 for kernel tasks
    public byte* Name;           // ASCII, NUL-terminated
    public int NameLen;
}

internal static unsafe partial class Scheduler
{
    public const int Console = Layout.SlotConsole;   // the shell / console (PID 1)
    public const int Count = Layout.TaskCount;       // console + process slots

    // Argument string capacity for external programs (per slot). Holds up to
    // 128 bytes + NUL, matching the 6Ah argsLen cap and the 23h GETARGS cap
    // (cap 129 returns 128 chars).
    public const int ArgsCap = 130;

    // MEMGROW grants memory in 2 MiB steps up to a 16 MiB per-process limit, so
    // a slot needs at most 16 MiB / 2 MiB = 8 distinct blocks. Each block's
    // physical base is retained so it can be reclaimed on rollback and on
    // process exit (O8.12 M1); the old code kept only a frame count and could
    // never actually free the memory.
    public const int MaxUserBlocks = 8;

    private unsafe struct TaskTable
    {
        public fixed ulong sp[Count];             // saved kernel SP (osb_switch)
        public fixed ulong pid[Count];            // authoritative 64-bit PID
        public fixed ulong ppid[Count];           // parent PID (0 = none)
        public fixed int state[Count];
        public fixed int kind[Count];
        public fixed int fg[Count];               // 1 = foreground job
        public fixed int exitCode[Count];
        public fixed ulong arg[Count];            // kind-specific (user entry RIP)
        public fixed ulong cr3[Count];            // address space (page table root)
        public fixed ulong fsbase[Count];         // user FS base (thread pointer), 0 = kernel task
        public fixed ulong ublock[Count];         // 2 MiB user block (phys), 0 if none
        public fixed ulong ptphys[Count];         // 3 page-table frames (phys), 0 if none
        public fixed ulong round[Count];          // demo: rounds printed
        public fixed ulong deadline[Count];       // demo: next print tick
        public fixed byte name[Count * Layout.MaxName];
        public fixed byte args[Count * ArgsCap];  // external programs: argument string
        public fixed int sessionId[Count];        // 0 = kernel task; else user session id
        public fixed int uid[Count];              // effective uid (0 = root, -1 = none)
        public fixed int realUid[Count];          // session user's real uid (fixed on sudo)
        public fixed int gid[Count];              // group id
        public fixed int fhInode[Count * 8];          // file handle: inode
        public fixed int fhOff[Count * 8];            // file handle: offset
        public fixed int fhSize[Count * 8];           // file handle: size
        public fixed int fhUsed[Count * 8];           // file handle: in use
        public fixed int fhWrite[Count * 8];          // file handle: 1 = write mode
        public fixed int stdoutH[Count];              // per-task stdout handle (-1 = VGA)
        public fixed int stdinH[Count];               // per-task stdin handle (-1 = console)
        public fixed int userFrames[Count];           // additional user frames (4 KiB each) beyond base 2 MiB
        // MEMGROW block ownership (O8.12 M1): per slot, up to MaxUserBlocks
        // (base phys, frame count) pairs plus the active block count. userFrames
        // stays the running total (for the query/limit); these arrays are the
        // ownership handles Free needs to reclaim the frames.
        public fixed ulong userBlocks[Count * MaxUserBlocks];
        public fixed int userBlockFrames[Count * MaxUserBlocks];
        public fixed int userBlockCount[Count];
    }

    private unsafe struct StackPool { public fixed byte bytes[Count * Layout.StackSize]; }

    private static TaskTable _tasks;
    private static StackPool _stacks;
    private static int _current;                  // slot currently on the CPU
    private static PidAllocator _pids;            // authoritative PID source
    private static int _foreground;               // foreground job slot, or -1

    // Slot currently on the CPU.
    public static int Current
    {
        get => _current;
        set => _current = value;
    }

    // Foreground job slot, or -1 when the shell owns the console.
    public static int Foreground
    {
        get => _foreground;
        set => _foreground = value;
    }

    public static void Init()
    {
        for (int i = 0; i < Count; i++)
        {
            _tasks.state[i] = Layout.TaskFree;
            _tasks.kind[i] = Layout.KindNone;
            _tasks.fg[i] = 0;
            _tasks.pid[i] = 0;
            _tasks.ppid[i] = 0;
            _tasks.exitCode[i] = 0;
            _tasks.sp[i] = 0;
            _tasks.cr3[i] = AddressSpace.Master;
            _tasks.fsbase[i] = 0;
            _tasks.ublock[i] = 0;
            _tasks.ptphys[i] = 0;
            _tasks.sessionId[i] = 0;
            _tasks.uid[i] = 0;
            _tasks.realUid[i] = 0;
            _tasks.gid[i] = 0;
            _tasks.stdoutH[i] = -1;
            _tasks.stdinH[i] = -1;
            _tasks.userFrames[i] = 0;
            _tasks.userBlockCount[i] = 0;
        }
        // Slot 0 is the shell/console, running on the boot stack in the kernel
        // address space. Its saved SP is captured by the first osb_switch out.
        _tasks.state[Console] = Layout.TaskReady;
        _tasks.kind[Console] = Layout.KindShell;
        _tasks.pid[Console] = 1;
        _tasks.sessionId[Console] = 0;   // kernel task (session 0)
        _tasks.uid[Console] = 0;         // root
        _tasks.realUid[Console] = 0;
        _tasks.gid[Console] = 0;
        SetName(Console, "CONSOLE");
        _current = Console;
        // PID sequence resets on every boot (decision 4): a fresh allocator
        // starts at 1. Slot 0 (console) is PID 1, so consume 1 from the
        // allocator here; it then hands out 2,3,... to spawned processes.
        _pids.Reset();
        _pids.TryAllocate(out _);                       // reserve PID 1 for console
        _tasks.pid[Console] = PidAllocator.FirstPid;   // 1
        _tasks.ppid[Console] = 0;                       // no parent
        _foreground = -1;
    }

    // Snapshot of a slot (name points into the table: copy it out before the
    // scheduler reuses the slot).
    public static TaskInfo Get(int slot)
    {
        TaskInfo t;
        t.Pid = _tasks.pid[slot];
        t.State = _tasks.state[slot];
        t.UserBlock = _tasks.ublock[slot];
        fixed (byte* p = _tasks.name)
        {
            t.Name = p + slot * Layout.MaxName;
        }
        int n = 0;
        while (n < Layout.MaxName && t.Name[n] != 0) n++;
t.NameLen = n;
        return t;
    }

    // Seed a task's kernel stack so the first switch into it lands at `entry`.
    // Layout mirrors what osb_switch pushes: [r15][r14][r13][r12][rbp][rbx][ret].
    private static ulong SeedTask(byte* stackBase, int size, ulong entry)
    {
        ulong top = (ulong)stackBase + (ulong)size;
        ulong sp = (top - 64) & ~(ulong)15;           // 16-byte aligned
        for (int i = 0; i < 6; i++) *(ulong*)(sp + (ulong)i * 8) = 0;
        *(ulong*)(sp + 48) = entry;
        return sp;
    }

    private static int AllocSlot()
    {
        for (int i = Console + 1; i < Count; i++)
            if (_tasks.state[i] == Layout.TaskFree) return i;   // skip zombies (not yet reaped)
        return -1;
    }

    private static void SeedSlot(int slot, ulong entry, string name, int kind,
        int sessionId, int realUid, int uid, int gid)
    {
        ulong sp;
        fixed (byte* p = _stacks.bytes)
            sp = SeedTask(p + slot * Layout.StackSize, Layout.StackSize, entry);
        _tasks.sp[slot] = sp;
        // PID is Kernel-owned and never chosen by ring 3 (decisions 6/7). The
        // allocator is monotonic and non-reusing; on the (theoretical) exhausted
        // path it yields 0, which the spawn paths detect and refuse.
        _tasks.pid[slot] = _pids.TryAllocate(out ulong newPid) ? newPid : 0UL;
        _tasks.ppid[slot] = _tasks.pid[_current];
        // Q4: the session/uid fields are set by the caller BEFORE state=READY
        // (the last line), so a task can never be scheduled with stale
        // credentials. Normal spawns pass the parent's; the boot program passes
        // session 1 / uid -1 (it must LOGIN before it can do anything).
        _tasks.sessionId[slot] = sessionId;
        _tasks.realUid[slot] = realUid;
        _tasks.uid[slot] = uid;
        _tasks.gid[slot] = gid;
        _tasks.kind[slot] = kind;
        _tasks.fg[slot] = 0;
        _tasks.exitCode[slot] = 0;
        _tasks.arg[slot] = 0;
        _tasks.cr3[slot] = AddressSpace.Master;
        _tasks.ublock[slot] = 0;
        _tasks.ptphys[slot] = 0;
        _tasks.round[slot] = 0;
        _tasks.deadline[slot] = 0;
        SetName(slot, name);
    }

    // Explicitly commit a prepared task: makes it runnable by setting state=TaskReady.
    // This is the final publication store. All task fields must be initialized before calling.
    private static void CommitTask(int slot)
    {
        _tasks.state[slot] = Layout.TaskReady;
    }

    // Start a kernel job (the demo task). Returns its slot, or -1.
    public static int StartDemo()
    {
        int slot = AllocSlot();
        if (slot < 0) return -1;
        SeedSlot(slot, NativeMethods.osb_sched_get_demo_entry(), "DEMO", Layout.KindDemo,
            0, 0, 0, 0);   // kernel task: session 0, root
        CommitTask(slot);
        return slot;
    }

    // Initialize the per-task TLS object at the top of the user block (M3.4.2).
    // The linker-relaxed NativeAOT sequence is `mov %fs:0,%rax; lea -0xD0(%rax),%rax`,
    // so FS base = TlsTop, the 8 bytes at TlsTop hold TlsTop (self-pointer), and
    // the 208-byte tls_CurrentThread object lives at TlsTop-0xD0 with
    // combined_limit at +0 and alloc_ptr at +8. `block` is the task's private
    // 2 MiB physical block (the kernel is identity-mapped, so it doubles as VA).
    private static void InitTaskTls(int slot, ulong block)
    {
        ulong top = Layout.UserTlsTop(slot);
        byte* b = (byte*)block + (top - Layout.UserBase);
        *(ulong*)b = top;                                   // [FS:0] = TP
        byte* obj = b - Layout.TlsObjectSize;               // tls_CurrentThread
        *(ulong*)obj = Layout.UserTlsHeapEnd;               // combined_limit
        *(ulong*)(obj + 8) = Layout.UserTlsEnd;             // alloc_ptr (default heap)
        _tasks.fsbase[slot] = top;
    }

    // Force-terminate a slot: release its address space and free the slot. Used
    // by KILL and by Ctrl+C on a foreground job.
    public static void Release(int slot)
    {
        if (slot <= Console) return;
        AddressSpace.Release(_tasks.ublock[slot], _tasks.ptphys[slot]);
        // Reclaim additional user frames (MEMGROW blocks) by their recorded
        // physical bases and reset the per-slot accounting (O8.12 M1).
        ReleaseUserFrames(slot);
        _tasks.ublock[slot] = 0;
        _tasks.ptphys[slot] = 0;
        _tasks.cr3[slot] = AddressSpace.Master;
        _tasks.fsbase[slot] = 0;
        _tasks.state[slot] = Layout.TaskFree;
        _tasks.kind[slot] = Layout.KindNone;
        _tasks.fg[slot] = 0;
        SetName(slot, "free");
        if (_foreground == slot) _foreground = -1;
    }

    // Reap a zombie: collect its exit code, then release everything.
    public static int Reap(int slot)
    {
        int code = _tasks.exitCode[slot];
        Release(slot);
        return code;
    }

    // The running task leaves: it becomes a zombie (wait/reap collects it) and
    // the scheduler switches away for good. Never returns.
    public static void ExitCurrent(int code)
    {
        int slot = _current;
        _tasks.exitCode[slot] = code;
        _tasks.state[slot] = Layout.TaskZombie;
        _tasks.fg[slot] = 0;
        if (_foreground == slot) _foreground = -1;
        Preempt();
        for (;;) NativeMethods.halt();
    }

    // Mark a job as the foreground one (the shell waits for it).
    public static void MakeForeground(int slot)
    {
        _foreground = slot;
        _tasks.fg[slot] = 1;
    }

    // wait(pid): block until the process exits, then reap it. Returns the exit
    // code, or -1 if the pid is unknown / already gone.
    public static int WaitFor(ulong pid)
    {
        int slot;
        if (!TryFindPid(pid, out slot)) return -1;
        while (_tasks.state[slot] == Layout.TaskReady)
        {
            NativeMethods.halt();
            if (ConsoleInput.CtrlCPending)
            {
                ConsoleInput.ConsumeCtrlCPending();
                return -7; // EINTR
            }
        }
        if (_tasks.state[slot] == Layout.TaskZombie) return Reap(slot) & 0xFF;
        return -1;   // vanished (e.g. killed)
    }

    // Called from the IRQ0 handler on every tick. Round-robins to the next ready
    // task by swapping kernel stacks; the switch is atomic (interrupt gate) and
    // the interrupted ISR frame stays parked on the stopped task's stack.
    public static void Preempt()
    {
        int cur = _current;
        int next = cur;
        for (int i = 0; i < Count; i++)
        {
            next++;
            if (next >= Count) next = 0;
            if (_tasks.state[next] == Layout.TaskReady) break;
        }
        if (next == cur) return;                      // only one runnable task

        if (_tasks.kind[next] == Layout.KindUser)
            Gdt.SetKernelStack(KernelStackTop(next)); // TSS RSP0 for the next ring-3 task
        NativeMethods.osb_load_cr3(_tasks.cr3[next]);               // per-process address space
        // M3.4.2: FS base is per-CPU and survives iretq/osb_switch unchanged, so
        // every switch-in must establish the target task's thread pointer. Kernel
        // tasks have fsbase 0 (the kernel itself never uses FS).
        NativeMethods.osb_set_fs(_tasks.fsbase[next]);
        _current = next;
        fixed (ulong* sp = _tasks.sp)
            NativeMethods.osb_switch(sp + cur, sp + next);
    }

    // Job body: demo. Runs on its slot's kernel stack, prints once per second and
    // sleeps in between. It never yields: the PIT preempts it. Persists until
    // killed or terminated.
    [RuntimeExport("osb_demo_task_main")]
    private static void DemoTaskMain()
    {
        int slot = _current;
        ulong pid = (ulong)_tasks.pid[slot];
        for (;;)
        {
            if (Pit.Ticks >= _tasks.deadline[slot])
            {
                _tasks.round[slot]++;
                Vga.Write("DEMO [");
                Vga.WriteDec(pid);
                Vga.Write("]: round ");
                Vga.WriteDec(_tasks.round[slot]);
                Vga.Write("  (tick ");
                Vga.WriteDec(Pit.Ticks);
                Vga.WriteLine(")");
                _tasks.deadline[slot] = Pit.Ticks + (ulong)Pit.Hz;   // next round in 1 s
            }
            NativeMethods.halt(); // sleep until the next interrupt; the PIT schedules the rest
        }
    }

    // Job body: ring-3 program. The scheduler has already loaded this process's
    // CR3, so Layout.UserBase (0x02000000) is its private block. Drops to CPL3
    // and never comes back (it exits via INT 21h or faults).
    [RuntimeExport("osb_user_task_main")]
    private static void UserTaskMain()
    {
        int slot = _current;
        // M3.4.2: establish this task's thread pointer before the first iretq to
        // ring 3. Preempt also sets it on every later switch-in.
        NativeMethods.osb_set_fs(_tasks.fsbase[slot]);
        NativeMethods.osb_enter_user(_tasks.arg[slot], Layout.UserStackTop);
        for (;;) NativeMethods.halt();                              // not reached
    }
}