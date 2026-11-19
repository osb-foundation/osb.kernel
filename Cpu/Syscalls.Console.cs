using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Storage;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 01h WRITE(buf in RDI, len in RSI): bytes to the console or stdout handle
    private static void HandleWrite(ulong* frame)
    {
        ulong rax = frame[14];
        ulong rsi = frame[10];
        ulong rdi = frame[9];

        ulong n = rsi > 4096 ? 4096 : rsi;
        if (!UserPtr(rdi, (int)n))
        {
            frame[14] = unchecked((ulong)-1); // EFAULT
            return;
        }
        int stdoutH = Scheduler.GetStdout();
        if (stdoutH != -1)
        {
            int written = Scheduler.FileWrite(stdoutH, (byte*)rdi, (int)n);
            if (written < 0) { frame[14] = unchecked((ulong)-5); return; }
            frame[14] = (ulong)written;
            return;
        }
        byte* p = (byte*)rdi;
        for (ulong i = 0; i < n; i++) Vga.PutChar((char)p[i]);
        Vga.FlushCursor();
        frame[14] = n;
    }

    // 09h PRINT: print '$'-terminated string at RSI
    private static void HandlePrint(ulong* frame)
    {
        ulong rsi = frame[10];
        if (!UserPtr(rsi, 0))
        {
            frame[14] = unchecked((ulong)-1); // EFAULT
            return;
        }
        byte* p = (byte*)rsi;
        int n = 0;
        while (n < 4096 && rsi + (ulong)n < Layout.UserLimit && p[n] != (byte)'$')
        {
            Vga.PutChar((char)p[n]);
            n++;
        }
        Vga.FlushCursor();
        frame[14] = 0;
    }

    // 6Fh CLEAR: console clear + serial ESC[2J H
    private static void HandleClear(ulong* frame)
    {
        Vga.Clear();
        frame[14] = 0;
    }

    // 70h SETCOLOR: fg in AL, bg in AH
    private static void HandleSetColor(ulong* frame)
    {
        ulong rax = frame[14];
        byte fg = (byte)(rax & 0xFF);
        byte bg = (byte)((rax >> 8) & 0xFF);
        if (fg == bg) { frame[14] = unchecked((ulong)-5); return; } // EINVAL: same fg/bg
        Vga.SetColor(fg, bg);
        frame[14] = 0;
    }

    // 71h GETCOLOR: out fg, bg (2 bytes at RDI)
    private static void HandleGetColor(ulong* frame)
    {
        ulong rdi = frame[9];
        if (!UserPtr(rdi, 2)) { frame[14] = unchecked((ulong)-1); return; } // EFAULT
        byte attr = Vga.Attr;
        ((byte*)rdi)[0] = (byte)(attr & 0x0F);       // fg
        ((byte*)rdi)[1] = (byte)((attr >> 4) & 0x0F); // bg
        frame[14] = 0;
    }

    // 72h DATETIME: out u8[8] = Y,M,D,h,m,s,0,0 at RDI
    private static void HandleDateTime(ulong* frame)
    {
        ulong rdi = frame[9];
        if (!UserPtr(rdi, 8)) { frame[14] = unchecked((ulong)-1); return; } // EFAULT
        byte* o = (byte*)rdi;
        o[0] = Rtc.Year;
        o[1] = Rtc.Month;
        o[2] = Rtc.Day;
        o[3] = Rtc.Hour;
        o[4] = Rtc.Minute;
        o[5] = Rtc.Second;
        o[6] = 0;
        o[7] = 0;
        frame[14] = 0;
    }

    // 6Eh GETCHAR: blocking character input (AH=6Eh), returns char in AL
    private static void HandleGetChar(ulong* frame)
    {
        int c = ConsoleInput.PopBlocking();
        frame[14] = (ulong)(c & 0xFF);
    }
}