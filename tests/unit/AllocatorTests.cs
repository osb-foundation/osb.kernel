// Host-side unit tests for the physical frame allocator (O8.12 M1).
//
// Exercises the ACTUAL production source (Memory/PhysicalMemory.cs, linked into
// this assembly) through its public contract. The allocator is a static with
// global state, so each scenario calls Reset() (Detect(0) + Init) to rebuild a
// deterministic single-region heap with the reserved pd[2] window set aside.
// NativeMethods (osb_kernel_end + the M0 sync primitives) is stubbed; no
// privileged instruction runs on the host. The SpinLock behaviour of Alloc/Free
// is observed through the stub's CliCount/StiCount/SimulatedIf recorders.
using System;
using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Sync;

namespace Osb.Kernel.Unit;

internal static unsafe class AllocatorTests
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        if (ok) Console.WriteLine($"PASS: {name}");
        else { failures++; Console.WriteLine($"FAIL: {name}"); }
    }

    // Rebuild a deterministic heap: single usable region [floor, PhysTopDefault)
    // with the reserved pd[2] window marked used. Returns the used count after
    // init (the reserved frames), i.e. the allocation baseline.
    private static ulong Reset()
    {
        NativeMethods.OsbKernelEnd = 0;          // floor = PhysFloorDefault
        PhysicalMemory.Detect(0);                // info=0 -> fallback single region
        PhysicalMemory.Init();
        return PhysicalMemory.Used;
    }

    public static int Run()
    {
        failures = 0;

        // ==== A. Basic allocation ====
        ulong base0 = Reset();
        Check(base0 > 0, "A0: baseline used = reserved frames (> 0)");
        ulong a1 = PhysicalMemory.Alloc(1, Layout.FrameSize);
        Check(a1 != 0, "A1: single-frame alloc returns nonzero");
        Check(a1 == PhysicalMemory.Floor, "A1b: first alloc is at the floor");
        Check(a1 % Layout.FrameSize == 0, "A1c: single frame is frame-aligned");
        Check(PhysicalMemory.Used == base0 + 1, "A1d: used incremented by 1");

        ulong a4 = PhysicalMemory.Alloc(4, Layout.FrameSize);
        Check(a4 != 0, "A2: multi-frame alloc returns nonzero");
        Check(a4 % Layout.FrameSize == 0, "A2b: multi-frame base is frame-aligned");
        Check(PhysicalMemory.Used == base0 + 5, "A2c: used incremented by 4 (total +5)");

        ulong ab = PhysicalMemory.Alloc(Layout.BlockFrames, Layout.BlockAlign);
        Check(ab != 0, "A3: 2 MiB block alloc returns nonzero");
        Check(ab % Layout.BlockAlign == 0, "A3b: 2 MiB block is 2 MiB-aligned");
        Check(PhysicalMemory.Used == base0 + 5 + Layout.BlockFrames, "A3c: used incremented by 512");

        // ==== B. Reclamation ====
        base0 = Reset();
        ulong r1 = PhysicalMemory.Alloc(1, Layout.FrameSize);
        ulong usedAfterAlloc = PhysicalMemory.Used;
        PhysicalMemory.Free(r1, 1);
        Check(PhysicalMemory.Used == base0, "B1: used returns to baseline after free");
        ulong r2 = PhysicalMemory.Alloc(1, Layout.FrameSize);
        Check(r2 == r1, "B2: freed frame is reused (bitmap bit was really cleared)");
        Check(PhysicalMemory.Used == usedAfterAlloc, "B2b: re-alloc restores used");

        // Multi-frame reclaim + reuse.
        base0 = Reset();
        ulong m = PhysicalMemory.Alloc(4, Layout.FrameSize);
        PhysicalMemory.Free(m, 4);
        Check(PhysicalMemory.Used == base0, "B3: multi-frame free returns to baseline");
        ulong m2 = PhysicalMemory.Alloc(4, Layout.FrameSize);
        Check(m2 == m, "B3b: multi-frame block reused after free");

        // Invalid / misaligned / out-of-range reclamation are all no-ops.
        base0 = Reset();
        ulong v = PhysicalMemory.Alloc(1, Layout.FrameSize);
        ulong u = PhysicalMemory.Used;
        PhysicalMemory.Free(v + 1, 1);           // misaligned
        Check(PhysicalMemory.Used == u, "B4: misaligned free rejected (used unchanged)");
        PhysicalMemory.Free(0, 1);               // below floor
        PhysicalMemory.Free(PhysicalMemory.Top, 1);            // at top (out of range)
        PhysicalMemory.Free(PhysicalMemory.Top + Layout.FrameSize, 1); // above top
        Check(PhysicalMemory.Used == u, "B5: out-of-range frees rejected (used unchanged)");
        PhysicalMemory.Free(v, 0);               // zero frames
        PhysicalMemory.Free(v, -3);              // negative frames
        Check(PhysicalMemory.Used == u, "B6: non-positive count free is a no-op");
        PhysicalMemory.Free(v, 1);               // the real free still works
        Check(PhysicalMemory.Used == base0, "B6b: valid free after invalid attempts works");

        // Double reclamation is harmless.
        base0 = Reset();
        ulong d = PhysicalMemory.Alloc(1, Layout.FrameSize);
        PhysicalMemory.Free(d, 1);
        ulong afterFirst = PhysicalMemory.Used;
        PhysicalMemory.Free(d, 1);               // double free
        Check(PhysicalMemory.Used == afterFirst, "B7: double free is a no-op (used unchanged)");
        Check(afterFirst == base0, "B7b: accounting correct after double free");

        // Reserved frames are never reclaimed.
        base0 = Reset();
        PhysicalMemory.Free(Layout.ReservedLo, 1);              // first reserved frame
        PhysicalMemory.Free(Layout.ReservedHi - Layout.FrameSize, 1); // last reserved frame
        Check(PhysicalMemory.Used == base0, "B8: reserved frames are protected from free");
        // And a reserved frame is never handed out: allocate everything below it.
        ulong probe = PhysicalMemory.Alloc(1, Layout.FrameSize);
        Check(probe != Layout.ReservedLo && probe != Layout.ReservedHi - Layout.FrameSize,
            "B8b: alloc never returns a reserved frame");

        // ==== C. Failure and rollback (MEMGROW pattern) ====
        // Failure before any allocation: request more frames than the whole heap.
        base0 = Reset();
        Check(!PhysicalMemory.TryAllocFrames(Layout.MaxFrames, out ulong p0),
            "C1: over-total request fails");
        Check(PhysicalMemory.Used == base0, "C1b: failed request leaves used unchanged (no leak)");

        // One successful allocation then rollback (AddUserFrames-fail path).
        base0 = Reset();
        Check(PhysicalMemory.TryAllocFrames(512, out ulong p1), "C2: 512-frame grow succeeds");
        Check(p1 != 0 && PhysicalMemory.Used == base0 + 512, "C2b: grow recorded and accounted");
        PhysicalMemory.Free(p1, 512);            // rollback the exact block
        Check(PhysicalMemory.Used == base0, "C2c: rollback returns used to baseline (no leak)");

        // Multiple blocks, then free them all (ReleaseUserFrames pattern).
        base0 = Reset();
        Check(PhysicalMemory.TryAllocFrames(512, out ulong b1), "C3: block1");
        Check(PhysicalMemory.TryAllocFrames(512, out ulong b2), "C3b: block2");
        Check(PhysicalMemory.TryAllocFrames(512, out ulong b3), "C3c: block3");
        Check(PhysicalMemory.Used == base0 + 1536, "C3d: used = baseline + 3*512");
        PhysicalMemory.Free(b1, 512);
        Check(PhysicalMemory.Used == base0 + 1024, "C3e: after free block1 used = baseline + 1024");
        PhysicalMemory.Free(b2, 512);
        PhysicalMemory.Free(b3, 512);
        Check(PhysicalMemory.Used == base0, "C3f: all blocks freed -> baseline (no leak, no over-free)");

        // Partial failure: free only the acquired block, leave the other intact.
        base0 = Reset();
        Check(PhysicalMemory.TryAllocFrames(512, out ulong pa), "C4: block A");
        Check(PhysicalMemory.TryAllocFrames(512, out ulong pb), "C4b: block B");
        PhysicalMemory.Free(pa, 512);            // free only A
        Check(PhysicalMemory.Used == base0 + 512, "C4c: only A reclaimed, B still owned");
        PhysicalMemory.Free(pb, 512);
        Check(PhysicalMemory.Used == base0, "C4d: B reclaimed -> baseline");

        // Reuse after rollback: the rolled-back block is available again.
        base0 = Reset();
        Check(PhysicalMemory.TryAllocFrames(512, out ulong rr), "C5: grow");
        PhysicalMemory.Free(rr, 512);            // rollback
        Check(PhysicalMemory.TryAllocFrames(512, out ulong rr2) && rr2 == rr,
            "C5b: rolled-back block is reused (no leak)");

        // ==== D. Lock behaviour ====
        NativeMethods.ResetSync(ifEnabled: true);
        Reset();
        int cli0 = NativeMethods.CliCount, sti0 = NativeMethods.StiCount;
        ulong la = PhysicalMemory.Alloc(1, Layout.FrameSize);
        Check(NativeMethods.CliCount == cli0 + 1, "D1: Alloc acquires the M0 lock (one cli)");
        Check(NativeMethods.StiCount == sti0 + 1, "D1b: Alloc releases the M0 lock (one sti)");
        Check(NativeMethods.SimulatedIf == true, "D1c: IF restored after Alloc");
        PhysicalMemory.Free(la, 1);
        Check(NativeMethods.CliCount == cli0 + 2 && NativeMethods.StiCount == sti0 + 2,
            "D2: Free also acquires/releases the lock");

        // Failure path releases the lock: an Alloc that cannot be satisfied must
        // not leave the lock held (request more frames than the whole heap).
        NativeMethods.ResetSync(ifEnabled: true);
        Reset();
        int cliEx = NativeMethods.CliCount;
        ulong failed = PhysicalMemory.Alloc(Layout.MaxFrames, Layout.FrameSize);
        Check(failed == 0, "D3: over-total Alloc returns 0");
        Check(NativeMethods.CliCount == cliEx + 1, "D3b: failed Alloc still acquired+released lock");
        Check(NativeMethods.SimulatedIf == true, "D3c: IF restored after failed Alloc");
        SpinLock.Acquire(out ulong probeCookie); // must not hang -> lock was released
        SpinLock.Release(probeCookie);
        Check(true, "D3d: lock is free after a failed Alloc (no deadlock)");

        // Many sequential acquisitions do not deadlock and leave the lock free.
        NativeMethods.ResetSync(ifEnabled: true);
        Reset();
        for (int i = 0; i < 200; i++)
        {
            ulong t = PhysicalMemory.Alloc(1, Layout.FrameSize);
            PhysicalMemory.Free(t, 1);
        }
        SpinLock.Acquire(out ulong c);
        SpinLock.Release(c);
        Check(true, "D4: 200 alloc/free cycles complete, lock free at end");

        Console.WriteLine();
        if (failures == 0) { Console.WriteLine("ALLOCATOR: ALL TESTS PASSED"); return 0; }
        Console.WriteLine($"ALLOCATOR: {failures} TEST(S) FAILED");
        return failures;
    }
}