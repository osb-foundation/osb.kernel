// Test-only isolation helper for the Program Registry (M1.2.1).
//
// The Registry keeps all state as private statics, so a single host process
// cannot construct two independent Registry instances. Existing sections A-E
// leave the Registry initialized, so Test 1 (which requires a genuinely fresh,
// uninitialized Registry whose first OsAlloc fails) needs the mutable static
// state rolled back to pre-initialization before it runs.
//
// This test-only partial class resets every mutable Registry + lease-table
// field and frees any retained descriptor/lease storage through the test stub
// allocator, so no storage survives across test scenarios.
//
// Compiled ONLY into the host unit-test assembly (tests/unit). It is NOT part
// of the production kernel build (osb.kernel/Osb.Kernel.csproj does not
// reference tests/unit), so no production reset API is introduced.

namespace Osb.Kernel.Processes
{
    internal static unsafe partial class ProgramRegistry
    {
        // Roll back all mutable registry and lease-table state to
        // pre-initialization. Frees retained storage via the test stub.
        public static void TestOnly_Reset()
        {
            if (_descriptors != null)
            {
                int bytes = _capacity * sizeof(ProgramDescriptor);
                if (bytes > 0) Kernel.OsFree(_descriptors, bytes);
                _descriptors = null;
            }
            _count = 0;
            _capacity = 0;
            _nextId = 0;
            _initialized = false;

            if (_leases != null)
            {
                int bytes = _leaseCapacity * sizeof(LeaseEntry);
                if (bytes > 0) Kernel.OsFree(_leases, bytes);
                _leases = null;
            }
            _leaseSlotCount = 0;
            _leaseCapacity = 0;
            _leaseCounter = 0;
        }
    }
}
