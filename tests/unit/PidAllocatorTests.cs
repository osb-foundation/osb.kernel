// Host-side unit tests for the authoritative PID allocator (Card 3).
//
// Exercises the REAL PidAllocator (Processes/PidAllocator.cs, linked into this
// assembly) through its contract: incremental allocation from 1, uniqueness /
// non-reuse, reset-on-boot, and explicit exhaustion at ulong.MaxValue with no
// wrap-around and no reuse. No hardware, no QEMU. Mirrors the style of
// AllocatorTests/SpinLockTests.
using Osb.Kernel.Processes;

namespace Osb.Kernel.Unit;

internal static class PidAllocatorTests
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        if (ok) System.Console.WriteLine($"PASS: {name}");
        else { failures++; System.Console.WriteLine($"FAIL: {name}"); }
    }

    public static int Run()
    {
        failures = 0;

        // ==== A. First PID after boot is 1, then incremental ====
        var a = new PidAllocator();
        a.Reset();
        Check(!a.Exhausted, "A1: fresh allocator not exhausted");
        Check(a.Peek() == 1, "A2: first PID is 1");
        Check(a.TryAllocate(out ulong p1) && p1 == 1, "A3: first TryAllocate == 1");
        Check(a.TryAllocate(out ulong p2) && p2 == 2, "A4: second == 2 (incremental)");
        Check(a.TryAllocate(out ulong p3) && p3 == 3, "A5: third == 3");
        Check(p1 != p2 && p2 != p3 && p1 != p3, "A6: allocated PIDs are distinct");

        // ==== B. Uniqueness / non-reuse over many allocations ====
        a.Reset();
        const int N = 1000;
        bool allDistinct = true;
        ulong prev = 0;
        bool monotonic = true;
        for (int i = 0; i < N; i++)
        {
            if (!a.TryAllocate(out ulong p)) { allDistinct = false; break; }
            if (p <= prev) monotonic = false;   // strictly increasing => no reuse
            prev = p;
        }
        Check(allDistinct, "B1: 1000 allocations all succeed");
        Check(monotonic, "B2: PIDs strictly increase (no reuse)");
        Check(a.Peek() == N + 1, "B3: next PID after N allocations is N+1");

        // ==== C. Reset restarts the sequence at 1 (new boot) ====
        a.Reset();
        Check(a.Peek() == 1 && !a.Exhausted, "C1: Reset -> next 1, not exhausted");
        Check(a.TryAllocate(out ulong r1) && r1 == 1, "C2: post-reset first PID is 1 again");

        // ==== D. Exhaustion at ulong.MaxValue: no wrap, no reuse ====
        var e = new PidAllocator();
        e.TestOnly_ResetTo(PidAllocator.MaxPid);
        Check(e.Peek() == PidAllocator.MaxPid, "D1: Peek == MaxValue at boundary");
        Check(e.TryAllocate(out ulong last) && last == PidAllocator.MaxPid, "D2: MaxValue is handed out exactly once");
        Check(e.Exhausted, "D3: exhausted after handing out MaxValue");
        Check(e.Peek() == 0, "D4: Peek == 0 when exhausted");
        Check(!e.TryAllocate(out ulong after), "D5: further allocation refused");
        Check(after == 0, "D6: refused allocation yields pid 0 (no wrap to 0/1)");
        Check(!e.TryAllocate(out ulong after2) && after2 == 0, "D7: repeated refusal stays exhausted (no reuse)");

        // ==== E. Reset clears exhaustion ====
        e.Reset();
        Check(!e.Exhausted && e.Peek() == 1, "E1: Reset clears exhaustion, next is 1");

        System.Console.WriteLine();
        if (failures == 0) { System.Console.WriteLine("PIDALLOC: ALL TESTS PASSED"); return 0; }
        System.Console.WriteLine($"PIDALLOC: {failures} TEST(S) FAILED");
        return failures;
    }
}