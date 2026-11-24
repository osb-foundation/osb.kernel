// Test-only stub for Osb.Kernel.Hardware.NativeMethods.
// The real methods are [DllImport("osbnative")] and cannot run under the host.
// These return fixed user-blob bounds so ProgramRegistry.Initialize() can
// register HELLO/BAD/SPAWNER on the host. OsbSpawnerEnd is settable so tests
// can force a mid-initialization failure (end <= start). Not production code.
namespace Osb.Kernel.Hardware;

internal static class NativeMethods
{
    public static ulong OsbUserStubBegin = 0x00100000;
    public static ulong OsbUserStubEnd = 0x00101000;
    public static ulong OsbUserBadBegin = 0x00102000;
    public static ulong OsbUserBadEnd = 0x00103000;
    public static ulong OsbSpawnerBegin = 0x00104000;
    public static ulong OsbSpawnerEnd = 0x00105000;

    public static ulong osb_userstub_begin() => OsbUserStubBegin;
    public static ulong osb_userstub_end() => OsbUserStubEnd;
    public static ulong osb_userbad_begin() => OsbUserBadBegin;
    public static ulong osb_userbad_end() => OsbUserBadEnd;
    public static ulong osb_spawner_begin() => OsbSpawnerBegin;
    public static ulong osb_spawner_end() => OsbSpawnerEnd;
}
