// Cooked console input for ring-3 programs (GETCHAR, AH=6Eh).
//
// Merges the two hardware input sources into one byte stream:
//   * serial (COM1, IRQ4) bytes are already ASCII -> pushed as-is
//   * PS/2 (IRQ1) scancodes are translated to ASCII here (shift/caps/extended)
// Ctrl+C is delivered IN BAND: the ring holds ushort entries (0..255 = a byte,
// 0x100 = a Ctrl+C marker), so Pop returns bytes and -2 in the exact arrival
// order. A line typed before Ctrl+C is consumed (and then discarded by the
// aborting reader); a line typed after it is a new line. The legacy in-kernel
// shell keeps its own ConsoleControl path; this class is only for ring 3.

using Osb.Kernel.Memory;

namespace Osb.Kernel.Hardware;

internal static unsafe class ConsoleInput
{
    // Marker pushed into the ring for a Ctrl+C press. Bytes are 0..255, so
    // 0x100 can never collide with a real byte.
    private const ushort CtrlCMark = 0x100;

    // Cooked entry ring: a byte (0..255) or CtrlCMark (0x100), in arrival order.
    private unsafe struct EntryRing
    {
        public fixed ushort data[256];
        public int head;
        public int tail;
    }

    private static EntryRing _ring;
    private static bool _shift;       // PS/2 shift currently held
    private static bool _ctrl;        // PS/2 ctrl currently held
    private static bool _caps;        // PS/2 caps lock on/off (toggled)
    private static bool _extended;    // PS/2 0xE0 prefix seen
    private static bool _ctrlCPending;// a Ctrl+C press is waiting to be consumed

    // Serial path: bytes are already ASCII (control bytes handled by the IRQ).
    public static void PushSerial(byte c)
    {
        int next = _ring.tail + 1;
        if (next >= 256) next = 0;
        if (next != _ring.head)
        {
            _ring.data[_ring.tail] = c;
            _ring.tail = next;
        }
    }

    // A Ctrl+C was pressed (from serial 0x03 or PS/2 Ctrl+C). Pushed IN BAND as
    // CtrlCMark so Pop returns -2 at the exact arrival position, not after all
    // buffered bytes have been drained. Also sets the pending flag so blocking
    // syscalls (WAIT) can detect it without draining the ring.
    public static void RequestCtrlC()
    {
        _ctrlCPending = true;
        int next = _ring.tail + 1;
        if (next >= 256) next = 0;
        if (next != _ring.head)
        {
            _ring.data[_ring.tail] = CtrlCMark;
            _ring.tail = next;
        }
    }

    // True when a Ctrl+C has arrived and not yet been consumed.
    public static bool CtrlCPending => _ctrlCPending;

    // Consume the pending Ctrl+C flag. Called by WAIT when it returns -7 EINTR.
    public static void ConsumeCtrlCPending() { _ctrlCPending = false; }

    // PS/2 path: a raw scancode from IRQ1. Translates to ASCII and pushes.
    // Tracks shift/ctrl/caps so Ctrl+C is caught here (Keyboard consumes it for
    // the legacy shell; ConsoleInput sees the raw stream and handles it for ring 3).
    public static void PushScancode(byte sc)
    {
        if (sc == 0x2A || sc == 0x36) { _shift = true; return; }   // shift down
        if (sc == 0xAA || sc == 0xB6) { _shift = false; return; }  // shift up
        if (sc == 0x1D) { _ctrl = true; return; }                  // ctrl down
        if (sc == 0x9D) { _ctrl = false; return; }                 // ctrl up
        if (sc == 0x3A) { _caps = !_caps; return; }                // caps lock toggle
        if (sc == 0xE0) { _extended = true; return; }              // extended prefix

        if (_extended)
        {
            _extended = false;
            // S1: extended (0xE0) keys are synthesized to the SAME ANSI
            // sequences a serial terminal delivers, so both input sources are
            // identical for the line editor (no shell behavior for them yet).
            switch (sc)
            {
                case 0x48: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'A'); return; // Up
                case 0x50: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'B'); return; // Down
                case 0x4D: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'C'); return; // Right
                case 0x4B: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'D'); return; // Left
                case 0x47: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'H'); return; // Home
                case 0x4F: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'F'); return; // End
                case 0x53: PushSerial(0x1B); PushSerial((byte)'['); PushSerial((byte)'3'); PushSerial((byte)'~'); return; // Delete
                default: return;   // other extended keys: not cooked yet
            }
        }

        if (sc == 0x2E && _ctrl) { RequestCtrlC(); return; }       // Ctrl+C (in band)
        if (sc == 0x3E && _ctrl) return;                           // Ctrl+F4: not cooked

        if ((sc & 0x80) != 0) return;   // ignore break codes

        if (sc == 0x1C) { PushSerial((byte)'\r'); return; }   // Enter
        if (sc == 0x0E) { PushSerial((byte)'\b'); return; }   // Backspace
        if (sc == 0x0F) { PushSerial((byte)'\t'); return; }   // Tab

        char c = Keyboard.MapScancode(sc, _shift);
        // ConsoleInput only: letters are lowercase unless Shift XOR CapsLock.
        // (The legacy Keyboard.MapScancode always returns uppercase and is left
        // untouched; see MIGRATION.md for the decision.)
        if (c >= 'A' && c <= 'Z' && !(_shift ^ _caps)) c = (char)(c + 32);
        if (c != 0) PushSerial((byte)c);
    }

    // S2: drop any buffered cooked bytes and a pending Ctrl+C. Called when a new
    // foreground program is spawned so stale input (or a Ctrl+C from a previous
    // line) does not leak into the next program. Physical modifier state
    // (_shift/_ctrl) is PRESERVED: a spawn must not forget that Shift is held.
    // (Flush-on-spawn moves to the foreground hand-off in slice 6, where a
    // background spawn must not eat the shell's type-ahead.)
    public static void Flush()
    {
        _ring.head = 0;
        _ring.tail = 0;
    }

    // GETCHAR result: next cooked byte (0..255), -1 when empty, -2 for a
    // Ctrl+C marker, in arrival order (non-blocking). Ctrl+C is no longer
    // deferred behind buffered bytes.
    public static int Pop()
    {
        if (_ring.head != _ring.tail)
        {
            ushort e = _ring.data[_ring.head];
            int next = _ring.head + 1;
            if (next >= 256) next = 0;
            _ring.head = next;
            if (e == CtrlCMark) return -2;
            return e;
        }
        return -1;
    }

    // Blocking version for CONS_KEY GETKEY (sub=0). Waits until a byte or Ctrl+C is available.
    public static int PopBlocking()
    {
        while (true)
        {
            int result = Pop();
            if (result != -1) return result;
            // No data available, halt CPU until next interrupt
            NativeMethods.hlt_cpu();
        }
    }
    
    public static bool Available()
    {
        return _ring.head != _ring.tail;
    }
}