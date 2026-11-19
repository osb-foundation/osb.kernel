using Osb.Kernel.Hardware;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 65h-69h Session identity
    private static void HandleIdentity(int func, ulong* frame)
    {
        ulong rax = frame[14];
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

        int slot = Scheduler.Current;
        int sub = (int)(rax & 0xFF);

        if (func == 0x65) // LOGIN
        {
            ulong nlen = rsi;
            if (nlen == 0 || nlen > 260 || !UserPtr(rdi, (int)nlen)) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdx, (int)rcx)) { frame[14] = unchecked((ulong)-1); return; }
            char* cname = stackalloc char[260];
            for (int i = 0; i < (int)nlen; i++) cname[i] = (char)((byte*)rdi)[i];
            char* cpass = stackalloc char[260];
            int plen = (int)rcx;
            for (int i = 0; i < plen; i++) cpass[i] = (char)((byte*)rdx)[i];

            // Only session leader may LOGIN
            if (slot != 0 && Scheduler.Get(slot).Pid != 1) { frame[14] = unchecked((ulong)-3); return; }

            // Active session teardown first
            if (Users.CurrentSessionActive)
            {
                Users.LogoutSession(slot);
            }

            if (!Users.Login(cname, (int)nlen, cpass, plen))
            {
                frame[14] = unchecked((ulong)-3); // EACCES (unknown user or wrong password)
                return;
            }

            // Set session cwd to user's home
            char* home = stackalloc char[64];
            int hlen = 0;
            if (Users.GetHome(Users.CurrentSessionUid, home, ref hlen))
            {
                Cwd.Set((byte*)home, hlen);
            }
            else
            {
                Cwd.SetRoot();
            }

            frame[14] = 0;
            return;
        }

        if (func == 0x66) // LOGOUT
        {
            // Only session leader may LOGOUT
            if (slot != 0 && Scheduler.Get(slot).Pid != 1) { frame[14] = unchecked((ulong)-3); return; }

            Users.LogoutSession(slot);
            frame[14] = 0;
            return;
        }

        if (func == 0x67) // SUDO
        {
            // Elevate calling task's effective uid to 0 iff session user is in SUDOERS
            char* name = stackalloc char[64];
            int nlen = Users.GetName(Users.CurrentSessionUid, name, 64);
            if (nlen < 0 || !Users.IsSudoer(name, nlen)) { frame[14] = unchecked((ulong)-3); return; }
            Scheduler.SetEffectiveUid(slot, 0);
            frame[14] = 0;
            return;
        }

        if (func == 0x68) // END_SUDO
        {
            // Restore calling task's effective uid to session user's real uid
            Scheduler.SetEffectiveUid(slot, Users.CurrentSessionUid);
            frame[14] = 0;
            return;
        }

        if (func == 0x69) // WHOAMI
        {
            ulong cap = rsi;
            if (cap == 0 || !UserPtr(rdi, (int)cap)) { frame[14] = unchecked((ulong)-1); return; }
            if (!Users.CurrentSessionActive) { frame[14] = unchecked((ulong)-3); return; }
            char* name = stackalloc char[64];
            int nlen = Users.GetName(Users.CurrentSessionUid, name, 64);
            if (nlen < 0) { frame[14] = unchecked((ulong)-3); return; }
            int lim = nlen < (int)cap - 1 ? nlen : (int)cap - 1;
            for (int k = 0; k < lim; k++) ((byte*)rdi)[k] = (byte)name[k];
            ((byte*)rdi)[lim] = 0;
            frame[14] = (ulong)nlen;
            return;
        }

        frame[14] = unchecked((ulong)-2); // ENOSYS
    }
}