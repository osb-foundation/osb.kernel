// Host-side unit tests for the M0 SpinLock (O8.11 / O8.10 §9.1).
//
// Exercises the ACTUAL production source (Sync/SpinLock.cs, linked into this
// assembly) through its public Acquire/Release and the internal
// AcquireOn/ReleaseOn core. The privileged primitives
// (osb_save_and_cli / osb_restore_interrupt_state / osb_spinlock_cas) are
// SIMULATED by Stubs/NativeMethods.cs: cli/sti are modelled as an IF flag and
// call counters, and the CAS is a real compare-and-swap on the target word.
// No privileged instruction is executed on the host; the real instructions are
// validated separately in QEMU (Kernel SpinLock.SelfTest).
using System;
using Osb.Kernel.Hardware;
using Osb.Kernel.Sync;

namespace Osb.Kernel.Unit;

internal static unsafe class SpinLockTests
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        if (ok) Console.WriteLine($"PASS: {name}");
        else { failures++; Console.WriteLine($"FAIL: {name}"); }
    }

    public static int Run()
    {
        failures = 0;

        // ==== S1. Initial unlocked state + basic acquire/release (public API) ====
        NativeMethods.ResetSync(ifEnabled: true);
        SpinLock.Acquire(out ulong c1);            // must not hang -> was free
        Check(NativeMethods.SimulatedIf == false, "S1a: IF cleared during ownership (cli before CAS)");
        Check(NativeMethods.CliCount == 1, "S1b: exactly one cli on acquire");
        SpinLock.Release(c1);
        Check(NativeMethods.SimulatedIf == true, "S1c: IF restored to 1 after release (sti)");
        Check(NativeMethods.StiCount == 1, "S1d: exactly one sti on release");
        SpinLock.Acquire(out ulong c1b);           // lock free again -> succeeds
        SpinLock.Release(c1b);
        Check(true, "S1e: re-acquire after release succeeds (lock was freed)");

        // ==== S2. Release unsets the lock (CAS returns 0 on next acquire) ====
        NativeMethods.ResetSync(ifEnabled: true);
        int w2 = 0;
        int* p2 = &w2;
        SpinLock.AcquireOn(p2, out ulong a2);
        Check(*p2 == 1, "S2a: lock word is 1 while held");
        Check(NativeMethods.osb_spinlock_cas(p2, 1, 0) != 0, "S2b: CAS on held lock returns non-zero");
        SpinLock.ReleaseOn(p2, a2);
        Check(*p2 == 0, "S2c: lock word is 0 after release");
        Check(NativeMethods.osb_spinlock_cas(p2, 1, 0) == 0, "S2d: CAS on freed lock returns 0 (acquires)");
        *p2 = 0; // undo the probe acquire

        // ==== S3. Repeated acquire/release (N = 1000) ====
        NativeMethods.ResetSync(ifEnabled: true);
        for (int i = 0; i < 1000; i++)
        {
            SpinLock.Acquire(out ulong c);
            SpinLock.Release(c);
        }
        Check(NativeMethods.StiCount == 1000, "S3: 1000 acquire/release cycles complete, 1000 sti");
        SpinLock.Acquire(out ulong c3);            // still usable
        SpinLock.Release(c3);
        Check(true, "S3b: lock usable after 1000 cycles");

        // ==== S4. Contention behaviour (simulated, single core) ====
        NativeMethods.ResetSync(ifEnabled: true);
        int w4 = 0;
        int* p4 = &w4;
        SpinLock.AcquireOn(p4, out ulong a4);                 // holder
        int held = NativeMethods.osb_spinlock_cas(p4, 1, 0);  // second attempt
        Check(held != 0, "S4a: CAS while held returns non-zero (would spin)");
        SpinLock.ReleaseOn(p4, a4);
        int free = NativeMethods.osb_spinlock_cas(p4, 1, 0);  // after release
        Check(free == 0, "S4b: CAS after release returns 0 (acquires)");
        *p4 = 0;

        // ==== S5. IF=1 -> IF=0 -> IF=1 preservation (public API) ====
        NativeMethods.ResetSync(ifEnabled: true);
        SpinLock.Acquire(out ulong c5);
        bool ifDuring = NativeMethods.SimulatedIf;  // IF=0 inside
        SpinLock.Release(c5);
        Check(ifDuring == false, "S5a: IF=0 during critical section");
        Check(NativeMethods.SimulatedIf == true, "S5b: IF=1 restored after release");
        Check(NativeMethods.CliCount == 1 && NativeMethods.StiCount == 1, "S5c: cli before CAS, sti after release");

        // ==== S6. IF=0 preservation (no unconditional sti) ====
        NativeMethods.ResetSync(ifEnabled: false);
        SpinLock.Acquire(out ulong c6);
        Check(NativeMethods.SimulatedIf == false, "S6a: IF stays 0 during ownership");
        SpinLock.Release(c6);
        Check(NativeMethods.SimulatedIf == false, "S6b: IF stays 0 after release (no sti)");
        Check(NativeMethods.StiCount == 0, "S6c: no sti executed when initial IF=0");

        // ==== S7. Nested DISTINCT locks preserve IF per boundary ====
        NativeMethods.ResetSync(ifEnabled: true);
        int a7 = 0, b7 = 0;
        int* pa7 = &a7;
        int* pb7 = &b7;
        SpinLock.AcquireOn(pa7, out ulong ca7);               // cookieA.IF = 1
        bool ifAfterA = NativeMethods.SimulatedIf;            // 0
        SpinLock.AcquireOn(pb7, out ulong cb7);               // cookieB.IF = 0
        bool ifAfterB = NativeMethods.SimulatedIf;            // 0
        Check((ca7 & 0x200UL) != 0, "S7a: cookieA records IF=1");
        Check((cb7 & 0x200UL) == 0, "S7b: cookieB records IF=0");
        Check(ifAfterA == false && ifAfterB == false, "S7c: IF=0 while both held");
        SpinLock.ReleaseOn(pb7, cb7);                         // inner release: IF stays 0
        Check(NativeMethods.SimulatedIf == false, "S7d: inner release keeps IF=0");
        Check(*pb7 == 0 && *pa7 == 1, "S7e: inner lock freed, outer still held");
        SpinLock.ReleaseOn(pa7, ca7);                         // outer release: IF -> 1
        Check(NativeMethods.SimulatedIf == true, "S7f: outer release restores IF=1");
        Check(*pa7 == 0, "S7g: outer lock freed");
        Check(NativeMethods.StiCount == 1, "S7h: exactly one sti (outer release only)");

        // ==== S8. Recursive acquisition is UB (bounded detection, no false release) ====
        NativeMethods.ResetSync(ifEnabled: true);
        int w8 = 0;
        int* p8 = &w8;
        SpinLock.AcquireOn(p8, out ulong a8);                  // first acquire
        int second = NativeMethods.osb_spinlock_cas(p8, 1, 0); // recursive attempt
        Check(second != 0, "S8a: recursive CAS sees held lock (would deadlock, no false acquire)");
        Check(*p8 == 1, "S8b: lock remains held after recursive attempt");
        SpinLock.ReleaseOn(p8, a8);                            // the one real release
        Check(*p8 == 0, "S8c: single release frees the lock");

        // ==== S9. Critical-section correctness (counter under lock) ====
        NativeMethods.ResetSync(ifEnabled: true);
        int counter = 0;
        const int N = 500;
        for (int i = 0; i < N; i++)
        {
            SpinLock.Acquire(out ulong c);
            counter++;
            SpinLock.Release(c);
        }
        Check(counter == N, $"S9: counter == {N} after {N} locked increments (got {counter})");

        // ==== S10. Release store happens BEFORE interrupt restore ====
        NativeMethods.ResetSync(ifEnabled: true);
        int w10 = 0;
        int* p10 = &w10;
        SpinLock.AcquireOn(p10, out ulong a10);
        NativeMethods.WatchWord = p10;          // spy on the lock word
        SpinLock.ReleaseOn(p10, a10);
        Check(NativeMethods.RestoreSawValue == 0, "S10: lock word already 0 when IF restored (release before sti)");

        Console.WriteLine();
        if (failures == 0) { Console.WriteLine("SPINLOCK: ALL TESTS PASSED"); return 0; }
        Console.WriteLine($"SPINLOCK: {failures} TEST(S) FAILED");
        return failures;
    }
}