// 65h-69h: session identity syscalls (LOGIN/LOGOUT/SUDO/END_SUDO/WHOAMI).
// Split from Syscalls.cs to keep each file under the 400-line limit. All
// strings are BYTE strings with explicit lengths, validated with UserPtr
// (ABI.md v4). Every authentication/authorization failure returns -3 EACCES;
// unknown user and wrong password are indistinguishable.

using Osb.Kernel.Processes;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // Handles 65h-69h. Returns true when the function was one of ours.
    public static bool HandleIdentity(int func, ulong* frame)
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
                { frame[14] = unchecked((ulong)-1); return true; }   // EFAULT
                frame[14] = unchecked((ulong)Scheduler.Login((byte*)rdi, nameLen, (byte*)rdx, passLen));
                return true;
            }

            case 0x66: // LOGOUT()
                frame[14] = unchecked((ulong)Scheduler.Logout());
                return true;

            case 0x67: // SUDO()
                frame[14] = unchecked((ulong)Scheduler.Sudo());
                return true;

            case 0x68: // END_SUDO()
                frame[14] = unchecked((ulong)Scheduler.EndSudo());
                return true;

            case 0x69: // WHOAMI(buf RDI, cap RSI) -> name length, -3 if not logged in
            {
                int cap = (int)rsi;
                if (cap < 0 || cap > 256 || !UserPtr(rdi, cap))
                { frame[14] = unchecked((ulong)-1); return true; }   // EFAULT
                frame[14] = unchecked((ulong)Scheduler.WhoAmI((byte*)rdi, cap));
                return true;
            }

            default:
                return false;
        }
    }
}