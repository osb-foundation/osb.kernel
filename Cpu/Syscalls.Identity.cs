using Osb.Kernel.Processes;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 65h-69h: session identity syscalls (LOGIN/LOGOUT/SUDO/END_SUDO/WHOAMI).
    private static void HandleIdentity(int func, ulong* frame)
    {
        ulong rdi = frame[9];    // arg1
        ulong rsi = frame[10];   // arg2
        ulong rdx = frame[11];   // arg3
        ulong rcx = frame[12];   // arg4

        switch (func)
        {
            case 0x65: // LOGIN(name RDI, nameLen RSI, pass RDX, passLen RCX)
            {
                int nameLen = (int)rsi;
                int passLen = (int)rcx;
                if (nameLen < 0 || nameLen > 256 || !UserPtr(rdi, nameLen) ||
                    passLen < 0 || (passLen > 0 && !UserPtr(rdx, passLen)))
                { frame[14] = unchecked((ulong)-1); return; }   // EFAULT
                frame[14] = unchecked((ulong)Scheduler.Login((byte*)rdi, nameLen, (byte*)rdx, passLen));
                return;
            }

            case 0x66: // LOGOUT()
                frame[14] = unchecked((ulong)Scheduler.Logout());
                return;

            case 0x67: // SUDO()
                frame[14] = unchecked((ulong)Scheduler.Sudo());
                return;

            case 0x68: // END_SUDO()
                frame[14] = unchecked((ulong)Scheduler.EndSudo());
                return;

            case 0x69: // WHOAMI(buf RDI, cap RSI) -> name length, -3 if not logged in
            {
                int cap = (int)rsi;
                if (cap < 0 || cap > 256 || !UserPtr(rdi, cap))
                { frame[14] = unchecked((ulong)-1); return; }   // EFAULT
                frame[14] = unchecked((ulong)Scheduler.WhoAmI((byte*)rdi, cap));
                return;
            }

            default:
                frame[14] = unchecked((ulong)-2); // ENOSYS
                return;
        }
    }
}
