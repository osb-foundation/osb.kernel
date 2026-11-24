// Host-side unit tests for the Program Registry (M1.2).
//
// Exercises the ACTUAL implementation (EmbeddedRegistry.cs +
// EmbeddedRegistry.Leases.cs, linked into this assembly) through its public
// contract. Allocations go through a test-only stub allocator so
// allocator-failure paths can be injected safely. No production code is
// changed for these tests.
using System;
using Osb.Kernel.Hardware;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Unit;

internal static class Program
{
    private static int failures;
    private static readonly string[] ExpectedName = new string[64]; // 1-based by program id

    private static void Check(bool ok, string name)
    {
        if (ok) Console.WriteLine($"PASS: {name}");
        else { failures++; Console.WriteLine($"FAIL: {name}"); }
    }

    private static string NameOf(uint id) => ProgramRegistry.GetName(id) ?? "";

    private static void ExpectDescriptor(uint id, string name)
    {
        Check(ProgramRegistry.TryGetById(id, out var d), $"TryGetById({id})");
        Check(d.Id == id && NameOf(id) == name, $"descriptor {id} == {name}");
        Check(ProgramRegistry.TryGetByName(name, out var dn) && dn.Id == id, $"TryGetByName({name}) == {id}");
    }

    // Register a unique, valid program. Returns the assigned id. On success
    // the id is invariant-equal to Count (ids are contiguous 1..Count).
    private static uint RegisterUnique(string name)
    {
        uint id;
        var r = ProgramRegistry.Register(name, ProgramSourceKind.Embedded, 0x10000, 0x20000, 0, out id);
        Check(r == RegistryError.Ok, $"Register({name}) == Ok");
        Check(id == ProgramRegistry.Count, $"Register({name}) id==Count ({id}=={ProgramRegistry.Count})");
        if (r == RegistryError.Ok) ExpectedName[id] = name;
        return id;
    }

    public static int Main()
    {
        // ==== A. Initialization atomicity ====
        // Force SPAWNER registration to fail (end <= start) mid-Initialize.
        NativeMethods.OsbSpawnerEnd = NativeMethods.OsbSpawnerBegin;
        Kernel.ResetAlloc();
        Check(!ProgramRegistry.Initialized, "A1: not initialized before init");
        Check(!ProgramRegistry.Initialize(), "A2: Initialize fails when SPAWNER invalid");
        Check(!ProgramRegistry.Initialized, "A3: Initialize leaves _initialized false");
        Check(ProgramRegistry.Count == 0, "A4: failed init leaves clean state (Count 0)");
        Check(!ProgramRegistry.TryGetById(1, out _), "A5: no partial descriptor visible by id");
        Check(!ProgramRegistry.TryGetByName("HELLO", out _), "A6: no partial name reserved");

        // Restore SPAWNER and retry: retry must succeed deterministically.
        NativeMethods.OsbSpawnerEnd = 0x00105000;
        Check(ProgramRegistry.Initialize(), "A7: retry Initialize succeeds");
        Check(ProgramRegistry.Initialized, "A8: initialized after retry");
        Check(ProgramRegistry.Count == 3, "A9: Count 3 after retry (no duplication)");
        Check(ProgramRegistry.LegacyToInternal(0) == 1, "A10: legacy AL 0 -> id 1 (HELLO)");
        Check(ProgramRegistry.LegacyToInternal(1) == 2, "A11: legacy AL 1 -> id 2 (BAD)");
        Check(ProgramRegistry.LegacyToInternal(2) == 3, "A12: legacy AL 2 -> id 3 (SPAWNER)");
        Check(ProgramRegistry.LegacyToInternal(3) == 0, "A13: legacy AL 3 -> 0 (NotFound)");
        Check(ProgramRegistry.LegacyToInternal(255) == 0, "A14: legacy AL 255 -> 0 (out of domain)");
        Check(!ProgramRegistry.TryGetById(0, out _), "A15: id 0 is invalid");
        ExpectedName[1] = "HELLO"; ExpectedName[2] = "BAD"; ExpectedName[3] = "SPAWNER";
        for (uint id = 1; id <= 3; id++) ExpectDescriptor(id, ExpectedName[id]);

        // ==== B. Program ID allocation ====
        uint id4 = RegisterUnique("PROG4");
        Check(id4 == 4, "B1: next id is 4 (monotonic, starts at 1)");
        uint junk;
        Check(ProgramRegistry.Register("", ProgramSourceKind.Embedded, 0x10000, 0x20000, 0, out junk) == RegistryError.InvalidName, "B2: empty name -> InvalidName");
        Check(junk == 0, "B3: failed registration id is 0");
        Check(ProgramRegistry.Count == 4, "B4: failed registration does not consume an id");
        Check(ProgramRegistry.Register("HELLO", ProgramSourceKind.Embedded, 0x10000, 0x20000, 0, out junk) == RegistryError.DuplicateName, "B5: duplicate name -> DuplicateName");
        Check(ProgramRegistry.Count == 4, "B6: duplicate registration does not consume an id");
        Check(ProgramRegistry.Register("PROG_BADSRC", ProgramSourceKind.Embedded, 0x20000, 0x10000, 0, out junk) == RegistryError.InvalidSource, "B7: end<=start -> InvalidSource");
        Check(ProgramRegistry.Count == 4, "B8: bad-source registration does not consume an id");
        uint id5 = RegisterUnique("PROG_X");
        Check(id5 == 5, "B9: next id still 5 after failures (no id consumed by failures)");

        // ==== C. Names remain reserved after disable; ids never reused ====
        Check(ProgramRegistry.Disable(1), "C1: Disable(HELLO)");
        Check(!ProgramRegistry.IsAvailable(1), "C2: HELLO not available while disabled");
        Check(ProgramRegistry.Register("HELLO", ProgramSourceKind.Embedded, 0x10000, 0x20000, 0, out junk) == RegistryError.DuplicateName, "C3: disabled name still reserved (duplicate)");
        Check(ProgramRegistry.Count == 5, "C4: duplicate disabled-name registration does not consume an id");
        uint id6 = RegisterUnique("PROG_C");
        Check(id6 == 6, "C5: new id after disable is 6 (ids never reused after disable)");
        Check(ProgramRegistry.Enable(1), "C6: Enable(HELLO)");
        Check(ProgramRegistry.IsAvailable(1), "C7: HELLO available again after enable");
        ExpectDescriptor(1, "HELLO");
        Check(ProgramRegistry.TryGetById(1, out var d1) && d1.Id == 1, "C8: id unchanged after disable/enable");

        // ==== D. Descriptor storage growth (16 -> 32) + growth failure ====
        // Count is 6, capacity 16. Fill to capacity: ids 7..16.
        for (uint n = 7; n <= 16; n++) RegisterUnique($"PROG{n}");
        Check(ProgramRegistry.Count == 16, "D1: Count 16 at capacity");
        Check(ProgramRegistry.TryGetById(16, out var d16) && d16.Id == 16, "D2: id 16 present before growth");

        // Growth failure: next OsAlloc returns null -> CapacityFailure, registry intact.
        Kernel.FailNext();
        Check(ProgramRegistry.Register("PROG_DFAIL", ProgramSourceKind.Embedded, 0x10000, 0x20000, 0, out junk) == RegistryError.CapacityFailure, "D3: failed growth -> CapacityFailure");
        Check(ProgramRegistry.Count == 16, "D4: failed growth leaves Count 16 (no id consumed)");
        Check(ProgramRegistry.TryGetById(16, out var d16b) && d16b.Id == 16, "D5: existing descriptors intact after failed growth");
        Check(NameOf(16) == "PROG16", "D6: existing names intact after failed growth");
        Kernel.ResetAlloc();

        // Growth success: array doubles to 32, existing descriptors survive.
        uint id17 = RegisterUnique("PROG_DOK");
        Check(id17 == 17, "D7: growth assigns id 17");
        Check(ProgramRegistry.Count == 17, "D8: Count 17 after growth");
        Check(ProgramRegistry.TryGetById(17, out var d17) && d17.Id == 17, "D9: id 17 present after growth");
        bool allLookupOk = true;
        for (uint id = 1; id <= 17; id++)
        {
            if (!ProgramRegistry.TryGetById(id, out var dd) || dd.Id != id || NameOf(id) != ExpectedName[id]) allLookupOk = false;
            if (!ProgramRegistry.TryGetByName(ExpectedName[id], out var dn) || dn.Id != id) allLookupOk = false;
        }
        Check(allLookupOk, "D10: lookup by id and name for all 17 descriptors before/after growth");
        Check(ProgramRegistry.Disable(10), "D11: Disable(id 10)");
        Check(ProgramRegistry.TryGetById(10, out var d10) && d10.Id == 10, "D12: disabled descriptor still discoverable by id");
        Check(ProgramRegistry.TryGetByName("PROG10", out var dn10) && dn10.Id == 10, "D13: disabled descriptor still discoverable by name");
        Check(!ProgramRegistry.IsAvailable(10), "D14: disabled descriptor not available");

        // ==== E. Spawn lease table growth and semantics ====
        SpawnLease l1;
        Check(ProgramRegistry.TryAcquireSpawnLease(1, out l1), "E1: acquire lease id 1");
        Check(l1.ProgramId == 1 && l1.Token != 0, "E2: lease has valid nonzero token");
        SpawnLease l2;
        Check(ProgramRegistry.TryAcquireSpawnLease(1, out l2), "E3: second simultaneous lease for same id");
        Check(l2.Token != 0 && l2.Token != l1.Token, "E4: distinct tokens for distinct leases");
        Check(ProgramRegistry.TryGetById(1, out var dd1) && dd1.SpawnLeases == 2, "E5: outstanding lease count is 2");
        Check(ProgramRegistry.ReleaseSpawnLease(l1), "E6: release l1");
        Check(ProgramRegistry.TryGetById(1, out var dd1b) && dd1b.SpawnLeases == 1, "E7: releasing one lease does not affect the other (count 1)");
        Check(ProgramRegistry.ReleaseSpawnLease(l2), "E8: release l2");
        Check(ProgramRegistry.TryGetById(1, out var dd1c) && dd1c.SpawnLeases == 0, "E9: count 0 after all leases released");
        Check(!ProgramRegistry.ReleaseSpawnLease(l1), "E10: double release of l1 rejected");
        Check(!ProgramRegistry.ReleaseSpawnLease(l2), "E11: double release of l2 rejected");
        Check(!ProgramRegistry.ReleaseSpawnLease(new SpawnLease { ProgramId = 1, Token = l1.Token }), "E12: stale token release rejected");
        Check(!ProgramRegistry.ReleaseSpawnLease(new SpawnLease { ProgramId = 1, Token = 999 }), "E13: mismatched token rejected");
        Check(!ProgramRegistry.ReleaseSpawnLease(new SpawnLease { ProgramId = 999, Token = l2.Token }), "E14: mismatched program id rejected");
        Check(!ProgramRegistry.ReleaseSpawnLease(new SpawnLease { ProgramId = 1, Token = 0 }), "E15: invalid token zero rejected");
        Check(!ProgramRegistry.TryAcquireSpawnLease(0, out _), "E16: id 0 cannot acquire a lease");
        Check(!ProgramRegistry.TryAcquireSpawnLease(999, out _), "E17: unknown id cannot acquire a lease");

        // Inactive slot reuse.
        SpawnLease l3;
        Check(ProgramRegistry.TryAcquireSpawnLease(1, out l3), "E18: re-acquire after release reuses inactive slot");
        Check(l3.Token != 0 && l3.Token != l1.Token && l3.Token != l2.Token, "E19: reused slot issues a new distinct token");
        Check(ProgramRegistry.ReleaseSpawnLease(l3), "E20: release l3");

        // Disable/enable lease lifecycle (spec).
        SpawnLease lb;
        Check(ProgramRegistry.TryAcquireSpawnLease(2, out lb), "E21: acquire lease id 2 (BAD)");
        Check(ProgramRegistry.Disable(2), "E22: Disable(BAD)");
        Check(!ProgramRegistry.TryAcquireSpawnLease(2, out _), "E23: new lease acquisition fails while disabled");
        Check(ProgramRegistry.ReleaseSpawnLease(lb), "E24: previously acquired lease remains valid after disable");
        Check(ProgramRegistry.Enable(2), "E25: Enable(BAD)");
        SpawnLease lb2;
        Check(ProgramRegistry.TryAcquireSpawnLease(2, out lb2), "E26: new lease succeeds after enable");
        Check(lb2.ProgramId == 2, "E27: new lease ProgramId unchanged after enable/disable");
        Check(NameOf(2) == "BAD", "E28: name unchanged after enable/disable");
        Check(ProgramRegistry.ReleaseSpawnLease(lb2), "E29: release lb2");

        // ==== F. Test 1: Initial Registry allocation failure ====
        // Fresh (uninitialized) Registry state. The FIRST OsAlloc made by
        // Initialize() (the initial descriptor-storage Grow(16)) returns null.
        ProgramRegistry.TestOnly_Reset();
        Kernel.ResetAlloc();
        Kernel.FailNext();
        Check(!ProgramRegistry.Initialized, "F1: not initialized before init");
        Check(!ProgramRegistry.Initialize(), "F2: Initialize fails on first OsAlloc");
        Check(!ProgramRegistry.Initialized, "F3: Initialized remains false");
        Check(ProgramRegistry.Count == 0, "F4: Count remains zero");
        Check(!ProgramRegistry.TryGetById(1, out _), "F5: no descriptor found by id");
        Check(!ProgramRegistry.TryGetByName("HELLO", out _), "F6: no descriptor found by name (HELLO)");
        Check(!ProgramRegistry.TryGetByName("BAD", out _), "F7: no descriptor found by name (BAD)");
        Check(!ProgramRegistry.TryGetByName("SPAWNER", out _), "F8: no descriptor found by name (SPAWNER)");
        Check(!ProgramRegistry.IsAvailable(1), "F9: id 1 not available");
        Check(Kernel.AllocCalls == 1, "F10: exactly one (failed) OsAlloc call, nothing retained");
        Check(!ProgramRegistry.TryGetById(0, out _), "F11: id 0 invalid");

        // Retry after disabling failure injection.
        Kernel.ResetAlloc();
        Check(ProgramRegistry.Initialize(), "F12: retry Initialize succeeds");
        Check(ProgramRegistry.Initialized, "F13: initialized after retry");
        Check(ProgramRegistry.Count == 3, "F14: Count 3 after retry (no id consumed by failed init)");
        Check(ProgramRegistry.LegacyToInternal(0) == 1, "F15: id 1 == HELLO");
        Check(ProgramRegistry.LegacyToInternal(1) == 2, "F16: id 2 == BAD");
        Check(ProgramRegistry.LegacyToInternal(2) == 3, "F17: id 3 == SPAWNER");
        Check(ProgramRegistry.TryGetById(1, out var h) && h.Id == 1, "F18: descriptor 1 present");
        Check(ProgramRegistry.TryGetById(2, out var b) && b.Id == 2, "F19: descriptor 2 present");
        Check(ProgramRegistry.TryGetById(3, out var s) && s.Id == 3, "F20: descriptor 3 present");
        Check(ProgramRegistry.TryGetByName("HELLO", out var hn) && hn.Id == 1, "F21: name HELLO -> id 1");
        Check(ProgramRegistry.TryGetByName("BAD", out var bn) && bn.Id == 2, "F22: name BAD -> id 2");
        Check(ProgramRegistry.TryGetByName("SPAWNER", out var sn) && sn.Id == 3, "F23: name SPAWNER -> id 3");
        Check(NameOf(1) == "HELLO" && NameOf(2) == "BAD" && NameOf(3) == "SPAWNER", "F24: names readable");
        Check(ProgramRegistry.IsAvailable(1) && ProgramRegistry.IsAvailable(2) && ProgramRegistry.IsAvailable(3), "F25: registry usable after retry");

        // ==== G. Test 2: Lease-table growth (16 -> 32) ====
        // Program 1 (HELLO) is registered and enabled. Acquire 17 simultaneous
        // leases; the 17th must trigger real growth of the lease table.
        SpawnLease[] gLeases = new SpawnLease[17];
        uint[] gTokens = new uint[17];
        bool gAll16 = true;
        for (int i = 0; i < 16; i++)
        {
            gAll16 &= ProgramRegistry.TryAcquireSpawnLease(1, out gLeases[i]);
            gTokens[i] = gLeases[i].Token;
        }
        Check(gAll16, "G1: first 16 leases acquired successfully");
        Check(ProgramRegistry.TryAcquireSpawnLease(1, out gLeases[16]), "G2: 17th lease triggers growth and succeeds");
        gTokens[16] = gLeases[16].Token;

        bool gTokensOk = true;
        for (int i = 0; i < 17; i++)
        {
            if (gTokens[i] == 0) gTokensOk = false;
            for (int j = i + 1; j < 17; j++)
                if (gTokens[i] == gTokens[j]) gTokensOk = false;
        }
        Check(gTokensOk, "G3: all 17 tokens distinct and nonzero");
        Check(ProgramRegistry.TryGetById(1, out var gD) && gD.SpawnLeases == 17, "G4: outstanding lease count is 17");

        // Snapshot descriptor data before release.
        ulong gImgStart = gD.ImageStart, gImgEnd = gD.ImageEnd, gEntry = gD.EntryOffset;
        bool gEnabled = gD.Enabled;

        bool gAllReleased = true;
        for (int i = 0; i < 17; i++)
            gAllReleased &= ProgramRegistry.ReleaseSpawnLease(gLeases[i]);
        Check(gAllReleased, "G5: all 17 leases released successfully");
        Check(ProgramRegistry.TryGetById(1, out var gD2) && gD2.SpawnLeases == 0, "G6: lease count returns to zero");
        Check(gD2.Id == 1 && gD2.Enabled == gEnabled && gD2.ImageStart == gImgStart && gD2.ImageEnd == gImgEnd && gD2.EntryOffset == gEntry, "G7: descriptor data unchanged");
        Check(NameOf(1) == "HELLO", "G8: name unchanged");
        Check(ProgramRegistry.Count == 3, "G9: registry descriptor count unchanged");

        SpawnLease gRe;
        Check(ProgramRegistry.TryAcquireSpawnLease(1, out gRe), "G10: re-acquire after full release works");
        Check(ProgramRegistry.ReleaseSpawnLease(gRe), "G11: release re-acquired lease");

        // ==== H. Test 3: Lease-table growth allocation failure ====
        // Fresh scenario: reset registry + lease table, re-initialize, then
        // acquire 16 active leases filling the 16-slot lease table.
        ProgramRegistry.TestOnly_Reset();
        Kernel.ResetAlloc();
        Check(ProgramRegistry.Initialize(), "H-init: fresh initialize succeeds");
        Check(ProgramRegistry.Count == 3, "H-init: Count 3 after fresh init");

        SpawnLease[] hLeases = new SpawnLease[16];
        uint[] hTokens = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            Check(ProgramRegistry.TryAcquireSpawnLease(1, out hLeases[i]), $"H-acq-{i}: lease {i + 1} acquired");
            hTokens[i] = hLeases[i].Token;
        }
        Check(ProgramRegistry.TryGetById(1, out var hD0) && hD0.SpawnLeases == 16, "H1: 16 active leases, count 16");

        bool hTokensOk = true;
        for (int i = 0; i < 16; i++)
        {
            if (hTokens[i] == 0) hTokensOk = false;
            for (int j = i + 1; j < 16; j++)
                if (hTokens[i] == hTokens[j]) hTokensOk = false;
        }
        Check(hTokensOk, "H2: 16 tokens distinct and nonzero");

        // Fail the next OsAlloc, which is the 17th lease's growth (16 -> 32).
        Kernel.FailNext();
        Check(!ProgramRegistry.TryAcquireSpawnLease(1, out var hFail), "H3: 17th lease acquisition fails safely");
        Check(hFail.Token == 0 && hFail.ProgramId == 0, "H4: no invalid lease created on failure");
        Check(ProgramRegistry.TryGetById(1, out var hD2) && hD2.SpawnLeases == 16, "H5: lease count still 16 (no lease consumed)");
        bool hReleased = true;
        for (int i = 0; i < 16; i++)
            hReleased &= ProgramRegistry.ReleaseSpawnLease(hLeases[i]);
        Check(hReleased, "H6: all 16 leases still releasable after failed growth");
        Check(ProgramRegistry.TryGetById(1, out var hD3) && hD3.SpawnLeases == 0, "H7: count returns to zero");
        Check(ProgramRegistry.TryGetById(1, out var hD4) && hD4.Id == 1 && NameOf(1) == "HELLO", "H8: registry usable after failed growth");

        // Token-generation behavior after failed growth: NextLeaseToken runs
        // before table growth, so the failed attempt consumed token 17 while
        // creating no lease. Tokens remain unique and nonzero; no active token
        // is reused. This is the pre-existing design choice (token allocated
        // before the table slot), documented here rather than changed.
        Check(ProgramRegistry.TryAcquireSpawnLease(1, out var hProbe) && hProbe.Token == 18, "H9: probe acquisition succeeds; token 17 consumed by failed growth, next token is 18");
        Check(ProgramRegistry.ReleaseSpawnLease(hProbe), "H10: release probe lease");

        // Disable failure injection: a new acquisition succeeds and growth
        // completes correctly.
        Kernel.ResetAlloc();
        SpawnLease[] hRec = new SpawnLease[17];
        uint[] hRecTokens = new uint[17];
        bool hRecOk = true;
        for (int i = 0; i < 17; i++)
        {
            hRecOk &= ProgramRegistry.TryAcquireSpawnLease(1, out hRec[i]);
            hRecTokens[i] = hRec[i].Token;
        }
        Check(hRecOk, "H11: 17 leases acquired after recovery (growth completed)");
        bool hRecDistinct = true;
        for (int i = 0; i < 17; i++)
        {
            if (hRecTokens[i] == 0) hRecDistinct = false;
            for (int j = i + 1; j < 17; j++)
                if (hRecTokens[i] == hRecTokens[j]) hRecDistinct = false;
        }
        Check(hRecDistinct, "H12: recovered tokens distinct and nonzero");
        bool hRecRel = true;
        for (int i = 0; i < 17; i++)
            hRecRel &= ProgramRegistry.ReleaseSpawnLease(hRec[i]);
        Check(hRecRel, "H13: all recovered leases released");
        Check(ProgramRegistry.TryGetById(1, out var hD5) && hD5.SpawnLeases == 0, "H14: count back to zero after recovery");

        // ==== Summary ====
        Console.WriteLine();
        if (failures == 0) { Console.WriteLine("ALL TESTS PASSED"); return 0; }
        Console.WriteLine($"{failures} TEST(S) FAILED");
        return 1;
    }
}
