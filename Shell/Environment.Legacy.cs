// Legacy-shell convenience + listing for the per-task environment.
//
// The in-kernel shell (still compiled as the behavioral reference) calls these
// slot-less overloads; they act on the CURRENT task, exactly like the ring-3
// 63h/64h calls. Split from Environment.cs to keep both files under the
// 400-line limit.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Shell;

internal static unsafe partial class Environment
{
    // ---- current-task convenience ----

    public static int SetChars(char* name, int nlen, char* value, int vlen)
        => SetChars(Scheduler.Current, name, nlen, value, vlen);

    public static bool ForceSetChars(char* name, int nlen, char* value, int vlen)
        => ForceSetChars(Scheduler.Current, name, nlen, value, vlen);

    public static void SetSessionUser(int userIdx)
        => SetSessionUser(Scheduler.Current, userIdx);

    public static bool TryGet(char* name, int nlen, out int valueOff, out int valueLen)
        => TryGet(Scheduler.Current, name, nlen, out valueOff, out valueLen);

    public static char ValueAt(int off) => ValueAt(Scheduler.Current, off);

    public static int Expand(char* src, int slen, char* dst, int dcap)
        => Expand(Scheduler.Current, src, slen, dst, dcap);

    public static void Lock(string name) => Lock(Scheduler.Current, name);

    // ---- legacy SET listing ----

    public static void ListAll()
    {
        int slot = Scheduler.Current;
        int used = _tab.used[slot];
        for (int i = 0; i < used; i++)
        {
            int mi = slot * Layout.MaxEnvVars + i;
            for (int k = 0; k < _tab.nameLen[mi]; k++)
                Vga.PutChar((char)_tab.names[slot * Layout.EnvNameBufSize + _tab.nameOff[mi] + k]);
            Vga.PutChar('=');
            for (int k = 0; k < _tab.valueLen[mi]; k++)
                Vga.PutChar((char)_tab.values[slot * Layout.EnvValueBufSize + _tab.valueOff[mi] + k]);
            Vga.PutChar('\n');
        }
    }

    public static void ListPrefix(char* name, int nlen)
    {
        int slot = Scheduler.Current;
        bool any = false;
        int used = _tab.used[slot];
        for (int i = 0; i < used; i++)
        {
            int mi = slot * Layout.MaxEnvVars + i;
            if (_tab.nameLen[mi] < nlen) continue;
            bool pref = true;
            for (int k = 0; k < nlen; k++)
            {
                char c = name[k];
                if (c >= 'a' && c <= 'z') c = (char)(c - 32);
                if (c != (char)_tab.names[slot * Layout.EnvNameBufSize + _tab.nameOff[mi] + k]) { pref = false; break; }
            }
            if (!pref) continue;
            any = true;
            for (int k = 0; k < _tab.nameLen[mi]; k++)
                Vga.PutChar((char)_tab.names[slot * Layout.EnvNameBufSize + _tab.nameOff[mi] + k]);
            Vga.PutChar('=');
            for (int k = 0; k < _tab.valueLen[mi]; k++)
                Vga.PutChar((char)_tab.values[slot * Layout.EnvValueBufSize + _tab.valueOff[mi] + k]);
            Vga.PutChar('\n');
        }
        if (!any) Vga.WriteLine("Environment variable not defined.");
    }
}