// INT 21h: the DOS-style system-call gate a ring-3 program calls instead of a
// native syscall instruction. Function number in AH, arguments in RSI/RDI/...,
// result written back to RAX in the saved frame.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // ABI version reported by GETABI (AH=82h). Bump when this table or its
    // conventions change incompatibly; osbkt checks it at startup
    // (osbkt/docs/ABI.md). v3 (phase 1c): 6Ah/20h are byte strings (B1), not
    // UTF-16 chars, so the v2->v3 wire-format change must be visible to callers.
    public const int AbiVersion = 3;

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

        if (func == 0x09) // print '$'-terminated string at RSI
        {
            if (!UserPtr(rsi, 0)) // start must be in the window; scan bounded below
            {
                frame[14] = unchecked((ulong)-1); // EFAULT
                return;
            }
            byte* p = (byte*)rsi;
            int n = 0;
            while (n < 4096 && rsi + (ulong)n < Layout.UserLimit && p[n] != (byte)'$')
            {
                Vga.PutChar((char)p[n]);
                n++;
            }
            frame[14] = 0;
            return;
        }

        if (func == 0x30) // getpid: current process id in EAX
        {
            frame[14] = (ulong)Scheduler.Get(Scheduler.Current).Pid;
            return;
        }

        if (func == 0x4C) // exit(code in AL): become a zombie; wait() reaps us
        {
            Scheduler.ExitCurrent((int)(rax & 0xFF));
            return;
        }

        if (func == 0x80) // spawn(program id in AL): returns the child's pid, or -1
        {
            int prog = (int)(rax & 0xFF);
            int pid;
            SpawnResult r = Scheduler.SpawnUser(prog, Scheduler.Current, out pid);
            frame[14] = unchecked((ulong)(r == SpawnResult.Ok ? pid : -1));
            return;
        }

        if (func == 0x6A) // spawnpath(req in RDI): SpawnReq block (final v2 form)
        {
            // RDI points to a SpawnReq { u64 pathPtr; u32 pathLen; u64 argsPtr;
            // u32 argsLen; i32 stdinH; i32 stdoutH; } (osbkt/docs/ABI.md).
            // All string arguments are BYTE strings (B1); the kernel uses the
            // true byte length. Order (B3): validate the block, resolve the
            // path, check it is a regular file and that the caller may execute
            // it, THEN load and spawn — a refusal (EACCES/ENOENT/EINVAL) never
            // creates a task. Error codes (B4): any bad pointer (block, path,
            // args) -> -1 EFAULT; bad content (pathLen 0 or >260, argsLen >128,
            // handle != -1) -> -5 EINVAL. -3 EACCES, -4 ENOENT, -6 ENOMEM.
            if (!UserPtr(rdi, 40)) { frame[14] = unchecked((ulong)-1); return; } // EFAULT
            byte* req = (byte*)rdi;
            ulong pathPtr = Rd64(req, 0);
            uint pathLen = Rd32(req, 8);
            ulong argsPtr = Rd64(req, 16);
            uint argsLen = Rd32(req, 24);
            int stdinH = (int)Rd32(req, 32);
            int stdoutH = (int)Rd32(req, 36);

            // Content checks first (EINVAL). The block length fields must be in
            // range and the handles must be "inherit" (-1) for now.
            if (pathLen == 0 || pathLen > 260 || argsLen > 128 ||
                stdinH != -1 || stdoutH != -1)
            {
                frame[14] = unchecked((ulong)-5); // EINVAL
                return;
            }
            // Then pointer checks (EFAULT) using the true byte length.
            if (!UserPtr(pathPtr, (int)pathLen) ||
                (argsLen > 0 && !UserPtr(argsPtr, (int)argsLen)))
            {
                frame[14] = unchecked((ulong)-1); // EFAULT
                return;
            }

            // Resolve + type + exec checks BEFORE any task is created (B3).
            // B1: the path/args are BYTE strings at the ABI; convert them to
            // the char* (UTF-16) form the Ext4/Scheduler layer uses.
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)pathLen; i++) cpath[i] = (char)((byte*)pathPtr)[i];
            if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; } // ENOENT
            uint ino;
            if (!Ext4.Resolve(cpath, (int)pathLen, out ino)) { frame[14] = unchecked((ulong)-4); return; }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }
            ushort mode = Ext4.InodeGetMode(inode);
            if (!Ext4.IsReg(mode)) { frame[14] = unchecked((ulong)-4); return; }   // ENOENT (not a file)
            if (!SpawnExecutable(mode, Ext4.InodeGetUid(inode), Ext4.InodeGetGid(inode)))
            { frame[14] = unchecked((ulong)-3); return; }                          // EACCES

            int pid;
            SpawnResult r = Scheduler.SpawnFileInode(ino, cpath, (int)pathLen, Scheduler.Current, out pid);
            if (r == SpawnResult.BadImage) { frame[14] = unchecked((ulong)-4); return; }   // ENOENT (no valid image)
            if (r == SpawnResult.OutOfMemory) { frame[14] = unchecked((ulong)-6); return; } // ENOMEM
            if (r == SpawnResult.NoSlot) { frame[14] = unchecked((ulong)-6); return; }     // ENOMEM

            int slot;
            if (argsLen > 0 && Scheduler.TryFindPid(pid, out slot))
            {
                // B1: convert the byte args to char* for SetArgs.
                char* cargs = stackalloc char[128];
                for (int i = 0; i < (int)argsLen; i++) cargs[i] = (char)((byte*)argsPtr)[i];
                Scheduler.SetArgs(slot, cargs, (int)argsLen);
            }
            frame[14] = (ulong)pid;
            return;
        }

        if (func == 0x6E) // getchar: next cooked console byte (own return space)
        {
            // Returns 0..255 for a byte, -1 when empty (non-blocking), -2 for a
            // pending Ctrl+C (once per press). -2 here is NOT ENOSYS.
            frame[14] = unchecked((ulong)(long)ConsoleInput.Pop());
            return;
        }

        if (func == 0x81) // wait(pid in RSI): blocks until the child exits
        {
            frame[14] = unchecked((ulong)Scheduler.WaitFor((int)rsi));
            return;
        }

        if (func == 0x82) // getabi: ABI version in AX (osbkt/docs/ABI.md)
        {
            frame[14] = (ulong)(ushort)AbiVersion;
            return;
        }

        if (func == 0x01) // write(buf in RDI, len in RSI): bytes to the console
        {
            ulong n = rsi > 4096 ? 4096 : rsi;
            if (!UserPtr(rdi, (int)n))
            {
                frame[14] = unchecked((ulong)-1); // EFAULT
                return;
            }
            byte* p = (byte*)rdi;
            for (ulong i = 0; i < n; i++) Vga.PutChar((char)p[i]);
            frame[14] = n;
            return;
        }

        if (func == 0x10) // meminfo(out in RDI): [u64 totalKB, usedKB, freeKB]
        {
            if (!UserPtr(rdi, 24))
            {
                frame[14] = unchecked((ulong)-1);
                return;
            }
            ulong* o = (ulong*)rdi;
            ulong total = PhysicalMemory.Total * Layout.FrameSize / 1024UL;
            ulong used = PhysicalMemory.Used * Layout.FrameSize / 1024UL;
            o[0] = total;
            o[1] = used;
            o[2] = total > used ? total - used : 0;
            frame[14] = 1;
            return;
        }

        if (func == 0x20) // resolve(path in RDI, len in RSI, out in RDX)
        {
            // out: [u32 ino, u16 mode, u16 uid, u64 size]. The validated
            // length must be exactly the length used (B2): reject rsi > 260
            // outright (0 fail), never clamp the check and use the raw value.
            // B1: the path is a BYTE string; convert to char* (UTF-16) for the
            // Ext4 layer, which is char-based.
            ulong len = rsi;
            if (len > 260)
            {
                frame[14] = 0;
                return;
            }
            if (!UserPtr(rdi, (int)len) || !UserPtr(rdx, 16))
            {
                frame[14] = 0;
                return;
            }
            if (!Ext4.Mount()) { frame[14] = 0; return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            uint ino;
            if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = 0; return; }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = 0; return; }
            byte* o = (byte*)rdx;
            Wr32(o, 0, ino);
            Wr16(o, 4, Ext4.InodeGetMode(inode));
            Wr16(o, 6, (ushort)Ext4.InodeGetUid(inode));
            ulong sz = Ext4.InodeGetSize(inode);
            for (int k = 0; k < 8; k++) o[8 + k] = (byte)(sz >> (k * 8));
            frame[14] = 1;
            return;
        }

        if (func == 0x21) // dirnext(dir ino in RDI, index in RSI, out in RDX, cap in RCX)
        {
            // out: [u32 childIno, u8 type, u8 nameLen, name bytes...]; "." and
            // ".." are skipped. Returns 1 while entries remain, 0 at the end.
            if (!UserPtr(rdx, (int)(rcx > 240 ? 240 : rcx)))
            {
                frame[14] = 0;
                return;
            }
            if (!Ext4.Mount()) { frame[14] = 0; return; }
            byte* din = stackalloc byte[256];
            if (!Ext4.ReadInode((uint)rdi, din)) { frame[14] = 0; return; }
            if (!Ext4.IsDir(Ext4.InodeGetMode(din))) { frame[14] = 0; return; }
            uint size = Ext4.InodeGetSize(din);
            ulong blocks = ((ulong)size + (ulong)Ext4.BlockSize - 1) / (ulong)Ext4.BlockSize;
            if (blocks == 0) blocks = 1;
            byte* blk = stackalloc byte[Ext4.BlockSize];
            ulong seen = 0;
            for (ulong b = 0; b < blocks; b++)
            {
                uint bnum = Ext4.InodeGetBlock(din, b);
                if (bnum == 0) continue;
                Ext4.ReadBlock(bnum, blk);
                int off = 0;
                while (off + 8 <= Ext4.BlockSize)
                {
                    byte* de = blk + off;
                    uint iino = Ext4.DirEntryInode(de);
                    ushort rlen = Ext4.DirEntryRecLen(de);
                    if (rlen == 0) break;
                    byte nlen = Ext4.DirEntryNameLen(de);
                    if (iino != 0 && nlen > 0)
                    {
                        bool dotty = (nlen == 1 && de[Layout.DirName] == (byte)'.') ||
                                     (nlen == 2 && de[Layout.DirName] == (byte)'.' &&
                                                      de[Layout.DirName + 1] == (byte)'.');
                        if (!dotty)
                        {
                            if (seen == rsi)
                            {
                                byte* o = (byte*)rdx;
                                Wr32(o, 0, iino);
                                o[4] = Ext4.DirEntryType(de);
                                o[5] = nlen;
                                ulong cap = rcx > 240 ? 240 : rcx;   // leave room in the block
                                for (ulong k = 0; k < nlen && k + 6 < cap; k++)
                                    o[6 + k] = de[Layout.DirName + k];
                                frame[14] = 1;
                                return;
                            }
                            seen++;
                        }
                    }
                    off += rlen;
                    if (off > Ext4.BlockSize - 8) break;
                }
            }
            frame[14] = 0;   // end of directory
            return;
        }

        if (func == 0x22) // getcwd(buf in RDI, cap in RSI) -> length
        {
            ulong cap = rsi > 256 ? 256 : rsi;
            if (!UserPtr(rdi, (int)cap))
            {
                frame[14] = unchecked((ulong)-1);
                return;
            }
            byte* cwd = stackalloc byte[160];
            int cl = Storage.Cwd.CopyTo(cwd, 160);
            byte* o = (byte*)rdi;
            int n = cl < (int)cap - 1 ? cl : (int)cap - 1;
            for (int k = 0; k < n; k++) o[k] = cwd[k];
            o[n] = 0;
            frame[14] = (ulong)n;
            return;
        }

        if (func == 0x23) // getargs(buf in RDI, cap in RSI): the argument string
        {
            // cap up to 129 (128 chars + NUL); the returned length is cap-1.
            ulong cap = rsi > 129 ? 129 : rsi;
            if (!UserPtr(rdi, (int)cap))
            {
                frame[14] = unchecked((ulong)-1);
                return;
            }
            int n = Scheduler.GetArgs(Scheduler.Current, (byte*)rdi, (int)cap);
            frame[14] = (ulong)n;
            return;
        }

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
        int gid = 0;                                // phase 1b: no group layer
        bool x;
        if ((uint)uid == fileUid) x = (perm & 0x40) != 0;    // owner x (bit 6, 0100)
        else if (gid == (int)fileGid) x = (perm & 0x08) != 0; // group x (bit 3, 0010)
        else x = (perm & 0x01) != 0;               // other x (bit 0, 0001)
        return x;
    }
}