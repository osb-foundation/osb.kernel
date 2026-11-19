using Osb.Kernel.Hardware;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 63h GETENV(name in RDI, len in RSI, buf in RDX, cap in RCX)
    // 64h SETENV(name in RDI, len in RSI, val in RDX, valLen in RCX)
    private static void HandleEnv(int func, ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

        int slot = Scheduler.Current;

        if (func == 0x63) // GETENV
        {
            ulong nlen = rsi;
            if (nlen > 64) { frame[14] = unchecked((ulong)-5); return; }
            ulong cap = rcx > 1024 ? 1024 : rcx;
            if (cap < 1 || !UserPtr(rdx, (int)cap))
            {
                frame[14] = unchecked((ulong)-1); // EFAULT
                return;
            }
            byte* o = (byte*)rdx;
            if (nlen == 0)
            {
                frame[14] = (ulong)Environment.CopyAll(slot, o, (int)cap);
                return;
            }
            if (!UserPtr(rdi, (int)nlen))
            {
                frame[14] = unchecked((ulong)-1);
                return;
            }
            char* nm = stackalloc char[64];
            for (int i = 0; i < (int)nlen; i++) nm[i] = (char)((byte*)rdi)[i];
            int vo;
            int vl;
            if (!Environment.TryGet(slot, nm, (int)nlen, out vo, out vl))
            {
                frame[14] = unchecked((ulong)-4); // ENOENT
                return;
            }
            int lim = vl < (int)cap - 1 ? vl : (int)cap - 1;
            for (int k = 0; k < lim; k++) o[k] = (byte)Environment.ValueAt(slot, vo + k);
            o[lim] = 0;
            frame[14] = (ulong)vl;
            return;
        }

        if (func == 0x64) // SETENV
        {
            ulong nlen = rsi;
            ulong vlen = rcx;
            if (nlen == 0 || nlen > 64 || vlen > 256) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)nlen))
            {
                frame[14] = unchecked((ulong)-1);
                return;
            }
            char* nm = stackalloc char[64];
            for (int i = 0; i < (int)nlen; i++) nm[i] = (char)((byte*)rdi)[i];
            if (rdx == 0 && vlen == 0)
            {
                frame[14] = unchecked((ulong)Environment.DeleteChars(slot, nm, (int)nlen));
                return;
            }
            if (vlen > 0 && !UserPtr(rdx, (int)vlen))
            {
                frame[14] = unchecked((ulong)-1);
                return;
            }
            char* val = stackalloc char[256];
            for (int i = 0; i < (int)vlen; i++) val[i] = (char)((byte*)rdx)[i];
            frame[14] = unchecked((ulong)Environment.SetChars(slot, nm, (int)nlen, val, (int)vlen));
            return;
        }

        frame[14] = unchecked((ulong)-2); // ENOSYS
    }
}