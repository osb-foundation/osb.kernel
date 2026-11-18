// Per-task environment (Slice 3, C1/C2).
//
// Every task slot owns a private variable store: GETENV/SETENV act on the
// CALLING task, and a spawn copies the parent's store so a child's changes
// never reach the parent. USER/HOME are session-derived: the kernel sets them
// at LOGIN, clears them at LOGOUT, and locks them against SETENV from any
// task (elevated or not).
//
// Fixed buffers, no managed arrays (freestanding AOT). Names are upper-cased
// so lookups are case-insensitive (DOS/OSB convention); values keep their
// characters. Redefining a variable appends the new value (the old bytes are
// left behind), exactly like the legacy store. Delete shifts the remaining
// entries down so insertion order stays stable.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Shell;

internal static unsafe partial class Environment
{
    private const int Count = Layout.TaskCount;
    private const int NameCap = 64;      // SETENV accepts 1..64
    private const int ValueCap = 256;    // SETENV accepts 0..256

    private unsafe struct EnvTable
    {
        public fixed int nameOff[Count * Layout.MaxEnvVars];
        public fixed int nameLen[Count * Layout.MaxEnvVars];
        public fixed int valueOff[Count * Layout.MaxEnvVars];
        public fixed int valueLen[Count * Layout.MaxEnvVars];
        public fixed int namePos[Count];
        public fixed int valuePos[Count];
        public fixed int used[Count];
        public fixed byte names[Count * Layout.EnvNameBufSize];
        public fixed byte values[Count * Layout.EnvValueBufSize];
        public fixed byte locked[Count * Layout.MaxEnvVars];  // 1 = kernel-managed
    }

    private static EnvTable _tab;

    // ---- boot / session lifecycle ----

    // Zero the whole table and seed the console slot with the boot defaults.
    public static void Init()
    {
        fixed (byte* p = _tab.names) for (int i = 0; i < Count * Layout.EnvNameBufSize; i++) p[i] = 0;
        fixed (byte* p = _tab.values) for (int i = 0; i < Count * Layout.EnvValueBufSize; i++) p[i] = 0;
        fixed (byte* p = _tab.locked) for (int i = 0; i < Count * Layout.MaxEnvVars; i++) p[i] = 0;
        fixed (int* p = _tab.nameOff) for (int i = 0; i < Count * Layout.MaxEnvVars; i++) p[i] = 0;
        fixed (int* p = _tab.nameLen) for (int i = 0; i < Count * Layout.MaxEnvVars; i++) p[i] = 0;
        fixed (int* p = _tab.valueOff) for (int i = 0; i < Count * Layout.MaxEnvVars; i++) p[i] = 0;
        fixed (int* p = _tab.valueLen) for (int i = 0; i < Count * Layout.MaxEnvVars; i++) p[i] = 0;
        fixed (int* p = _tab.namePos) for (int i = 0; i < Count; i++) p[i] = 0;
        fixed (int* p = _tab.valuePos) for (int i = 0; i < Count; i++) p[i] = 0;
        fixed (int* p = _tab.used) for (int i = 0; i < Count; i++) p[i] = 0;
        SetDefaults(Scheduler.Console);
    }

    // The boot/session defaults, in the legacy order (USER/HOME included so a
    // login rewrites them in place instead of appending). USER/HOME are locked
    // from the start: no task can SETENV them (C2).
    private static void SetDefaults(int slot)
    {
        Set2(slot, "PATH", "A:\\OSB\\BIN");
        Set2(slot, "PROMPT", "$USER@OSB $P$G");
        Set2(slot, "USER", "guest");
        Set2(slot, "HOME", "A:\\OSB\\ROOT");   // root's home; login rewrites it
        Set2(slot, "TEMP", "A:\\OSB\\TEMP");
        Set2(slot, "COMSPEC", "A:\\OSB\\BIN\\COMMAND.COM");
        Set2(slot, "OS", "OSB");
        Set2(slot, "VER", "0.1.0");
        Set2(slot, "LASTDRIVE", "Z");
        Lock(slot, "USER");
        Lock(slot, "HOME");
    }

    // Drops every variable in a slot (the buffers are left for reuse).
    private static void Reset(int slot)
    {
        _tab.used[slot] = 0;
        _tab.namePos[slot] = 0;
        _tab.valuePos[slot] = 0;
        for (int i = 0; i < Layout.MaxEnvVars; i++) _tab.locked[slot * Layout.MaxEnvVars + i] = 0;
    }

    // LOGOUT / re-LOGIN: drop everything (including user variables) and put
    // the session defaults back, so no value leaks into the next login (C2).
    public static void ResetToDefaults(int slot)
    {
        Reset(slot);
        SetDefaults(slot);
    }

    private static void Set2(int slot, string name, string value)
    {
        fixed (char* n = name)
        fixed (char* v = value)
        {
            SetCore(slot, n, name.Length, v, value.Length, force: true);
        }
    }

    // Binds $USER/$HOME to the session user (LOGIN). They already exist and are
    // locked, so this is an in-place update that keeps the legacy ordering.
    public static void SetSessionUser(int slot, int userIdx)
    {
        char* userVar = stackalloc char[8];
        fixed (char* p = "USER") { for (int k = 0; k < 4; k++) userVar[k] = p[k]; }
        char* nm = stackalloc char[Users.NameCap];
        int nl = Users.NameAt(userIdx, nm);
        ForceSetChars(slot, userVar, 4, nm, nl);

        char* homeVar = stackalloc char[8];
        fixed (char* p = "HOME") { for (int k = 0; k < 4; k++) homeVar[k] = p[k]; }
        char* home = stackalloc char[Users.HomeCap];
        int hl = Users.HomeAt(userIdx, home);
        ForceSetChars(slot, homeVar, 4, home, hl);
    }

    // Copies src's whole store into dst (spawn). The child gets an independent
    // copy: later SETENV in either task is invisible to the other (C1).
    public static void CopyFrom(int dst, int src)
    {
        if (dst < 0 || dst >= Count || src < 0 || src >= Count || dst == src) return;
        fixed (byte* names = _tab.names)
        fixed (byte* values = _tab.values)
        fixed (byte* locked = _tab.locked)
        {
            int np = _tab.namePos[src];
            int vp = _tab.valuePos[src];
            int no = src * Layout.EnvNameBufSize, do_ = dst * Layout.EnvNameBufSize;
            int vo = src * Layout.EnvValueBufSize, dv = dst * Layout.EnvValueBufSize;
            for (int k = 0; k < np; k++) names[do_ + k] = names[no + k];
            for (int k = 0; k < vp; k++) values[dv + k] = values[vo + k];
            for (int k = 0; k < Layout.MaxEnvVars; k++)
                locked[dst * Layout.MaxEnvVars + k] = locked[src * Layout.MaxEnvVars + k];
        }
        _tab.namePos[dst] = _tab.namePos[src];
        _tab.valuePos[dst] = _tab.valuePos[src];
        _tab.used[dst] = _tab.used[src];
        for (int k = 0; k < Layout.MaxEnvVars; k++)
        {
            _tab.nameOff[dst * Layout.MaxEnvVars + k] = _tab.nameOff[src * Layout.MaxEnvVars + k];
            _tab.nameLen[dst * Layout.MaxEnvVars + k] = _tab.nameLen[src * Layout.MaxEnvVars + k];
            _tab.valueOff[dst * Layout.MaxEnvVars + k] = _tab.valueOff[src * Layout.MaxEnvVars + k];
            _tab.valueLen[dst * Layout.MaxEnvVars + k] = _tab.valueLen[src * Layout.MaxEnvVars + k];
        }
    }

    // ---- set / delete (SETENV) ----

    // Returns 0 on success, -3 locked, -5 bad name, -6 store full.
    public static int SetChars(int slot, char* name, int nlen, char* value, int vlen)
    {
        return SetCore(slot, name, nlen, value, vlen, force: false);
    }

    // Login path: bypasses the read-only flag (the name is USER/HOME).
    public static bool ForceSetChars(int slot, char* name, int nlen, char* value, int vlen)
    {
        return SetCore(slot, name, nlen, value, vlen, force: true) == 0;
    }

    private static int SetCore(int slot, char* name, int nlen, char* value, int vlen, bool force)
    {
        if (slot < 0 || slot >= Count) return -5;
        if (nlen <= 0 || nlen > NameCap) return -5;
        if (vlen < 0 || vlen > ValueCap) return -5;
        int used = _tab.used[slot];
        for (int i = 0; i < used; i++)
        {
            int mi = slot * Layout.MaxEnvVars + i;
            if (_tab.nameLen[mi] != nlen) continue;
            if (!NameEq(slot, i, name, nlen)) continue;
            if (!force && _tab.locked[mi] != 0) return -3;
            if (_tab.valuePos[slot] + vlen > Layout.EnvValueBufSize) return -6;
            int vo = _tab.valuePos[slot];
            for (int k = 0; k < vlen; k++) _tab.values[slot * Layout.EnvValueBufSize + vo + k] = (byte)value[k];
            _tab.valueOff[mi] = vo;
            _tab.valueLen[mi] = vlen;
            _tab.valuePos[slot] = vo + vlen;
            return 0;
        }
        if (used >= Layout.MaxEnvVars) return -6;
        if (_tab.namePos[slot] + nlen > Layout.EnvNameBufSize) return -6;
        if (_tab.valuePos[slot] + vlen > Layout.EnvValueBufSize) return -6;
        int mi2 = slot * Layout.MaxEnvVars + used;
        int no = _tab.namePos[slot];
        for (int k = 0; k < nlen; k++)
        {
            char c = name[k];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            _tab.names[slot * Layout.EnvNameBufSize + no + k] = (byte)c;
        }
        _tab.namePos[slot] = no + nlen;
        int vo2 = _tab.valuePos[slot];
        for (int k = 0; k < vlen; k++) _tab.values[slot * Layout.EnvValueBufSize + vo2 + k] = (byte)value[k];
        _tab.valuePos[slot] = vo2 + vlen;
        _tab.nameOff[mi2] = no;
        _tab.nameLen[mi2] = nlen;
        _tab.valueOff[mi2] = vo2;
        _tab.valueLen[mi2] = vlen;
        _tab.used[slot] = used + 1;
        return 0;
    }

    // SETENV with a null value pointer and length 0: delete. Returns 0, -4 if
    // undefined, -3 if locked, -5 on a bad name.
    public static int DeleteChars(int slot, char* name, int nlen)
    {
        if (slot < 0 || slot >= Count) return -5;
        if (nlen <= 0 || nlen > NameCap) return -5;
        int used = _tab.used[slot];
        for (int i = 0; i < used; i++)
        {
            int mi = slot * Layout.MaxEnvVars + i;
            if (_tab.nameLen[mi] != nlen || !NameEq(slot, i, name, nlen)) continue;
            if (_tab.locked[mi] != 0) return -3;
            for (int j = i; j < used - 1; j++)
            {
                int a = slot * Layout.MaxEnvVars + j, b = a + 1;
                _tab.nameOff[a] = _tab.nameOff[b];
                _tab.nameLen[a] = _tab.nameLen[b];
                _tab.valueOff[a] = _tab.valueOff[b];
                _tab.valueLen[a] = _tab.valueLen[b];
                _tab.locked[a] = _tab.locked[b];
            }
            _tab.used[slot] = used - 1;
            return 0;
        }
        return -4;
    }

    // Marks an existing variable read-only (SET/SETENV refuse to change it).
    public static void Lock(int slot, string name)
    {
        fixed (char* n = name)
        {
            int used = _tab.used[slot];
            for (int i = 0; i < used; i++)
            {
                int mi = slot * Layout.MaxEnvVars + i;
                if (_tab.nameLen[mi] != name.Length) continue;
                bool same = true;
                for (int k = 0; k < name.Length; k++)
                    if ((char)_tab.names[slot * Layout.EnvNameBufSize + _tab.nameOff[mi] + k] != n[k]) { same = false; break; }
                if (same) { _tab.locked[mi] = 1; return; }
            }
        }
    }

    // ---- lookup (GETENV) ----

    // Case-insensitive; returns the value's range in the value buffer.
    public static bool TryGet(int slot, char* name, int nlen, out int valueOff, out int valueLen)
    {
        valueOff = 0;
        valueLen = 0;
        if (slot < 0 || slot >= Count || nlen <= 0 || nlen > NameCap) return false;
        int used = _tab.used[slot];
        for (int i = 0; i < used; i++)
        {
            int mi = slot * Layout.MaxEnvVars + i;
            if (_tab.nameLen[mi] != nlen || !NameEq(slot, i, name, nlen)) continue;
            valueOff = _tab.valueOff[mi];
            valueLen = _tab.valueLen[mi];
            return true;
        }
        return false;
    }

    // One character of a value, addressed by the offsets TryGet returns.
    public static char ValueAt(int slot, int off)
    {
        return (char)_tab.values[slot * Layout.EnvValueBufSize + off];
    }

    // 63h with nameLen 0: render every variable as "NAME=VALUE\n" in insertion
    // order, truncated to cap. Returns the number of bytes written.
    public static int CopyAll(int slot, byte* dst, int cap)
    {
        int o = 0;
        int used = _tab.used[slot];
        for (int i = 0; i < used; i++)
        {
            int mi = slot * Layout.MaxEnvVars + i;
            for (int k = 0; k < _tab.nameLen[mi]; k++)
            {
                if (o >= cap) return o;
                dst[o++] = _tab.names[slot * Layout.EnvNameBufSize + _tab.nameOff[mi] + k];
            }
            if (o >= cap) return o;
            dst[o++] = (byte)'=';
            for (int k = 0; k < _tab.valueLen[mi]; k++)
            {
                if (o >= cap) return o;
                dst[o++] = _tab.values[slot * Layout.EnvValueBufSize + _tab.valueOff[mi] + k];
            }
            if (o >= cap) return o;
            dst[o++] = (byte)'\n';
        }
        return o;
    }

    // Expands $VAR (case-insensitive) into dst. Undefined variables
    // round-trip literally, so SET PROMPT=$P$G keeps its special characters.
    public static int Expand(int slot, char* src, int slen, char* dst, int dcap)
    {
        int o = 0;
        int i = 0;
        while (i < slen && o < dcap - 1)
        {
            if (src[i] != '$') { dst[o++] = src[i++]; continue; }
            if (i + 1 >= slen) { dst[o++] = '$'; i++; continue; }
            char n0 = src[i + 1];
            bool ident = (n0 >= 'A' && n0 <= 'Z') || (n0 >= 'a' && n0 <= 'z') || n0 == '_';
            if (!ident) { dst[o++] = '$'; i++; continue; }
            int st = i + 1;
            int j = st;
            while (j < slen && (((src[j] >= 'A' && src[j] <= 'Z') || (src[j] >= 'a' && src[j] <= 'z') ||
                                 (src[j] >= '0' && src[j] <= '9') || src[j] == '_'))) j++;
            int nameLen = j - st;
            char* nm = stackalloc char[NameCap];
            for (int k = 0; k < nameLen && k < NameCap - 1; k++) nm[k] = src[st + k];
            int vo;
            int vl;
            if (TryGet(slot, nm, nameLen, out vo, out vl))
            {
                for (int k = 0; k < vl && o < dcap - 1; k++) dst[o++] = ValueAt(slot, vo + k);
                i = j;
                continue;
            }
            dst[o++] = '$';
            i++;
        }
        dst[o] = '\0';
        return o;
    }

    private static bool NameEq(int slot, int i, char* name, int nlen)
    {
        int mi = slot * Layout.MaxEnvVars + i;
        int no = _tab.nameOff[mi];
        for (int k = 0; k < nlen; k++)
        {
            char c = name[k];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            if (c != (char)_tab.names[slot * Layout.EnvNameBufSize + no + k]) return false;
        }
        return true;
    }
}