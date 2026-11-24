// Test-only stub for Osb.Kernel.Memory.Layout. Provides only the constants the
// Registry consumes (FrameSize, BlockFrames). Not production code.
namespace Osb.Kernel.Memory;

internal static class Layout
{
    public const int FrameSize = 4096;
    public const int BlockFrames = 512; // 2 MiB user block
}
