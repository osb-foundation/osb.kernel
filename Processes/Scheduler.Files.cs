// File handle operations for the scheduler (per-task, 8 handles per slot).

using Osb.Kernel.Memory;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    // File handle helpers (Slice 4): per-task, 8 handles per slot.
    public static int FileOpen(int slot, uint ino, int size)
    {
        for (int h = 0; h < 8; h++)
        {
            int hi = slot * 8 + h;
            if (_tasks.fhUsed[hi] == 0)
            {
                _tasks.fhUsed[hi] = 1;
                _tasks.fhInode[hi] = (int)ino;
                _tasks.fhOff[hi] = 0;
                _tasks.fhSize[hi] = size;
                return hi;   // global handle id
            }
        }
        return -1;
    }

    public static int FileRead(int handle, byte* buf, int count)
    {
        if (handle < 0 || handle >= Count * 8) return -1;
        if (_tasks.fhUsed[handle] == 0) return -1;
        int ino = _tasks.fhInode[handle];
        int off = _tasks.fhOff[handle];
        int size = _tasks.fhSize[handle];
        if (off >= size) return 0;
        int want = count;
        if (want > size - off) want = size - off;
        if (want <= 0) return 0;
        // read via Ext4: need inode + block walk
        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode((uint)ino, inode)) return -1;
        int got = 0;
        while (got < want)
        {
            ulong blockIndex = (ulong)(off + got) / (ulong)Ext4.BlockSize;
            int blockOff = (off + got) % Ext4.BlockSize;
            uint bnum = Ext4.InodeGetBlock(inode, blockIndex);
            if (bnum == 0) break;
            byte* blk = stackalloc byte[Ext4.BlockSize];
            Ext4.ReadBlock(bnum, blk);
            int avail = Ext4.BlockSize - blockOff;
            if (avail > want - got) avail = want - got;
            for (int k = 0; k < avail; k++) buf[got + k] = blk[blockOff + k];
            got += avail;
        }
        _tasks.fhOff[handle] = off + got;
        return got;
    }

    public static void FileClose(int handle)
    {
        if (handle < 0 || handle >= Count * 8) return;
        _tasks.fhUsed[handle] = 0;
    }

    // Seek file handle to offset from whence (0=set, 1=cur, 2=end). Returns new offset or -1.
    public static int FileSeek(int handle, int offset, int whence)
    {
        if (handle < 0 || handle >= Count * 8) return -1;
        if (_tasks.fhUsed[handle] == 0) return -1;
        int off = _tasks.fhOff[handle];
        int size = _tasks.fhSize[handle];
        if (whence == 0) off = offset;                    // SEEK_SET
        else if (whence == 1) off += offset;              // SEEK_CUR
        else if (whence == 2) off = size + offset;        // SEEK_END
        else return -1;
        if (off < 0) off = 0;
        if (off > size) off = size;
        _tasks.fhOff[handle] = off;
        return off;
    }

    // Create or truncate a file for writing, return a write handle.
    public static int FileCreate(int slot, uint ino)
    {
        for (int h = 0; h < 8; h++)
        {
            int hi = slot * 8 + h;
            if (_tasks.fhUsed[hi] == 0)
            {
                _tasks.fhUsed[hi] = 1;
                _tasks.fhInode[hi] = (int)ino;
                _tasks.fhOff[hi] = 0;
                _tasks.fhSize[hi] = 0;
                _tasks.fhWrite[hi] = 1;   // write mode
                return hi;
            }
        }
        return -1;
    }

    // Write data to a write-mode file handle, extending the file as needed.
    public static int FileWrite(int handle, byte* data, int count)
    {
        if (handle < 0 || handle >= Count * 8) return -1;
        if (_tasks.fhUsed[handle] == 0 || _tasks.fhWrite[handle] == 0) return -1;
        int ino = _tasks.fhInode[handle];
        int off = _tasks.fhOff[handle];

        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode((uint)ino, inode)) return -1;

        int written = 0;
        while (written < count)
        {
            ulong blockIndex = (ulong)(off + written) / (ulong)Ext4.BlockSize;
            int blockOff = (off + written) % (int)Ext4.BlockSize;
            uint bnum = Ext4.InodeGetBlock(inode, blockIndex);
            if (bnum == 0)
            {
                uint pb;
                if (!Ext4.AllocBlock(out pb)) break;
                // set block pointer in inode: InoBlock + index*4
                int boff = Layout.InoBlock + (int)blockIndex * 4;
                inode[boff] = (byte)(pb & 0xFF);
                inode[boff + 1] = (byte)((pb >> 8) & 0xFF);
                inode[boff + 2] = (byte)((pb >> 16) & 0xFF);
                inode[boff + 3] = (byte)((pb >> 24) & 0xFF);
                bnum = pb;
            }
            byte* blk = stackalloc byte[Ext4.BlockSize];
            Ext4.ReadBlock(bnum, blk);
            int avail = (int)Ext4.BlockSize - blockOff;
            if (avail > count - written) avail = count - written;
            for (int k = 0; k < avail; k++) blk[blockOff + k] = data[written + k];
            Ext4.WriteBlock(bnum, blk);
            written += avail;
        }

        _tasks.fhOff[handle] = off + written;
        int newSize = off + written;
        if (newSize > (int)Ext4.InodeGetSize(inode))
        {
            inode[Layout.InoSizeLo] = (byte)(newSize & 0xFF);
            inode[Layout.InoSizeLo + 1] = (byte)((newSize >> 8) & 0xFF);
            inode[Layout.InoSizeLo + 2] = (byte)((newSize >> 16) & 0xFF);
            inode[Layout.InoSizeLo + 3] = (byte)((newSize >> 24) & 0xFF);
        }
        Ext4.WriteInode((uint)ino, inode);
        return written;
    }
}