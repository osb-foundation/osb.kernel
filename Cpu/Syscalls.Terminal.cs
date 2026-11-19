using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;

namespace Osb.Kernel.Cpu;

internal static unsafe partial class Syscalls
{
    // 7Ah-7Fh Terminal API (K2)
    private static void HandleTerminal(int func, ulong* frame)
    {
        ulong rdi = frame[9];
        ulong rsi = frame[10];
        ulong rdx = frame[11];
        ulong rcx = frame[12];
        ulong rax = frame[14];

        if (func == 0x7A) // CONS_INFO: RDI = out u16[2] (cols, rows)
        {
            if (!UserPtr(rdi, 4)) { frame[14] = unchecked((ulong)-1); return; }
            ushort* o = (ushort*)rdi;
            o[0] = (ushort)Vga.Cols;  // 80
            o[1] = (ushort)Vga.Rows;  // 25
            frame[14] = 0;
            return;
        }

        if (func == 0x7B) // CONS_CURSOR: AL=0 get, AL=1 set, AL=2 hide, AL=3 show
        {
            byte sub = (byte)(rax & 0xFF);
            if (sub == 0) // get cursor position
            {
                if (!UserPtr(rdi, 4)) { frame[14] = unchecked((ulong)-1); return; }
                ushort* o = (ushort*)rdi;
                o[0] = (ushort)(Vga.CursorRow + 1); // 1-based
                o[1] = (ushort)(Vga.CursorCol + 1); // 1-based
                frame[14] = 0;
                return;
            }
            if (sub == 1) // set cursor position
            {
                ushort row = (ushort)rdi;
                ushort col = (ushort)rsi;
                if (row == 0 || row > Vga.Rows || col == 0 || col > Vga.Cols)
                {
                    frame[14] = unchecked((ulong)-5); // EINVAL
                    return;
                }
                Vga.SetCursor((short)(row - 1), (short)(col - 1));
                frame[14] = 0;
                return;
            }
            if (sub == 2) // hide cursor
            {
                Vga.ShowCursor(false);
                frame[14] = 0;
                return;
            }
            if (sub == 3) // show cursor
            {
                Vga.ShowCursor(true);
                frame[14] = 0;
                return;
            }
            frame[14] = unchecked((ulong)-5); // EINVAL
            return;
        }

        if (func == 0x7C) // CONS_WRITE_AT: RDI=row, RSI=col, RDX=buf, RCX=len
        {
            ushort row = (ushort)rdi;
            ushort col = (ushort)rsi;
            if (row == 0 || row > Vga.Rows || col == 0 || col > Vga.Cols)
            {
                frame[14] = unchecked((ulong)-5); // EINVAL
                return;
            }
            ulong len = rcx > 1024 ? 1024 : rcx;
            if (!UserPtr(rdx, (int)len)) { frame[14] = unchecked((ulong)-1); return; }
            byte* buf = (byte*)rdx;
            int written = 0;
            for (ulong i = 0; i < len; i++)
            {
                int r = (row - 1) + (col - 1 + written) / Vga.Cols;
                int c = (col - 1 + written) % Vga.Cols;
                if (r >= Vga.Rows) break;
                Vga.PutCharAt((char)buf[i], r, c);
                written++;
            }
            Vga.FlushCursor();
            frame[14] = (ulong)written;
            return;
        }

        if (func == 0x7D) // CONS_CLEAR: AL=0 full, AL=1 line, AL=2 area
        {
            byte sub = (byte)(rax & 0xFF);
            if (sub == 0) // full
            {
                Vga.Clear();
                frame[14] = 0;
                return;
            }
            if (sub == 1) // line (RDI=row)
            {
                ushort row = (ushort)rdi;
                if (row == 0 || row > Vga.Rows) { frame[14] = unchecked((ulong)-5); return; }
                Vga.ClearLine((short)(row - 1));
                frame[14] = 0;
                return;
            }
            if (sub == 2) // area (RDI=top, RSI=left, RDX=bottom, RCX=right)
            {
                ushort top = (ushort)rdi;
                ushort left = (ushort)rsi;
                ushort bottom = (ushort)rdx;
                ushort right = (ushort)rcx;
                if (top == 0 || top > Vga.Rows || left == 0 || left > Vga.Cols ||
                    bottom == 0 || bottom > Vga.Rows || right == 0 || right > Vga.Cols ||
                    top > bottom || left > right)
                {
                    frame[14] = unchecked((ulong)-5); // EINVAL
                    return;
                }
                Vga.ClearArea((short)(top - 1), (short)(left - 1), (short)(bottom - 1), (short)(right - 1));
                frame[14] = 0;
                return;
            }
            frame[14] = unchecked((ulong)-5); // EINVAL
            return;
        }

        if (func == 0x7E) // CONS_KEY: AL=0 GETKEY (blocking), AL=1 READKEY (non-block), AL=2 KEYAVAILABLE (bool)
        {
            byte sub = (byte)(rax & 0xFF);
            // KeyEvent struct in user window (12 bytes)
            // We use a fixed per-process slot in the user window
            ulong keyEventPtr = Layout.UserLimit - 16; // reserve 16 bytes at top of user window
            if (!UserPtr(keyEventPtr, 16)) { frame[14] = unchecked((ulong)-1); return; }

            if (sub == 0) // GETKEY (blocking)
            {
                // For now, fall back to GETCHAR (6Eh) which blocks on cooked input
                // TODO: implement proper blocking raw key read with interruptible wait
                int ch = ConsoleInput.PopBlocking(); // blocking version
                if (ch < 0) { frame[14] = unchecked((ulong)ch); return; }
                WriteKeyEvent(keyEventPtr, (ushort)ch, 0, 0, 0, 0);
                frame[14] = keyEventPtr;
                return;
            }
            if (sub == 1) // READKEY (non-blocking)
            {
                int ch = ConsoleInput.Pop(); // non-blocking
                if (ch < 0)
                {
                    frame[14] = 0; // no key available
                    return;
                }
                WriteKeyEvent(keyEventPtr, (ushort)ch, 0, 0, 0, 0);
                frame[14] = keyEventPtr;
                return;
            }
            if (sub == 2) // KEYAVAILABLE
            {
                frame[14] = ConsoleInput.Available() ? 1UL : 0UL;
                return;
            }
            frame[14] = unchecked((ulong)-5); // EINVAL
            return;
        }

        if (func == 0x7F) // CONS_FRAME: AL=0 BEGINFRAME, AL=1 ENDFRAME, AL=2 FLUSH
        {
            byte sub = (byte)(rax & 0xFF);
            if (sub == 0) // BEGINFRAME
            {
                Vga.BeginFrame();
                frame[14] = 0;
                return;
            }
            if (sub == 1) // ENDFRAME
            {
                Vga.EndFrame();
                frame[14] = 0;
                return;
            }
            if (sub == 2) // FLUSH
            {
                Vga.FlushFrame();
                frame[14] = 0;
                return;
            }
            frame[14] = unchecked((ulong)-5); // EINVAL
            return;
        }

        frame[14] = unchecked((ulong)-2); // ENOSYS
    }

    private static void WriteKeyEvent(ulong ptr, ushort keycode, ushort ch, byte ctrl, byte alt, byte shift)
    {
        byte* p = (byte*)ptr;
        p[0] = (byte)(keycode & 0xFF);
        p[1] = (byte)((keycode >> 8) & 0xFF);
        p[2] = (byte)(ch & 0xFF);
        p[3] = (byte)((ch >> 8) & 0xFF);
        p[4] = ctrl;
        p[5] = alt;
        p[6] = shift;
        p[7] = 0; p[8] = 0; p[9] = 0; // reserved
    }
}