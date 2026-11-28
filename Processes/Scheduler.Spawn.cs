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
    //
    // Sequence: resolve the internal uint Program ID via the explicit legacy
    // translation layer, acquire a spawn lease, obtain a descriptor copy,
    // validate the embedded source metadata, create the address space, then
    // seed the scheduler slot and assign the PID. The lease is released on
    // every exit path; failures leak neither leases, frames, address-space
    // storage, nor scheduler slots.
    public static SpawnResult SpawnUser(int prog, int parentSlot, out int pid)
    {
        pid = -1;

        // 1. Resolve the internal uint Program ID. prog is the legacy AL
        //    value; validate the integer domain before any narrowing so an
        //    out-of-range int cannot alias a valid legacy ID through a
        //    (byte) truncation (e.g. 256->0/HELLO, 257->1/BAD, -1->255).
        if (prog < 0 || prog > 255) return SpawnResult.NotFound;
        uint internalId = ProgramRegistry.LegacyToInternal((byte)prog);
        if (internalId == 0) return SpawnResult.NotFound;

        // 2. Acquire a spawn lease before loading. A disabled program cannot
        //    acquire a new lease.
        SpawnLease lease;
        if (!ProgramRegistry.TryAcquireSpawnLease(internalId, out lease)) return SpawnResult.NotFound;

        // 3. Obtain a descriptor copy (no internal pointer escapes the Registry).
        if (!ProgramRegistry.TryGetById(internalId, out var desc))
        {
            ProgramRegistry.ReleaseSpawnLease(lease);
            return SpawnResult.NotFound;
        }

        // 4. Validate the embedded source metadata.
        if (desc.ImageEnd <= desc.ImageStart)
        {
            ProgramRegistry.ReleaseSpawnLease(lease);
            return SpawnResult.NotFound;
        }
        ulong size = desc.ImageEnd - desc.ImageStart;
        if (size > (ulong)Layout.BlockFrames * Layout.FrameSize || desc.EntryOffset >= size)
        {
            ProgramRegistry.ReleaseSpawnLease(lease);
            return SpawnResult.NotFound;
        }

        // 5/6. Allocate a scheduler slot (before AddressSpace so a NoSlot
        //    refusal never leaks an address space), copy the environment,
        //    create the address space via CommonLoader, seed the slot and assign the PID.
        int slot = AllocSlot();
        if (slot < 0)
        {
            ProgramRegistry.ReleaseSpawnLease(lease);
            return SpawnResult.NoSlot;
        }
        // Refuse (no truncation) if the next PID cannot be represented by the
        // int spawn ABI. The slot is still free (AllocSlot does not reserve).
        if (!SpawnPidFits())
        {
            ProgramRegistry.ReleaseSpawnLease(lease);
            return SpawnResult.NoSlot;
        }
        Environment.CopyFrom(slot, _current);

        var exe = new ResolvedExecutable
        {
            Data = (byte*)desc.ImageStart,
            Length = desc.ImageEnd - desc.ImageStart,
            EntryOffset = desc.EntryOffset
        };
        if (!CommonLoader.TryLoad(exe))
        {
            // Reclaim the slot (no address space is attached to it yet;
            // AddressSpace.Create frees its own frames on failure).
            _tasks.state[slot] = Layout.TaskFree;
            ProgramRegistry.ReleaseSpawnLease(lease);
            return SpawnResult.OutOfMemory;
        }

        string name = ProgramRegistry.GetName(internalId) ?? "UNKNOWN";
        SeedSlot(slot, NativeMethods.osb_sched_get_user_entry(), name, Layout.KindUser,
            _tasks.sessionId[_current], _tasks.realUid[_current],
            _tasks.uid[_current], _tasks.gid[_current]);
        PublishUserTask(slot, desc.EntryOffset, AddressSpace.Block,
            AddressSpace.Cr3, AddressSpace.PageTables, parentSlot, out pid);

        // 7. Release the lease on every exit path.
        ProgramRegistry.ReleaseSpawnLease(lease);
        return SpawnResult.Ok;
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
        // No args/handles for boot spawn
        return SpawnFileInode(ino, path, pathLen, parentSlot, out pid, boot, null);
    }

    // Spawn a file with explicit argument string (for boot program).
    public static SpawnResult SpawnFileWithArgs(char* path, int pathLen, int parentSlot, out int pid, char* args, int argsLen)
    {
        pid = -1;
        if (!Ext4.Mount()) return SpawnResult.NotFound;
        uint ino;
        if (!Ext4.Resolve(path, pathLen, out ino)) return SpawnResult.NotFound;
        byte* inode = stackalloc byte[256];
        if (!Ext4.ReadInode(ino, inode)) return SpawnResult.NotFound;
        if (!Ext4.IsReg(Ext4.InodeGetMode(inode))) return SpawnResult.NotFound;

        var ctx = new SpawnContext
        {
            Args = args,
            ArgsLen = argsLen,
            StdinH = -1,
            StdoutH = -1
        };
        return SpawnFileInode(ino, path, pathLen, parentSlot, out pid, boot: true, &ctx);
    }

    // Spawn context for AH=6Ah: argument string and stdin/stdout handle indices.
    internal unsafe struct SpawnContext
    {
        public char* Args;
        public int ArgsLen;
        public int StdinH;
        public int StdoutH;
    }

    // Load and start an executable file whose inode is already resolved (the
    // 6Ah path resolves + checks type/exec first, then calls this so a refusal
    // leaves no task behind). `path`/`pathLen` are used only to derive the task
    // name. Same OSBX header contract as SpawnFile.
    // If ctx is non-null, its args/handles are applied before task publication.
    public static SpawnResult SpawnFileInode(uint ino, char* path, int pathLen, int parentSlot, out int pid, bool boot = false, SpawnContext* ctx = null)
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

        // AllocSlot BEFORE Create (P0.5): if there is no free slot we return
        // NoSlot without having allocated an address space, so its frames are
        // not leaked. (AllocSlot only reads state; it does not reserve, so a
        // later creation failure leaves the slot free for reuse.)
        int slot = AllocSlot();
        if (slot < 0) return SpawnResult.NoSlot;
        // Refuse (no truncation) if the next PID cannot be represented by the
        // int spawn ABI. The slot is still free (AllocSlot does not reserve).
        if (!SpawnPidFits()) return SpawnResult.NoSlot;
        // Copy the parent's environment while the slot is still FREE (C1).
        Environment.CopyFrom(slot, _current);

        var exe = new ResolvedExecutable
        {
            Data = buf + 16,
            Length = imgSize,
            EntryOffset = entryOff
        };
        if (!CommonLoader.TryLoad(exe)) return SpawnResult.OutOfMemory;

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

        // Apply AH=6Ah context (args/handles) before publication if provided
        if (ctx != null)
        {
            if (ctx->ArgsLen > 0 && ctx->Args != null)
                SetArgs(slot, ctx->Args, ctx->ArgsLen);
            PropagateHandles(slot, ctx->StdinH, ctx->StdoutH);
        }

        PublishUserTask(slot, entryOff, AddressSpace.Block,
            AddressSpace.Cr3, AddressSpace.PageTables, parentSlot, out pid);
        return SpawnResult.Ok;
    }

    private static byte* ImgData()
    {
        fixed (byte* p = _img.data) { return p; }
    }
}