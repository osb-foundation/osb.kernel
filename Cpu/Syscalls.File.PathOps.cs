using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 20h RESOLVE(path in RDI, len in RSI, out in RDX)
    // out: [u32 ino, u16 mode, u16 uid, u64 size]
    private static void HandleResolve(ulong* frame)
    {
        ulong rsi = frame[10];
        ulong rdi = frame[9];
        ulong rdx = frame[11];

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
    }

    // 21h DIRNEXT(dir ino in RDI, index in RSI, out in RDX, cap in RCX)
    // out: [u32 childIno, u8 type, u8 nameLen, name bytes...]
    private static void HandleDirNext(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

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
                            ulong cap = rcx > 240 ? 240 : rcx;
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
    }

    // 22h GETCWD(buf in RDI, cap in RSI) -> length
    private static void HandleGetCwd(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];

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
    }

    // 23h GETARGS(buf in RDI, cap in RSI): the argument string
    private static void HandleGetArgs(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];

        ulong cap = rsi > 129 ? 129 : rsi;
        if (!UserPtr(rdi, (int)cap))
        {
            frame[14] = unchecked((ulong)-1);
            return;
        }
        int n = Scheduler.GetArgs(Scheduler.Current, (byte*)rdi, (int)cap);
        frame[14] = (ulong)n;
    }

    // 24h CHDIR(path in RDI, len in RSI)
    private static void HandleChDir(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];

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
        if (!Ext4.IsDir(Ext4.InodeGetMode(inode))) { frame[14] = unchecked((ulong)-5); return; }

        // Verify read permission on the target directory
        uint targetOwner = Ext4.InodeGetUid(inode);
        ushort targetMode = Ext4.InodeGetMode(inode);
        if (!Users.CanRead(targetOwner, targetMode))
        {
            frame[14] = unchecked((ulong)-3);
            return;
        }

        // Update global CWD with canonical path
        byte* canon = stackalloc byte[160];
        int canonLen = 160;
        if (Ext4.ResolveCanonical(cpath, (int)len, out _, canon, ref canonLen))
        {
            Cwd.Set(canon, canonLen);
        }
        frame[14] = 0;
    }
}