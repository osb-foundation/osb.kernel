// Dynamic Program Registry (M1.1).
//
// Session-scoped, dynamically growable registry of program definitions.
// Internal Program IDs are uint (zero reserved and never reused within a
// kernel session). Storage is a contiguous descriptor array allocated through
// the kernel allocator (Kernel.OsAlloc/Kernel.OsFree), growing geometrically
// (x2) from an initial capacity of 16; there is no fixed 256-entry limit.
//
// Concurrency: the system is single-core and has no SMP synchronization
// primitive. All Registry mutations and lookups are encapsulated behind the
// public contract so a future lock can be added without changing callers, but
// the immediate implementation is single-core only and makes NO SMP safety
// claim. Interrupts are not relied upon as a synchronization mechanism.
//
// Spawn-lease identities live in a separate Registry-owned lease table
// (EmbeddedRegistry.Leases.cs); the descriptor itself carries only the
// outstanding-lease count. The descriptor carries program-definition metadata
// only: no PID, scheduler slot, address space, or per-process runtime state.
// External callers never receive internal pointers; descriptors are returned
// by value.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;

namespace Osb.Kernel.Processes;

internal enum ProgramSourceKind : uint
{
    Embedded = 1,
}

internal enum RegistryError
{
    Ok = 0,
    NotInitialized,
    InvalidId,
    NotFound,
    Disabled,
    DuplicateName,
    InvalidName,
    InvalidSource,
    CapacityFailure,
    IdExhausted,
    InvalidLease,
}

internal struct SpawnLease
{
    public uint ProgramId;
    public uint Token;   // 0 == invalid / never issued
}

// Program-definition metadata only.
internal unsafe struct ProgramDescriptor
{
    public uint Id;                       // stable program ID (>= 1)
    public fixed byte Name[32];           // 31 ASCII chars + NUL
    public ProgramSourceKind SourceKind;
    public ulong ImageStart;              // embedded image start (linker symbol)
    public ulong ImageEnd;                // embedded image end (linker symbol)
    public uint EntryOffset;              // entry offset from ImageStart
    public bool Enabled;                  // availability state
    public uint SpawnLeases;              // outstanding spawn leases (lease accounting)
}

internal static unsafe partial class ProgramRegistry
{
    private static ProgramDescriptor* _descriptors;
    private static int _count;
    private static int _capacity;
    // Next Program ID to assign. 0 means "not yet seeded to 1". IDs are
    // monotonic starting at 1 and are never reused within a session.
    private static uint _nextId;
    private static bool _initialized;

    public static bool Initialized => _initialized;
    public static int Count => _count;

    public static bool Initialize()
    {
        if (_initialized) return true;

        if (!Grow(16)) return false;

        // Built-in embedded programs. IDs are assigned by the Registry in
        // registration order, starting at 1. The legacy AH=80h AL value is
        // NOT the internal ID; see LegacyToInternal for the explicit map.
        // Initialization is atomic: if any built-in registration fails, the
        // partially built state is torn down so no partial descriptor, ID,
        // reserved name, or storage remains, and a retry starts clean.
        bool ok = RegisterEmbedded("HELLO", NativeMethods.osb_userstub_begin(), NativeMethods.osb_userstub_end(), 0, out _)
               && RegisterEmbedded("BAD", NativeMethods.osb_userbad_begin(), NativeMethods.osb_userbad_end(), 0, out _)
               && RegisterEmbedded("SPAWNER", NativeMethods.osb_spawner_begin(), NativeMethods.osb_spawner_end(), 0, out _);

        if (ok) { _initialized = true; return true; }

        TeardownStorage();
        return false;
    }

    // Release the descriptor storage and reset every mutable registry field to
    // its pre-initialization state. Used only to roll back a failed
    // Initialize so the registry is clean and retryable. No descriptor,
    // allocated ID, or reserved name survives a failed initialization.
    private static void TeardownStorage()
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
    }

    // Boot-time registration of an embedded program. Every validation runs
    // before any mutation, so a failed registration leaves the Registry
    // intact and consumes no ID.
    private static bool RegisterEmbedded(string name, ulong start, ulong end, uint entryOffset, out uint id)
    {
        return TryInsert(name, ProgramSourceKind.Embedded, start, end, entryOffset, out id);
    }

    // Runtime-capable registration. Returns a RegistryError; on failure the
    // Registry is untouched and no ID is consumed.
    public static RegistryError Register(string name, ProgramSourceKind sourceKind, ulong start, ulong end, uint entryOffset, out uint id)
    {
        id = 0;
        if (!_initialized) return RegistryError.NotInitialized;
        if (!TryInsert(name, sourceKind, start, end, entryOffset, out id))
        {
            if (!ValidateName(name)) return RegistryError.InvalidName;
            if (FindByName(name) >= 0) return RegistryError.DuplicateName;
            if (end <= start) return RegistryError.InvalidSource;
            ulong size = end - start;
            if (size > (ulong)Layout.BlockFrames * Layout.FrameSize) return RegistryError.InvalidSource;
            if ((_nextId == 0 ? 1u : _nextId) == uint.MaxValue) return RegistryError.IdExhausted;
            return RegistryError.CapacityFailure;
        }
        return RegistryError.Ok;
    }

    public static bool TryGetById(uint id, out ProgramDescriptor descriptor)
    {
        descriptor = default;
        if (!_initialized || id == 0) return false;
        int idx = FindById(id);
        if (idx < 0) return false;
        descriptor = _descriptors[idx];   // by value: no internal pointer escapes
        return true;
    }

    public static bool TryGetByName(string name, out ProgramDescriptor descriptor)
    {
        descriptor = default;
        if (!_initialized) return false;
        int idx = FindByName(name);
        if (idx < 0) return false;
        descriptor = _descriptors[idx];   // by value: no internal pointer escapes
        return true;
    }

    // Availability: initialized, exists, and enabled. Disabled programs are
    // unavailable but retain their ID and name.
    public static bool IsAvailable(uint id)
    {
        if (!TryGetById(id, out var d)) return false;
        return d.Enabled;
    }

    // Logical re-enable. Idempotent. Restores availability without changing
    // ID or name. Returns false for unknown IDs.
    public static bool Enable(uint id)
    {
        if (!_initialized || id == 0) return false;
        int idx = FindById(id);
        if (idx < 0) return false;
        _descriptors[idx].Enabled = true;
        return true;
    }

    // Logical disable. Idempotent. Prevents new spawn leases; previously
    // acquired leases remain valid. ID and name stay reserved. Returns false
    // for unknown IDs.
    public static bool Disable(uint id)
    {
        if (!_initialized || id == 0) return false;
        int idx = FindById(id);
        if (idx < 0) return false;
        _descriptors[idx].Enabled = false;
        return true;
    }

    // Legacy AH=80h translation layer: explicit AL -> internal uint ID map.
    // Unknown AL values return 0 (invalid), which the syscall path treats as
    // NotFound, preserving the existing error behavior. The legacy numeric
    // ID is never assumed to equal the internal Program ID and is never
    // silently truncated from a wider value.
    public static uint LegacyToInternal(byte legacyAl)
    {
        switch (legacyAl)
        {
            case 0: return 1; // HELLO
            case 1: return 2; // BAD
            case 2: return 3; // SPAWNER
            default: return 0;
        }
    }

    // Display-only name resolution; allocates a managed string. Not on the
    // core lookup/comparison path.
    public static string? GetName(uint programId)
    {
        if (!TryGetById(programId, out var desc)) return null;
        int len = 0;
        while (len < 31 && desc.Name[len] != 0) len++;
        char[] chars = new char[len];
        for (int i = 0; i < len; i++) chars[i] = (char)desc.Name[i];
        return new string(chars);
    }

    // ---- private ----

    // Core insertion. Caller must ensure initialization when required
    // (Register enforces it; Initialize/RegisterEmbedded run before the
    // flag is set). All validation precedes any mutation; on failure the
    // Registry is untouched and no ID is consumed.
    private static bool TryInsert(string name, ProgramSourceKind sourceKind, ulong start, ulong end, uint entryOffset, out uint id)
    {
        id = 0;
        if (!ValidateName(name)) return false;
        if (FindByName(name) >= 0) return false;   // duplicate names rejected, even when disabled
        if (end <= start) return false;
        ulong size = end - start;
        if (size > (ulong)Layout.BlockFrames * Layout.FrameSize) return false;
        if (entryOffset >= size) return false;

        uint candidate = _nextId == 0 ? 1u : _nextId;
        if (candidate == uint.MaxValue) return false;   // ID exhaustion; never wraps to 0
        uint next = candidate + 1;
        if (next == 0) return false;                    // candidate == uint.MaxValue

        if (_count >= _capacity)
        {
            if (!Grow(_capacity == 0 ? 16 : _capacity * 2)) return false;
        }

        ref var desc = ref _descriptors[_count];
        desc.Id = candidate;
        WriteName(ref desc, name);
        desc.SourceKind = sourceKind;
        desc.ImageStart = start;
        desc.ImageEnd = end;
        desc.EntryOffset = entryOffset;
        desc.Enabled = true;
        desc.SpawnLeases = 0;
        _count++;
        _nextId = next;
        id = candidate;
        return true;
    }

    private static int FindById(uint id)
    {
        for (int i = 0; i < _count; i++)
            if (_descriptors[i].Id == id) return i;
        return -1;
    }

    // Case-insensitive name lookup under the ASCII contract. Scans every
    // descriptor, so disabled names remain reserved and duplicates are
    // rejected regardless of enabled state. No managed string allocation.
    private static int FindByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        int n = name.Length;
        for (int i = 0; i < _count; i++)
        {
            int dlen = 0;
            while (dlen < 31 && _descriptors[i].Name[dlen] != 0) dlen++;
            if (dlen != n) continue;
            bool match = true;
            unsafe
            {
                fixed (char* pn = name)
                {
                    for (int k = 0; k < n; k++)
                        if (Fold((char)_descriptors[i].Name[k]) != Fold(pn[k])) { match = false; break; }
                }
            }
            if (match) return i;
        }
        return -1;
    }

    // ASCII printable only (0x20-0x7E), non-empty, max 31 chars. Locale-free.
    private static bool ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length > 31) return false;
        unsafe
        {
            fixed (char* pn = name)
            {
                for (int i = 0; i < name.Length; i++)
                {
                    char c = pn[i];
                    if (c < 0x20 || c > 0x7E) return false;
                }
            }
        }
        return true;
    }

    private static char Fold(char c)
    {
        // Deterministic ASCII folding, independent of locale.
        if (c >= 'A' && c <= 'Z') return (char)(c + 32);
        return c;
    }

    private static void WriteName(ref ProgramDescriptor desc, string name)
    {
        for (int i = 0; i < 32; i++) desc.Name[i] = 0;
        unsafe
        {
            fixed (char* pn = name)
            {
                for (int i = 0; i < name.Length && i < 31; i++) desc.Name[i] = (byte)pn[i];
            }
        }
    }

    // Geometric (x2) growth of the descriptor array via the kernel allocator.
    // Allocation failure leaves the original Registry intact. The old buffer
    // is released only after the new allocation and copy succeed.
    private static bool Grow(int newCap)
    {
        if (newCap < _capacity) newCap = _capacity;
        if (newCap <= 0) newCap = 16;

        // Check multiplication and capacity overflow before allocation.
        ulong bytes = (ulong)newCap * (ulong)sizeof(ProgramDescriptor);
        if (bytes > int.MaxValue) return false;   // capacity overflow
        int bytesInt = (int)bytes;

        var ptr = (ProgramDescriptor*)Kernel.OsAlloc(bytesInt);
        if (ptr == null) return false;            // allocation failure: original intact

        for (int i = 0; i < bytesInt; i++) ((byte*)ptr)[i] = 0;
        if (_descriptors != null && _count > 0)
        {
            for (int i = 0; i < _count; i++) ptr[i] = _descriptors[i];
        }
        // Only replace the old allocation after successful allocation + copy;
        // release the previous allocation with the matching allocator API.
        if (_descriptors != null)
        {
            int oldBytes = _capacity * sizeof(ProgramDescriptor);
            int oldFrames = (oldBytes + Layout.FrameSize - 1) / Layout.FrameSize;
            if (oldFrames > 0) Kernel.OsFree(_descriptors, oldBytes);
        }
        _descriptors = ptr;
        _capacity = newCap;
        return true;
    }
}
