using Osb.Kernel.Hardware;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 73h CHMOD(path RDI, len RSI, mode RDX) -> 0 ok / -3 EACCES / -4 not found / -5 EINVAL / -1 EFAULT
    // 74h CHOWN(path RDI, len RSI, uid RDX) -> 0 ok / -3 EACCES / -4 not found / -5 EINVAL / -1 EFAULT
    // 75h USEROP(sub in AL) — account management
    private static void HandleUser(int func, ulong* frame)
    {
        ulong rax = frame[14];
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];

        if (func == 0x73) // CHMOD
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

        if (func == 0x74) // CHOWN
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

        if (func == 0x75) // USEROP
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
                        if (hl > 0 && !Ext4.DeleteTree(hm, hl)) { frame[14] = unchecked((ulong)-5); return; }
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

        frame[14] = unchecked((ulong)-2); // ENOSYS
    }
}