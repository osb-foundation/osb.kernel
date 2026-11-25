// M0 SpinLock kernel/QEMU self-test (O8.11).
//
// Exercises the REAL assembly primitives (osb_save_and_cli,
// osb_restore_interrupt_state, osb_spinlock_cas) in ring 0 at early boot.
// This is the only place the privileged instructions execute for real; the
// hosted unit tests use a simulated stub.
//
// SCOPE: this runs at the IF=0 path only (boot default). It deliberately does
// NOT execute the sti branch of osb_restore_interrupt_state: the IDT is not
// loaded yet (Interrupts.Init runs later) and the PIT handler calls
// Scheduler.Preempt() before Scheduler.Init, so enabling interrupts here would
// triple-fault or preempt an uninitialized scheduler. The sti branch is
// validated by the hosted unit tests (simulated IF) and the NativeAOT codegen
// experiment (O8.11 evidence), not by this ring-0 boot path.
//
// The test is silent on success except for a single OK marker written to the
// serial console (stripped by the golden/smoke harnesses, never written to the
// VGA buffer). On failure it returns false and the caller prints and halts.

using Osb.Kernel.Hardware;

namespace Osb.Kernel.Sync;

internal static unsafe partial class SpinLock
{
    internal static bool SelfTest()
    {
        bool ok = true;

        // IF=0 path (boot default): save IF=0, cli, CAS 0->1, restore (no sti).
        int w = 0;
        int* p = &w;
        AcquireOn(p, out ulong c0);
        if (*p != 1) ok = false;              // acquired
        ReleaseOn(p, c0);
        if (*p != 0) ok = false;              // released

        // Nested distinct locks (still IF=0): acquire A then B, release B then A.
        int a = 0, b = 0;
        int* pa = &a;
        int* pb = &b;
        AcquireOn(pa, out ulong ca);
        AcquireOn(pb, out ulong cb);
        if (*pa != 1 || *pb != 1) ok = false;
        ReleaseOn(pb, cb);
        if (*pb != 0 || *pa != 1) ok = false; // inner freed, outer held
        ReleaseOn(pa, ca);
        if (*pa != 0) ok = false;             // outer freed

        if (ok) SerialSay("SPINLOCK_SELFTEST_OK\n");
        return ok;
    }

    private static void SerialSay(string s)
    {
        for (int i = 0; i < s.Length; i++) Serial.PutChar(s[i]);
    }
}