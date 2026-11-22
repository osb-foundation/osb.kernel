// Scheduler spawn paths: built-in ring-3 programs (SpawnUser) and external
// A:\OSB\BIN\*.EXE files (SpawnFile/SpawnFileInode). Split from Scheduler.cs
// into a partial to keep each file under the 400-line limit.

using System.Runtime;
using Osb.Kernel.Cpu;
using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Processes;

internal static unsafe partial class Scheduler
{
    // Create a process running a built-in ring-3 program, in its own address
    // space. Returns Ok and the new pid; does not wait for the child.
    public static SpawnResult SpawnUser(int prog, int parentSlot, out int pid)
    {
        pid = -1;
        int slot = AllocSlot();
        if (slot < 0) return SpawnResult.NoSlot;
        // Copy the parent's environment while the slot is still FREE, so the
        // child can never be scheduled with an empty store (C1: copy-at-spawn).
        Environment.CopyFrom(slot, _current);
        ulong src, end;
        if (prog == Layout.ProgBad) { src = NativeMethods.osb_userbad_begin(); end = NativeMethods.osb_userbad_end(); }
        else if (prog == Layout.ProgSpawner) { src = NativeMethods.osb_spawner_begin(); end = NativeMethods.osb_spawner_end(); }
        else { src = NativeMethods.osb_userstub_begin(); end = NativeMethods.osb_userstub_end(); }

        if (!AddressSpace.Create(src, end)) return SpawnResult.OutOfMemory;

        SeedSlot(slot, NativeMethods.osb_sched_get_user_entry(), ProgramName(prog), Layout.KindUser,
            _tasks.sessionId[_current], _tasks.realUid[_current],
            _tasks.uid[_current], _tasks.gid[_current]);
        InitTaskTls(slot, AddressSpace.Block);              // M3.4.2
        _tasks.arg[slot] = Layout.UserBase;                 // entry RIP in the private block
        _tasks.cr3[slot] = AddressSpace.Cr3;
        _tasks.ublock[slot] = AddressSpace.Block;
        _tasks.ptphys[slot] = AddressSpace.PageTables;
        _tasks.ppid[slot] = _tasks.pid[parentSlot];
        pid = _tasks.pid[slot];
        ConsoleInput.Flush();   // S2: no stale cooked input / Ctrl+C for the child
        return SpawnResult.Ok;
    }

    public static string ProgramName(int prog)
    {
        if (prog == Layout.ProgBad) return "BAD";
        if (prog == Layout.ProgSpawner) return "SPAWNER";
        return "HELLO";
    }

    // ---- external commands (A:\OSB\BIN\*.EXE) ----

    // The OSBEXE image is staged here before AddressSpace copies it into the
    // private block. Only the console spawns, so one buffer is enough. The cap
    // leaves the upper half of the 2 MiB block free for the user stack (top at
    // Layout.UserStackTop, 512 KiB above the image base).
    private const int ImgCap = 256 * 1024;
    private unsafe struct ImgBuf { public fixed byte data[ImgCap]; }
    private static ImgBuf _img;

    // Load and start an executable file from the ext4 image. Resolves the path,
    // checks the inode is a regular file, then loads and spawns it. The file
    // starts with the 16-byte OSBEXE header ("OSBX", version, entry offset,
    // image size); the code lands at UserBase and the process enters ring 3 at
    // UserBase + entry. Returns why it refused, for the shell to explain.
    public static SpawnResult SpawnFile(char* path, int pathLen, int parentSlot, out int pid, bool boot = false)
    {
        pid = -1;
        if (!Ext4.Mount()) return SpawnResult.NotFound;
        uint ino;
        if (!Ext4.Resolve(path, pathLen, out ino)) return SpawnResult.NotFound;
        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, inode)) return SpawnResult.NotFound;
        if (!Ext4.IsReg(Ext4.InodeGetMode(inode))) return SpawnResult.NotFound;
        return SpawnFileInode(ino, path, pathLen, parentSlot, out pid, boot);
    }

    // Load and start an executable file whose inode is already resolved (the
    // 6Ah path resolves + checks type/exec first, then calls this so a refusal
    // leaves no task behind). `path`/`pathLen` are used only to derive the task
    // name. Same OSBX header contract as SpawnFile.
    public static SpawnResult SpawnFileInode(uint ino, char* path, int pathLen, int parentSlot, out int pid, bool boot = false)
    {
        pid = -1;
        byte* buf = ImgData();
        int got = 0;
        if (!Ext4.ReadFileAll(ino, buf, ImgCap, out got) || got < 16) return SpawnResult.BadImage;

        // header: magic "OSBX" | u16 version | u16 header size | u32 entry | u32 size
        if (buf[0] != 'O' || buf[1] != 'S' || buf[2] != 'B' || buf[3] != 'X') return SpawnResult.BadImage;
        uint version = (uint)(buf[4] | (buf[5] << 8));
        if (version != 1) return SpawnResult.BadImage;
        uint entryOff = (uint)(buf[8] | (buf[9] << 8) | (buf[10] << 16) | ((uint)buf[11] << 24));
        uint imgSize = (uint)(buf[12] | (buf[13] << 8) | (buf[14] << 16) | ((uint)buf[15] << 24));
        if (imgSize == 0 || 16 + imgSize > (uint)got) return SpawnResult.BadImage;
        if (entryOff >= imgSize) return SpawnResult.BadImage;

        // AllocSlot BEFORE CreateBuf (P0.5): if there is no free slot we return
        // NoSlot without having allocated an address space, so its frames are
        // not leaked. (AllocSlot only reads state; it does not reserve, so a
        // later CreateBuf failure leaves the slot free for reuse.)
        int slot = AllocSlot();
        if (slot < 0) return SpawnResult.NoSlot;
        // Copy the parent's environment while the slot is still FREE (C1).
        Environment.CopyFrom(slot, _current);

        if (!AddressSpace.CreateBuf(buf + 16, (int)imgSize)) return SpawnResult.OutOfMemory;

        // the task name is the file's base name, like the shell's "MEM.EXE"
        char* nm = stackalloc char[16];
        int nl = 0;
        int start = 0;
        for (int k = pathLen - 1; k >= 0; k--)
        {
            if (path[k] == '\\' || path[k] == '/') { start = k + 1; break; }
        }
        for (int k = start; k < pathLen && nl < 15; k++) nm[nl++] = path[k];

        if (boot)
            // Q4: the boot program (the shell) is the session leader of a fresh
            // session that starts with NO uid. Set session 1 / uid -1 BEFORE
            // state=READY (inside SeedSlot) so it can never run for even one tick
            // with the Console's uid 0 / session 0.
            SeedSlot(slot, NativeMethods.osb_sched_get_user_entry(), "USER", Layout.KindUser,
                1, -1, -1, -1);
        else
            SeedSlot(slot, NativeMethods.osb_sched_get_user_entry(), "USER", Layout.KindUser,
                _tasks.sessionId[_current], _tasks.realUid[_current],
                _tasks.uid[_current], _tasks.gid[_current]);
        SetNameChars(slot, nm, nl);
        InitTaskTls(slot, AddressSpace.Block);              // M3.4.2
        _tasks.arg[slot] = Layout.UserBase + entryOff;   // user RIP in the private block
        _tasks.cr3[slot] = AddressSpace.Cr3;
        _tasks.ublock[slot] = AddressSpace.Block;
        _tasks.ptphys[slot] = AddressSpace.PageTables;
        _tasks.ppid[slot] = _tasks.pid[parentSlot];
        pid = _tasks.pid[slot];
        ConsoleInput.Flush();   // S2: no stale cooked input / Ctrl+C for the child
        return SpawnResult.Ok;
    }

    private static byte* ImgData()
    {
        fixed (byte* p = _img.data) { return p; }
    }
}
