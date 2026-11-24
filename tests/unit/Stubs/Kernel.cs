// Test-only stub for Kernel.OsAlloc/Kernel.OsFree.
// The real Kernel is freestanding NativeAOT and cannot run under the host BCL.
// This stub provides a simple allocator (Marshal.AllocHGlobal/FreeHGlobal)
// with an optional failure-injection knob so allocator-failure paths can be
// exercised safely in the test process. Not production code.
using System;
using System.Runtime.InteropServices;

namespace Osb.Kernel;

internal static unsafe class Kernel
{
    public static int AllocCalls;
    public static int FailOnAllocCall = -1; // -1 = never fail
    public static bool FailNextAlloc;

    public static unsafe void* OsAlloc(int bytes)
    {
        AllocCalls++;
        if (FailNextAlloc) { FailNextAlloc = false; return null; }
        if (FailOnAllocCall >= 0 && AllocCalls >= FailOnAllocCall) return null;
        return (void*)Marshal.AllocHGlobal(bytes);
    }

    public static unsafe void OsFree(void* ptr, int bytes)
    {
        if (ptr == null) return;
        Marshal.FreeHGlobal((IntPtr)ptr);
    }

    public static void ResetAlloc()
    {
        AllocCalls = 0;
        FailOnAllocCall = -1;
        FailNextAlloc = false;
    }

    public static void FailNext() => FailNextAlloc = true;
}
