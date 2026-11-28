// INT 21h: the DOS-style system-call gate a ring-3 program calls instead of a
// native syscall instruction. Function number in AH, arguments in RSI/RDI/...,
// result written back to RAX in the saved frame.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // ABI version reported by GETABI (AH=82h). Bump when this table or its
    // conventions change incompatibly; osbkt checks it at startup
    // (osbkt/docs/ABI.md). v10 (Card 3.1, phase 3): non-blocking topic IPC
    // (83h-8Ah). v7: console (6Fh CLEAR, 70h SETCOLOR, 71h GETCOLOR,
    // 72h DATETIME); WAIT interruptible (-7 EINTR); KILL/SETFG uid-gated.
    // v6 (slice 6): console (6Fh CLEAR, 70h SETCOLOR, 71h GETCOLOR, 72h DATETIME);
    // WAIT interruptible (-7 EINTR); KILL/SETFG uid-gated. v5 (phase 3, slice 3):
    // environment (63h/64h). v4 (phase 1c): session identity (65h-69h).
    public const int AbiVersion = 10;

    public static void Handle(ulong* frame)
    {
        ulong rax = frame[14];           // full RAX as passed by the user
        ulong rsi = frame[10];           // arg0 (e.g. string pointer)
        ulong rdi = frame[9];            // arg1
        ulong rdx = frame[11];           // arg2
        ulong rcx = frame[12];           // arg3
        ulong func = (rax >> 8) & 0xFF;  // AH = function number

        // Graphics + input services (XWIN) live in their own partial.
        if (SyscallGfx.Handle(func, frame)) return;

        // Session identity (65h-69h) lives in its own partial.
        if (func >= 0x65 && func <= 0x69) { HandleIdentity((int)func, frame); return; }

        // Environment (63h-64h) lives in its own partial.
        if (func == 0x63 || func == 0x64) { HandleEnv((int)func, frame); return; }

        // User management (73h-75h) lives in its own partial.
        if (func >= 0x73 && func <= 0x75) { HandleUser((int)func, frame); return; }

        // Terminal API (7Ah-7Fh) lives in its own partial.
        if (func >= 0x7A && func <= 0x7F) { HandleTerminal((int)func, frame); return; }

        // Console I/O
        if (func == 0x01) { HandleWrite(frame); return; }
        if (func == 0x09) { HandlePrint(frame); return; }
        if (func == 0x6F) { HandleClear(frame); return; }
        if (func == 0x70) { HandleSetColor(frame); return; }
        if (func == 0x71) { HandleGetColor(frame); return; }
        if (func == 0x72) { HandleDateTime(frame); return; }

        // Memory
        if (func == 0x10) { HandleMemInfo(frame); return; }
        if (func == 0x11) { HandleMemGrow(frame); return; }

        // Filesystem
        if (func == 0x20) { HandleResolve(frame); return; }
        if (func == 0x21) { HandleDirNext(frame); return; }
        if (func == 0x22) { HandleGetCwd(frame); return; }
        if (func == 0x23) { HandleGetArgs(frame); return; }
        if (func == 0x24) { HandleChDir(frame); return; }
        if (func == 0x25) { HandleMkdir(frame); return; }
        if (func == 0x26) { HandleRmDir(frame); return; }
        if (func == 0x27) { HandleUnlink(frame); return; }
        if (func == 0x28) { HandleRename(frame); return; }
        if (func == 0x29) { HandleCopy(frame); return; }
        if (func == 0x2A) { HandleOpen(frame); return; }
        if (func == 0x2B) { HandleRead(frame); return; }
        if (func == 0x2C) { HandleClose(frame); return; }
        if (func == 0x60) { HandleStat(frame); return; }
        if (func == 0x61) { HandleLink(frame); return; }
        if (func == 0x76) { HandleCreateFile(frame); return; }
        if (func == 0x77) { HandleWriteFile(frame); return; }
        if (func == 0x78) { HandleSetStdout(frame); return; }
        if (func == 0x79) { HandleSetStdin(frame); return; }

        // Processes
        if (func == 0x30) { HandleGetPid(frame); return; }
        if (func == 0x4C) { HandleExit(frame); return; }
        if (func == 0x6A) { HandleSpawnPath(frame); return; }
        if (func == 0x6B) { HandleTaskList(frame); return; }
        if (func == 0x6C) { HandleKill(frame); return; }
        if (func == 0x6D) { HandleSetFg(frame); return; }
        if (func == 0x80) { HandleSpawn(frame); return; }
        if (func == 0x81) { HandleWait(frame); return; }
        if (func == 0x82) { HandleGetAbi(frame); return; }

        // IPC (83h-8Ah) lives in its own partial.
        if (func >= 0x83 && func <= 0x8A) { HandleIpc((int)func, frame); return; }

        // Input
        if (func == 0x6E) { HandleGetChar(frame); return; }

        frame[14] = unchecked((ulong)-2); // ENOSYS
    }

    private static void Wr32(byte* p, int o, uint v)
    {
        p[o] = (byte)(v & 0xFF);
        p[o + 1] = (byte)((v >> 8) & 0xFF);
        p[o + 2] = (byte)((v >> 16) & 0xFF);
        p[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void Wr16(byte* p, int o, ushort v)
    {
        p[o] = (byte)(v & 0xFF);
        p[o + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static uint Rd32(byte* p, int o)
    {
        return (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));
    }

    private static ulong Rd64(byte* p, int o)
    {
        ulong v = 0;
        for (int k = 0; k < 8; k++) v |= ((ulong)p[o + k]) << (k * 8);
        return v;
    }

    // Overflow-safe user-window pointer check (A7/S3): the whole [ptr, ptr+len)
    // must lie in the user window. A len that wraps ptr+len past UserLimit (or
    // a negative len) is rejected. ptr == 0 with len == 0 is allowed (empty).
    private static bool UserPtr(ulong ptr, int len)
    {
        if (len < 0) return false;
        if (len == 0) return ptr >= Layout.UserBase && ptr <= Layout.UserLimit;
        if (ptr < Layout.UserBase) return false;
        ulong end = ptr + (ulong)len;              // may wrap; check both ways
        if (end < ptr) return false;               // overflow -> reject
        return end <= Layout.UserLimit;
    }

    // True when the file (mode, uid, gid) is executable by the current task.
    // The exec bit is checked for the caller's class (B3): owner x for the
    // file's owner, else group x if the caller's gid matches the file's gid,
    // else other x — never "any x bit". The current uid/gid come from the
    // Scheduler (phase 1b: both 0; 1c wires the real session uid).
    private static bool SpawnExecutable(ushort mode, uint fileUid, uint fileGid)
    {
        int perm = mode & 0x1FF;                    // low 9 bits = rwx
        int uid = Scheduler.CurrentUid();
        // Root (uid 0) passes if ANY execute bit is set (owner/group/other).
        if (uid == 0) return (perm & 0x49) != 0;
        int gid = Scheduler.CurrentGid();
        if ((uint)uid == fileUid) return (perm & 0x40) != 0;    // owner x (0100)
        if ((uint)gid == fileGid) return (perm & 0x08) != 0;    // group x (0010)
        return (perm & 0x01) != 0;               // other x (0001)
    }
}