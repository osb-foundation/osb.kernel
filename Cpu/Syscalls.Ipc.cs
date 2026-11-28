// INT 21h IPC syscalls (ABI v10, Card 3.1 Phase 3). Non-blocking topic IPC
// exposed to ring 3 as AH=83h..8Ah. Every operation runs with IF=0 (DPL3
// interrupt gate), so the broker (Processes/Ipc*.cs) is serialized without a
// lock. The caller PID is taken from the Scheduler, never from the user frame.
//
// Register convention (same as the rest of the gate):
//   AH = function, AL = flags/subop
//   RDI = arg1, RSI = arg2, RDX = arg3, RCX = arg4   (result in RAX)
// A negative RAX is an error (IpcStatus); a non-negative RAX is the operation
// result (handle for CREATE, MessageId for PUBLISH, 0 for status ops).

using Osb.Kernel.Memory;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    private const int IpcCreateNum     = 0x83; // RDI=name, RSI=len, AL=flags(system bit0)
    private const int IpcDestroyNum    = 0x84; // RDI=topic handle
    private const int IpcSubscribeNum  = 0x85; // RDI=topic handle
    private const int IpcUnsubscribeNum= 0x86; // RDI=topic handle
    private const int IpcPublishNum    = 0x87; // RDI=topic, RSI=payload, RDX=len
    private const int IpcReceiveNum    = 0x88; // RDI=topic, RSI=dest, RDX=cap, RCX=out24
    private const int IpcAckNum        = 0x89; // RDI=delivery handle
    private const int IpcFailNum       = 0x8A; // RDI=delivery, AL=transient

    private static void HandleIpc(int func, ulong* frame)
    {
        ulong rax = frame[14];
        ulong arg1 = frame[9];   // RDI
        ulong arg2 = frame[10];  // RSI
        ulong arg3 = frame[11];  // RDX
        ulong arg4 = frame[12];  // RCX
        ulong pid = Scheduler.Get(Scheduler.Current).Pid;   // trusted caller id
        int al = (int)(rax & 0xFF);

        switch (func)
        {
            case IpcCreateNum:
                HandleIpcCreate(frame, pid, arg1, arg2, al);
                return;
            case IpcDestroyNum:
                SetStatus(frame, Ipc.DestroyTopic(pid, IpcHandle.FromU64(arg1)));
                return;
            case IpcSubscribeNum:
                SetStatus(frame, Ipc.Subscribe(pid, IpcHandle.FromU64(arg1)));
                return;
            case IpcUnsubscribeNum:
                SetStatus(frame, Ipc.Unsubscribe(pid, IpcHandle.FromU64(arg1)));
                return;
            case IpcPublishNum:
                HandleIpcPublish(frame, pid, arg1, arg2, arg3);
                return;
            case IpcReceiveNum:
                HandleIpcReceive(frame, pid, arg1, arg2, arg3, arg4);
                return;
            case IpcAckNum:
                SetStatus(frame, Ipc.Ack(pid, IpcHandle.FromU64(arg1)));
                return;
            case IpcFailNum:
                SetStatus(frame, Ipc.FailDelivery(pid, IpcHandle.FromU64(arg1), al != 0, Pit.Ticks));
                return;
        }
        frame[14] = unchecked((ulong)-2); // ENOSYS
    }

    private static void HandleIpcCreate(ulong* frame, ulong pid, ulong namePtr, ulong nameLen, int al)
    {
        int len = (int)nameLen;
        if (len <= 0 || len > IpcLimits.MaxNameBytes || !UserPtr(namePtr, len))
        {
            SetStatus(frame, IpcStatus.Einval);
            return;
        }
        bool system = (al & 0x1) != 0;
        IpcHandle h;
        IpcStatus st = Ipc.CreateTopic(pid, (byte*)namePtr, len, system, out h);
        if (st == IpcStatus.Ok) frame[14] = h.ToU64();
        else SetStatus(frame, st);
    }

    private static void HandleIpcPublish(ulong* frame, ulong pid, ulong topic, ulong payload, ulong len)
    {
        int n = (int)len;
        if (n < 0 || n > IpcLimits.MaxPayload) { SetStatus(frame, IpcStatus.Einval); return; }
        if (n > 0 && !UserPtr(payload, n)) { SetStatus(frame, IpcStatus.Efault); return; }
        ulong mid;
        IpcStatus st = Ipc.Publish(pid, IpcHandle.FromU64(topic), (byte*)payload, n, out mid);
        if (st == IpcStatus.Ok) frame[14] = mid;
        else SetStatus(frame, st);
    }

    private static void HandleIpcReceive(ulong* frame, ulong pid, ulong topic, ulong dest, ulong cap, ulong outPtr)
    {
        int capacity = (int)cap;
        if (capacity < 0 || !UserPtr(outPtr, 24)) { SetStatus(frame, IpcStatus.Efault); return; }
        if (capacity > 0 && !UserPtr(dest, capacity)) { SetStatus(frame, IpcStatus.Efault); return; }
        IpcDeliveryView view;
        IpcStatus st = Ipc.Receive(pid, IpcHandle.FromU64(topic), (byte*)dest, capacity, out view);
        if (st != IpcStatus.Ok) { SetStatus(frame, st); return; }
        byte* o = (byte*)outPtr;
        Wr64(o, 0, view.MessageId);
        Wr64(o, 8, view.Delivery.ToU64());
        Wr32(o, 16, (uint)view.Size);
        frame[14] = 0;
    }

    private static void SetStatus(ulong* frame, IpcStatus s)
    {
        frame[14] = unchecked((ulong)(long)s);
    }

    private static void Wr64(byte* p, int o, ulong v)
    {
        for (int k = 0; k < 8; k++) p[o + k] = (byte)(v >> (k * 8));
    }
}