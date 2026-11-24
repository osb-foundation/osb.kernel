// Common Loader — source-independent executable loading.
// Validates common executable invariants and creates the AddressSpace.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

internal unsafe struct ResolvedExecutable
{
    public byte* Data;
    public ulong Length;
    public ulong EntryOffset;
}

internal static unsafe class CommonLoader
{
    // Attempts to load an executable into a new AddressSpace.
    // Returns true on success; the caller reads AddressSpace.Block/Cr3/PageTables.
    // On failure, no AddressSpace is exposed and no resources are leaked.
    public static bool TryLoad(in ResolvedExecutable exe)
    {
        if (exe.Data == null) return false;
        if (exe.Length == 0) return false;
        if (exe.Length > (ulong)Layout.BlockFrames * Layout.FrameSize) return false;
        if (exe.EntryOffset >= exe.Length) return false;

        return AddressSpace.Create(exe.Data, exe.Length);
    }
}