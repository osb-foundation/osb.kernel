using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 25h MKDIR(path in RDI, len in RSI, mode in RDX)
    private static void HandleMkdir(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];

        ulong len = rsi;
        if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
        if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
        if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }

        char* cpath = stackalloc char[260];
        for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];

        int slash = (int)len - 1;
        while (slash >= 0 && cpath[slash] != '\\' && cpath[slash] != '/') slash--;
        if (slash < 0) { frame[14] = unchecked((ulong)-4); return; }

        char* dirPath = stackalloc char[260];
        for (int i = 0; i < slash; i++) dirPath[i] = cpath[i];
        uint parent;
        if (!Ext4.Resolve(dirPath, slash, out parent)) { frame[14] = unchecked((ulong)-4); return; }

        byte* pinode = stackalloc byte[256];
        if (!Ext4.ReadInode(parent, pinode)) { frame[14] = unchecked((ulong)-4); return; }
        if (!Ext4.IsDir(Ext4.InodeGetMode(pinode))) { frame[14] = unchecked((ulong)-4); return; }

        uint owner = Ext4.InodeGetUid(pinode);
        ushort pmode = Ext4.InodeGetMode(pinode);
        if (!Users.CanWrite(owner, pmode)) { frame[14] = unchecked((ulong)-3); return; }

        int baseLen = (int)len - slash - 1;
        char* baseName = cpath + slash + 1;

        uint existing; byte etype;
        if (Ext4.DirEntryExists(parent, baseName, baseLen, out existing, out etype))
        {
            frame[14] = unchecked((ulong)-7); // EEXIST
            return;
        }

        ushort mode = (ushort)rdx;
        if (mode == 0) mode = 0x4180; // default dir mode
        uint newIno;
        if (!Ext4.AllocInode(out newIno, mode)) { frame[14] = unchecked((ulong)-6); return; }

        if (!Ext4.DirAddEntry(parent, baseName, baseLen, newIno, 2)) // 2 = directory
        {
            Ext4.FreeInode(newIno);
            frame[14] = unchecked((ulong)-5);
            return;
        }

        // Initialize directory with . and .. entries
        byte* newInode = stackalloc byte[256];
        if (Ext4.ReadInode(newIno, newInode))
        {
            uint dotBlock;
            if (Ext4.AllocBlock(out dotBlock))
            {
                byte* dotBlk = stackalloc byte[Ext4.BlockSize];
                // Clear block
                for (int k = 0; k < Ext4.BlockSize; k++) dotBlk[k] = 0;
                // Add . entry (offset 0)
                int recLen = 12; // 8 + 1 + 3 padding = 12 (aligned to 4)
                Ext4.Wr32(dotBlk, Layout.DirIno, newIno);
                Ext4.Wr16(dotBlk, Layout.DirRecLen, (ushort)recLen);
                dotBlk[Layout.DirNameLen] = 1;
                dotBlk[Layout.DirType] = 2; // directory
                dotBlk[Layout.DirName] = (byte)'.';
                // Add .. entry (offset 12)
                int off2 = 12;
                recLen = Ext4.BlockSize - 12;
                Ext4.Wr32(dotBlk, off2 + Layout.DirIno, parent);
                Ext4.Wr16(dotBlk, off2 + Layout.DirRecLen, (ushort)recLen);
                dotBlk[off2 + Layout.DirNameLen] = 2;
                dotBlk[off2 + Layout.DirType] = 2; // directory
                dotBlk[off2 + Layout.DirName] = (byte)'.';
                dotBlk[off2 + Layout.DirName + 1] = (byte)'.';
                Ext4.WriteBlock(dotBlock, dotBlk);
                Ext4.InodeSetBlock(newInode, 0, dotBlock);
                newInode[Layout.InoSizeLo] = (byte)(Ext4.BlockSize & 0xFF);
                newInode[Layout.InoSizeLo + 1] = (byte)((Ext4.BlockSize >> 8) & 0xFF);
                newInode[Layout.InoSizeLo + 2] = (byte)((Ext4.BlockSize >> 16) & 0xFF);
                newInode[Layout.InoSizeLo + 3] = (byte)((Ext4.BlockSize >> 24) & 0xFF);
                Ext4.WriteInode(newIno, newInode);
            }
        }

        frame[14] = 0;
    }

    // 26h RMDIR(path in RDI, len in RSI)
    private static void HandleRmDir(ulong* frame)
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

        // Check if directory is empty (only . and ..)
        uint size = Ext4.InodeGetSize(inode);
        ulong blocks = ((ulong)size + (ulong)Ext4.BlockSize - 1) / (ulong)Ext4.BlockSize;
        if (blocks == 0) blocks = 1;
        byte* blk = stackalloc byte[Ext4.BlockSize];
        bool hasEntries = false;
        for (ulong b = 0; b < blocks && !hasEntries; b++)
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
                    bool dotty = (nlen == 1 && de[Layout.DirName] == (byte)'.') ||
                                 (nlen == 2 && de[Layout.DirName] == (byte)'.' &&
                                                  de[Layout.DirName + 1] == (byte)'.');
                    if (!dotty) { hasEntries = true; break; }
                }
                off += rlen;
                if (off > Ext4.BlockSize - 8) break;
            }
        }
        if (hasEntries) { frame[14] = unchecked((ulong)-5); return; } // ENOTEMPTY

        // Check write permission on parent
        int slash = (int)len - 1;
        while (slash >= 0 && cpath[slash] != '\\' && cpath[slash] != '/') slash--;
        if (slash < 0) { frame[14] = unchecked((ulong)-4); return; }

        char* dirPath = stackalloc char[260];
        for (int i = 0; i < slash; i++) dirPath[i] = cpath[i];
        uint parent;
        if (!Ext4.Resolve(dirPath, slash, out parent)) { frame[14] = unchecked((ulong)-4); return; }

        byte* pinode = stackalloc byte[256];
        if (!Ext4.ReadInode(parent, pinode)) { frame[14] = unchecked((ulong)-4); return; }
        uint owner = Ext4.InodeGetUid(pinode);
        ushort pmode = Ext4.InodeGetMode(pinode);
        if (!Users.CanWrite(owner, pmode)) { frame[14] = unchecked((ulong)-3); return; }

        int baseLen = (int)len - slash - 1;
        char* baseName = cpath + slash + 1;

        if (!Ext4.DirRemoveEntry(parent, baseName, baseLen))
        {
            frame[14] = unchecked((ulong)-4);
            return;
        }

        // Free block and inode
        for (ulong b = 0; b < blocks; b++)
        {
            uint bnum = Ext4.InodeGetBlock(inode, b);
            if (bnum != 0) Ext4.FreeBlock(bnum);
        }
        Ext4.FreeInode(ino);

        frame[14] = 0;
    }

    // 27h READDIR(path in RDI, len in RSI, index in RDX, out in RCX, cap in R8)
    private static void HandleReadDir(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];
        ulong r8 = frame[13];

        ulong len = rsi;
        if (len == 0 || len > 260) { frame[14] = 0; return; }
        if (!UserPtr(rdi, (int)len) || !UserPtr(rcx, (int)(r8 > 240 ? 240 : r8))) { frame[14] = 0; return; }
        if (!Ext4.Mount()) { frame[14] = 0; return; }

        char* cpath = stackalloc char[260];
        for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];

        uint ino;
        if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = 0; return; }

        byte* din = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, din)) { frame[14] = 0; return; }
        if (!Ext4.IsDir(Ext4.InodeGetMode(din))) { frame[14] = 0; return; }

        uint size = Ext4.InodeGetSize(din);
        ulong blocks = ((ulong)size + (ulong)Ext4.BlockSize - 1) / (ulong)Ext4.BlockSize;
        if (blocks == 0) blocks = 1;
        byte* blk = stackalloc byte[Ext4.BlockSize];
        ulong seen = 0;
        ulong idx = rdx;
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
                    if (seen == idx)
                    {
                        byte* o = (byte*)rcx;
                        Wr32(o, 0, iino);
                        o[4] = Ext4.DirEntryType(de);
                        o[5] = nlen;
                        ulong cap = r8 > 240 ? 240 : r8;
                        for (ulong k = 0; k < nlen && k + 6 < cap; k++)
                            o[6 + k] = de[Layout.DirName + k];
                        frame[14] = 1;
                        return;
                    }
                    seen++;
                }
                off += rlen;
                if (off > Ext4.BlockSize - 8) break;
            }
        }
        frame[14] = 0;
    }
}