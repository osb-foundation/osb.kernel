// Test-only stub for Osb.Kernel.Memory.Layout. Provides the constants the
// linked production PhysicalMemory.cs consumes (and the Registry's FrameSize /
// BlockFrames). Values mirror the real Layout defaults so the allocator behaves
// as it does in the kernel. Not production code.
namespace Osb.Kernel.Memory;

internal static class Layout
{
    public const int FrameSize = 4096;
    public const int BlockFrames = 512;              // 2 MiB user block
    public const ulong BlockAlign = 0x200000;

    // Physical frame allocator window (matches real Layout defaults).
    public const ulong PhysFloorDefault = 0x200000;  // just above the kernel image
    public const ulong PhysTopDefault = 0x4000000;   // 64 MiB (QEMU -m 64M)
    public const ulong PhysMapLimit = 0x1000000000UL; // 64 GiB identity-map ceiling
    public const int MaxFrames = 16777216;           // 64 GiB / 4 KiB

    // Reserved pd[2] window: frames here are never handed out or reclaimed.
    public const ulong ReservedLo = 0x2000000;
    public const ulong ReservedHi = 0x2200000;
}