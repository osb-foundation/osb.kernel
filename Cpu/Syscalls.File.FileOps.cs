using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 2Ah OPEN(path in RDI, len in RSI, mode in RDX) -> handle
    // mode bitmask: 1=read, 2=write, 4=append, 8=create
    private static void HandleOpen(ulong* frame)
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

        uint mode = (uint)rdx;
        bool wantRead = (mode & 1) != 0;
        bool wantWrite = (mode & 2) != 0;
        bool wantAppend = (mode & 4) != 0;
        bool wantCreate = (mode & 8) != 0;

        uint ino;
        bool exists = Ext4.Resolve(cpath, (int)len, out ino);

        if (!exists)
        {
            if (!wantCreate) { frame[14] = unchecked((ulong)-4); return; } // ENOENT

            // Create new file
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
                ino = existing;
            }
            else
            {
                if (!Ext4.AllocInode(out ino, 0x8184)) { frame[14] = unchecked((ulong)-6); return; }
                if (!Ext4.DirAddEntry(parent, baseName, baseLen, ino, 1))
                {
                    Ext4.FreeInode(ino);
                    frame[14] = unchecked((ulong)-5);
                    return;
                }
            }
        }

        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, inode)) { frame[14] = unchecked((ulong)-4); return; }

        if (!Ext4.IsReg(Ext4.InodeGetMode(inode))) { frame[14] = unchecked((ulong)-4); return; }

        // Check permissions
        uint fileOwner = Ext4.InodeGetUid(inode);
        ushort fileMode = Ext4.InodeGetMode(inode);

        if (wantRead && !Users.CanRead(fileOwner, fileMode)) { frame[14] = unchecked((ulong)-3); return; }
        if ((wantWrite || wantAppend) && !Users.CanWrite(fileOwner, fileMode)) { frame[14] = unchecked((ulong)-3); return; }

        int slot = Scheduler.Current;
        int h = Scheduler.FileOpen(slot, ino, (int)Ext4.InodeGetSize(inode));
        if (h < 0) { frame[14] = unchecked((ulong)-6); return; }

        // Handle append mode by seeking to end
        if (wantAppend)
        {
            Scheduler.FileSeek(h, (int)Ext4.InodeGetSize(inode), 0); // SEEK_SET
        }

        frame[14] = (ulong)h;
    }

    // 2Bh READ(handle in RDI, buf in RSI, count in RDX) -> bytes or negative
    private static void HandleRead(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];

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
    }

    // 2Ch CLOSE(handle in RDI)
    private static void HandleClose(ulong* frame)
    {
        ulong rdi = frame[9];
        Scheduler.FileClose((int)rdi);
        frame[14] = 0;
    }

    // 60h STAT(path in RDI, len in RSI, out in RDX) -> 1/0
    private static void HandleStat(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];

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
    }

    // 77h WRITEFILE(handle in RDI, data in RSI, dataLen in RDX) -> bytes written
    private static void HandleWriteFile(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];

        int handle = (int)rdi;
        byte* data = (byte*)rsi;
        int count = (int)rdx;
        if (count < 0 || count > 65536) { frame[14] = unchecked((ulong)-5); return; }
        if (count > 0 && !UserPtr((ulong)data, count)) { frame[14] = unchecked((ulong)-1); return; }
        int written = Scheduler.FileWrite(handle, data, count);
        if (written < 0) { frame[14] = unchecked((ulong)-5); return; }
        frame[14] = (ulong)written;
    }
}