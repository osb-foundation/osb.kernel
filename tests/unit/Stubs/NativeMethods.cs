// Test-only stub for Osb.Kernel.Hardware.NativeMethods.
// The real methods are [DllImport("osbnative")] and cannot run under the host.
// These return fixed user-blob bounds so ProgramRegistry.Initialize() can
// register HELLO/BAD/SPAWNER on the host. OsbSpawnerEnd is settable so tests
// can force a mid-initialization failure (end <= start). Not production code.
//
// It also simulates the three M0 synchronization primitives
// (osb_save_and_cli / osb_restore_interrupt_state / osb_spinlock_cas) so the
// real Osb.Kernel.Sync.SpinLock source can be exercised on the host WITHOUT
// executing privileged cli/sti. The simulation models the IF (bit 9) semantics
// exactly and performs a real compare-and-swap on the given word.
namespace Osb.Kernel.Hardware;

internal static unsafe class NativeMethods
{
    public static ulong OsbUserStubBegin = 0x00100000;
    public static ulong OsbUserStubEnd = 0x00101000;
    public static ulong OsbUserBadBegin = 0x00102000;
    public static ulong OsbUserBadEnd = 0x00103000;
    public static ulong OsbSpawnerBegin = 0x00104000;
    public static ulong OsbSpawnerEnd = 0x00105000;

    // PhysicalMemory.Detect reads the kernel end to compute the allocator floor.
    // Default 0 makes the floor fall back to Layout.PhysFloorDefault; tests may
    // set it to exercise the "floor follows the image" path.
    public static ulong OsbKernelEnd;
    public static ulong osb_kernel_end() => OsbKernelEnd;

    public static ulong osb_userstub_begin() => OsbUserStubBegin;
    public static ulong osb_userstub_end() => OsbUserStubEnd;
    public static ulong osb_userbad_begin() => OsbUserBadBegin;
    public static ulong osb_userbad_end() => OsbUserBadEnd;
    public static ulong osb_spawner_begin() => OsbSpawnerBegin;
    public static ulong osb_spawner_end() => OsbSpawnerEnd;

    // ---- Simulated M0 synchronization primitives (test only) ----
    // Simulated interrupt-enable state (IF) and call recorders.
    public static bool SimulatedIf;
    public static int CliCount;
    public static int StiCount;
    // Optional spy: when set, osb_restore_interrupt_state records the value at
    // this word so a test can prove the lock was cleared BEFORE IF was restored.
    public static int* WatchWord;
    public static int RestoreSawValue = -1;

    public static void ResetSync(bool ifEnabled)
    {
        SimulatedIf = ifEnabled;
        CliCount = 0;
        StiCount = 0;
        WatchWord = null;
        RestoreSawValue = -1;
    }

    // ulong osb_save_and_cli(): capture old RFLAGS (IF in bit 9), then cli.
    public static ulong osb_save_and_cli()
    {
        CliCount++;
        ulong rflags = SimulatedIf ? 0x200UL : 0UL;   // bit 9 = IF
        SimulatedIf = false;                           // cli
        return rflags;
    }

    // void osb_restore_interrupt_state(ulong rflags): conditional sti on bit 9.
    public static void osb_restore_interrupt_state(ulong rflags)
    {
        if (WatchWord != null) RestoreSawValue = *WatchWord;
        if ((rflags & 0x200UL) != 0) { StiCount++; SimulatedIf = true; }
        // else: no sti, IF unchanged
    }

    // int osb_spinlock_cas(int* addr, int newVal, int oldVal): real CAS.
    public static int osb_spinlock_cas(int* addr, int newVal, int oldVal)
    {
        int found = *addr;
        if (found == oldVal) *addr = newVal;
        return found;
    }
}