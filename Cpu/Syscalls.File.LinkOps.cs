using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 61h LINK(targetPath in RDI, targetLen in RSI, linkPath in RDX, linkLen in RCX)
    private static void HandleLink(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

        ulong tlen = rsi;
        ulong llen = rcx;
        if (tlen == 0 || tlen > 260 || llen == 0 || llen > 260) { frame[14] = unchecked((ulong)-5); return; }
        if (!UserPtr(rdi, (int)tlen) || !UserPtr(rdx, (int)llen)) { frame[14] = unchecked((ulong)-1); return; }
        if (!Ext4.Mount()) { frame[14] = unchecked((ulong)-4); return; }

        char* tp = stackalloc char[260];
        char* lp = stackalloc char[260];
        for (int i = 0; i < (int)tlen; i++) tp[i] = (char)((byte*)rdi)[i];
        for (int i = 0; i < (int)llen; i++) lp[i] = (char)((byte*)rdx)[i];

        // Resolve target
        uint targetIno;
        if (!Ext4.Resolve(tp, (int)tlen, out targetIno)) { frame[14] = unchecked((ulong)-4); return; }

        byte* targetInode = stackalloc byte[256];
        if (!Ext4.ReadInode(targetIno, targetInode)) { frame[14] = unchecked((ulong)-4); return; }

        // Must be a regular file (no linking directories)
        if (!Ext4.IsReg(Ext4.InodeGetMode(targetInode))) { frame[14] = unchecked((ulong)-5); return; }

        // Check read permission on target
        uint tOwner = Ext4.InodeGetUid(targetInode);
        ushort tMode = Ext4.InodeGetMode(targetInode);
        if (!Users.CanRead(tOwner, tMode)) { frame[14] = unchecked((ulong)-3); return; }

        // Resolve link parent
        int slash = (int)llen - 1;
        while (slash >= 0 && lp[slash] != '\\' && lp[slash] != '/') slash--;
        if (slash < 0) { frame[14] = unchecked((ulong)-4); return; }

        char* linkDirPath = stackalloc char[260];
        for (int i = 0; i < slash; i++) linkDirPath[i] = lp[i];
        uint linkParent;
        if (!Ext4.Resolve(linkDirPath, slash, out linkParent)) { frame[14] = unchecked((ulong)-4); return; }

        byte* linkPInode = stackalloc byte[256];
        if (!Ext4.ReadInode(linkParent, linkPInode)) { frame[14] = unchecked((ulong)-4); return; }
        if (!Ext4.IsDir(Ext4.InodeGetMode(linkPInode))) { frame[14] = unchecked((ulong)-4); return; }

        uint lOwner = Ext4.InodeGetUid(linkPInode);
        ushort lMode = Ext4.InodeGetMode(linkPInode);
        if (!Users.CanWrite(lOwner, lMode)) { frame[14] = unchecked((ulong)-3); return; }

        int baseLen = (int)llen - slash - 1;
        char* linkBaseName = lp + slash + 1;

        uint existing; byte etype;
        if (Ext4.DirEntryExists(linkParent, linkBaseName, baseLen, out existing, out etype))
        {
            frame[14] = unchecked((ulong)-7); // EEXIST
            return;
        }

        // Add hard link (increment link count)
        if (!Ext4.DirAddEntry(linkParent, linkBaseName, baseLen, targetIno, 1))
        {
            frame[14] = unchecked((ulong)-5);
            return;
        }

        // Increment link count on target inode
        if (Ext4.ReadInode(targetIno, targetInode))
        {
            Ext4.InodeIncLinks(targetInode);
            Ext4.WriteInode(targetIno, targetInode);
        }

        frame[14] = 0;
    }

    // 78h SETSTDOUT(handle in RDI)
    private static void HandleSetStdout(ulong* frame)
    {
        ulong rdi = frame[9];
        int handle = (int)rdi;
        if (handle < -1 || handle >= Scheduler.Count * 8) { frame[14] = unchecked((ulong)-5); return; }
        Scheduler.SetStdout(handle);
        frame[14] = 0;
    }

    // 79h SETSTDIN(handle in RDI)
    private static void HandleSetStdin(ulong* frame)
    {
        ulong rdi = frame[9];
        int handle = (int)rdi;
        if (handle < -1 || handle >= Scheduler.Count * 8) { frame[14] = unchecked((ulong)-5); return; }
        Scheduler.SetStdin(handle);
        frame[14] = 0;
    }
}