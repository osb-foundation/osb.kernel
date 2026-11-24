// Spawn-lease tracking for the Program Registry (M1.1.1).
//
// Multiple independent leases per ProgramId are supported through a
// Registry-owned, dynamically expandable lease table. No managed collections
// or managed-heap allocations are used. The system is single-core; no SMP
// safety is claimed and no SMP synchronization infrastructure is introduced.

using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

// One outstanding spawn lease. Multiple entries may be active for the same
// ProgramId, each with a distinct monotonic token, so leases are independent.
internal unsafe struct LeaseEntry
{
    public uint ProgramId;
    public uint Token;
    public bool Active;
}

internal static unsafe partial class ProgramRegistry
{
    // Monotonic lease token generator; token 0 is reserved as invalid.
    private static uint _leaseCounter;
    // Dynamically expandable lease table. Entries are slots; Active marks an
    // outstanding lease. Slots are reused after release so the table stays
    // compact without managed collections.
    private static LeaseEntry* _leases;
    private static int _leaseSlotCount;  // high-water index into _leases
    private static int _leaseCapacity;   // allocated slots in _leases

    // Acquire a spawn lease before beginning executable loading. A disabled
    // program cannot acquire a new lease. Each acquisition creates a distinct
    // lease identity; multiple leases for the same ProgramId may coexist and
    // are released independently. Lease accounting cannot overflow.
    public static bool TryAcquireSpawnLease(uint programId, out SpawnLease lease)
    {
        lease = default;
        if (!_initialized || programId == 0) return false;
        int idx = FindById(programId);
        if (idx < 0) return false;
        if (!_descriptors[idx].Enabled) return false;
        if (_descriptors[idx].SpawnLeases == uint.MaxValue) return false;

        uint token = NextLeaseToken();
        if (token == 0) return false; // token exhaustion

        // Allocate (or reuse) a lease-table slot.
        int slot = FindInactiveLeaseSlot();
        if (slot < 0)
        {
            if (_leaseSlotCount >= _leaseCapacity)
            {
                if (!GrowLeases(_leaseCapacity == 0 ? 16 : _leaseCapacity * 2)) return false;
            }
            slot = _leaseSlotCount++;
        }

        _leases[slot].ProgramId = programId;
        _leases[slot].Token = token;
        _leases[slot].Active = true;
        _descriptors[idx].SpawnLeases++;

        lease.ProgramId = programId;
        lease.Token = token;
        return true;
    }

    // Release a spawn lease. Only the exact (ProgramId, Token) pair that is
    // currently active may be released, exactly once. Invalid, duplicated,
    // stale, or mismatched tokens do not touch the active lease count.
    public static bool ReleaseSpawnLease(SpawnLease lease)
    {
        if (!_initialized || lease.ProgramId == 0 || lease.Token == 0) return false;
        int idx = FindById(lease.ProgramId);
        if (idx < 0) return false;
        int slot = FindLeaseSlot(lease.ProgramId, lease.Token);
        if (slot < 0 || !_leases[slot].Active) return false;
        if (_descriptors[idx].SpawnLeases == 0) return false;
        _leases[slot].Active = false;
        _leases[slot].ProgramId = 0;
        _leases[slot].Token = 0;
        _descriptors[idx].SpawnLeases--;
        return true;
    }

    // Monotonic lease token; token 0 is reserved as invalid. The returned
    // token is guaranteed not to collide with any currently active lease, so
    // multiple outstanding leases are independently identifiable. Token
    // reuse within a session is avoided; on exhaustion acquisition fails
    // safely rather than reusing an active token.
    private static uint NextLeaseToken()
    {
        int maxAttempts = _leaseCapacity + 16;
        if (maxAttempts < 16) maxAttempts = 16;
        for (int i = 0; i < maxAttempts; i++)
        {
            uint t = ++_leaseCounter;
            if (t == 0) { _leaseCounter = 1; t = 1; } // skip invalid token 0
            if (!IsTokenActive(t)) return t;
        }
        return 0; // token exhaustion; fail safely
    }

    private static bool IsTokenActive(uint token)
    {
        if (token == 0) return true;
        for (int i = 0; i < _leaseSlotCount; i++)
            if (_leases[i].Active && _leases[i].Token == token) return true;
        return false;
    }

    // First inactive lease-table slot, or -1 when every allocated slot is active.
    private static int FindInactiveLeaseSlot()
    {
        for (int i = 0; i < _leaseSlotCount; i++)
            if (!_leases[i].Active) return i;
        return -1;
    }

    // Lease-table slot holding the given (ProgramId, Token), active or not.
    private static int FindLeaseSlot(uint programId, uint token)
    {
        for (int i = 0; i < _leaseSlotCount; i++)
            if (_leases[i].ProgramId == programId && _leases[i].Token == token) return i;
        return -1;
    }

    // Geometric (x2) growth of the lease table via the kernel allocator. The
    // old buffer is released only after the new allocation and copy succeed.
    private static bool GrowLeases(int newCap)
    {
        if (newCap < _leaseCapacity) newCap = _leaseCapacity;
        if (newCap <= 0) newCap = 16;

        ulong bytes = (ulong)newCap * (ulong)sizeof(LeaseEntry);
        if (bytes > int.MaxValue) return false;
        int bytesInt = (int)bytes;

        var ptr = (LeaseEntry*)Kernel.OsAlloc(bytesInt);
        if (ptr == null) return false;

        for (int i = 0; i < bytesInt; i++) ((byte*)ptr)[i] = 0;
        if (_leases != null && _leaseSlotCount > 0)
        {
            for (int i = 0; i < _leaseSlotCount; i++) ptr[i] = _leases[i];
        }
        if (_leases != null)
        {
            int oldBytes = _leaseCapacity * sizeof(LeaseEntry);
            int oldFrames = (oldBytes + Layout.FrameSize - 1) / Layout.FrameSize;
            if (oldFrames > 0) Kernel.OsFree(_leases, oldBytes);
        }
        _leases = ptr;
        _leaseCapacity = newCap;
        return true;
    }
}
