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
    // (osbkt/docs/ABI.md). v6 (slice 6): console (6Fh CLEAR, 70h SETCOLOR,
    // 71h GETCOLOR, 72h DATETIME); WAIT interruptible (-7 EINTR); KILL/SETFG
    // uid-gated. v5 (phase 3, slice 3): environment (63h/64h). v4 (phase 1c):
    // session identity (65h-69h).
    public const int AbiVersion = 7;

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

        // Session identity (65h-69h) lives in its own partial (I2).
        if (func >= 0x65 && func <= 0x69) { HandleIdentity((int)func, frame); return; }

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
            Vga.FlushCursor();
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
            // range. Handles may be -1 (inherit console) or a valid open handle.
            if (pathLen == 0 || pathLen > 260 || argsLen > 128)
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
            if (Scheduler.TryFindPid(pid, out slot))
            {
                // Copy parent's file handles to child, set stdin/stdout handles.
                Scheduler.PropagateHandles(slot, stdinH, stdoutH);
                if (argsLen > 0)
                {
                    // B1: convert the byte args to char* for SetArgs.
                    char* cargs = stackalloc char[128];
                    for (int i = 0; i < (int)argsLen; i++) cargs[i] = (char)((byte*)argsPtr)[i];
                    Scheduler.SetArgs(slot, cargs, (int)argsLen);
                }
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

        if (func == 0x01) // write(buf in RDI, len in RSI): bytes to the console or stdout handle
        {
            ulong n = rsi > 4096 ? 4096 : rsi;
            if (!UserPtr(rdi, (int)n))
            {
                frame[14] = unchecked((ulong)-1); // EFAULT
                return;
            }
            int stdoutH = Scheduler.GetStdout();
            if (stdoutH != -1)
            {
                int written = Scheduler.FileWrite(stdoutH, (byte*)rdi, (int)n);
                if (written < 0) { frame[14] = unchecked((ulong)-5); return; }
                frame[14] = (ulong)written;
                return;
            }
            byte* p = (byte*)rdi;
            for (ulong i = 0; i < n; i++) Vga.PutChar((char)p[i]);
            Vga.FlushCursor();
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

        if (func == 0x24) // chdir(path in RDI, len in RSI)
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            byte* canon = stackalloc byte[300];
            int canonLen = 0;
            uint ino;
            if (!Ext4.ResolveCanonical(cpath, (int)len, out ino, canon, ref canonLen)) { frame[14] = unchecked((ulong)-4); return; }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }
            if (!Ext4.IsDir(Ext4.InodeGetMode(inode))) { frame[14] = unchecked((ulong)-4); return; }
            Cwd.Set(canon, canonLen);
            frame[14] = 0;
            return;
        }

        if (func == 0x2A) // open(path in RDI, len in RSI) -> handle or negative
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            uint ino;
            if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = unchecked((ulong)-4); return; }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }
            if (!Ext4.IsReg(Ext4.InodeGetMode(inode))) { frame[14] = unchecked((ulong)-4); return; }
            // I6: read permission enforcement
            uint owner = Ext4.InodeGetUid(inode);
            ushort mode = Ext4.InodeGetMode(inode);
            if (!Users.CanRead(owner, mode)) { frame[14] = unchecked((ulong)-3); return; }
            int size = (int)Ext4.InodeGetSize(inode);
            int slot = Scheduler.Current;
            int h = Scheduler.FileOpen(slot, ino, size);
            if (h < 0) { frame[14] = unchecked((ulong)-6); return; }
            frame[14] = (ulong)h;
            return;
        }

        if (func == 0x2B) // read(handle in RDI, buf in RSI, count in RDX) -> bytes or negative
        {
            int handle = (int)rdi;
            ulong count = rdx;
            if (count == 0) { frame[14] = 0; return; }
            ulong cap = count > 4096 ? 4096 : count;
            if (!UserPtr(rsi, (int)cap)) { frame[14] = unchecked((ulong)-1); return; }
            byte* buf = stackalloc byte[4096];
            int got = Scheduler.FileRead(handle, buf, (int)cap);
            if (got < 0) { frame[14] = unchecked((ulong)-1); return; }
            byte* o = (byte*)rsi;
            for (int k = 0; k < got; k++) o[k] = buf[k];
            frame[14] = (ulong)got;
            return;
        }

        if (func == 0x2C) // close(handle in RDI)
        {
            Scheduler.FileClose((int)rdi);
            frame[14] = 0;
            return;
        }

        if (func == 0x60) // stat(path in RDI, len in RSI, out in RDX) -> 1/0
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = 0; return; }
            if (!UserPtr(rdi, (int)len) || !UserPtr(rdx, 16)) { frame[14] = 0; return; }
            if (!Ext4.Mount()) { frame[14] = 0; return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            uint ino;
            if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = 0; return; }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = 0; return; }
            byte* o = (byte*)rdx;
            ushort mode = Ext4.InodeGetMode(inode);
            o[0] = (byte)(mode & 0xFF); o[1] = (byte)(mode >> 8);
            uint uid = Ext4.InodeGetUid(inode);
            o[2] = (byte)(uid & 0xFF); o[3] = (byte)((uid >> 8) & 0xFF);
            uint gid = Ext4.InodeGetGid(inode);
            o[4] = (byte)(gid & 0xFF); o[5] = (byte)((gid >> 8) & 0xFF);
            uint size = Ext4.InodeGetSize(inode);
            for (int k = 0; k < 4; k++) o[6 + k] = (byte)(size >> (k * 8));
            uint mtime = Ext4.InodeGetMtime(inode);
            for (int k = 0; k < 4; k++) o[10 + k] = (byte)(mtime >> (k * 8));
            ushort links = (ushort)Ext4.InodeGetLinks(inode);
            o[14] = (byte)(links & 0xFF); o[15] = (byte)(links >> 8);
            frame[14] = 1;
            return;
        }

        if (func == 0x25) // mkdir(path in RDI, len in RSI) -> 0 ok, -3 EACCES, -4 not found/exists
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            uint newIno;
            if (Osb.Kernel.Shell.Commands.FileCommands.MakeDir(cpath, (int)len, (ushort)(0x4000 | 0x1ED), (uint)Scheduler.CurrentUid(), out newIno))
            {
                Vga.WriteLine("OK");
                frame[14] = 0;
            }
            else
            {
                frame[14] = unchecked((ulong)-4);
            }
            return;
        }

        if (func == 0x26) // rmdir(path in RDI, len in RSI) -> 0 ok, -4 fail
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            if (Osb.Kernel.Shell.Commands.FileCommands.RemoveDir(cpath, (int)len))
            {
                Vga.WriteLine("OK");
                frame[14] = 0;
            }
            else
            {
                frame[14] = unchecked((ulong)-4);
            }
            return;
        }

        if (func == 0x27) // delete(path in RDI, len in RSI) -> 0 ok, -4 fail
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            Osb.Kernel.Shell.Commands.FileCommands.Del(cpath, (int)len);
            frame[14] = 0;
            return;
        }

        if (func == 0x28) // rename(oldPath in RDI, oldLen in RSI, newPath in RDX, newLen in RCX) -> 0 ok, -4 fail
        {
            ulong olen = rsi;
            ulong nlen = rcx;
            if (olen == 0 || olen > 260 || nlen == 0 || nlen > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)olen) || !UserPtr(rdx, (int)nlen)) { frame[14] = unchecked((ulong)-1); return; }
            char* op = stackalloc char[260];
            char* np = stackalloc char[260];
            for (int i = 0; i < (int)olen; i++) op[i] = (char)((byte*)rdi)[i];
            for (int i = 0; i < (int)nlen; i++) np[i] = (char)((byte*)rdx)[i];
            // Ren takes a single line "REN old new"; build it
            char* line = stackalloc char[520];
            int ll = 0;
            // "REN " + old + " " + new
            fixed (char* r = "REN ") { for (int k = 0; k < 4; k++) line[ll++] = r[k]; }
            for (int k = 0; k < (int)olen; k++) line[ll++] = op[k];
            line[ll++] = ' ';
            for (int k = 0; k < (int)nlen; k++) line[ll++] = np[k];
            Osb.Kernel.Shell.Commands.FileCommands.Ren(line, ll);
            frame[14] = 0;
            return;
        }

        if (func == 0x29) // copy(srcPath in RDI, srcLen in RSI, dstPath in RDX, dstLen in RCX) -> 0 ok, -4 fail
        {
            ulong slen = rsi;
            ulong dlen = rcx;
            if (slen == 0 || slen > 260 || dlen == 0 || dlen > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)slen) || !UserPtr(rdx, (int)dlen)) { frame[14] = unchecked((ulong)-1); return; }
            char* sp = stackalloc char[260];
            char* dp = stackalloc char[260];
            for (int i = 0; i < (int)slen; i++) sp[i] = (char)((byte*)rdi)[i];
            for (int i = 0; i < (int)dlen; i++) dp[i] = (char)((byte*)rdx)[i];
            char* line = stackalloc char[520];
            int ll = 0;
            fixed (char* c = "COPY ") { for (int k = 0; k < 5; k++) line[ll++] = c[k]; }
            for (int k = 0; k < (int)slen; k++) line[ll++] = sp[k];
            line[ll++] = ' ';
            for (int k = 0; k < (int)dlen; k++) line[ll++] = dp[k];
            Osb.Kernel.Shell.Commands.FileCommands.Copy(line, ll);
            frame[14] = 0;
            return;
        }

        if (func == 0x61) // ln(command line in RDI, len in RSI) -> 0 ok, -4 fail
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cline = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cline[i] = (char)((byte*)rdi)[i];
            Osb.Kernel.Shell.Commands.FileCommands.Ln(cline, (int)len);
            frame[14] = 0;
            return;
        }

        if (func == 0x62) // trash(command line in RDI, len in RSI) -> 0 ok
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cline = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cline[i] = (char)((byte*)rdi)[i];
            Osb.Kernel.Shell.Commands.TrashCommands.Run(cline, (int)len);
            frame[14] = 0;
            return;
        }

        if (func == 0x63) // getenv(name in RDI, len in RSI, buf in RDX, cap in RCX)
        {
            // Per-task store (Slice 3, C1). nameLen 0 lists every variable as
            // "NAME=VALUE\n" in insertion order (cap up to 1024, truncated).
            // Otherwise: copy the value (case-insensitive name) NUL-terminated
            // and return the FULL value length (a short buffer truncates the
            // copy, not the reported length). Errors: -1 EFAULT, -4 ENOENT
            // (unset), -5 EINVAL (name > 64).
            int slot = Scheduler.Current;
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

        if (func == 0x64) // setenv(name in RDI, len in RSI, val in RDX, valLen in RCX)
        {
            // Per-task store (Slice 3, C1). Name 1..64, value 0..256. Returns 0
            // on success, -1 EFAULT, -3 EACCES (kernel-locked USER/HOME), -5
            // EINVAL (bad name/oversize), -6 ENOMEM (store full). A NULL value
            // pointer with length 0 deletes the variable (-4 if unset).
            int slot = Scheduler.Current;
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

        if (func == 0x6B) // tasklist(out in RDI, cap in RSI) -> count
        {
            if (rdi == 0 || rsi == 0) { frame[14] = unchecked((ulong)-1); return; }
            if (!UserPtr(rdi, (int)rsi)) { frame[14] = unchecked((ulong)-1); return; }
            int uid = Scheduler.CurrentUid();
            byte* outp = (byte*)rdi;
            int cap = (int)rsi;
            int n = 0;
            int off = 0;
            for (int slot = 0; slot < 8; slot++)
            {
                if (Scheduler.IsFree(slot)) continue;
                var ti = Scheduler.Get(slot);

                if (off + 16 > cap) break;
                // write pid (4), state (4), name (8 bytes)
                outp[off] = (byte)(ti.Pid & 0xFF);
                outp[off+1] = (byte)((ti.Pid>>8)&0xFF);
                outp[off+2] = (byte)((ti.Pid>>16)&0xFF);
                outp[off+3] = (byte)((ti.Pid>>24)&0xFF);
                outp[off+4] = (byte)(ti.State & 0xFF);
                outp[off+5]=outp[off+6]=outp[off+7]=0;
                for (int k=0;k<8;k++) outp[off+8+k]= (k < ti.NameLen && ti.Name!=null ? ti.Name[k] : (byte)0);
                off += 16; n++;
            }
            frame[14] = unchecked((ulong)n);
            return;
        }

        if (func == 0x6C) // kill(pid in RDI) -> 0 ok / -4 not found / -3 denied
        {
            int pid = (int)rdi;
            int slot;
            if (!Scheduler.TryFindPid(pid, out slot)) { frame[14] = unchecked((ulong)-4); return; }
            if (slot == 0) { frame[14] = unchecked((ulong)-3); return; } // console
            int uid = Scheduler.CurrentUid();
            int targetUid = Scheduler.GetUid(slot);
            if (uid != 0 && uid != targetUid) { frame[14] = unchecked((ulong)-3); return; } // EACCES
            Scheduler.Release(slot);
            frame[14] = 0;
            return;
        }

        if (func == 0x6D) // setfg(pid in RDI) -> 0 ok / -4 not found / -3 denied
        {
            int pid = (int)rdi;
            int slot;
            if (!Scheduler.TryFindPid(pid, out slot)) { frame[14] = unchecked((ulong)-4); return; }
            int uid = Scheduler.CurrentUid();
            int targetUid = Scheduler.GetUid(slot);
            if (uid != 0 && uid != targetUid) { frame[14] = unchecked((ulong)-3); return; } // EACCES
            Scheduler.MakeForeground(slot);
            frame[14] = 0;
            return;
        }

        if (func == 0x6F) // clear: console clear + serial ESC[2J H
        {
            Vga.Clear();
            frame[14] = 0;
            return;
        }

        if (func == 0x70) // setcolor: fg in AL, bg in AH
        {
            byte fg = (byte)(rax & 0xFF);
            byte bg = (byte)((rax >> 8) & 0xFF);
            if (fg == bg) { frame[14] = unchecked((ulong)-5); return; } // EINVAL: same fg/bg
            Vga.SetColor(fg, bg);
            frame[14] = 0;
            return;
        }

        if (func == 0x71) // getcolor: out fg, bg (2 bytes at RDI)
        {
            if (!UserPtr(rdi, 2)) { frame[14] = unchecked((ulong)-1); return; } // EFAULT
            byte attr = Vga.Attr;
            ((byte*)rdi)[0] = (byte)(attr & 0x0F);       // fg
            ((byte*)rdi)[1] = (byte)((attr >> 4) & 0x0F); // bg
            frame[14] = 0;
            return;
        }

        if (func == 0x72) // datetime: out u8[8] = Y,M,D,h,m,s,0,0 at RDI
        {
            if (!UserPtr(rdi, 8)) { frame[14] = unchecked((ulong)-1); return; } // EFAULT
            byte* o = (byte*)rdi;
            o[0] = Rtc.Year;
            o[1] = Rtc.Month;
            o[2] = Rtc.Day;
            o[3] = Rtc.Hour;
            o[4] = Rtc.Minute;
            o[5] = Rtc.Second;
            o[6] = 0;
            o[7] = 0;
            frame[14] = 0;
            return;
        }

        // 73h CHMOD(path RDI, len RSI, mode RDX) -> 0 ok / -3 EACCES / -4 not found / -5 EINVAL / -1 EFAULT
        // Root or the file owner may set the low 12 permission bits.
        if (func == 0x73)
        {
            ushort mode = (ushort)rdx;
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }
            uint ino;
            if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = unchecked((ulong)-4); return; }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }
            int uid = Scheduler.CurrentUid();
            uint fileUid = Ext4.InodeGetUid(inode);
            if (uid != 0 && (uint)uid != fileUid) { frame[14] = unchecked((ulong)-3); return; } // EACCES
            if (!Users.ChmodPath(cpath, (int)len, mode)) { frame[14] = unchecked((ulong)-4); return; }
            frame[14] = 0;
            return;
        }

        // 74h CHOWN(path RDI, len RSI, uid RDX) -> 0 ok / -3 EACCES / -4 not found / -5 EINVAL / -1 EFAULT
        // Root only.
        if (func == 0x74)
        {
            if (Scheduler.CurrentUid() != 0) { frame[14] = unchecked((ulong)-3); return; } // EACCES
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            uint newUid = (uint)rdx;
            if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }
            uint ino;
            if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = unchecked((ulong)-4); return; }
            if (!Users.Chown(cpath, (int)len, newUid)) { frame[14] = unchecked((ulong)-4); return; }
            frame[14] = 0;
            return;
        }

        // 75h USEROP(sub in AL) — account management.
        //   AL=0 LIST:  RDI=buf, RSI=cap -> writes "name uid home sudo\n" per user, returns byte count
        //   AL=1 ADD:   RDI=name, RSI=nameLen, RDX=pass, RCX=passLen (root only)
        //   AL=2 CHANGEPASS: RDI=name, RSI=nameLen, RDX=pass, RCX=passLen (root or own)
        //   AL=3 DEL:   RDI=name, RSI=nameLen (root only, not root user)
        //   AL=4 SUDO:  RDI=name, RSI=nameLen (root only)
        //   AL=5 UNSUDO: RDI=name, RSI=nameLen (root only)
        // Returns 0 ok / -3 EACCES / -4 not found / -5 EINVAL / -6 ENOMEM / -1 EFAULT.
        if (func == 0x75)
        {
            int sub = (int)(rax & 0xFF);
            if (sub == 0) // LIST
            {
                ulong cap = rsi;
                if (cap == 0 || !UserPtr(rdi, (int)cap)) { frame[14] = unchecked((ulong)-1); return; }
                byte* o = (byte*)rdi;
                int written = 0;
                char* nm = stackalloc char[Users.NameCap];
                char* hm = stackalloc char[64];
                int sessUid = Scheduler.CurrentUid();
                for (int i = 0; i < Users.Count; i++)
                {
                    int nl = Users.NameAt(i, nm);
                    int hl = Users.HomeAt(i, hm);
                    for (int k = 0; k < nl && written < (int)cap - 1; k++) o[written++] = (byte)nm[k];
                    if (written < (int)cap - 1) o[written++] = (byte)' ';
                    var ubuf = stackalloc char[8];
                    int un = 0;
                    {
                        uint uv = (uint)Users.UidAt(i);
                        if (uv == 0) ubuf[un++] = '0';
                        else { byte* tmp = stackalloc byte[8]; int tn = 0; while (uv != 0) { tmp[tn++] = (byte)('0' + (int)(uv % 10)); uv /= 10; } while (tn > 0) ubuf[un++] = (char)tmp[--tn]; }
                    }
                    for (int k = 0; k < un && written < (int)cap - 1; k++) o[written++] = (byte)ubuf[k];
                    if (written < (int)cap - 1) o[written++] = (byte)' ';
                    for (int k = 0; k < hl && written < (int)cap - 1; k++) o[written++] = (byte)hm[k];
                    if (written < (int)cap - 1) o[written++] = (byte)' ';
                    o[written++] = Users.IsSudoerAt(i) ? (byte)'1' : (byte)'0';
                    if (written < (int)cap - 1) o[written++] = (byte)' ';
                    if (Users.UidAt(i) == sessUid) o[written++] = (byte)'*';
                    if (written < (int)cap - 1) o[written++] = (byte)'\n';
                }
                frame[14] = (ulong)written;
                return;
            }

            // name-taking subfunctions
            ulong nlen = rsi;
            if (nlen == 0 || nlen > 260 || !UserPtr(rdi, (int)nlen)) { frame[14] = unchecked((ulong)-5); return; }
            char* cname = stackalloc char[260];
            for (int i = 0; i < (int)nlen; i++) cname[i] = (char)((byte*)rdi)[i];

            int idx = Users.Find(cname, (int)nlen);
            bool isRoot = Scheduler.CurrentUid() == 0;

            switch (sub)
            {
                case 1: // ADD (root only)
                    if (!isRoot) { frame[14] = unchecked((ulong)-3); return; }
                    ulong plen = rcx;
                    char* cpass = stackalloc char[260];
                    if (plen > 0) { if (!UserPtr(rdx, (int)plen)) { frame[14] = unchecked((ulong)-1); return; } for (int i = 0; i < (int)plen; i++) cpass[i] = (char)((byte*)rdx)[i]; }
                    if (!Users.Add(cname, (int)nlen, cpass, (int)plen)) { frame[14] = unchecked((ulong)-6); return; } // ENOMEM (includes exists/invalid)
                    frame[14] = 0;
                    return;
                case 2: // CHANGEPASS (root or own)
                    if (idx < 0) { frame[14] = unchecked((ulong)-4); return; }
                    if (!isRoot && Users.UidAt(idx) != Scheduler.CurrentUid()) { frame[14] = unchecked((ulong)-3); return; }
                    ulong plen2 = rcx;
                    char* cpass2 = stackalloc char[260];
                    if (plen2 > 0) { if (!UserPtr(rdx, (int)plen2)) { frame[14] = unchecked((ulong)-1); return; } for (int i = 0; i < (int)plen2; i++) cpass2[i] = (char)((byte*)rdx)[i]; }
                    if (!Users.ChangePassword(idx, cpass2, (int)plen2)) { frame[14] = unchecked((ulong)-5); return; }
                    frame[14] = 0;
                    return;
                case 3: // DEL (root only, not root user). RDX bit0 = /HOME
                    if (!isRoot) { frame[14] = unchecked((ulong)-3); return; }
                    if (idx < 0) { frame[14] = unchecked((ulong)-4); return; }
                    if (Users.UidAt(idx) == 0) { frame[14] = unchecked((ulong)-5); return; } // cannot delete root
                    bool delHome = (rdx & 1) != 0;
                    if (delHome)
                    {
                        char* hm = stackalloc char[64];
                        int hl = Users.HomeAt(idx, hm);
                        if (hl > 0 && !DeleteTree(hm, hl)) { frame[14] = unchecked((ulong)-5); return; }
                    }
                    if (!Users.Del(idx)) { frame[14] = unchecked((ulong)-5); return; }
                    frame[14] = 0;
                    return;
                case 4: // SUDO (root only)
                    if (!isRoot) { frame[14] = unchecked((ulong)-3); return; }
                    if (idx < 0) { frame[14] = unchecked((ulong)-4); return; }
                    Users.AddSudoer(idx);
                    frame[14] = 0;
                    return;
                case 5: // UNSUDO (root only)
                    if (!isRoot) { frame[14] = unchecked((ulong)-3); return; }
                    if (idx < 0) { frame[14] = unchecked((ulong)-4); return; }
                    Users.DelSudoer(idx);
                    frame[14] = 0;
                    return;
                default:
                    frame[14] = unchecked((ulong)-5); return;
            }
        }

        // 0x76 CREATEFILE(path RDI, len RSI) -> handle or error
        if (func == 0x76)
        {
            ulong len = rsi;
            if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
            if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }
            char* cpath = stackalloc char[260];
            for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];
            uint ino;
            if (!Ext4.Resolve(cpath, (int)len, out ino))
            {
                int slash = (int)len - 1;
                while (slash >= 0 && cpath[slash] != '\\' && cpath[slash] != '/') slash--;
                if (slash < 0) { frame[14] = unchecked((ulong)-4); return; }
                char* dirPath = stackalloc char[260];
                for (int i = 0; i < slash; i++) dirPath[i] = cpath[i];
                uint parent;
                if (!Ext4.Resolve(dirPath, slash, out parent)) { frame[14] = unchecked((ulong)-4); return; }
                int baseLen = (int)len - slash - 1;
                char* baseName = cpath + slash + 1;
                uint existing; byte etype;
                if (Ext4.DirEntryExists(parent, baseName, baseLen, out existing, out etype))
                    ino = existing;
                else
                {
                    if (!Ext4.AllocInode(out ino, 0x8180)) { frame[14] = unchecked((ulong)-6); return; }
                    if (!Ext4.DirAddEntry(parent, baseName, baseLen, ino, 1)) { Ext4.FreeInode(ino); frame[14] = unchecked((ulong)-5); return; }
                }
            }
            byte* inode = stackalloc byte[256];
            if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }
            uint sz = Ext4.InodeGetSize(inode);
            uint nb = (sz + (uint)Ext4.BlockSize - 1) / (uint)Ext4.BlockSize;
            if (nb == 0 && sz > 0) nb = 1;
            for (uint lb = 0; lb < nb; lb++)
            {
                uint pb = Ext4.InodeGetBlock(inode, lb);
                if (pb != 0) Ext4.FreeBlock(pb);
            }
            inode[Layout.InoSizeLo] = 0; inode[Layout.InoSizeLo + 1] = 0;
            inode[Layout.InoSizeLo + 2] = 0; inode[Layout.InoSizeLo + 3] = 0;
            for (int b = 0; b < 15; b++)
            {
                int boff = Layout.InoBlock + b * 4;
                inode[boff] = 0; inode[boff + 1] = 0; inode[boff + 2] = 0; inode[boff + 3] = 0;
            }
            Ext4.WriteInode(ino, inode);
            int slot = Scheduler.Current;
            int h = Scheduler.FileCreate(slot, ino);
            if (h < 0) { frame[14] = unchecked((ulong)-6); return; }
            frame[14] = (ulong)h;
            return;
        }

        // 0x77 WRITEFILE(handle RDI, data RSI, dataLen RDX) -> bytes written or error
        if (func == 0x77)
        {
            int handle = (int)rdi;
            byte* data = (byte*)rsi;
            int count = (int)rdx;
            if (count < 0 || count > 65536) { frame[14] = unchecked((ulong)-5); return; }
            if (count > 0 && !UserPtr((ulong)data, count)) { frame[14] = unchecked((ulong)-1); return; }
            int written = Scheduler.FileWrite(handle, data, count);
            if (written < 0) { frame[14] = unchecked((ulong)-5); return; }
            frame[14] = (ulong)written;
            return;
        }

        // 0x78 SETSTDOUT(handle RDI) -> sets current task's stdout handle (-1 = VGA)
        if (func == 0x78)
        {
            int handle = (int)rdi;
            if (handle < -1 || handle >= Scheduler.Count * 8) { frame[14] = unchecked((ulong)-5); return; }
            Scheduler.SetStdout(handle);
            frame[14] = 0;
            return;
        }

        // 0x79 SETSTDIN(handle RDI) -> sets current task's stdin handle (-1 = console)
        if (func == 0x79)
        {
            int handle = (int)rdi;
            if (handle < -1 || handle >= Scheduler.Count * 8) { frame[14] = unchecked((ulong)-5); return; }
            Scheduler.SetStdin(handle);
            frame[14] = 0;
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
        // Root (uid 0) passes if ANY execute bit is set (owner/group/other).
        if (uid == 0) return (perm & 0x49) != 0;
        int gid = Scheduler.CurrentGid();
        if ((uint)uid == fileUid) return (perm & 0x40) != 0;    // owner x (0100)
        if ((uint)gid == fileGid) return (perm & 0x08) != 0;    // group x (0010)
        return (perm & 0x01) != 0;               // other x (0001)
    }

    // Recursively delete a directory tree at `path` (len bytes).
    // Returns true on success, false on failure.
    private static bool DeleteTree(char* path, int len)
    {
        if (!Ext4.Mount()) return false;
        uint ino;
        if (!Ext4.Resolve(path, len, out ino)) return false;
        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, inode)) return false;
        ushort mode = Ext4.InodeGetMode(inode);

        if (Ext4.IsDir(mode))
        {
            uint size = Ext4.InodeGetSize(inode);
            ulong blocks = ((ulong)size + (ulong)Ext4.BlockSize - 1) / (ulong)Ext4.BlockSize;
            if (blocks == 0) blocks = 1;
            byte* blk = stackalloc byte[Ext4.BlockSize];
            for (ulong b = 0; b < blocks; b++)
            {
                uint bnum = Ext4.InodeGetBlock(inode, b);
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
                        bool isDot = (nlen == 1 && de[Layout.DirName] == (byte)'.');
                        bool isDotDot = (nlen == 2 && de[Layout.DirName] == (byte)'.' &&
                                                       de[Layout.DirName + 1] == (byte)'.');
                        if (!isDot && !isDotDot)
                        {
                            var cpath = stackalloc char[260];
                            int clen = 0;
                            for (int k = 0; k < len && clen < 259; k++) cpath[clen++] = path[k];
                            if (clen > 0 && cpath[clen - 1] != '\\' && cpath[clen - 1] != '/' && clen < 259)
                                cpath[clen++] = '\\';
                            for (int k = 0; k < nlen && clen < 259; k++) cpath[clen++] = (char)de[Layout.DirName + k];
                            if (!DeleteTree(cpath, clen)) return false;
                        }
                    }
                    off += rlen;
                    if (off > Ext4.BlockSize - 8) break;
                }
            }
        }

        char* baseName = stackalloc char[256];
        int baseLen = 0;
        uint parentIno;
        if (!Osb.Kernel.Shell.Commands.FileCommands.ResolveParent(path, len, out parentIno, baseName, ref baseLen))
            return false;
        if (baseLen <= 0) return false;

        byte* delInode = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, delInode)) return false;
        uint sz = Ext4.InodeGetSize(delInode);
        uint nb = (sz + (uint)Ext4.BlockSize - 1) / (uint)Ext4.BlockSize;
        if (nb == 0 && sz > 0) nb = 1;
        for (uint lb = 0; lb < nb; lb++)
        {
            uint pb = Ext4.InodeGetBlock(delInode, lb);
            if (pb != 0) Ext4.FreeBlock(pb);
        }
        Ext4.FreeInode(ino);
        Ext4.DirRemoveEntry(parentIno, baseName, baseLen);
        return true;
    }
}