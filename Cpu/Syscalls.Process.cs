using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 30h GETPID: current process id in EAX
    private static void HandleGetPid(ulong* frame)
    {
        frame[14] = (ulong)Scheduler.Get(Scheduler.Current).Pid;
    }

    // 4Ch EXIT(code in AL): become a zombie; wait() reaps us
    private static void HandleExit(ulong* frame)
    {
        ulong rax = frame[14];
        Scheduler.ExitCurrent((int)(rax & 0xFF));
    }

    // 6Ah SPAWNPATH(req in RDI): SpawnReq block
    private static void HandleSpawnPath(ulong* frame)
    {
        ulong rdi = frame[9];

        if (!UserPtr(rdi, 40)) { frame[14] = unchecked((ulong)-1); return; } // EFAULT
        byte* req = (byte*)rdi;
        ulong pathPtr = Rd64(req, 0);
        uint pathLen = Rd32(req, 8);
        ulong argsPtr = Rd64(req, 16);
        uint argsLen = Rd32(req, 24);
        int stdinH = (int)Rd32(req, 32);
        int stdoutH = (int)Rd32(req, 36);

        // Content checks first (EINVAL)
        if (pathLen == 0 || pathLen > 260 || argsLen > 128)
        {
            frame[14] = unchecked((ulong)-5); // EINVAL
            return;
        }
        // Pointer checks (EFAULT)
        if (!UserPtr(pathPtr, (int)pathLen) ||
            (argsLen > 0 && !UserPtr(argsPtr, (int)argsLen)))
        {
            frame[14] = unchecked((ulong)-1); // EFAULT
            return;
        }

        // Resolve + type + exec checks BEFORE any task is created (B3)
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
                char* cargs = stackalloc char[128];
                for (int i = 0; i < (int)argsLen; i++) cargs[i] = (char)((byte*)argsPtr)[i];
                Scheduler.SetArgs(slot, cargs, (int)argsLen);
            }
        }
        frame[14] = (ulong)pid;
    }

    // 6Bh TASKLIST(out in RDI, cap in RSI) -> count
    private static void HandleTaskList(ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];

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
    }

    // 6Ch KILL(pid in RDI) -> 0 ok / -4 not found / -3 denied
    private static void HandleKill(ulong* frame)
    {
        ulong rdi = frame[9];
        int pid = (int)rdi;
        int slot;
        if (!Scheduler.TryFindPid(pid, out slot)) { frame[14] = unchecked((ulong)-4); return; }
        if (slot == 0) { frame[14] = unchecked((ulong)-3); return; } // console
        int uid = Scheduler.CurrentUid();
        int targetUid = Scheduler.GetUid(slot);
        if (uid != 0 && uid != targetUid) { frame[14] = unchecked((ulong)-3); return; } // EACCES
        Scheduler.Release(slot);
        frame[14] = 0;
    }

    // 6Dh SETFG(pid in RDI) -> 0 ok / -4 not found / -3 denied
    private static void HandleSetFg(ulong* frame)
    {
        ulong rdi = frame[9];
        int pid = (int)rdi;
        int slot;
        if (!Scheduler.TryFindPid(pid, out slot)) { frame[14] = unchecked((ulong)-4); return; }
        int uid = Scheduler.CurrentUid();
        int targetUid = Scheduler.GetUid(slot);
        if (uid != 0 && uid != targetUid) { frame[14] = unchecked((ulong)-3); return; } // EACCES
        Scheduler.MakeForeground(slot);
        frame[14] = 0;
    }

    // 80h SPAWN(program id in AL): returns the child's pid, or -1
    private static void HandleSpawn(ulong* frame)
    {
        ulong rax = frame[14];
        int prog = (int)(rax & 0xFF);
        int pid;
        SpawnResult r = Scheduler.SpawnUser(prog, Scheduler.Current, out pid);
        frame[14] = unchecked((ulong)(r == SpawnResult.Ok ? pid : -1));
    }

    // 81h WAIT(pid in RSI): blocks until the child exits
    private static void HandleWait(ulong* frame)
    {
        ulong rsi = frame[10];
        frame[14] = unchecked((ulong)Scheduler.WaitFor((int)rsi));
    }

    // 82h GETABI: ABI version in AX
    private static void HandleGetAbi(ulong* frame)
    {
        frame[14] = (ulong)(ushort)AbiVersion;
    }
}