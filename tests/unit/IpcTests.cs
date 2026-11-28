// Host-side unit tests for the Kernel IPC core (Card 3.1, Phase 1).
//
// Exercises the ACTUAL implementation (Processes/Ipc*.cs, linked into this
// assembly) through its internal contract. Allocation goes through the test
// stub allocator (Stubs/Kernel.cs) so allocation-failure paths can be injected.
// No QEMU, no NativeAOT: runs in <1 s. Mirrors PidAllocatorTests/AllocatorTests.
using System;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Unit;

internal static unsafe class IpcTests
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        if (ok) Console.WriteLine($"PASS: {name}");
        else { failures++; Console.WriteLine($"FAIL: {name}"); }
    }

    private static IpcHandle MakeTopic(ulong pid, string name, bool system = false)
    {
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        for (int i = 0; i < name.Length && i < 31; i++) buf[i] = (byte)name[i];
        IpcHandle h;
        var st = Ipc.CreateTopic(pid, buf, name.Length, system, out h);
        Check(st == IpcStatus.Ok, $"CreateTopic({name},pid={pid},sys={system}) == Ok");
        return h;
    }

    private static IpcStatus PublishBytes(ulong pid, IpcHandle topic, int len, out ulong mid)
    {
        byte[] payload = new byte[len];
        for (int i = 0; i < len; i++) payload[i] = (byte)(i & 0xFF);
        fixed (byte* pp = payload)
            return Ipc.Publish(pid, topic, pp, len, out mid);
    }

    private static IpcStatus ReceiveInto(ulong pid, IpcHandle topic, byte[] dest, out IpcDeliveryView view)
    {
        fixed (byte* dp = dest)
            return Ipc.Receive(pid, topic, dp, dest.Length, out view);
    }

    public static int Run()
    {
        failures = 0;

        // ==== A. Topic creation, ownership, system-topic protection ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle sys = MakeTopic(0, "SYS", system: true);
        IpcHandle dyn = MakeTopic(7, "DYN");
        Check(Ipc.TestOnly_Topic(0, out ulong sOwner, out uint sFlags, out _, out uint sGen) && sOwner == 0 && (sFlags & 1) != 0 && sGen != 0,
            "A1: system topic owned by Kernel (pid 0), system flag set");
        Check(Ipc.TestOnly_Topic(1, out ulong dOwner, out _, out _, out _) && dOwner == 7,
            "A2: dynamic topic owned by creator's actual pid (7)");
        // Ring 3 cannot create a system topic (D20).
        byte* nm = stackalloc byte[8]; nm[0] = (byte)'X'; nm[1] = 0;
        IpcHandle bad;
        Check(Ipc.CreateTopic(7, nm, 1, true, out bad) == IpcStatus.Eacces, "A3: Ring 3 cannot create system topic");
        // Stale/unknown handle does not resolve.
        Check(Ipc.CreateTopic(7, nm, 1, false, out _) == IpcStatus.Ok, "A4: create dynamic topic X");
        Check(Ipc.Subscribe(7, new IpcHandle(0, 999)) == IpcStatus.Enoent, "A5: stale generation handle rejected");

        // ==== B. Subscription uniqueness (idempotent) ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle t = MakeTopic(1, "T");
        Check(Ipc.Subscribe(2, t) == IpcStatus.Ok, "B1: first Subscribe Ok");
        Check(Ipc.Subscribe(2, t) == IpcStatus.Ok, "B2: repeated Subscribe Ok (idempotent, D26)");
        Check(Ipc.TestOnly_FindSub(2, 0) >= 0, "B3: exactly one subscription exists for (pid2,topic)");
        int subCount = 0;
        for (int i = 0; i < 64; i++) if (Ipc.TestOnly_Sub(i, out _, out _, out _, out _, out _, out _, out _)) subCount++;
        Check(subCount == 1, "B4: no duplicate delivery stream created");

        // ==== C. Multiple independent subscribers; Message ID vs Delivery ID ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle tc = MakeTopic(1, "C");
        Check(Ipc.Subscribe(2, tc) == IpcStatus.Ok && Ipc.Subscribe(3, tc) == IpcStatus.Ok, "C1: two subscribers");
        Check(PublishBytes(1, tc, 16, out ulong mid) == IpcStatus.Ok, "C2: publish Ok");
        Check(Ipc.TestOnly_Message(0, out ulong mid2, out int msize, out int mref, out _) && mid2 == mid && msize == 16 && mref == 2,
            "C3: one message, refcount 2 (two deliveries)");
        int d2 = Ipc.TestOnly_FindDelivery(2, 0, (int)IpcDeliveryState.Queued);
        int d3 = Ipc.TestOnly_FindDelivery(3, 0, (int)IpcDeliveryState.Queued);
        Check(d2 >= 0 && d3 >= 0, "C4: one queued delivery per subscriber");
        Check(Ipc.TestOnly_Delivery(d2, out _, out _, out uint m2, out _, out _, out uint g2) && g2 != 0, "C5: delivery has a handle");
        Check(Ipc.TestOnly_Delivery(d3, out _, out _, out uint m3, out _, out _, out uint g3) && g3 != 0, "C6: delivery has a handle");
        Check(m2 == 0 && m3 == 0 && g2 != g3, "C7: distinct Delivery IDs, same Message (index 0)");

        // ==== D. Successful atomic fan-out + Receive + explicit ACK ====
        byte[] buf = new byte[32];
        Check(ReceiveInto(2, tc, buf, out IpcDeliveryView v2) == IpcStatus.Ok && v2.MessageId == mid && v2.Size == 16,
            "D1: subscriber 2 receives the message (copy)");
        Check(Ipc.TestOnly_Delivery(d2, out _, out _, out _, out int st2, out int at2, out _) && st2 == (int)IpcDeliveryState.InFlight && at2 == 1,
            "D2: delivery is InFlight after receive (attempt 1)");
        // Receive does NOT auto-ack (D6): still InFlight, capacity held.
        Check(Ipc.TestOnly_Sub(Ipc.TestOnly_FindSub(2, 0), out _, out _, out int q2, out int if2, out _, out _, out _) && q2 == 0 && if2 == 1,
            "D3: receive does not ack (InFlight=1, Queued=0)");
        Check(Ipc.Ack(2, v2.Delivery) == IpcStatus.Ok, "D4: explicit ACK Ok");
        Check(Ipc.TestOnly_FindDelivery(2, 0, (int)IpcDeliveryState.InFlight) < 0, "D5: ACK frees the delivery (no InFlight left)");
        // Message still alive because subscriber 3 still holds a delivery.
        Check(Ipc.TestOnly_Message(0, out _, out _, out int refAfterAck, out uint genAfterAck) && refAfterAck == 1 && genAfterAck != 0,
            "D6: message retained while another delivery references it");

        // ==== E. Non-blocking Receive with nothing available ====
        Check(ReceiveInto(2, tc, buf, out _) == IpcStatus.Eagain, "E1: non-blocking receive empty -> Eagain");

        // ==== F. Invalid and foreign ACK ====
        Check(Ipc.Ack(2, new IpcHandle(0, 999)) == IpcStatus.Enoent, "F1: invalid handle ACK -> Enoent");
        // Subscriber 3 still has an InFlight-capable queued delivery; a foreign ACK by pid 2 must fail.
        Check(ReceiveInto(3, tc, buf, out IpcDeliveryView v3) == IpcStatus.Ok, "F2: subscriber 3 receives");
        Check(Ipc.Ack(2, v3.Delivery) == IpcStatus.Eacces, "F3: foreign ACK (pid2 on pid3 delivery) -> Eacces");
        Check(Ipc.Ack(3, v3.Delivery) == IpcStatus.Ok, "F4: owner ACK Ok");

        // ==== G. QueueFull with NO partial publication ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle tf = MakeTopic(1, "F");
        Check(Ipc.Subscribe(2, tf) == IpcStatus.Ok, "G1: subscriber A");
        for (int i = 0; i < IpcLimits.QueueDepth; i++)
            Check(PublishBytes(1, tf, 4, out _) == IpcStatus.Ok, $"G2.{i}: fill A queue ({i + 1})");
        Check(Ipc.Subscribe(3, tf) == IpcStatus.Ok, "G3: subscriber B joins after A is full");
        int msgsBefore = 0; Ipc.TestOnly_Counts(out _, out _, out msgsBefore, out _, out _, out _, out _);
        Check(PublishBytes(1, tf, 4, out _) == IpcStatus.QueueFull, "G4: publish rejected QueueFull when A is full");
        int msgsAfter; Ipc.TestOnly_Counts(out _, out _, out msgsAfter, out _, out _, out _, out _);
        Check(msgsAfter == msgsBefore, "G5: QueueFull created no message (no partial fan-out)");
        Check(Ipc.TestOnly_FindDelivery(3, 0, (int)IpcDeliveryState.Queued) < 0, "G6: B received nothing from the rejected publish");

        // ==== H. Allocation failure and complete rollback ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle th = MakeTopic(1, "H");
        Check(Ipc.Subscribe(2, th) == IpcStatus.Ok, "H1: subscriber");
        Kernel.ResetAlloc(); Kernel.FailNext(); // fail the payload allocation (first OsAlloc)
        Check(PublishBytes(1, th, 8, out _) == IpcStatus.Enomem, "H2: publish fails Enomem on payload alloc");
        Ipc.TestOnly_Counts(out _, out _, out int mH, out int dH, out long pbH, out _, out _);
        Check(mH == 0 && dH == 0 && pbH == 0, "H3: no message/delivery/payload after failed payload alloc");
        // Fail the descriptor growth AFTER the payload is allocated -> rollback frees payload.
        Kernel.ResetAlloc(); Kernel.FailOnAllocCall = 2; // 1st=payload ok, 2nd=grow fails
        Check(PublishBytes(1, th, 8, out _) == IpcStatus.Enomem, "H4: publish fails Enomem on descriptor growth");
        Ipc.TestOnly_Counts(out _, out _, out int mH2, out int dH2, out long pbH2, out _, out _);
        Check(mH2 == 0 && dH2 == 0 && pbH2 == 0, "H5: rollback freed payload, no message/delivery, no payload bytes");
        Kernel.ResetAlloc();
        Check(PublishBytes(1, th, 8, out _) == IpcStatus.Ok, "H6: publish works after recovery");

        // ==== I. Retry: max 3 attempts, same Delivery ID, no extra queue slot ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle ti = MakeTopic(1, "I");
        Check(Ipc.Subscribe(2, ti) == IpcStatus.Ok, "I1: subscriber");
        Check(PublishBytes(1, ti, 4, out _) == IpcStatus.Ok, "I2: publish");
        int dCount0; Ipc.TestOnly_Counts(out _, out _, out _, out dCount0, out _, out _, out _);
        Check(ReceiveInto(2, ti, buf, out IpcDeliveryView vi) == IpcStatus.Ok, "I3: receive (attempt 1)");
        Check(Ipc.FailDelivery(2, vi.Delivery, true, 0) == IpcStatus.Ok, "I4: transient failure -> retry");
        Check(Ipc.TestOnly_Sub(Ipc.TestOnly_FindSub(2, 0), out _, out _, out int qI, out int ifI, out int rpI, out _, out _) && qI + ifI + rpI == 1,
            "I5: retry consumes no extra capacity (total still 1)");
        int dCount1; Ipc.TestOnly_Counts(out _, out _, out _, out dCount1, out _, out _, out _);
        Check(dCount1 == dCount0, "I6: retry did not allocate a new delivery slot");
        Check(Ipc.AdvanceRetries(100) == 1, "I7: backoff elapsed -> requeue");
        Check(ReceiveInto(2, ti, buf, out vi) == IpcStatus.Ok, "I8: receive (attempt 2)");
        Check(Ipc.FailDelivery(2, vi.Delivery, true, 200) == IpcStatus.Ok && Ipc.AdvanceRetries(300) == 1, "I9: second retry");
        Check(ReceiveInto(2, ti, buf, out vi) == IpcStatus.Ok, "I10: receive (attempt 3)");
        Check(Ipc.TestOnly_Delivery((int)vi.Delivery.Index, out _, out _, out _, out _, out int att3, out _) && att3 == 3, "I11: third attempt recorded");
        Check(Ipc.FailDelivery(2, vi.Delivery, true, 400) == IpcStatus.Ok, "I12: third failure -> terminal (no more retries)");
        int dTerm = Ipc.TestOnly_FindDelivery(2, 0, (int)IpcDeliveryState.TerminalFail);
        Check(dTerm >= 0, "I13: delivery is TerminalFail (tombstone)");
        Check(ReceiveInto(2, ti, buf, out _) == IpcStatus.Eagain, "I14: no redelivery after terminal");
        Ipc.TestOnly_Counts(out _, out _, out _, out _, out long pbI, out int lmI, out int ldI);
        Check(pbI == 0 && lmI == 0 && ldI == 0, "I15: terminal released payload + message + active delivery accounting");
        Check(Ipc.AdvanceRetries(1000) == 0, "I16: no retry pending after terminal (timer fast-path returns 0)");

        // ==== J. Process termination cleanup ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle sysJ = MakeTopic(0, "SYSJ", system: true);
        IpcHandle dynJ = MakeTopic(5, "DYNJ");        // owned by pid 5
        Check(Ipc.Subscribe(5, dynJ) == IpcStatus.Ok && Ipc.Subscribe(6, dynJ) == IpcStatus.Ok, "J1: subs on dynamic");
        Check(Ipc.Subscribe(6, sysJ) == IpcStatus.Ok, "J2: sub on system");
        Check(PublishBytes(5, dynJ, 4, out _) == IpcStatus.Ok, "J3: publish to dynamic");
        Check(PublishBytes(0, sysJ, 4, out _) == IpcStatus.Ok, "J4: publish to system (kernel)");
        Ipc.OnProcessExit(5);
        Check(Ipc.TestOnly_Topic(1, out _, out _, out _, out uint dynGen) && dynGen == 0, "J5: dynamic topic destroyed on owner exit");
        Check(Ipc.TestOnly_Topic(0, out _, out _, out _, out uint sysGen) && sysGen != 0, "J6: system topic survives");
        Check(Ipc.TestOnly_FindSub(5, 1) < 0, "J7: exiting process's subscription removed");
        Check(Ipc.TestOnly_FindSub(6, 1) < 0, "J8: subscriber on destroyed topic removed");
        Check(Ipc.TestOnly_FindSub(6, 0) >= 0, "J9: subscriber on system topic preserved");
        Check(Ipc.TestOnly_FindDelivery(6, 0, (int)IpcDeliveryState.Queued) >= 0, "J10: system delivery to pid6 preserved");
        Check(Ipc.TestOnly_FindDelivery(5, 1, (int)IpcDeliveryState.Queued) < 0 && Ipc.TestOnly_FindDelivery(6, 1, (int)IpcDeliveryState.Queued) < 0,
            "J11: deliveries on destroyed topic cancelled");

        // ==== K. Stale handle rejection after destroy/reuse ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle tk = MakeTopic(1, "K");
        Check(Ipc.Subscribe(2, tk) == IpcStatus.Ok, "K1: subscribe");
        Check(Ipc.DestroyTopic(1, tk) == IpcStatus.Ok, "K2: owner destroys topic");
        Check(Ipc.Subscribe(2, tk) == IpcStatus.Enoent, "K3: stale topic handle after destroy -> Enoent");
        Check(PublishBytes(1, tk, 4, out _) == IpcStatus.Enoent, "K4: publish to stale handle -> Enoent");
        // Reuse the slot with a new generation; the old handle must still fail.
        IpcHandle tk2 = MakeTopic(1, "K2");
        Check(tk2.Index == tk.Index && tk2.Generation != tk.Generation, "K5: slot reused with a new generation");
        Check(Ipc.Subscribe(2, tk) == IpcStatus.Enoent, "K6: old handle still rejected after slot reuse");

        // ==== L. Resource accounting returns to zero after full cleanup ====
        Ipc.TestOnly_Reset(); Kernel.ResetAlloc();
        IpcHandle tl = MakeTopic(1, "L");
        Check(Ipc.Subscribe(2, tl) == IpcStatus.Ok && Ipc.Subscribe(3, tl) == IpcStatus.Ok, "L1: two subs");
        Check(PublishBytes(1, tl, 32, out _) == IpcStatus.Ok, "L2: publish");
        Ipc.TestOnly_Counts(out _, out _, out _, out _, out long pbL, out int lmL, out int ldL);
        Check(pbL == 32 && lmL == 1 && ldL == 2, "L3: accounting reflects payload + 1 message + 2 active deliveries");
        Ipc.OnProcessExit(2);
        Ipc.OnProcessExit(3);
        Ipc.DestroyTopic(1, tl);
        Ipc.TestOnly_Counts(out _, out _, out _, out _, out long pbL2, out int lmL2, out int ldL2);
        Check(pbL2 == 0 && lmL2 == 0 && ldL2 == 0, "L4: all resources reclaimed after cleanup");

        // ==== M. Handle codec + ABI error-value mapping (v10 contract) ====
        // The u64 handle is (generation<<32)|index; the ABI error space is the
        // negative IpcStatus values the syscall layer returns in RAX.
        Check(new IpcHandle(0, 0).IsNone, "M1: generation 0 is the none handle");
        Check(!new IpcHandle(0, 1).IsNone, "M2: generation 1 is a live handle");
        ulong[] idxs = { 0u, 1u, 7u, 255u, 65535u, 4294967295u };
        ulong[] gens = { 1u, 2u, 255u, 65535u, 4294967295u };
        bool codecOk = true;
        for (int a = 0; a < idxs.Length; a++)
            for (int b = 0; b < gens.Length; b++)
            {
                var h = new IpcHandle((uint)idxs[a], (uint)gens[b]);
                var back = IpcHandle.FromU64(h.ToU64());
                if (back.Index != h.Index || back.Generation != h.Generation) codecOk = false;
            }
        Check(codecOk, "M3: ToU64/FromU64 round-trips index+generation");
        Check(new IpcHandle(3, 9).ToU64() == ((ulong)9 << 32 | 3), "M4: handle packs generation high, index low");
        Check((int)IpcStatus.Ok == 0 && (int)IpcStatus.Efault == -1 && (int)IpcStatus.Enosys == -2
            && (int)IpcStatus.Eacces == -3 && (int)IpcStatus.Enoent == -4 && (int)IpcStatus.Einval == -5
            && (int)IpcStatus.Enomem == -6 && (int)IpcStatus.Eintr == -7 && (int)IpcStatus.Ebadf == -8
            && (int)IpcStatus.QueueFull == -9 && (int)IpcStatus.Eagain == -10, "M5: IpcStatus values match the v10 ABI error space");

        Console.WriteLine();
        if (failures == 0) { Console.WriteLine("IPC: ALL TESTS PASSED"); return 0; }
        Console.WriteLine($"IPC: {failures} TEST(S) FAILED");
        return failures;
    }
}