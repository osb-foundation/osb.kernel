using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 27h UNLINK(path in RDI, len in RSI)
    private static void HandleUnlink(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];

        ulong len = rsi;
        if (len == 0 || len > 260) { frame[14] = unchecked((ulong)-5); return; }
        if (!UserPtr(rdi, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
        if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }

        char* cpath = stackalloc char[260];
        for (int i = 0; i < (int)len; i++) cpath[i] = (char)((byte*)rdi)[i];

        // Resolve path
        uint ino;
        if (!Ext4.Resolve(cpath, (int)len, out ino)) { frame[14] = unchecked((ulong)-4); return; }

        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }

        // Must not be a directory
        if (Ext4.IsDir(Ext4.InodeGetMode(inode))) { frame[14] = unchecked((ulong)-5); return; } // EINVAL (is dir)

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

        // Remove directory entry
        int baseLen = (int)len - slash - 1;
        char* baseName = cpath + slash + 1;
        if (!Ext4.DirRemoveEntry(parent, baseName, baseLen))
        {
            frame[14] = unchecked((ulong)-4);
            return;
        }

        // Free blocks and inode
        uint size = Ext4.InodeGetSize(inode);
        ulong blocks = ((ulong)size + (ulong)Ext4.BlockSize - 1) / (ulong)Ext4.BlockSize;
        if (blocks == 0 && size > 0) blocks = 1;
        for (ulong b = 0; b < blocks; b++)
        {
            uint bnum = Ext4.InodeGetBlock(inode, b);
            if (bnum != 0) Ext4.FreeBlock(bnum);
        }
        Ext4.FreeInode(ino);

        frame[14] = 0;
    }

    // 28h RENAME(oldPath in RDI, oldLen in RSI, newPath in RDX, newLen in RCX)
    private static void HandleRename(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

        ulong olen = rsi;
        ulong nlen = rcx;
        if (olen == 0 || olen > 260 || nlen == 0 || nlen > 260) { frame[14] = unchecked((ulong)-5); return; }
        if (!UserPtr(rdi, (int)olen) || !UserPtr(rdx, (int)nlen)) { frame[14] = unchecked((ulong)-1); return; }
        if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }

        char* op = stackalloc char[260];
        char* np = stackalloc char[260];
        for (int i = 0; i < (int)olen; i++) op[i] = (char)((byte*)rdi)[i];
        for (int i = 0; i < (int)nlen; i++) np[i] = (char)((byte*)rdx)[i];

        // Resolve source
        uint srcIno;
        if (!Ext4.Resolve(op, (int)olen, out srcIno)) { frame[14] = unchecked((ulong)-4); return; }

        byte* srcInode = stackalloc byte[256];
        if (!Ext4.ReadInode(srcIno, srcInode)) { frame[14] = unchecked((ulong)-4); return; }

        // Check write permission on source parent
        int slash = (int)olen - 1;
        while (slash >= 0 && op[slash] != '\\' && op[slash] != '/') slash--;
        if (slash < 0) { frame[14] = unchecked((ulong)-4); return; }

        char* srcDirPath = stackalloc char[260];
        for (int i = 0; i < slash; i++) srcDirPath[i] = op[i];
        uint srcParent;
        if (!Ext4.Resolve(srcDirPath, slash, out srcParent)) { frame[14] = unchecked((ulong)-4); return; }

        byte* srcPInode = stackalloc byte[256];
        if (!Ext4.ReadInode(srcParent, srcPInode)) { frame[14] = unchecked((ulong)-4); return; }
        uint owner = Ext4.InodeGetUid(srcPInode);
        ushort mode = Ext4.InodeGetMode(srcPInode);
        if (!Users.CanWrite(owner, mode)) { frame[14] = unchecked((ulong)-3); return; }

        // Resolve destination parent
        int slash2 = (int)nlen - 1;
        while (slash2 >= 0 && np[slash2] != '\\' && np[slash2] != '/') slash2--;
        if (slash2 < 0) { frame[14] = unchecked((ulong)-4); return; }

        char* dstDirPath = stackalloc char[260];
        for (int i = 0; i < slash2; i++) dstDirPath[i] = np[i];
        uint dstParent;
        if (!Ext4.Resolve(dstDirPath, slash2, out dstParent)) { frame[14] = unchecked((ulong)-4); return; }

        byte* dstPInode = stackalloc byte[256];
        if (!Ext4.ReadInode(dstParent, dstPInode)) { frame[14] = unchecked((ulong)-4); return; }
        uint owner2 = Ext4.InodeGetUid(dstPInode);
        ushort mode2 = Ext4.InodeGetMode(dstPInode);
        if (!Users.CanWrite(owner2, mode2)) { frame[14] = unchecked((ulong)-3); return; }

        // Check if destination exists
        int baseLen2 = (int)nlen - slash2 - 1;
        char* dstBaseName = np + slash2 + 1;
        uint existing; byte etype;
        if (Ext4.DirEntryExists(dstParent, dstBaseName, baseLen2, out existing, out etype))
        {
            frame[14] = unchecked((ulong)-7); // EEXIST
            return;
        }

        // Get source base name
        int baseLen1 = (int)olen - slash - 1;
        char* srcBaseName = op + slash + 1;

        // Remove source entry and add destination entry
        if (!Ext4.DirRemoveEntry(srcParent, srcBaseName, baseLen1))
        {
            frame[14] = unchecked((ulong)-4);
            return;
        }

        byte dtype = Ext4.IsDir(Ext4.InodeGetMode(srcInode)) ? (byte)2 : (byte)1;
        if (!Ext4.DirAddEntry(dstParent, dstBaseName, baseLen2, srcIno, dtype))
        {
            // Rollback
            Ext4.DirAddEntry(srcParent, srcBaseName, baseLen1, srcIno, dtype);
            frame[14] = unchecked((ulong)-5);
            return;
        }

        frame[14] = 0;
    }

    // 29h COPY(srcPath in RDI, srcLen in RSI, dstPath in RDX, dstLen in RCX)
    private static void HandleCopy(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

        ulong slen = rsi;
        ulong dlen = rcx;
        if (slen == 0 || slen > 260 || dlen == 0 || dlen > 260) { frame[14] = unchecked((ulong)-5); return; }
        if (!UserPtr(rdi, (int)slen) || !UserPtr(rdx, (int)dlen)) { frame[14] = unchecked((ulong)-1); return; }
        if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }

        char* sp = stackalloc char[260];
        char* dp = stackalloc char[260];
        for (int i = 0; i < (int)slen; i++) sp[i] = (char)((byte*)rdi)[i];
        for (int i = 0; i < (int)dlen; i++) dp[i] = (char)((byte*)rdx)[i];

        // Resolve source
        uint srcIno;
        if (!Ext4.Resolve(sp, (int)slen, out srcIno)) { frame[14] = unchecked((ulong)-4); return; }

        byte* srcInode = stackalloc byte[256];
        if (!Ext4.ReadInode(srcIno, srcInode)) { frame[14] = unchecked((ulong)-4); return; }

        // Must be a regular file
        if (!Ext4.IsReg(Ext4.InodeGetMode(srcInode))) { frame[14] = unchecked((ulong)-4); return; }

        // Check read permission on source
        uint srcOwner = Ext4.InodeGetUid(srcInode);
        ushort srcMode = Ext4.InodeGetMode(srcInode);
        if (!Users.CanRead(srcOwner, srcMode)) { frame[14] = unchecked((ulong)-3); return; }

        // Resolve destination parent
        int slash = (int)dlen - 1;
        while (slash >= 0 && dp[slash] != '\\' && dp[slash] != '/') slash--;
        if (slash < 0) { frame[14] = unchecked((ulong)-4); return; }

        char* dstDirPath = stackalloc char[260];
        for (int i = 0; i < slash; i++) dstDirPath[i] = dp[i];
        uint dstParent;
        if (!Ext4.Resolve(dstDirPath, slash, out dstParent)) { frame[14] = unchecked((ulong)-4); return; }

        byte* dstPInode = stackalloc byte[256];
        if (!Ext4.ReadInode(dstParent, dstPInode)) { frame[14] = unchecked((ulong)-4); return; }
        if (!Ext4.IsDir(Ext4.InodeGetMode(dstPInode))) { frame[14] = unchecked((ulong)-4); return; }

        uint dstOwner = Ext4.InodeGetUid(dstPInode);
        ushort dstMode = Ext4.InodeGetMode(dstPInode);
        if (!Users.CanWrite(dstOwner, dstMode)) { frame[14] = unchecked((ulong)-3); return; }

        // Check if destination exists
        int baseLen = (int)dlen - slash - 1;
        char* dstBaseName = dp + slash + 1;
        uint existing; byte etype;
        if (Ext4.DirEntryExists(dstParent, dstBaseName, baseLen, out existing, out etype))
        {
            frame[14] = unchecked((ulong)-7); // EEXIST
            return;
        }

        // Create new file
        uint newIno;
        if (!Ext4.AllocInode(out newIno, 0x8184)) { frame[14] = unchecked((ulong)-6); return; } // regular file

        if (!Ext4.DirAddEntry(dstParent, dstBaseName, baseLen, newIno, 1)) // 1 = file
        {
            Ext4.FreeInode(newIno);
            frame[14] = unchecked((ulong)-5);
            return;
        }

        // Copy data
        uint srcSize = Ext4.InodeGetSize(srcInode);
        uint nb = (srcSize + (uint)Ext4.BlockSize - 1) / (uint)Ext4.BlockSize;
        if (nb == 0 && srcSize > 0) nb = 1;

        for (uint lb = 0; lb < nb; lb++)
        {
            uint srcBlock = Ext4.InodeGetBlock(srcInode, lb);
            if (srcBlock == 0) continue;

            uint dstBlock;
            if (!Ext4.AllocBlock(out dstBlock))
            {
                // Cleanup: free any allocated blocks and inode
                byte* cleanupInode = stackalloc byte[256];
                if (Ext4.ReadInode(newIno, cleanupInode))
                {
                    for (uint k = 0; k < lb; k++)
                    {
                        uint b = Ext4.InodeGetBlock(cleanupInode, k);
                        if (b != 0) Ext4.FreeBlock(b);
                    }
                }
                Ext4.DirRemoveEntry(dstParent, dstBaseName, baseLen);
                Ext4.FreeInode(newIno);
                frame[14] = unchecked((ulong)-6);
                return;
            }

            byte* buf = stackalloc byte[Ext4.BlockSize];
            Ext4.ReadBlock(srcBlock, buf);
            Ext4.WriteBlock(dstBlock, buf);
            
            // Update the new inode's block pointer
            byte* writeInode = stackalloc byte[256];
            if (Ext4.ReadInode(newIno, writeInode))
            {
                Ext4.InodeSetBlock(writeInode, (int)lb, dstBlock);
                Ext4.WriteInode(newIno, writeInode);
            }
        }

        // Update size
        byte* newInode = stackalloc byte[256];
        if (!Ext4.ReadInode(newIno, newInode)) { frame[14] = unchecked((ulong)-4); return; }
        newInode[Layout.InoSizeLo] = (byte)(srcSize & 0xFF);
        newInode[Layout.InoSizeLo + 1] = (byte)((srcSize >> 8) & 0xFF);
        newInode[Layout.InoSizeLo + 2] = (byte)((srcSize >> 16) & 0xFF);
        newInode[Layout.InoSizeLo + 3] = (byte)((srcSize >> 24) & 0xFF);
        Ext4.WriteInode(newIno, newInode);

        frame[14] = 0;
    }

    // 76h CREATEFILE(path in RDI, len in RSI) -> handle
    private static void HandleCreateFile(ulong* frame)
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
                if (!Ext4.AllocInode(out ino, 0x8184)) { frame[14] = unchecked((ulong)-6); return; }
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
    }
}